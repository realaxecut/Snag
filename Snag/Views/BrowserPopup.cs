using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Snag.Services;

namespace Snag.Views;

/// <summary>
/// A small window for sign-in pop-ups from the Browser tab ("Continue with Google" on TikTok, "Log in with
/// Facebook" on Instagram and so on). They have to be real windows: they report back to the page that opened them
/// and then close themselves. Same rules as the tab: only the three sites and their sign-in pages, no downloads,
/// no permissions, nothing else opens inside it.
/// </summary>
public sealed class BrowserPopup : Window
{
    private readonly WebView2 _web = new();
    private readonly string _userAgent;
    private readonly Action<Uri> _openExternally;

    public BrowserPopup(Window? owner, CoreWebView2WindowFeatures features, string userAgent, Action<Uri> openExternally)
    {
        _userAgent = userAgent;
        _openExternally = openExternally;
        Owner = owner;
        Title = "Sign in";
        Icon = owner?.Icon;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = features.HasSize ? Math.Clamp(features.Width, 380, 900) : 520;
        Height = features.HasSize ? Math.Clamp(features.Height, 480, 900) : 700;
        SetResourceReference(BackgroundProperty, "WindowBrush");
        var window = ThemeManager.Current.Window;
        _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, window.R, window.G, window.B);
        Content = _web;
        SourceInitialized += (_, _) => ColorTitleBar();
        Closed += (_, _) =>
        {
            try
            {
                _web.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error("Closing a sign-in window", ex);
            }
        };
    }

    public CoreWebView2 Core => _web.CoreWebView2;

    /// <summary>Shows the window and readies its page, which the opener then fills (it mustn't navigate first).</summary>
    public async Task OpenAsync(CoreWebView2Environment environment)
    {
        Show();
        await _web.EnsureCoreWebView2Async(environment);
        var core = _web.CoreWebView2;
        var settings = core.Settings;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.UserAgent = _userAgent;

        core.WindowCloseRequested += (_, _) => Close();
        core.DocumentTitleChanged += (_, _) => Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "Sign in" : core.DocumentTitle;
        core.NavigationStarting += (_, e) =>
        {
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) { e.Cancel = true; return; }
            if (uri.Scheme == "about") return;
            var web = uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp;
            if (web && UrlGuard.IsBrowserHost(uri.Host)) return;
            e.Cancel = true;
            if (web && e.IsUserInitiated) _openExternally(uri);
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && UrlGuard.IsBrowserHost(uri.Host))
                core.Navigate(uri.AbsoluteUri);
        };
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.Profile.PreferredColorScheme = ThemeManager.Current.IsLight ? CoreWebView2PreferredColorScheme.Light : CoreWebView2PreferredColorScheme.Dark;
    }

    /// <summary>The title bar in the window's colour, like Snag's own (Windows 11).</summary>
    private void ColorTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var palette = ThemeManager.Current;
        var caption = palette.Window.R | (palette.Window.G << 8) | (palette.Window.B << 16);
        var text = palette.IsLight ? 0x1F1B1B : 0xFFFFFF;
        DwmSetWindowAttribute(hwnd, 35 /* DWMWA_CAPTION_COLOR */, ref caption, sizeof(int));
        DwmSetWindowAttribute(hwnd, 36 /* DWMWA_TEXT_COLOR */, ref text, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
