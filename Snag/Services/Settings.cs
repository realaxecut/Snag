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
    public bool CaptionsOn { get; set; }
    public double CaptionScale { get; set; } = 1.0;
    public string CaptionBackground { get; set; } = "box";
    public bool AutoUpdateYtDlp { get; set; } = true;
    public bool CheckAppUpdates { get; set; } = true;
    public string Theme { get; set; } = ThemeManager.DefaultThemeId;
    public Appearance Appearance { get; set; } = Appearance.Dark;
    public string CustomAccent { get; set; } = "#7C5CFF";
    public bool CustomTint { get; set; } = true;
    public List<ThemePresetDto> ThemePresets { get; set; } = [];
    public bool OptionsPopup { get; set; }
    public DateTime? LastAppUpdateCheckUtc { get; set; }
    public bool NumberPlaylistItems { get; set; } = true;
    public double Volume { get; set; } = 0.8;
    public bool Shuffle { get; set; }
    public string Repeat { get; set; } = "Off";
    public List<PlaylistEntryDto> Playlist { get; set; } = [];
}

/// <summary>A saved custom theme: its colour, background tint and light/dark mode.</summary>
public sealed class ThemePresetDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Accent { get; set; } = "#7C5CFF";
    public bool Tint { get; set; } = true;
    public Appearance Appearance { get; set; } = Appearance.Dark;
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
        if (!Enum.IsDefined(settings.Appearance)) settings.Appearance = Appearance.Dark;
        settings.CaptionScale = double.IsFinite(settings.CaptionScale) ? Math.Clamp(settings.CaptionScale, 0.5, 3) : 1.0;
        if (settings.CaptionBackground is not ("box" or "solid" or "none")) settings.CaptionBackground = "box";
        if (!ThemeManager.TryParseHex(settings.CustomAccent, out _)) settings.CustomAccent = "#7C5CFF";
        settings.ThemePresets = (settings.ThemePresets ?? [])
            .Where(p => p is not null && ThemeManager.IsPresetId(p.Id) && ThemeManager.TryParseHex(p.Accent, out _) && Enum.IsDefined(p.Appearance))
            .DistinctBy(p => p.Id)
            .Take(MaxThemePresets)
            .ToList();
        foreach (var preset in settings.ThemePresets) preset.Name = CleanPresetName(preset.Name);
        if (!ThemeManager.IsKnown(settings.Theme) && settings.Theme != ThemeManager.CustomId && settings.ThemePresets.All(p => p.Id != settings.Theme))
            settings.Theme = ThemeManager.DefaultThemeId;
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

    public const int MaxThemePresets = 24;

    /// <summary>One line, no control characters, at most 30 characters.</summary>
    public static string CleanPresetName(string? name)
    {
        var clean = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length > 30) clean = clean[..30].TrimEnd();
        return clean.Length == 0 ? "My theme" : clean;
    }

    /// <summary>The custom colour being edited plus every saved preset, as themes Snag can apply.</summary>
    public static IEnumerable<ThemeColor> CustomThemes(AppSettings settings)
    {
        if (ThemeManager.TryParseHex(settings.CustomAccent, out var accent))
            yield return ThemeManager.FromAccent(ThemeManager.CustomId, "Custom", accent, settings.CustomTint);
        foreach (var preset in settings.ThemePresets)
        {
            if (ThemeManager.TryParseHex(preset.Accent, out var color))
                yield return ThemeManager.FromAccent(preset.Id, preset.Name, color, preset.Tint);
        }
    }

    /// <summary>"best" or a plain number such as "1080" or "320". Anything else is rejected.</summary>
    public static bool IsValidQuality(string? value) =>
        value == "best" || (value is { Length: > 0 and <= 4 } && value.All(char.IsAsciiDigit));
}
