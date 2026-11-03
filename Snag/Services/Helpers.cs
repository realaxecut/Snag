using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Snag.Services;

public static class Fmt
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(double bytes)
    {
        var unit = 0;
        while (bytes >= 1024 && unit < Units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes:0} {Units[unit]}" : $"{bytes:0.0} {Units[unit]}";
    }

    public static string Duration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    public static string Count(long n) => n switch
    {
        >= 1_000_000_000 => $"{n / 1e9:0.#}B",
        >= 1_000_000 => $"{n / 1e6:0.#}M",
        >= 1_000 => $"{n / 1e3:0.#}K",
        _ => n.ToString(),
    };
}

public static class PathSafe
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Turns a playlist title into a single, harmless folder name.</summary>
    public static string FolderName(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = (name ?? "").Select(c => invalid.Contains(c) || c == '%' || char.IsControl(c) ? '_' : c).ToArray();
        var cleaned = string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd('.');
        if (cleaned.Length > 80) cleaned = cleaned[..80].TrimEnd('.', ' ');
        if (cleaned.Length == 0 || cleaned.All(c => c is '.' or '_')) cleaned = "Playlist";
        if (ReservedNames.Contains(cleaned)) cleaned = "_" + cleaned;
        return cleaned;
    }
}

public static class PowerActions
{
    private static string WindowsDir => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>Turns the PC off through Windows' own shutdown.exe (full path, no shell, no force).</summary>
    public static void ShutDownNow()
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("/s");
        psi.ArgumentList.Add("/t");
        psi.ArgumentList.Add("0");
        Log.Info("Shutting down the PC (playlist finished)");
        Process.Start(psi);
    }

    /// <summary>
    /// Deletes a file the way Explorer does: it goes to the Recycle Bin, so a mistake can be undone.
    /// Returns false (and leaves the file alone) if Windows refuses, e.g. because the file is in use.
    /// </summary>
    public static bool MoveToRecycleBin(string path)
    {
        if (!File.Exists(path)) return false;
        var op = new ShFileOperation
        {
            Func = FileOperationDelete,
            From = Path.GetFullPath(path) + "\0\0",
            Flags = AllowUndo | NoConfirmation | Silent | NoErrorUi,
        };
        var result = SHFileOperation(ref op);
        var deleted = result == 0 && !op.AnyOperationsAborted && !File.Exists(path);
        Log.Info(deleted ? $"Moved to Recycle Bin: {path}" : $"Recycle Bin move failed ({result}): {path}");
        return deleted;
    }

    private const uint FileOperationDelete = 3;
    private const ushort Silent = 0x0004;
    private const ushort NoConfirmation = 0x0010;
    private const ushort AllowUndo = 0x0040;
    private const ushort NoErrorUi = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOperation
    {
        public IntPtr Window;
        public uint Func;
        public string From;
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOperation op);

    public static void ShowInExplorer(string path)
    {
        try
        {
            var explorer = Path.Combine(WindowsDir, "explorer.exe");
            // Windows paths can't contain quotes, so wrapping in quotes is safe here.
            if (File.Exists(path)) Process.Start(explorer, $"/select,\"{path}\"");
            else if (Directory.Exists(path)) Process.Start(explorer, $"\"{path}\"");
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't open Explorer", ex);
        }
    }
}
