namespace Snag.Services;

/// <summary>Which files the player can take, and turning dropped or picked folders into those files.</summary>
public static class MediaFiles
{
    public static readonly string[] Extensions =
    [
        ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv",
        ".mp3", ".m4a", ".aac", ".wav", ".wma", ".flac", ".ogg", ".opus",
    ];

    /// <summary>For an open-file dialog.</summary>
    public static string DialogFilter => $"Video and audio|{string.Join(';', Extensions.Select(x => "*" + x))}|All files|*.*";

    public static bool IsMedia(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Folders become their media files (sorted, up to 2,000); files pass through.</summary>
    public static IEnumerable<string> Expand(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                yield return path;
            }
            else if (Directory.Exists(path))
            {
                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                        .Where(IsMedia)
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .Take(2000)
                        .ToList();
                }
                catch (Exception ex)
                {
                    Log.Error($"Couldn't read folder {path}", ex);
                    continue;
                }
                foreach (var file in files) yield return file;
            }
        }
    }
}
