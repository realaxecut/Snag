using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Snag.Services;

/// <summary>
/// A picture for a file in the player: a frame from halfway through a video, or a song's embedded cover
/// art. Made with Snag's own FFmpeg and kept in the thumbnail folder, so each file is only done once
/// (until it changes).
/// </summary>
public static class FrameGrabber
{
    private static readonly SemaphoreSlim Gate = new(2); // adding a whole folder shouldn't swamp the CPU
    private static readonly ConcurrentDictionary<string, Task<string?>> Running = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>The picture's path, or null when there's nothing to show (no FFmpeg yet, no cover art, unreadable file).</summary>
    public static Task<string?> GetAsync(string mediaPath, bool isAudio)
    {
        string target;
        try
        {
            var file = new FileInfo(mediaPath);
            if (!file.Exists || !File.Exists(FfTools.Ffmpeg)) return Task.FromResult<string?>(null);
            // Keyed on the file's identity, so an edited or replaced file gets a fresh picture.
            var key = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24].ToLowerInvariant();
            target = Path.Combine(ThumbnailCache.Dir, $"frame-{hash}.jpg");
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't look at {mediaPath}", ex);
            return Task.FromResult<string?>(null);
        }

        if (File.Exists(target)) return Task.FromResult<string?>(target);
        return Running.GetOrAdd(target, _ => Task.Run(async () =>
        {
            try
            {
                return await MakeAsync(mediaPath, isAudio, target).ConfigureAwait(false);
            }
            finally
            {
                Running.TryRemove(target, out Task<string?>? _);
            }
        }));
    }

    private static async Task<string?> MakeAsync(string mediaPath, bool isAudio, string target)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        var temp = target + ".part.jpg"; // FFmpeg picks the image format from the extension
        try
        {
            Directory.CreateDirectory(ThumbnailCache.Dir);
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
            if (!isAudio)
            {
                // Halfway in: the ends are often black, a fade or a title card.
                var duration = await DurationAsync(mediaPath).ConfigureAwait(false);
                var at = duration is > 2 ? duration.Value / 2 : 0;
                args.AddRange(["-ss", at.ToString("0.###", CultureInfo.InvariantCulture)]);
            }
            // Songs: their first picture stream is the cover art; without one FFmpeg fails and there's no picture.
            args.AddRange(["-i", mediaPath, "-map", "0:v:0", "-frames:v", "1",
                "-vf", "scale=w=480:h=480:force_original_aspect_ratio=decrease", "-q:v", "4", "-y", temp]);

            var (code, _) = await FfTools.RunAsync(FfTools.Ffmpeg, args, Timeout).ConfigureAwait(false);
            if (code != 0 || !File.Exists(temp) || new FileInfo(temp).Length == 0) return null;
            File.Move(temp, target, overwrite: true);
            return target;
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't make a picture for {mediaPath}", ex);
            return null;
        }
        finally
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch
            {
                // Harmless leftover.
            }
            Gate.Release();
        }
    }

    private static async Task<double?> DurationAsync(string mediaPath)
    {
        var (code, output) = await FfTools.RunAsync(FfTools.Ffprobe,
            ["-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", mediaPath], Timeout).ConfigureAwait(false);
        return code == 0 && double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : null;
    }
}
