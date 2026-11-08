using System.Text.Json;

namespace Snag.Services;

/// <summary>
/// A video's title and channel from YouTube's public oEmbed endpoint: one small request, much quicker than
/// asking yt-dlp. Used for videos added from the Browser tab whose title the page didn't give us.
/// </summary>
public static class YouTubeMeta
{
    private static readonly HttpClient Http = CreateClient();

    public static async Task<(string Title, string? Channel)?> GetAsync(string videoId, CancellationToken ct = default)
    {
        if (!UrlGuard.IsValidVideoId(videoId)) return null;
        try
        {
            var url = "https://www.youtube.com/oembed?format=json&url=" + Uri.EscapeDataString(UrlGuard.WatchUrl(videoId));
            using var response = await Http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var title = CleanText(doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() : null);
            var channel = CleanText(doc.RootElement.TryGetProperty("author_name", out var a) ? a.GetString() : null);
            return title is null ? null : (title, channel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Info($"Title lookup for {videoId} failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>One line, no control characters, at most 300 characters; null when nothing's left.</summary>
    public static string? CleanText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var clean = string.Join(' ', new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length > 300) clean = clean[..300].TrimEnd() + "…";
        return clean.Length == 0 ? null : clean;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Snag/{AppUpdater.CurrentVersionText}");
        return client;
    }
}
