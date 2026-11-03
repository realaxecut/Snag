using System.Security.Cryptography;
using System.Text;

namespace Snag.Services;

/// <summary>
/// Keeps a local copy of each finished download's thumbnail in %LOCALAPPDATA%\Snag\thumbs, because
/// TikTok and Instagram thumbnail links expire. Only https images from the allowed hosts are fetched.
/// </summary>
public static class ThumbnailCache
{
    private const int MaxBytes = 5 * 1024 * 1024;

    // No redirects: an allowed image host can't bounce the request somewhere else.
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };

    public static string Dir => Path.Combine(AppPaths.DataDir, "thumbs");

    public static bool IsCachedPath(string? path) =>
        path is not null && Path.IsPathFullyQualified(path) &&
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(Dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the local copy's path, or null if the image couldn't (or mustn't) be fetched.</summary>
    public static async Task<string?> SaveAsync(string? url)
    {
        if (url is null || !UrlGuard.IsAllowedImageUrl(url)) return null;
        try
        {
            Directory.CreateDirectory(Dir);
            var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..24].ToLowerInvariant() + ".img";
            var path = Path.Combine(Dir, name);
            if (File.Exists(path)) return path;

            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0 || bytes.Length > MaxBytes) return null;
            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't cache a thumbnail", ex);
            return null;
        }
    }

    public static void Delete(string? path)
    {
        if (!IsCachedPath(path)) return;
        try
        {
            File.Delete(path!);
        }
        catch
        {
            // Still on screen somewhere; it's tiny, so leaving it is fine.
        }
    }
}
