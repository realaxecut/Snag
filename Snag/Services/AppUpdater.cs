using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Snag.Services;

/// <summary>A newer Snag on GitHub: its installer and the checksum list it's verified against.</summary>
public sealed record AppRelease(
    Version Version, string Notes, string PageUrl, string InstallerName, string InstallerUrl, long InstallerSize, string ChecksumsUrl)
{
    public string VersionText => Version.ToString(3);
}

/// <summary>
/// Snag's own updates, from its GitHub releases. Checking only reads the latest release; nothing is
/// downloaded until the user asks. The installer is then only run if its SHA-256 matches the
/// SHA256SUMS.txt published in the same release, and links are only followed if they point at this
/// repository's own release downloads.
/// </summary>
public static partial class AppUpdater
{
    public const string Repository = "realaxecut/Snag";
    private const string ChecksumsName = "SHA256SUMS.txt";

    [GeneratedRegex(@"^Snag-Setup-\d+\.\d+\.\d+\.exe$")]
    private static partial Regex InstallerPattern();

    public static Version CurrentVersion { get; } = Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));
    public static string CurrentVersionText => CurrentVersion.ToString(3);
    public static string ReleasesPage => $"https://github.com/{Repository}/releases";
    private static string DownloadPrefix => $"https://github.com/{Repository}/releases/download/";
    private static string UpdatesDir => Path.Combine(AppPaths.DataDir, "updates");

    /// <summary>The latest release if it's newer than this copy of Snag, otherwise null.</summary>
    public static async Task<AppRelease?> CheckAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await ToolManager.Http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null; // nothing released yet
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = json.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var parsed))
            throw new InvalidDataException($"The latest release has an unexpected version number ({tag}).");
        var version = Normalize(parsed);
        if (version <= CurrentVersion) return null;

        string? installerName = null, installerUrl = null, checksumsUrl = null;
        long installerSize = 0;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (!url.StartsWith(DownloadPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (InstallerPattern().IsMatch(name))
            {
                installerName = name;
                installerUrl = url;
                installerSize = asset.GetProperty("size").GetInt64();
            }
            else if (name == ChecksumsName)
            {
                checksumsUrl = url;
            }
        }
        if (installerName is null || installerUrl is null || checksumsUrl is null)
            throw new InvalidDataException($"Snag {version.ToString(3)} is out, but its installer isn't up yet. Try again in a few minutes.");

        var pageUrl = root.TryGetProperty("html_url", out var page) && page.GetString() is { } p &&
                      p.StartsWith($"https://github.com/{Repository}/", StringComparison.OrdinalIgnoreCase)
            ? p
            : ReleasesPage;
        var notes = root.TryGetProperty("body", out var body) ? body.GetString() : null;
        return new AppRelease(version, CleanNotes(notes), pageUrl, installerName, installerUrl, installerSize, checksumsUrl);
    }

    /// <summary>Downloads the release's installer and returns its path, but only if its SHA-256 matches.</summary>
    public static async Task<string> DownloadAsync(AppRelease release, Action<double> report, CancellationToken ct)
    {
        ClearDownloads();
        var sums = await ToolManager.Http.GetStringAsync(release.ChecksumsUrl, ct).ConfigureAwait(false);
        var expected = ToolManager.HashForFile(sums, release.InstallerName);
        var temp = await ToolManager.DownloadVerifiedAsync(release.InstallerUrl, expected, UpdatesDir, report, ct).ConfigureAwait(false);
        var target = Path.Combine(UpdatesDir, release.InstallerName);
        File.Move(temp, target, overwrite: true);
        return target;
    }

    /// <summary>
    /// Runs the installer with just a progress window, and asks it to reopen Snag when it's done.
    /// Windows asks for permission first (Snag lives in Program Files); returns false if that was refused.
    /// </summary>
    public static bool StartInstaller(string path)
    {
        try
        {
            var psi = new ProcessStartInfo(path)
            {
                UseShellExecute = true, // lets Windows show its permission prompt
                Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH",
            };
            using var _ = Process.Start(psi);
            Log.Info($"Started the installer for an update: {path}");
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: permission refused
        {
            return false;
        }
    }

    /// <summary>Removes installers left from earlier updates.</summary>
    public static void ClearDownloads()
    {
        try
        {
            if (!Directory.Exists(UpdatesDir)) return;
            foreach (var file in Directory.EnumerateFiles(UpdatesDir))
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // Still in use (the installer may be finishing up); it goes next time.
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't clean the updates folder", ex);
        }
    }

    /// <summary>Opens a page of this repository on GitHub in the default browser (nothing else is allowed).</summary>
    public static void OpenInBrowser(string url)
    {
        if (!url.StartsWith($"https://github.com/{Repository}/", StringComparison.OrdinalIgnoreCase) &&
            !url.Equals(ReleasesPage, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't open the browser", ex);
        }
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>Release notes are Markdown; this keeps them readable as plain text.</summary>
    private static string CleanNotes(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var text = new StringBuilder();
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim().Replace("**", "").Replace("`", "");
            if (line.StartsWith('#')) line = line.TrimStart('#').Trim();
            else if (line.StartsWith("- ") || line.StartsWith("* ")) line = "• " + line[2..];
            text.Append(line).Append('\n');
        }
        var result = Regex.Replace(text.ToString(), @"\n{3,}", "\n\n").Trim();
        return result.Length > 1500 ? result[..1500].TrimEnd() + "…" : result;
    }
}
