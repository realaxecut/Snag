using System.Text.RegularExpressions;

namespace Snag.Services;

public enum Site { YouTube, TikTok, Instagram }

/// <summary>
/// The only gate between what the user pastes and yt-dlp. Accepts YouTube, TikTok and Instagram
/// links only and rebuilds them as clean https URLs, so nothing else ever reaches the downloader.
/// </summary>
public static partial class UrlGuard
{
    private static readonly Dictionary<string, Site> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["youtube.com"] = Site.YouTube,
        ["www.youtube.com"] = Site.YouTube,
        ["m.youtube.com"] = Site.YouTube,
        ["music.youtube.com"] = Site.YouTube,
        ["youtu.be"] = Site.YouTube,
        ["youtube-nocookie.com"] = Site.YouTube,
        ["www.youtube-nocookie.com"] = Site.YouTube,
        ["tiktok.com"] = Site.TikTok,
        ["www.tiktok.com"] = Site.TikTok,
        ["m.tiktok.com"] = Site.TikTok,
        ["vm.tiktok.com"] = Site.TikTok,
        ["vt.tiktok.com"] = Site.TikTok,
        ["instagram.com"] = Site.Instagram,
        ["www.instagram.com"] = Site.Instagram,
    };

    // Where thumbnails may be loaded from (https only).
    private static readonly string[] ImageHostSuffixes =
    [
        ".ytimg.com", ".tiktokcdn.com", ".tiktokcdn-us.com", ".tiktokcdn-eu.com", ".cdninstagram.com", ".fbcdn.net",
    ];

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoIdPattern();

    [GeneratedRegex("^/(@[^/]+|channel/[^/]+|c/[^/]+|user/[^/]+)/?$", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelRootPattern();

    public static bool TryParse(string? input, out Uri uri, out string error)
    {
        uri = null!;
        error = "";
        var text = (input ?? "").Trim();

        if (text.Length == 0)
        {
            error = "Paste a YouTube, TikTok or Instagram link first.";
            return false;
        }
        if (text.Length > 2048)
        {
            error = "That link is too long.";
            return false;
        }
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
        {
            error = "That doesn't look like a link.";
            return false;
        }
        if (!AllowedHosts.TryGetValue(parsed.Host, out var site) || parsed.UserInfo.Length > 0 || !parsed.IsDefaultPort)
        {
            error = "Only YouTube, TikTok and Instagram links are supported.";
            return false;
        }

        var path = parsed.AbsolutePath;
        // A bare YouTube channel page is a set of tabs; the Videos tab is what people mean.
        if (site == Site.YouTube && ChannelRootPattern().IsMatch(path)) path = path.TrimEnd('/') + "/videos";

        // Path and query are already escaped by Uri; the fragment and any credentials are dropped.
        uri = new Uri($"https://{parsed.Host.ToLowerInvariant()}{path}{parsed.Query}", UriKind.Absolute);
        return true;
    }

    public static Site SiteOf(Uri uri) => AllowedHosts.TryGetValue(uri.Host, out var site) ? site : Site.YouTube;

    /// <summary>
    /// For links that come back from yt-dlp (playlist entries, a video's page): accepted only if
    /// they're https links on one of the supported sites, and returned in clean form.
    /// </summary>
    public static string? CleanPageUrl(string? url) =>
        url is not null && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && TryParse(url, out var uri, out _)
            ? uri.AbsoluteUri
            : null;

    public static bool IsAllowedImageUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        ImageHostSuffixes.Any(suffix => ("." + uri.Host).EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    public static bool IsValidVideoId(string? id) => id is not null && VideoIdPattern().IsMatch(id);

    /// <summary>
    /// A single YouTube video (watch, Shorts, youtu.be, live or embed link) in one canonical form,
    /// https://www.youtube.com/watch?v=ID, or null. Used for videos streamed in the player and links
    /// from the YouTube tab, so the same video is always the same string.
    /// </summary>
    public static string? CanonicalVideoUrl(string? url) =>
        TryParse(url, out var uri, out _) && SiteOf(uri) == Site.YouTube && VideoIdOf(uri) is { } id ? WatchUrl(id) : null;

    /// <summary>Hosts YouTube streams media from (what yt-dlp hands the player). https only.</summary>
    public static bool IsAllowedStreamUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        (("." + uri.Host).EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
         ("." + uri.Host).EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>Where the YouTube tab may go: YouTube itself and Google's sign-in pages. Everything else opens in the real browser.</summary>
    public static bool IsBrowserHost(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        var dotted = "." + host.ToLowerInvariant();
        return dotted.EndsWith(".youtube.com", StringComparison.Ordinal) ||
               dotted is ".youtu.be" or ".youtube-nocookie.com" or ".www.youtube-nocookie.com" ||
               dotted.EndsWith(".google.com", StringComparison.Ordinal);
    }

    public static string WatchUrl(string videoId) => $"https://www.youtube.com/watch?v={videoId}";

    public static string ThumbnailFor(string videoId, bool large = false) =>
        $"https://i.ytimg.com/vi/{videoId}/{(large ? "hqdefault" : "mqdefault")}.jpg";

    /// <summary>The video a link points at, if any (watch, youtu.be, shorts, live and embed links).</summary>
    public static string? VideoIdOf(Uri uri)
    {
        string? id;
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
            id = segments.FirstOrDefault();
        else if (segments is ["watch", ..])
            id = QueryValue(uri, "v");
        else if (segments is ["shorts" or "live" or "embed" or "v", var second, ..])
            id = second;
        else
            id = null;
        return IsValidVideoId(id) ? id : null;
    }

    /// <summary>
    /// True when the link should be shown as a playlist. Auto-generated "Mix" radio lists
    /// (list=RD...) attached to a video are treated as just that video.
    /// </summary>
    public static bool WantsPlaylist(Uri uri)
    {
        var list = QueryValue(uri, "list");
        if (string.IsNullOrEmpty(list)) return false;
        return !(list.StartsWith("RD", StringComparison.Ordinal) && VideoIdOf(uri) is not null);
    }

    public static string? QueryValue(Uri uri, string key)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var name = eq < 0 ? pair : pair[..eq];
            if (name.Equals(key, StringComparison.Ordinal))
                return eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return null;
    }
}
