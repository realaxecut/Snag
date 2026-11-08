using System.Text.Json;

namespace Snag.Services;

/// <summary>A playlist saved on the Playlists page.</summary>
public sealed class SavedPlaylistDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public List<PlaylistEntryDto> Items { get; set; } = [];
}

/// <summary>Saved playlists in %LOCALAPPDATA%\Snag\playlists.json, in the order they're listed.</summary>
public static class PlaylistStore
{
    public const int MaxPlaylists = 200;
    public const int MaxItems = 5000;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static string FilePath => Path.Combine(AppPaths.DataDir, "playlists.json");

    public static List<SavedPlaylistDto> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            var playlists = JsonSerializer.Deserialize<List<SavedPlaylistDto>>(File.ReadAllText(FilePath), Json) ?? [];
            // Never trust what's on disk: only real local paths (or canonical YouTube links for streamed
            // videos), sane names, sane sizes.
            return playlists
                .Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Id))
                .DistinctBy(p => p.Id)
                .Take(MaxPlaylists)
                .Select(p =>
                {
                    p.Name = CleanName(p.Name);
                    p.Items = (p.Items ?? [])
                        .Where(i => i is not null && !string.IsNullOrWhiteSpace(i.Path) &&
                                    (Path.IsPathFullyQualified(i.Path) || UrlGuard.CanonicalVideoUrl(i.Path) == i.Path))
                        .Take(MaxItems)
                        .ToList();
                    return p;
                })
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't read saved playlists", ex);
            return [];
        }
    }

    public static void Save(IEnumerable<SavedPlaylistDto> playlists)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(playlists.Take(MaxPlaylists).ToList(), Json));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't save playlists", ex);
        }
    }

    /// <summary>One line, no control characters, at most 60 characters.</summary>
    public static string CleanName(string? name)
    {
        var clean = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length > 60) clean = clean[..60].TrimEnd();
        return clean.Length == 0 ? "Untitled playlist" : clean;
    }
}
