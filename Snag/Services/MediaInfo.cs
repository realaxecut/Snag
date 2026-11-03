using System.Globalization;
using System.Text.Json;

namespace Snag.Services;

public sealed class MediaInfo
{
    public required string Title { get; init; }
    public string? Channel { get; init; }
    /// <summary>The clean page link Snag hands back to yt-dlp to download this video.</summary>
    public string? PageUrl { get; init; }
    public string? ThumbnailUrl { get; init; }
    public double? Duration { get; init; }
    public long? ViewCount { get; init; }
    public DateTime? UploadDate { get; init; }
    public bool IsPlaylist { get; init; }
    public bool IsLive { get; init; }
    public bool IsUpcoming { get; init; }
    public IReadOnlyList<PlaylistVideo> Entries { get; init; } = [];
    public IReadOnlyList<VideoResolution> Resolutions { get; init; } = [];
    public long? AudioBytes { get; init; }
}

/// <summary>A video inside a playlist or profile. Url is always a cleaned page link on a supported site.</summary>
public sealed record PlaylistVideo(int Index, string Id, string Url, string Title, string? Channel, double? Duration, string? ThumbnailUrl, bool Unavailable);

/// <summary>
/// One resolution the video comes in, in one codec. Height is the short side, so vertical videos
/// read as 1080p, not 1920p.
/// </summary>
public sealed record VideoResolution(int Height, int Fps, long? VideoBytes, VideoCodec Codec);

public sealed class YtDlpException(string message) : Exception(message);

/// <summary>Reads the JSON that <c>yt-dlp -J --flat-playlist</c> prints.</summary>
public static class MediaInfoParser
{
    public static MediaInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return Str(root, "_type") == "playlist" ? ParsePlaylist(root) : ParseVideo(root);
    }

    private static MediaInfo ParsePlaylist(JsonElement root)
    {
        var entries = new List<PlaylistVideo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("entries", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in list.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var id = Str(e, "id");
                var ieKey = Str(e, "ie_key");
                // Skip nested tabs, profiles and playlists: only individual videos make it in.
                if (string.IsNullOrEmpty(id) || ieKey is not null && (ieKey.EndsWith("Tab") || ieKey.EndsWith("User") || ieKey.EndsWith("Playlist"))) continue;

                string? url, thumbnail;
                var entryUrl = Str(e, "url");
                var isYouTube = ieKey == "Youtube" ||
                                (ieKey is null && (entryUrl is null || entryUrl.Contains("youtu", StringComparison.OrdinalIgnoreCase)));
                if (isYouTube)
                {
                    if (!UrlGuard.IsValidVideoId(id)) continue;
                    url = UrlGuard.WatchUrl(id);
                    thumbnail = UrlGuard.ThumbnailFor(id);
                }
                else
                {
                    url = UrlGuard.CleanPageUrl(entryUrl) ?? UrlGuard.CleanPageUrl(Str(e, "webpage_url"));
                    thumbnail = BestThumbnail(e);
                }
                if (url is null || !seen.Add(url)) continue;

                var title = Str(e, "title") ?? Str(e, "description") ?? "Untitled";
                var unavailable = title is "[Private video]" or "[Deleted video]" or "[Unavailable video]" ||
                                  Str(e, "availability") is "private" or "needs_auth" or "subscriber_only" or "premium_only";
                entries.Add(new PlaylistVideo(entries.Count + 1, id, url, OneLine(title), Str(e, "channel") ?? Str(e, "uploader"),
                    Num(e, "duration"), thumbnail, unavailable));
            }
        }

        if (entries.Count == 0)
            throw new YtDlpException("No downloadable videos were found at that link.");

        return new MediaInfo
        {
            Title = OneLine(Str(root, "title") ?? "Playlist"),
            Channel = Str(root, "channel") ?? Str(root, "uploader"),
            IsPlaylist = true,
            Entries = entries,
        };
    }

    private static MediaInfo ParseVideo(JsonElement root)
    {
        // One entry per (codec, resolution), keeping the highest frame rate and the biggest known size.
        var variants = new Dictionary<(VideoCodec Codec, int Res), (int Fps, long? Bytes)>();
        long? audioBytes = null;
        var audioRank = -1;

        if (root.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in formats.EnumerateArray())
            {
                var vcodec = Str(f, "vcodec");
                var acodec = Str(f, "acodec");
                var size = Long(f, "filesize") ?? Long(f, "filesize_approx");
                var height = (int)(Num(f, "height") ?? 0);
                var width = (int)(Num(f, "width") ?? 0);

                if (vcodec != "none" && height > 0)
                {
                    var key = (CodecSupport.Family(vcodec), width > 0 ? Math.Min(width, height) : height);
                    var fps = (int)Math.Round(Num(f, "fps") ?? 0);
                    variants[key] = variants.TryGetValue(key, out var current)
                        ? (Math.Max(current.Fps, fps), size > current.Bytes || current.Bytes is null ? size : current.Bytes)
                        : (fps, size);
                }
                else if (vcodec == "none" && acodec is not null && acodec != "none" && size is not null)
                {
                    var rank = acodec.StartsWith("mp4a", StringComparison.Ordinal) ? 1 : 0;
                    if (rank > audioRank || (rank == audioRank && size > audioBytes))
                    {
                        audioBytes = size;
                        audioRank = rank;
                    }
                }
            }
        }

        var liveStatus = Str(root, "live_status");
        var id = Str(root, "id");
        var isYouTube = Str(root, "extractor_key") == "Youtube" && UrlGuard.IsValidVideoId(id);
        return new MediaInfo
        {
            Title = OneLine(Str(root, "title") ?? Str(root, "description") ?? "Untitled"),
            Channel = Str(root, "channel") ?? Str(root, "uploader"),
            PageUrl = isYouTube ? UrlGuard.WatchUrl(id!) : UrlGuard.CleanPageUrl(Str(root, "webpage_url")),
            ThumbnailUrl = isYouTube ? UrlGuard.ThumbnailFor(id!, large: true) : BestThumbnail(root),
            Duration = Num(root, "duration"),
            ViewCount = Long(root, "view_count"),
            UploadDate = DateTime.TryParseExact(Str(root, "upload_date"), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null,
            IsLive = liveStatus == "is_live" || (root.TryGetProperty("is_live", out var live) && live.ValueKind == JsonValueKind.True),
            IsUpcoming = liveStatus == "is_upcoming",
            Resolutions = variants.Select(kv => new VideoResolution(kv.Key.Res, kv.Value.Fps, kv.Value.Bytes, kv.Key.Codec)).ToList(),
            AudioBytes = audioBytes,
        };
    }

    /// <summary>The largest thumbnail on an allowed image host, else the plain "thumbnail" field if allowed.</summary>
    private static string? BestThumbnail(JsonElement e)
    {
        if (e.TryGetProperty("thumbnails", out var thumbs) && thumbs.ValueKind == JsonValueKind.Array)
        {
            var best = thumbs.EnumerateArray()
                .Where(t => t.ValueKind == JsonValueKind.Object && UrlGuard.IsAllowedImageUrl(Str(t, "url")))
                .OrderByDescending(t => (Num(t, "width") ?? 0) * (Num(t, "height") ?? 0))
                .ThenByDescending(t => Num(t, "preference") ?? 0)
                .Select(t => Str(t, "url"))
                .FirstOrDefault();
            if (best is not null) return best;
        }
        var single = Str(e, "thumbnail");
        return UrlGuard.IsAllowedImageUrl(single) ? single : null;
    }

    /// <summary>TikTok/Instagram "titles" are captions; keep them to one tidy line.</summary>
    private static string OneLine(string text)
    {
        var line = string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length > 160 ? line[..160].TrimEnd() + "…" : line;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static long? Long(JsonElement e, string name) => Num(e, name) is { } d ? (long)d : null;
}
