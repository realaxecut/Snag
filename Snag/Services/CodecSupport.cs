namespace Snag.Services;

/// <summary>Video codec families as Snag offers them. Auto = best quality available.</summary>
public enum VideoCodec { Auto, H264, Vp9, Av1, Hevc, Other }

/// <summary>
/// Codec helpers for the download options. Snag's player runs on the VLC engine, which plays all of
/// these, so picking a codec is only about file size, quality and what other devices can play.
/// </summary>
public static class CodecSupport
{
    public static readonly VideoCodec[] Choices = [VideoCodec.H264, VideoCodec.Vp9, VideoCodec.Av1, VideoCodec.Hevc];

    /// <summary>Which family a yt-dlp vcodec string belongs to ("avc1.64001F", "vp09.00.40.08", "h265", …).</summary>
    public static VideoCodec Family(string? vcodec)
    {
        if (string.IsNullOrEmpty(vcodec)) return VideoCodec.Other;
        foreach (var codec in Choices)
            if (Prefixes(codec).Any(p => vcodec.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return codec;
        return VideoCodec.Other;
    }

    /// <summary>How each family's vcodec strings start, for yt-dlp's [vcodec^=…] filters.</summary>
    public static string[] Prefixes(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => ["avc1", "h264"],
        VideoCodec.Vp9 => ["vp09", "vp9"],
        VideoCodec.Av1 => ["av01"],
        VideoCodec.Hevc => ["hev", "hvc", "h265"],
        _ => [],
    };

    public static string DisplayName(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => "H.264",
        VideoCodec.Vp9 => "VP9",
        VideoCodec.Av1 => "AV1",
        VideoCodec.Hevc => "HEVC",
        _ => "Auto",
    };

    public static string Blurb(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => "Plays on everything",
        VideoCodec.Av1 => "Smallest files",
        _ => "Smaller files",
    };

    /// <summary>
    /// yt-dlp's -f selector for MP4 downloads: the chosen codec first (separate video+audio, then a
    /// single combined file), and the best of anything if the video doesn't come in that codec.
    /// </summary>
    public static string FormatSelector(VideoCodec codec)
    {
        const string any = "bv*+ba/b";
        if (codec is VideoCodec.Auto or VideoCodec.Other) return any;
        var prefixes = Prefixes(codec);
        return string.Join('/', prefixes.Select(p => $"bv*[vcodec^={p}]+ba")
            .Concat(prefixes.Select(p => $"b[vcodec^={p}]"))
            .Append(any));
    }
}
