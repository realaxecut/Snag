using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Snag.Services;

/// <summary>One caption: what to show and when.</summary>
public sealed record CaptionCue(TimeSpan Start, TimeSpan End, string Text);

/// <summary>
/// A set of captions for a video: a text subtitle stream inside it (<see cref="StreamIndex"/>) or a
/// subtitle file (<see cref="FilePath"/>).
/// </summary>
public sealed record CaptionTrack(string Name, string MediaPath, int? StreamIndex, string? FilePath)
{
    public string Key => FilePath is not null ? "file:" + FilePath : $"stream:{StreamIndex}";
}

/// <summary>
/// Finds and reads captions for the player, which draws them itself. Embedded tracks are listed with
/// ffprobe and turned into SRT by FFmpeg; .srt and .vtt files are read directly; .ass/.ssa go through FFmpeg.
/// Picture-based subtitles (DVD, Blu-ray) can't be shown this way and are left out.
/// </summary>
public static partial class Captions
{
    private static readonly string[] TextCodecs = ["mov_text", "subrip", "srt", "ass", "ssa", "webvtt", "text", "microdvd", "subviewer", "jacosub", "sami"];
    private static readonly string[] FileExtensions = [".srt", ".vtt", ".ass", ".ssa"];
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [GeneratedRegex(@"(?:(\d+):)?(\d{1,2}):(\d{1,2})[,.](\d{1,3})")]
    private static partial Regex TimePattern();

    [GeneratedRegex(@"<[^>]*>|\{\\[^}]*\}")]
    private static partial Regex TagPattern();

    /// <summary>Captions inside the video, then subtitle files next to it (e.g. "Video.srt", "Video.en.vtt").</summary>
    public static async Task<List<CaptionTrack>> FindTracksAsync(string mediaPath, CancellationToken ct)
    {
        var tracks = new List<CaptionTrack>();
        try
        {
            var (code, json) = await FfTools.RunAsync(FfTools.Ffprobe,
                ["-v", "error", "-select_streams", "s", "-show_entries", "stream=index,codec_name:stream_tags=language,title", "-of", "json", mediaPath],
                Timeout, ct).ConfigureAwait(false);
            if (code == 0 && json.Length > 0)
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("streams", out var streams))
                {
                    foreach (var s in streams.EnumerateArray())
                    {
                        var codec = s.TryGetProperty("codec_name", out var c) ? c.GetString() : null;
                        if (codec is null || !TextCodecs.Contains(codec) || !s.TryGetProperty("index", out var i)) continue;
                        string? language = null, title = null;
                        if (s.TryGetProperty("tags", out var tags))
                        {
                            language = tags.TryGetProperty("language", out var l) ? l.GetString() : null;
                            title = tags.TryGetProperty("title", out var t) ? t.GetString() : null;
                        }
                        var name = !string.IsNullOrWhiteSpace(title) ? title! : LanguageName(language) ?? $"Track {tracks.Count + 1}";
                        tracks.Add(new CaptionTrack(name, mediaPath, i.GetInt32(), null));
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't list the captions in {mediaPath}", ex);
        }

        try
        {
            var folder = Path.GetDirectoryName(mediaPath);
            var stem = Path.GetFileNameWithoutExtension(mediaPath);
            if (folder is not null && Directory.Exists(folder))
            {
                foreach (var file in Directory.EnumerateFiles(folder, stem + ".*").Order(StringComparer.OrdinalIgnoreCase))
                {
                    if (!FileExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
                    // "Video.en.vtt" → "en"; "Video.srt" → nothing.
                    var middle = Path.GetFileNameWithoutExtension(file)[stem.Length..].TrimStart('.');
                    var name = LanguageName(middle) is { } language ? $"{language} (file)"
                        : middle.Length > 0 ? $"{middle} (file)"
                        : "Subtitle file";
                    tracks.Add(new CaptionTrack(name, mediaPath, null, file));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't look for subtitle files next to {mediaPath}", ex);
        }
        return tracks;
    }

    public static CaptionTrack FromFile(string mediaPath, string file) =>
        new($"{Path.GetFileName(file)}", mediaPath, null, file);

    /// <summary>The captions, in time order; null if they couldn't be read.</summary>
    public static async Task<List<CaptionCue>?> LoadAsync(CaptionTrack track, CancellationToken ct)
    {
        try
        {
            string text;
            var ext = track.FilePath is null ? "" : Path.GetExtension(track.FilePath).ToLowerInvariant();
            if (ext is ".srt" or ".vtt")
            {
                text = await File.ReadAllTextAsync(track.FilePath!, ct).ConfigureAwait(false); // UTF-8 unless the file says otherwise
            }
            else
            {
                var input = track.FilePath ?? track.MediaPath;
                List<string> args = ["-v", "error", "-nostdin", "-i", input];
                if (track.StreamIndex is { } index) args.AddRange(["-map", $"0:{index}"]);
                args.AddRange(["-f", "srt", "-"]);
                var (code, output) = await FfTools.RunAsync(FfTools.Ffmpeg, args, Timeout, ct).ConfigureAwait(false);
                if (code != 0) return null;
                text = output;
            }
            return Parse(text);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't read captions ({track.Name})", ex);
            return null;
        }
    }

    /// <summary>Reads SRT or WebVTT: blocks separated by blank lines, each with a "start --> end" line.</summary>
    public static List<CaptionCue> Parse(string text)
    {
        var cues = new List<CaptionCue>();
        var blocks = text.Replace("\r\n", "\n").Replace('\r', '\n').Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        foreach (var block in blocks)
        {
            var lines = block.Split('\n');
            var timing = Array.FindIndex(lines, l => l.Contains("-->", StringComparison.Ordinal));
            if (timing < 0) continue;
            var parts = lines[timing].Split("-->");
            if (!TryTime(parts[0], out var start) || !TryTime(parts[1], out var end) || end <= start) continue;

            var body = string.Join('\n', lines.Skip(timing + 1)
                .Select(l => WebUtility.HtmlDecode(TagPattern().Replace(l, "")).Replace("\\N", "\n").Trim())
                .Where(l => l.Length > 0));
            if (body.Length > 0) cues.Add(new CaptionCue(start, end, body));
        }
        cues.Sort((a, b) => a.Start.CompareTo(b.Start));
        return cues;
    }

    /// <summary>The text showing at <paramref name="time"/> (several cues at once are joined), or null.</summary>
    public static string? TextAt(List<CaptionCue> cues, TimeSpan time)
    {
        // Last cue starting at or before now, then look back for any still running.
        int lo = 0, hi = cues.Count - 1, last = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (cues[mid].Start <= time) { last = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        StringBuilder? text = null;
        for (var i = last; i >= 0 && i > last - 8; i--)
        {
            if (cues[i].End <= time) continue;
            text ??= new StringBuilder();
            if (text.Length > 0) text.Insert(0, '\n');
            text.Insert(0, cues[i].Text);
        }
        return text?.ToString();
    }

    private static bool TryTime(string text, out TimeSpan time)
    {
        time = default;
        var m = TimePattern().Match(text);
        if (!m.Success) return false;
        var hours = m.Groups[1].Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        var ms = m.Groups[4].Value.PadRight(3, '0');
        time = new TimeSpan(0, hours, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
            int.Parse(ms, CultureInfo.InvariantCulture));
        return true;
    }

    /// <summary>"eng" or "en" or "en-US" → "English"; null when it isn't a language code.</summary>
    private static string? LanguageName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code is "und" or "unk" || code.Length > 12) return null;
        try
        {
            var culture = CultureInfo.GetCultures(CultureTypes.AllCultures)
                .FirstOrDefault(c => string.Equals(c.ThreeLetterISOLanguageName, code, StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(c.Name, code, StringComparison.OrdinalIgnoreCase));
            return culture is { Name.Length: > 0 } ? culture.EnglishName : null;
        }
        catch
        {
            return null;
        }
    }
}
