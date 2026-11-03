using System.Text.Json;

namespace Snag.Services;

public sealed class AppSettings
{
    public string OutputFolder { get; set; } = AppPaths.DefaultDownloadsDir();
    public string Format { get; set; } = "mp4";
    public string VideoQuality { get; set; } = "1080";
    public string VideoCodec { get; set; } = "Auto";
    public string AudioQuality { get; set; } = "320";
    public bool EmbedThumbnail { get; set; } = true;
    public bool EmbedSubtitles { get; set; }
    public bool SponsorBlock { get; set; }
    public int MaxParallel { get; set; } = 2;
    public bool AddDownloadsToPlayer { get; set; } = true;
    public bool AutoUpdateYtDlp { get; set; } = true;
    public bool CheckAppUpdates { get; set; } = true;
    public DateTime? LastAppUpdateCheckUtc { get; set; }
    public bool NumberPlaylistItems { get; set; } = true;
    public double Volume { get; set; } = 0.8;
    public bool Shuffle { get; set; }
    public string Repeat { get; set; } = "Off";
    public List<PlaylistEntryDto> Playlist { get; set; } = [];
}

public sealed class PlaylistEntryDto
{
    public string Path { get; set; } = "";
    public string? Title { get; set; }
    public string? ArtworkUrl { get; set; }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(AppPaths.SettingsFile)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Json) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't read settings, using defaults", ex);
            settings = new AppSettings();
        }

        // Never trust what's on disk: clamp everything back into the shapes the app expects.
        settings.MaxParallel = Math.Clamp(settings.MaxParallel, 1, 4);
        if (settings.Format is not ("mp4" or "mp3")) settings.Format = "mp4";
        if (!IsValidQuality(settings.VideoQuality)) settings.VideoQuality = "1080";
        if (!Enum.TryParse<VideoCodec>(settings.VideoCodec, out _)) settings.VideoCodec = "Auto";
        if (!IsValidQuality(settings.AudioQuality)) settings.AudioQuality = "320";
        if (string.IsNullOrWhiteSpace(settings.OutputFolder) || !Path.IsPathFullyQualified(settings.OutputFolder))
            settings.OutputFolder = AppPaths.DefaultDownloadsDir();
        settings.Volume = double.IsFinite(settings.Volume) ? Math.Clamp(settings.Volume, 0, 1) : 0.8;
        settings.Playlist ??= [];
        return settings;
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            var temp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
            File.Move(temp, AppPaths.SettingsFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't save settings", ex);
        }
    }

    /// <summary>"best" or a plain number such as "1080" or "320". Anything else is rejected.</summary>
    public static bool IsValidQuality(string? value) =>
        value == "best" || (value is { Length: > 0 and <= 4 } && value.All(char.IsAsciiDigit));
}
