using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Snag.Services;

public sealed record DownloadOptions(
    bool AudioOnly,
    string VideoQuality,
    VideoCodec Codec,
    string AudioQuality,
    bool EmbedThumbnail,
    bool EmbedSubtitles,
    bool SponsorBlock);

/// <summary>One video to fetch. FilePrefix is digits and " - " only (playlist numbering), never user text.</summary>
public sealed record DownloadJob(string Url, string OutputDir, string FilePrefix, DownloadOptions Options);

public readonly record struct DownloadProgress(string Status, double? Downloaded, double? Total, double? Speed, double? Eta, bool IsAudioStream);

public sealed record DownloadResult(int ExitCode, string? Error);

public interface IDownloadSink
{
    void OnFormat(string formatId, int? height, int? width, int? fps, string? vcodec);
    void OnProgress(DownloadProgress progress);
    void OnPostProcess(string status, string processor);
    void OnFile(string path);
}

/// <summary>
/// Runs yt-dlp. Arguments always go through ProcessStartInfo.ArgumentList (no shell, no string
/// building), user config files and plugins are ignored, and the URL always comes after "--".
/// </summary>
public sealed partial class YtDlpService(ToolManager tools)
{
    private const string Marker = "SNAG|";

    [GeneratedRegex(@"^\[[^\]]+\]\s*[^:\s]{1,64}:\s*")]
    private static partial Regex ExtractorPrefix();

    public async Task<MediaInfo> AnalyzeAsync(Uri url, bool asPlaylist, CancellationToken ct)
    {
        var args = new List<string> { "-J", "--flat-playlist", asPlaylist ? "--yes-playlist" : "--no-playlist" };
        // TikTok/Instagram profiles can hold thousands of posts and are paged slowly; the newest 200 is plenty.
        if (UrlGuard.SiteOf(url) != Site.YouTube) args.AddRange(["--playlist-items", "1:200"]);
        args.AddRange(["--", url.AbsoluteUri]);
        using var process = Start(args, tools.Root);
        using var registration = ct.Register(() => Kill(process));

        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        await process.WaitForExitAsync(CancellationToken.None);
        var json = await stdout;
        var errors = await stderr;

        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
        {
            Log.Error($"Analyze failed (exit {process.ExitCode}): {errors.Trim()}");
            throw new YtDlpException(FriendlyError(errors) ?? "Couldn't read that link.");
        }
        return MediaInfoParser.Parse(json);
    }

    public async Task<DownloadResult> DownloadAsync(DownloadJob job, string tempDir, IDownloadSink sink, CancellationToken ct)
    {
        using var process = Start(BuildDownloadArgs(job, tempDir), tempDir);
        using var registration = ct.Register(() => Kill(process));

        string? error = null;
        var stdout = PumpLinesAsync(process.StandardOutput, line => HandleLine(line, sink));
        var stderr = PumpLinesAsync(process.StandardError, line =>
        {
            if (line.StartsWith("ERROR:", StringComparison.Ordinal)) error = line;
            else if (line.StartsWith("WARNING:", StringComparison.Ordinal)) Log.Info(line);
        });
        await Task.WhenAll(stdout, stderr);
        await process.WaitForExitAsync(CancellationToken.None);
        return new DownloadResult(process.ExitCode, FriendlyError(error));
    }

    private static List<string> BuildDownloadArgs(DownloadJob job, string tempDir)
    {
        var o = job.Options;
        var videoQuality = SettingsStore.IsValidQuality(o.VideoQuality) ? o.VideoQuality : "best";
        var audioQuality = SettingsStore.IsValidQuality(o.AudioQuality) ? o.AudioQuality : "best";

        var args = new List<string>
        {
            "--newline", "--progress", "--no-mtime", "--no-playlist", "--trim-filenames", "120",
            "-P", "home:" + job.OutputDir,
            "-P", "temp:" + tempDir,
            "-o", job.FilePrefix + "%(title)s.%(ext)s",
            "--progress-template", "download:SNAG|dl|%(progress.status)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s|%(info.vcodec)s",
            "--progress-template", "postprocess:SNAG|pp|%(progress.status)s|%(progress.postprocessor)s",
            "--print", "before_dl:SNAG|fmt|%(format_id)s|%(height)s|%(width)s|%(fps)s|%(vcodec)s",
            "--print", "after_move:SNAG|file|%(filepath)s",
        };

        if (o.AudioOnly)
        {
            args.AddRange(["-f", "ba/b", "-x", "--audio-format", "mp3", "--audio-quality", audioQuality == "best" ? "0" : audioQuality + "K"]);
        }
        else
        {
            // Largest resolution up to the cap, in the chosen codec (or, on Auto, H.264 + AAC where
            // there's a tie), and only codecs this PC can decode so the file plays in Snag's player.
            var res = videoQuality == "best" ? "res" : "res:" + videoQuality;
            args.AddRange(["-f", CodecSupport.FormatSelector(o.Codec), "-S", res + ",vcodec:h264,acodec:aac",
                "--merge-output-format", "mp4", "--remux-video", "mp4"]);
            if (o.EmbedSubtitles) args.AddRange(["--write-subs", "--sub-langs", "en.*", "--embed-subs"]);
            if (o.EmbedThumbnail) args.Add("--embed-chapters");
        }

        if (o.EmbedThumbnail) args.AddRange(["--embed-metadata", "--embed-thumbnail", "--convert-thumbnails", "jpg"]);
        if (o.SponsorBlock) args.AddRange(["--sponsorblock-remove", "sponsor,selfpromo"]);

        args.Add("--");
        args.Add(job.Url);
        return args;
    }

    private Process Start(IEnumerable<string> args, string workingDir)
    {
        var psi = new ProcessStartInfo(tools.YtDlpPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = workingDir,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        string[] common =
        [
            "--ignore-config", "--no-plugin-dirs", "--no-update", "--encoding", "utf-8",
            // Only the Deno that Snag installed and verified; no remote code downloads.
            "--no-js-runtimes", "--js-runtimes", "deno:" + tools.DenoPath, "--no-remote-components",
            "--ffmpeg-location", tools.FfmpegDir,
        ];
        foreach (var arg in common.Concat(args)) psi.ArgumentList.Add(arg);

        Log.Info("yt-dlp " + string.Join(' ', psi.ArgumentList.Skip(common.Length)));
        var process = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start yt-dlp.");
        ChildProcessJob.Attach(process);
        return process;
    }

    private static async Task PumpLinesAsync(StreamReader reader, Action<string> onLine)
    {
        while (await reader.ReadLineAsync() is { } line) onLine(line);
    }

    private static void HandleLine(string line, IDownloadSink sink)
    {
        if (!line.StartsWith(Marker, StringComparison.Ordinal)) return;
        var p = line.Split('|');
        switch (p[1])
        {
            case "dl" when p.Length >= 9:
                sink.OnProgress(new DownloadProgress(p[2], Num(p[3]), Num(p[4]) ?? Num(p[5]), Num(p[6]), Num(p[7]), p[8] == "none"));
                break;
            case "pp" when p.Length >= 4:
                sink.OnPostProcess(p[2], p[3]);
                break;
            case "fmt" when p.Length >= 6:
                sink.OnFormat(p[2], (int?)Num(p[3]), (int?)Num(p[4]), Num(p[5]) is { } fps ? (int)Math.Round(fps) : null, p.Length > 6 ? p[6] : null);
                break;
            case "file" when p.Length >= 3:
                sink.OnFile(string.Join('|', p[2..]));
                break;
        }
    }

    private static double? Num(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't stop yt-dlp", ex);
        }
    }

    /// <summary>Turns yt-dlp's last ERROR line into something a person can act on.</summary>
    public static string? FriendlyError(string? output)
    {
        var line = output?.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal));
        if (line is null) return null;
        var message = ExtractorPrefix().Replace(line["ERROR:".Length..].Trim(), "");

        bool Has(string text) => message.Contains(text, StringComparison.OrdinalIgnoreCase);
        if (Has("not a bot")) return "YouTube wants to confirm you're not a bot. Wait a few minutes and try again.";
        if (Has("empty media response") || Has("login required") || Has("rate-limit reached") || Has("not granting access"))
            return "Instagram wouldn't share this post without a login. Snag can only download public posts, and Instagram often blocks them for a while; try again later.";
        if (Has("IP address is blocked")) return "TikTok is blocking downloads from your network for this video right now.";
        if (Has("Unsupported URL")) return "That page doesn't have a video Snag can download.";
        if (Has("Private video")) return "This video is private.";
        if (Has("confirm your age") || Has("age-restricted")) return "This video is age-restricted and needs a signed-in account.";
        if (Has("members-only") || Has("join this channel")) return "This video is for channel members only.";
        if (Has("Video unavailable") || Has("This video is unavailable")) return "This video is unavailable.";
        if (Has("getaddrinfo") || Has("Unable to download webpage") || Has("timed out") || Has("Connection"))
            return "Couldn't reach YouTube. Check your internet connection.";
        if (Has("No space left")) return "Your disk is full.";
        return message.Length > 240 ? message[..240] + "…" : message;
    }
}
