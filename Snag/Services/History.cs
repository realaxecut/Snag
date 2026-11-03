using System.Text.Json;

namespace Snag.Services;

/// <summary>One finished download, as remembered on the History page.</summary>
public sealed class HistoryEntry
{
    public string Title { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string? ThumbnailUrl { get; set; }
    public string? SourceUrl { get; set; }
    public string Badge { get; set; } = "";
    public bool IsAudio { get; set; }
    public long SizeBytes { get; set; }
    public DateTime DownloadedUtc { get; set; }
}

/// <summary>Download history in %LOCALAPPDATA%\Snag\history.json, newest first.</summary>
public static class HistoryStore
{
    private const int MaxEntries = 5000;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static string FilePath => Path.Combine(AppPaths.DataDir, "history.json");

    public static List<HistoryEntry> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var entries = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(FilePath), Json) ?? [];
                return entries.Where(e => !string.IsNullOrWhiteSpace(e.FilePath) && Path.IsPathFullyQualified(e.FilePath)).ToList();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't read download history", ex);
        }
        return [];
    }

    public static void Save(IEnumerable<HistoryEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entries.Take(MaxEntries).ToList(), Json));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't save download history", ex);
        }
    }
}
