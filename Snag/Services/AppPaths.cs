using System.Runtime.InteropServices;

namespace Snag.Services;

/// <summary>Everything Snag writes lives under %LOCALAPPDATA%\Snag, except the videos themselves.</summary>
public static class AppPaths
{
    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>%LOCALAPPDATA%\Snag, or SNAG_DATA_DIR when set (lets a second copy run with its own settings).</summary>
    public static string DataDir { get; } =
        Environment.GetEnvironmentVariable("SNAG_DATA_DIR") is { Length: > 0 } custom && Path.IsPathFullyQualified(custom)
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snag");

    public static string ToolsDir => Path.Combine(DataDir, "tools");
    public static string TempDir => Path.Combine(DataDir, "temp");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string LogFile => Path.Combine(DataDir, "snag.log");

    /// <summary>The user's real Downloads folder (it may have been moved), plus a Snag subfolder.</summary>
    public static string DefaultDownloadsDir()
    {
        string? downloads = null;
        try
        {
            if (SHGetKnownFolderPath(DownloadsFolderId, 0, IntPtr.Zero, out var ptr) == 0)
            {
                downloads = Marshal.PtrToStringUni(ptr);
                Marshal.FreeCoTaskMem(ptr);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't look up the Downloads folder", ex);
        }

        downloads ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Path.Combine(downloads, "Snag");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);
}
