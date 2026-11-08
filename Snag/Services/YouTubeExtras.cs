using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Snag.Services;

/// <summary>A stretch of a video to skip: a sponsor read or self-promotion, from SponsorBlock.</summary>
public readonly record struct SkipSegment(double Start, double End);

/// <summary>
/// The two community add-ons people install for YouTube, built in:
/// <list type="bullet">
/// <item>SponsorBlock (sponsor.ajay.app): where the sponsor reads are, to skip them. Asked privately: Snag sends only
/// the first 4 characters of the video ID's SHA-256 hash and picks this video out of the answer itself.</item>
/// <item>Return YouTube Dislike (returnyoutubedislikeapi.com): the dislike count YouTube no longer shows. This one
/// is sent the video's ID, as the browser extension does.</item>
/// </list>
/// Answers are remembered for the session. Failures just mean nothing to skip, or no count.
/// </summary>
public static class YouTubeExtras
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly Dictionary<string, Task<IReadOnlyList<SkipSegment>>> Segments = [];
    private static readonly Dictionary<string, Task<long?>> Dislikes = [];

    /// <summary>The categories skipped: the same ones Snag cuts out of downloads with the SponsorBlock option.</summary>
    private const string Categories = "[\"sponsor\",\"selfpromo\"]";

    public static Task<IReadOnlyList<SkipSegment>> SkipSegmentsAsync(string videoId)
    {
        if (!UrlGuard.IsValidVideoId(videoId)) return Task.FromResult<IReadOnlyList<SkipSegment>>([]);
        lock (Segments)
        {
            if (!Segments.TryGetValue(videoId, out var task) || task.IsFaulted)
                Segments[videoId] = task = FetchSegmentsAsync(videoId);
            return task;
        }
    }

    public static Task<long?> DislikeCountAsync(string videoId)
    {
        if (!UrlGuard.IsValidVideoId(videoId)) return Task.FromResult<long?>(null);
        lock (Dislikes)
        {
            if (!Dislikes.TryGetValue(videoId, out var task) || task.IsFaulted)
                Dislikes[videoId] = task = FetchDislikesAsync(videoId);
            return task;
        }
    }

    private static async Task<IReadOnlyList<SkipSegment>> FetchSegmentsAsync(string videoId)
    {
        try
        {
            var prefix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(videoId)))[..4].ToLowerInvariant();
            var url = $"https://sponsor.ajay.app/api/skipSegments/{prefix}?categories={Uri.EscapeDataString(Categories)}" +
                      $"&actionTypes={Uri.EscapeDataString("[\"skip\"]")}";
            using var response = await Http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return []; // 404: nothing submitted for any video with this prefix
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var found = new List<SkipSegment>();
            foreach (var video in doc.RootElement.EnumerateArray())
            {
                if (!video.TryGetProperty("videoID", out var id) || id.GetString() != videoId) continue;
                if (!video.TryGetProperty("segments", out var segments) || segments.ValueKind != JsonValueKind.Array) continue;
                foreach (var segment in segments.EnumerateArray())
                {
                    if (!segment.TryGetProperty("segment", out var times) || times.ValueKind != JsonValueKind.Array || times.GetArrayLength() != 2) continue;
                    var start = times[0].GetDouble();
                    var end = times[1].GetDouble();
                    if (double.IsFinite(start) && double.IsFinite(end) && start >= 0 && end - start >= 1) found.Add(new SkipSegment(start, end));
                }
            }
            return found.OrderBy(s => s.Start).Take(50).ToList();
        }
        catch (Exception ex)
        {
            Log.Info($"SponsorBlock lookup for {videoId} failed: {ex.Message}");
            return [];
        }
    }

    private static async Task<long?> FetchDislikesAsync(string videoId)
    {
        try
        {
            using var response = await Http.GetAsync($"https://returnyoutubedislikeapi.com/votes?videoId={videoId}");
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("dislikes", out var d) && d.TryGetInt64(out var count) && count >= 0 ? count : null;
        }
        catch (Exception ex)
        {
            Log.Info($"Dislike lookup for {videoId} failed: {ex.Message}");
            return null;
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Snag/{AppUpdater.CurrentVersionText}");
        return client;
    }
}
