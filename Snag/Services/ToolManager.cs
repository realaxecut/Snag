using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Snag.Services;

public readonly record struct ToolProgress(string Message, double Percent);

public sealed class ToolVersions
{
    public string? YtDlp { get; set; }
    public string? Deno { get; set; }
    public string? Ffmpeg { get; set; }
    public string? FfmpegSha256 { get; set; }
    public string? Vlc { get; set; }
    public DateTime? LastUpdateCheckUtc { get; set; }
}

public sealed record UpdateReport(IReadOnlyList<string> Updated);

/// <summary>
/// Installs yt-dlp, FFmpeg and Deno (from their official GitHub releases) and the VLC engine (from
/// VideoLAN's own download server) into %LOCALAPPDATA%\Snag\tools. Each download is hashed while it
/// streams and thrown away unless its SHA-256 matches the checksum published next to it.
/// Zip entries are only extracted to paths Snag chooses.
/// </summary>
public sealed partial class ToolManager
{
    private const string FfmpegBaseUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/";
    private const string FfmpegAsset = "ffmpeg-master-latest-win64-gpl-shared.zip";
    private const string DenoAsset = "deno-x86_64-pc-windows-msvc.zip";
    private const string VlcBaseUrl = "https://download.videolan.org/pub/videolan/vlc/last/win64/";

    internal static readonly HttpClient Http = CreateHttpClient();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\b[0-9a-fA-F]{64}\b")]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex(@"^[^/]+/bin/[^/]+\.(exe|dll)$", RegexOptions.IgnoreCase)]
    private static partial Regex FfmpegEntryPattern();

    [GeneratedRegex(@"href=""vlc-(3\.\d+\.\d+)-win64\.zip""")]
    private static partial Regex VlcZipPattern();

    public ToolManager() => Versions = LoadVersions();

    public ToolVersions Versions { get; private set; }

    public string Root => AppPaths.ToolsDir;
    public string YtDlpPath => Path.Combine(Root, "yt-dlp", "yt-dlp.exe");
    public string FfmpegDir => Path.Combine(Root, "ffmpeg");
    public string DenoPath => Path.Combine(Root, "deno", "deno.exe");
    public static string VlcDir => Path.Combine(AppPaths.ToolsDir, "vlc");
    private string VersionsPath => Path.Combine(Root, "versions.json");
    private string DownloadDir => Path.Combine(Root, ".downloads");

    public bool HasYtDlp => File.Exists(YtDlpPath);
    public bool HasFfmpeg => File.Exists(Path.Combine(FfmpegDir, "ffmpeg.exe")) && File.Exists(Path.Combine(FfmpegDir, "ffprobe.exe"));
    public bool HasDeno => File.Exists(DenoPath);
    public static bool HasVlc => File.Exists(Path.Combine(VlcDir, "libvlc.dll")) && File.Exists(Path.Combine(VlcDir, "libvlccore.dll")) && Directory.Exists(Path.Combine(VlcDir, "plugins"));
    public bool IsReady => HasYtDlp && HasFfmpeg && HasDeno && HasVlc;

    /// <summary>True when the downloader is installed and only the newer VLC engine is missing.</summary>
    public bool OnlyVlcMissing => HasYtDlp && HasFfmpeg && HasDeno && !HasVlc;

    public Task InstallMissingAsync(IProgress<ToolProgress> progress, CancellationToken ct)
    {
        var steps = new List<Step>();
        if (!HasYtDlp) steps.Add(YtDlpStep(null));
        if (!HasDeno) steps.Add(DenoStep(null));
        if (!HasFfmpeg) steps.Add(FfmpegStep());
        if (!HasVlc) steps.Add(VlcStep());
        return RunAsync(steps, progress, ct);
    }

    public Task ReinstallAllAsync(IProgress<ToolProgress> progress, CancellationToken ct) =>
        RunAsync([YtDlpStep(null), DenoStep(null), FfmpegStep(), VlcStep()], progress, ct);

    /// <summary>Updates yt-dlp (and optionally Deno and VLC) when there's a newer release.</summary>
    public async Task<UpdateReport> UpdateAsync(bool includeDeno, IProgress<ToolProgress> progress, CancellationToken ct)
    {
        progress.Report(new ToolProgress("Checking for updates…", 0));
        var steps = new List<Step>();

        var ytdlpTag = await LatestTagAsync("yt-dlp/yt-dlp", ct).ConfigureAwait(false);
        if (ytdlpTag != Versions.YtDlp || !HasYtDlp) steps.Add(YtDlpStep(ytdlpTag));

        if (includeDeno)
        {
            var denoTag = await LatestTagAsync("denoland/deno", ct).ConfigureAwait(false);
            if (denoTag != Versions.Deno || !HasDeno) steps.Add(DenoStep(denoTag));

            var vlcVersion = await LatestVlcVersionAsync(ct).ConfigureAwait(false);
            if (vlcVersion != Versions.Vlc || !HasVlc) steps.Add(VlcStep());
        }

        Versions.LastUpdateCheckUtc = DateTime.UtcNow;
        await RunAsync(steps, progress, ct).ConfigureAwait(false);
        return new UpdateReport(steps.Select(s => s.Name).ToList());
    }

    private sealed record Step(string Name, double SizeMb, Func<Action<double>, CancellationToken, Task> Run);

    private Step YtDlpStep(string? tag) => new("yt-dlp", 23, (report, ct) => InstallYtDlpAsync(tag, report, ct));
    private Step DenoStep(string? tag) => new("Deno", 41, (report, ct) => InstallDenoAsync(tag, report, ct));
    private Step FfmpegStep() => new("FFmpeg", 87, InstallFfmpegAsync);
    private Step VlcStep() => new("VLC engine", 79, InstallVlcAsync);

    private async Task RunAsync(IReadOnlyList<Step> steps, IProgress<ToolProgress> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Root);
        var total = Math.Max(1, steps.Sum(s => s.SizeMb));
        var done = 0.0;
        foreach (var step in steps)
        {
            var start = done;
            progress.Report(new ToolProgress($"Downloading {step.Name}…", start / total * 100));
            await step.Run(f => progress.Report(new ToolProgress($"Downloading {step.Name}…", (start + f * step.SizeMb) / total * 100)), ct)
                .ConfigureAwait(false);
            done += step.SizeMb;
            SaveVersions();
            Log.Info($"Installed {step.Name}");
        }
        SaveVersions();
        progress.Report(new ToolProgress("Done", 100));
    }

    private async Task InstallYtDlpAsync(string? tag, Action<double> report, CancellationToken ct)
    {
        tag ??= await LatestTagAsync("yt-dlp/yt-dlp", ct).ConfigureAwait(false);
        var baseUrl = $"https://github.com/yt-dlp/yt-dlp/releases/download/{tag}/";
        var sums = await Http.GetStringAsync(baseUrl + "SHA2-256SUMS", ct).ConfigureAwait(false);
        var file = await DownloadVerifiedAsync(baseUrl + "yt-dlp.exe", HashForFile(sums, "yt-dlp.exe"), report, ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(YtDlpPath)!);
            File.Move(file, YtDlpPath, overwrite: true);
        }
        finally
        {
            TryDelete(file);
        }
        Versions.YtDlp = tag;
    }

    private async Task InstallDenoAsync(string? tag, Action<double> report, CancellationToken ct)
    {
        tag ??= await LatestTagAsync("denoland/deno", ct).ConfigureAwait(false);
        var baseUrl = $"https://github.com/denoland/deno/releases/download/{tag}/";
        var sumFile = await Http.GetStringAsync(baseUrl + DenoAsset + ".sha256sum", ct).ConfigureAwait(false);
        var expected = Sha256Pattern().Match(sumFile) is { Success: true } m
            ? m.Value
            : throw new InvalidDataException("Deno's release has no SHA-256 checksum, so it wasn't installed.");
        var zip = await DownloadVerifiedAsync(baseUrl + DenoAsset, expected, report, ct).ConfigureAwait(false);
        try
        {
            ExtractSelected(zip, Path.GetDirectoryName(DenoPath)!, entry => entry.Equals("deno.exe", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(zip);
        }
        Versions.Deno = tag;
    }

    private async Task InstallFfmpegAsync(Action<double> report, CancellationToken ct)
    {
        var sums = await Http.GetStringAsync(FfmpegBaseUrl + "checksums.sha256", ct).ConfigureAwait(false);
        var expected = HashForFile(sums, FfmpegAsset);
        var zip = await DownloadVerifiedAsync(FfmpegBaseUrl + FfmpegAsset, expected, report, ct).ConfigureAwait(false);
        try
        {
            ExtractSelected(zip, FfmpegDir, entry =>
                FfmpegEntryPattern().IsMatch(entry) && !entry.EndsWith("/ffplay.exe", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(zip);
        }
        Versions.FfmpegSha256 = expected;
        Versions.Ffmpeg = await ReadFfmpegVersionAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The VLC engine behind Snag's player (it plays H.264, HEVC, VP9, AV1 and every audio format).
    /// Only libvlc and its plugins are kept from the official zip; the VLC app itself isn't installed.
    /// </summary>
    private async Task InstallVlcAsync(Action<double> report, CancellationToken ct)
    {
        var version = await LatestVlcVersionAsync(ct).ConfigureAwait(false);
        var name = $"vlc-{version}-win64.zip";
        var sums = await Http.GetStringAsync(VlcBaseUrl + name + ".sha256", ct).ConfigureAwait(false);
        var zip = await DownloadVerifiedAsync(VlcBaseUrl + name, HashForFile(sums, name), report, ct).ConfigureAwait(false);
        try
        {
            ExtractTree(zip, VlcDir, $"vlc-{version}/", path =>
                path is "libvlc.dll" or "libvlccore.dll" or "vlc-cache-gen.exe" ||
                path.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(zip);
        }
        await BuildVlcPluginCacheAsync().ConfigureAwait(false);
        Versions.Vlc = version;
    }

    private static async Task<string> LatestVlcVersionAsync(CancellationToken ct)
    {
        var listing = await Http.GetStringAsync(VlcBaseUrl, ct).ConfigureAwait(false);
        return VlcZipPattern().Match(listing) is { Success: true } m
            ? m.Groups[1].Value
            : throw new InvalidDataException("Couldn't find the latest VLC release on videolan.org.");
    }

    /// <summary>VLC scans every plugin at startup unless it has a cache; building one makes the player open instantly.</summary>
    private static async Task BuildVlcPluginCacheAsync()
    {
        var tool = Path.Combine(VlcDir, "vlc-cache-gen.exe");
        if (!File.Exists(tool)) return;
        try
        {
            var psi = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = VlcDir };
            psi.ArgumentList.Add(Path.Combine(VlcDir, "plugins"));
            using var process = Process.Start(psi);
            if (process is null) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't build the VLC plugin cache (VLC still works, it just starts a little slower)", ex);
        }
    }

    /// <summary>Follows github.com/{repo}/releases/latest to its tag, e.g. "2026.10.26".</summary>
    private static async Task<string> LatestTagAsync(string repo, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, $"https://github.com/{repo}/releases/latest");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var path = response.RequestMessage?.RequestUri?.AbsolutePath ?? "";
        var tag = path[(path.LastIndexOf('/') + 1)..];
        if (!path.Contains("/releases/tag/", StringComparison.Ordinal) || !TagPattern().IsMatch(tag))
            throw new InvalidDataException($"Couldn't find the latest {repo} release on GitHub.");
        return tag;
    }

    private Task<string> DownloadVerifiedAsync(string url, string expectedSha256, Action<double> report, CancellationToken ct) =>
        DownloadVerifiedAsync(url, expectedSha256, DownloadDir, report, ct);

    /// <summary>Streams <paramref name="url"/> into a temp file in <paramref name="directory"/>, hashing as it goes; throws (and deletes it) unless the SHA-256 matches.</summary>
    internal static async Task<string> DownloadVerifiedAsync(string url, string expectedSha256, string directory, Action<double> report, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".part");
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var length = response.Content.Headers.ContentLength;

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var sink = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long received = 0;
                var lastReport = 0L;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await sink.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;
                    if (length > 0 && Environment.TickCount64 - lastReport > 100)
                    {
                        lastReport = Environment.TickCount64;
                        report(Math.Min(1, received / (double)length.Value));
                    }
                }
            }

            var actual = Convert.ToHexString(hash.GetHashAndReset());
            if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                Log.Error($"Checksum mismatch for {url}: expected {expectedSha256}, got {actual}");
                throw new InvalidDataException($"{Path.GetFileName(new Uri(url).AbsolutePath)} failed its SHA-256 check, so it was discarded.");
            }
            report(1);
            return target;
        }
        catch
        {
            TryDelete(target);
            throw;
        }
    }

    /// <summary>
    /// Extracts only the entries <paramref name="include"/> accepts, flattened to bare file names,
    /// into a staging folder that then replaces <paramref name="destination"/>.
    /// </summary>
    private static void ExtractSelected(string zipPath, string destination, Func<string, bool> include)
    {
        var staging = destination + ".new";
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        var stagingRoot = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;

        var extracted = 0;
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (entry.Name.Length == 0 || !include(name)) continue;
                var fileName = Path.GetFileName(entry.Name);
                if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;
                var target = Path.GetFullPath(Path.Combine(staging, fileName));
                if (!target.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase)) continue;
                entry.ExtractToFile(target, overwrite: true);
                extracted++;
            }
        }
        if (extracted == 0) throw new InvalidDataException("The downloaded archive didn't contain the expected files.");

        if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        Directory.Move(staging, destination);
    }

    /// <summary>
    /// Like <see cref="ExtractSelected"/> but keeps folders: entries under <paramref name="prefix"/> that
    /// <paramref name="include"/> accepts are written below <paramref name="destination"/>, and any path
    /// that would land outside it (".." tricks) is skipped.
    /// </summary>
    private static void ExtractTree(string zipPath, string destination, string prefix, Func<string, bool> include)
    {
        var staging = destination + ".new";
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        var stagingRoot = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;

        var extracted = 0;
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (entry.Name.Length == 0 || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var relative = name[prefix.Length..];
                if (!include(relative) || relative.Split('/').Any(part => part is "" or "." or "..")) continue;
                var target = Path.GetFullPath(Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                extracted++;
            }
        }
        if (extracted == 0) throw new InvalidDataException("The downloaded archive didn't contain the expected files.");

        if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        Directory.Move(staging, destination);
    }

    internal static string HashForFile(string checksums, string fileName)
    {
        foreach (var line in checksums.Split('\n'))
        {
            var parts = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*') == fileName && Sha256Pattern().IsMatch(parts[0])) return parts[0];
        }
        throw new InvalidDataException($"No published checksum for {fileName}, so it wasn't installed.");
    }

    private async Task<string?> ReadFfmpegVersionAsync()
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(FfmpegDir, "ffmpeg.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            psi.ArgumentList.Add("-version");
            using var process = Process.Start(psi);
            if (process is null) return null;
            var firstLine = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);
            var parts = firstLine?.Split(' ');
            return parts is { Length: > 2 } ? parts[2] : null;
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't read the FFmpeg version", ex);
            return null;
        }
    }

    private ToolVersions LoadVersions()
    {
        try
        {
            if (File.Exists(VersionsPath))
                return JsonSerializer.Deserialize<ToolVersions>(File.ReadAllText(VersionsPath)) ?? new ToolVersions();
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't read tool versions", ex);
        }
        return new ToolVersions();
    }

    private void SaveVersions()
    {
        try
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(VersionsPath, JsonSerializer.Serialize(Versions, Json));
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't save tool versions", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A leftover temp file is harmless; it's cleaned up next time.
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Snag/{AppUpdater.CurrentVersionText}");
        return client;
    }
}
