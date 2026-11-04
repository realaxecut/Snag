using System.Diagnostics;
using System.Text;

namespace Snag.Services;

/// <summary>Runs Snag's own FFmpeg and ffprobe (no shell, arguments as a list, killed if they hang).</summary>
internal static class FfTools
{
    private static string Dir => Path.Combine(AppPaths.ToolsDir, "ffmpeg");
    public static string Ffmpeg => Path.Combine(Dir, "ffmpeg.exe");
    public static string Ffprobe => Path.Combine(Dir, "ffprobe.exe");

    /// <summary>Exit code and standard output; (-1, "") if the tool is missing, couldn't start or took too long.</summary>
    public static async Task<(int Code, string Output)> RunAsync(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!File.Exists(exe)) return (-1, "");
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi);
        if (process is null) return (-1, "");
        ChildProcessJob.Attach(process);
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already gone.
            }
            ct.ThrowIfCancellationRequested();
            return (-1, "");
        }
        await stderr.ConfigureAwait(false);
        return (process.ExitCode, await stdout.ConfigureAwait(false));
    }
}
