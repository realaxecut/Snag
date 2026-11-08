using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Snag.Services;
using Snag.ViewModels;

namespace Snag.Views;

/// <summary>
/// The YouTube tab: YouTube in Microsoft Edge's WebView2 engine (built into Windows 11), with Snag's ad blocking,
/// its own saved sign-in, and ways to hand any video to Snag: play it in Snag's player (streamed, nothing
/// downloaded), add it to the queue, or download it. The page only ever shows YouTube and Google's sign-in;
/// any other link opens in the real browser.
/// </summary>
public partial class BrowserView : UserControl
{
    private const string HomeUrl = "https://www.youtube.com/";
    private const string SignInUrl =
        "https://accounts.google.com/ServiceLogin?service=youtube&passive=true&continue=" +
        "https%3A%2F%2Fwww.youtube.com%2Fsignin%3Faction_handle_signin%3Dtrue%26app%3Ddesktop%26next%3D%252F";
    private const string SignOutUrl = "https://accounts.google.com/Logout?service=youtube&continue=https%3A%2F%2Fwww.youtube.com%2F";
    private const string SwitchAccountUrl = "https://www.youtube.com/channel_switcher";
    private const string GoogleAccountUrl = "https://myaccount.google.com/";
    private const string EngineUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

    /// <summary>Ad servers and YouTube's ad endpoints. Requests to these never leave the PC while ad blocking is on.</summary>
    private static readonly string[] AdFilters =
    [
        "*://*.doubleclick.net/*",
        "*://*.googlesyndication.com/*",
        "*://*.googleadservices.com/*",
        "*://*.googletagservices.com/*",
        "*://adservice.google.com/*",
        "*://*.youtube.com/pagead/*",
        "*://*.youtube.com/api/stats/ads*",
        "*://*.youtube.com/get_midroll_*",
        "*://*.youtube.com/ptracking*",
        "*://*.youtube.com/pcs/activeview*",
    ];

    /// <summary>Default right-click entries that make no sense here (or would open another window).</summary>
    private static readonly HashSet<string> HiddenMenuItems = new(StringComparer.OrdinalIgnoreCase)
    {
        "inspectElement", "saveAs", "print", "share", "webCapture", "createQrCode", "openLinkInNewWindow",
        "saveLinkAs", "saveImageAs", "saveMediaAs", "addToCollections", "sendTabToDevices", "openInReaderMode",
    };

    private static readonly Lazy<string> TabScript = new(() =>
    {
        using var stream = typeof(BrowserView).Assembly.GetManifestResourceStream("Snag.Assets.youtube-tab.js")
                           ?? throw new InvalidOperationException("youtube-tab.js is missing from the build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    [GeneratedRegex(@"\s+Edg/[\d.]+")]
    private static partial Regex EdgeToken();

    [GeneratedRegex(@"^\(\d+\)\s*")]
    private static partial Regex NotificationCount();

    [GeneratedRegex(@"^\d{1,2}(:\d{2}){1,2}$")]
    private static partial Regex DurationText();

    private MainViewModel? _vm;
    private Window? _window;
    private WebView2? _web;
    private CoreWebView2? _core;
    private bool _starting;
    private string? _scriptId;
    private string? _pendingUrl;
    private string? _videoUrl;      // the canonical link of the video on the page, if it's a video page
    private string? _videoTitle;
    private bool _titleFresh;       // the page title belongs to the page now showing (it lags behind in-page navigation)
    private bool _signedIn;
    private bool _covered;
    private bool _refocusAfterCover;

    public BrowserView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _ = StartAsync();
            UpdateToast();
        };
        Loaded += (_, _) => AttachWindow();
        PageFrame.SizeChanged += (_, _) => RepositionToast();
        ToastPopup.CustomPopupPlacementCallback = (popup, target, _) =>
            [new CustomPopupPlacement(new Point((target.Width - popup.Width) / 2, target.Height - popup.Height - 10), PopupPrimaryAxis.None)];
        ThemeManager.Changed += ApplyTheme;
        _refocusTimer.Tick += OnRefocusTimer;
        UpdateShield();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnMainPropertyChanged;
            _vm.Shutdown.PropertyChanged -= OnShutdownPropertyChanged;
            _vm.Player.PropertyChanged -= OnPlayerPropertyChanged;
            _vm.BrowseRequested -= Navigate;
        }
        _vm = DataContext as MainViewModel;
        if (_vm is null) return;
        _vm.PropertyChanged += OnMainPropertyChanged;
        _vm.Shutdown.PropertyChanged += OnShutdownPropertyChanged;
        _vm.Player.PropertyChanged += OnPlayerPropertyChanged;
        _vm.BrowseRequested += Navigate;
        UpdateShield();
    }

    private void AttachWindow()
    {
        if (_window is not null) return;
        _window = Window.GetWindow(this);
        if (_window is null) return;
        // A toast is its own little window; it shouldn't float over other apps or stay behind when Snag moves.
        _window.Activated += (_, _) =>
        {
            UpdateToast();
            if (!IsVisible || _web is null) return;
            // Switched back to Snag by clicking the page: the browser takes focus itself (and if it didn't, it gets
            // it a moment later). Switched back with the keyboard or the taskbar while typing in the page: WPF would
            // hand focus to its own last control (the search box, a tab), so the page gets it back.
            if (IsPointerOverPage() || (_pageFocused && !IsPointerOverWindow())) _refocusTimer.Start();
        };
        _window.Deactivated += (_, _) => UpdateToast();
        _window.LocationChanged += (_, _) => RepositionToast();
        // A click on Snag's own controls means typing goes there now, not to the page.
        _window.AddHandler(PreviewMouseDownEvent, new MouseButtonEventHandler((_, _) => _pageFocused = false), handledEventsToo: true);
    }

    /// <summary>
    /// Typing belongs to the page: the person last clicked into it (it says so, see youtube-tab.js) or Snag put focus
    /// there. WPF isn't told when the page is clicked, so this is how Snag knows where to give focus back.
    /// </summary>
    private bool _pageFocused;

    /// <summary>
    /// Keyboard focus into the page. Never while Windows' focus is already inside it: taking it away there (even to
    /// give it straight back) blurs whatever box is being typed in, and the page and Snag end up fighting over it.
    /// </summary>
    private void FocusPage()
    {
        if (_web is not { IsVisible: true } web || _vm?.IsDialogOpen == true || FocusIsInPage()) return;
        // WPF only passes focus on to the page as it arrives; if WPF thinks the page already has it, start over.
        if (web.IsKeyboardFocusWithin) Keyboard.ClearFocus();
        web.Focus();
        _pageFocused = true;
    }

    /// <summary>Whether Windows' keyboard focus is in the page (the browser's own windows inside Snag's).</summary>
    private bool FocusIsInPage()
    {
        if (_web is null || _web.Handle == IntPtr.Zero) return false;
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(0, ref info) || info.Focus == IntPtr.Zero) return false;
        return info.Focus == _web.Handle || IsChild(_web.Handle, info.Focus);
    }

    /// <summary>A moment after Snag is switched back to, so the click (or WPF's own focus handling) has happened first.</summary>
    private readonly DispatcherTimer _refocusTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    private void OnRefocusTimer(object? sender, EventArgs e)
    {
        _refocusTimer.Stop();
        // Not if one of Snag's own boxes was clicked into meanwhile.
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
        FocusPage();
    }

    private bool IsPointerOverPage()
    {
        if (_web is not { IsVisible: true } web || !GetCursorPos(out var cursor)) return false;
        var topLeft = web.PointToScreen(new Point(0, 0));
        var bottomRight = web.PointToScreen(new Point(web.ActualWidth, web.ActualHeight));
        return cursor.X >= topLeft.X && cursor.X < bottomRight.X && cursor.Y >= topLeft.Y && cursor.Y < bottomRight.Y;
    }

    private bool IsPointerOverWindow()
    {
        if (_window is null || !GetCursorPos(out var cursor)) return false;
        var topLeft = _window.PointToScreen(new Point(0, 0));
        var bottomRight = _window.PointToScreen(new Point(_window.ActualWidth, _window.ActualHeight));
        return cursor.X >= topLeft.X && cursor.X < bottomRight.X && cursor.Y >= topLeft.Y && cursor.Y < bottomRight.Y;
    }

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.ToastText):
                UpdateToast();
                break;
            case nameof(MainViewModel.IsDialogOpen):
                UpdateCover();
                UpdateToast();
                break;
            case nameof(MainViewModel.AdsBlocked) or nameof(MainViewModel.BrowserAdBlock):
                UpdateShield();
                break;
            case nameof(MainViewModel.IsBrowserFullscreen):
                ApplyFullscreen(_vm!.IsBrowserFullscreen);
                break;
        }
    }

    private void OnShutdownPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShutdownViewModel.IsOpen)) UpdateCover();
    }

    /// <summary>Snag's player started: pause the page's video so two things don't play at once.</summary>
    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.IsPlaying) && _vm?.Player.IsPlaying == true) PauseWebVideo();
    }

    // ---------------------------------------------------------------- starting the engine

    /// <summary>The engine starts the first time the tab is opened, not with Snag.</summary>
    private async Task StartAsync()
    {
        if (_starting || _core is not null || _vm is null) return;
        _starting = true;
        StartingState.Visibility = Visibility.Visible;
        ProblemState.Visibility = Visibility.Collapsed;
        try
        {
            Directory.CreateDirectory(AppPaths.BrowserDir);
            var environment = await CoreWebView2Environment.CreateAsync(null, AppPaths.BrowserDir, new CoreWebView2EnvironmentOptions());
            var web = new WebView2 { DefaultBackgroundColor = DrawingColor(ThemeManager.Current.Window) };
            web.SizeChanged += (_, _) => UpdateWebRegion();
            PageGrid.Children.Add(web);
            _web = web;
            await web.EnsureCoreWebView2Async(environment);
            _core = web.CoreWebView2;
            Configure(_core);
            await InstallScriptAsync();
            StartingState.Visibility = Visibility.Collapsed;
            UpdateWebRegion();
            UpdateCover();
            _core.Navigate(_pendingUrl ?? HomeUrl);
            _pendingUrl = null;
            // Ready to type into straight away (YouTube's search box, the sign-in page).
            if (IsVisible) _ = Dispatcher.BeginInvoke(FocusPage, DispatcherPriority.Input);
            Log.Info($"YouTube tab started (WebView2 {environment.BrowserVersionString})");
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            Log.Error("WebView2 isn't installed", ex);
            ShowProblem("The YouTube tab needs Microsoft Edge WebView2",
                "It's part of Windows 11 and most Windows 10 PCs, but it's missing here. Install it from Microsoft (it's free and small), then try again.",
                offerEngine: true);
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't start the YouTube tab", ex);
            ShowProblem("The YouTube tab couldn't start", ex.Message, offerEngine: false);
        }
        finally
        {
            _starting = false;
        }
    }

    private void Configure(CoreWebView2 core)
    {
        var settings = core.Settings;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.IsWebMessageEnabled = true;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        // Google's sign-in page turns away browsers it thinks are embedded in an app; plain Chrome's user agent
        // (WebView2's minus the "Edg/" part) gets through more often.
        settings.UserAgent = EdgeToken().Replace(settings.UserAgent, "");
        core.Profile.PreferredColorScheme = ThemeManager.Current.IsLight ? CoreWebView2PreferredColorScheme.Light : CoreWebView2PreferredColorScheme.Dark;

        foreach (var filter in AdFilters)
            core.AddWebResourceRequestedFilter(filter, CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += OnResourceRequested;
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.SourceChanged += (_, _) =>
        {
            _titleFresh = false;
            OnPageChanged();
        };
        core.HistoryChanged += (_, _) => OnPageChanged();
        core.DocumentTitleChanged += (_, _) =>
        {
            _titleFresh = true;
            OnPageChanged();
        };
        core.NewWindowRequested += OnNewWindowRequested;
        core.ContainsFullScreenElementChanged += (_, _) =>
        {
            if (_vm is not null) _vm.IsBrowserFullscreen = core.ContainsFullScreenElement;
        };
        core.ContextMenuRequested += OnContextMenuRequested;
        core.WebMessageReceived += OnWebMessage;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, e) =>
        {
            e.Cancel = true;
            _vm?.ShowToast("Use Snag's Download button to save videos.");
        };
        core.ProcessFailed += OnProcessFailed;
    }

    /// <summary>The page script, with ad blocking baked in or not (it has to act before YouTube's own scripts run).</summary>
    private async Task InstallScriptAsync()
    {
        if (_core is null) return;
        if (_scriptId is not null) _core.RemoveScriptToExecuteOnDocumentCreated(_scriptId);
        var adBlock = _vm?.BrowserAdBlock == true ? "true" : "false";
        _scriptId = await _core.AddScriptToExecuteOnDocumentCreatedAsync(TabScript.Value.Replace("__SNAG_ADBLOCK__", adBlock));
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Log.Error($"YouTube tab: {e.ProcessFailedKind} ({e.Reason})");
        if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            TearDown();
            ShowProblem("The YouTube tab stopped working", "Its browser engine closed unexpectedly.", offerEngine: false);
        }
        else if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
        {
            _core?.Reload();
        }
    }

    private void ShowProblem(string title, string text, bool offerEngine)
    {
        TearDown();
        StartingState.Visibility = Visibility.Collapsed;
        ProblemTitle.Text = title;
        ProblemText.Text = text;
        GetEngineButton.Visibility = offerEngine ? Visibility.Visible : Visibility.Collapsed;
        ProblemState.Visibility = Visibility.Visible;
    }

    private void TearDown()
    {
        if (_web is not null)
        {
            PageGrid.Children.Remove(_web);
            try
            {
                _web.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error("Closing the YouTube tab's engine", ex);
            }
        }
        _web = null;
        _core = null;
        _scriptId = null;
        VideoActions.Visibility = Visibility.Collapsed;
    }

    /// <summary>Closes the engine when Snag closes.</summary>
    public void Shutdown()
    {
        ToastPopup.IsOpen = false;
        TearDown();
    }

    private void OnGetEngine(object sender, RoutedEventArgs e) => OpenExternally(new Uri(EngineUrl));

    private void OnRetry(object sender, RoutedEventArgs e) => _ = StartAsync();

    // ---------------------------------------------------------------- ad blocking

    private void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (_vm?.BrowserAdBlock != true || _core is null) return;
        e.Response = _core.Environment.CreateWebResourceResponse(null, 403, "Blocked by Snag", "");
        _vm.CountBlockedAd();
    }

    private async void OnToggleAdBlock(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.BrowserAdBlock = !_vm.BrowserAdBlock;
        _vm.ShowToast(_vm.BrowserAdBlock ? "Ad blocking is on" : "Ad blocking is off");
        try
        {
            await InstallScriptAsync();
            _core?.Reload();
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't switch ad blocking", ex);
        }
    }

    private void UpdateShield()
    {
        var on = _vm?.BrowserAdBlock ?? true;
        var count = _vm?.AdsBlocked ?? 0;
        if (on) ShieldGlyph.SetResourceReference(TextBlock.ForegroundProperty, "BrandBrush");
        else ShieldGlyph.SetResourceReference(TextBlock.ForegroundProperty, "FaintBrush");
        ShieldGlyph.Text = on ? "" : "";
        AdsBadge.Visibility = on && count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AdsCount.Text = count > 999 ? "999+" : count.ToString();
        ShieldButton.ToolTip = on
            ? $"Ad blocking is on{(count > 0 ? $": {count} ads and ad trackers stopped" : "")}. Click to turn it off."
            : "Ad blocking is off. Click to turn it on.";
    }

    // ---------------------------------------------------------------- navigation

    /// <summary>Shows a page in the tab (YouTube and Google sign-in only), starting the engine first if need be.</summary>
    public void Navigate(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !UrlGuard.IsBrowserHost(uri.Host)) return;
        if (_core is null)
        {
            _pendingUrl = uri.AbsoluteUri;
            if (IsVisible) _ = StartAsync();
            return;
        }
        _core.Navigate(uri.AbsoluteUri);
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
        {
            e.Cancel = true;
            return;
        }
        if (uri.Scheme == "about") return;
        var web = uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp;
        if (!web || !UrlGuard.IsBrowserHost(uri.Host))
        {
            // The tab is for YouTube. A link you click to somewhere else opens in your real browser instead;
            // a page trying to go elsewhere by itself is just stopped.
            e.Cancel = true;
            if (web && e.IsUserInitiated) OpenExternally(uri);
            return;
        }
        LoadingBar.Visibility = Visibility.Visible;
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        LoadingBar.Visibility = Visibility.Collapsed;
        OnPageChanged();
        _ = RefreshSignInAsync();
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // Never a second window: YouTube pages open here, anything else in the real browser.
        e.Handled = true;
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) return;
        if (UrlGuard.IsBrowserHost(uri.Host)) _core?.Navigate(uri.AbsoluteUri);
        else if (e.IsUserInitiated) OpenExternally(uri);
    }

    private void OpenExternally(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            _vm?.ShowToast("Opened in your browser");
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't open a link in the browser", ex);
        }
    }

    private void OnPageChanged()
    {
        if (_core is null) return;
        BackButton.IsEnabled = _core.CanGoBack;
        ForwardButton.IsEnabled = _core.CanGoForward;
        var source = _core.Source;
        if (!AddressBox.IsKeyboardFocusWithin) AddressBox.Text = DisplayAddress(source);

        var videoUrl = UrlGuard.CanonicalVideoUrl(source);
        var newVideo = videoUrl != _videoUrl;
        _videoUrl = videoUrl;
        if (newVideo) _ = SendVideoInfoAsync();
        var title = NotificationCount().Replace(_core.DocumentTitle ?? "", "");
        if (title.EndsWith(" - YouTube", StringComparison.Ordinal)) title = title[..^" - YouTube".Length];
        _videoTitle = title is "YouTube" or "" ? null : YouTubeMeta.CleanText(title);
        VideoActions.Visibility = _videoUrl is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>What the box shows: the search for a results page, otherwise the address without https://.</summary>
    private static string DisplayAddress(string? source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme == "about") return "";
        if (uri.AbsolutePath == "/results" && UrlGuard.QueryValue(uri, "search_query") is { Length: > 0 } query) return query.Replace('+', ' ');
        var text = uri.Host + uri.PathAndQuery;
        return text.EndsWith('/') ? text[..^1] : text;
    }

    /// <summary>The page's own title for the video showing, if it's caught up with the page.</summary>
    private string? CurrentTitle => _titleFresh ? _videoTitle : null;

    private void OnBack(object sender, RoutedEventArgs e) => _core?.GoBack();
    private void OnForward(object sender, RoutedEventArgs e) => _core?.GoForward();
    private void OnReload(object sender, RoutedEventArgs e) => _core?.Reload();
    private void OnHome(object sender, RoutedEventArgs e) => Navigate(HomeUrl);

    private void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            AddressBox.Text = DisplayAddress(_core?.Source);
            FocusPage();
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var text = AddressBox.Text.Trim();
        if (text.Length == 0) return;

        var looksLikeLink = !text.Contains(' ') && text.Contains('.') &&
                            (text.Contains("://") || text.StartsWith("www.", StringComparison.OrdinalIgnoreCase) || text.Contains('/'));
        if (looksLikeLink && Uri.TryCreate(text.Contains("://") ? text : "https://" + text, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            if (!UrlGuard.IsBrowserHost(uri.Host))
            {
                _vm?.ShowToast("This tab only opens YouTube. Paste other links on the Download page.");
                return;
            }
            Navigate("https://" + uri.Host + uri.PathAndQuery);
        }
        else
        {
            Navigate("https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(text));
        }
        FocusPage();
    }

    private void OnAddressFocus(object sender, KeyboardFocusChangedEventArgs e) => AddressBox.SelectAll();

    private void OnAddressBlur(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_core is not null) AddressBox.Text = DisplayAddress(_core.Source);
    }

    private void OnAddressTextChanged(object sender, TextChangedEventArgs e) =>
        AddressHint.Visibility = AddressBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ---------------------------------------------------------------- handing videos to Snag

    private void PlayInSnag(string url, string? title)
    {
        if (_vm is null) return;
        if (url == _videoUrl) PauseWebVideo();
        _vm.PlayStream(url, title, null);
    }

    private void PauseWebVideo()
    {
        try
        {
            _core?.PostWebMessageAsJson("{\"type\":\"snag-pause\"}");
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't pause the page's video", ex);
        }
    }

    private void SendMode()
    {
        if (_core is null || _vm is null) return;
        try
        {
            _core.PostWebMessageAsJson(_vm.BrowserPlayInSnag ? "{\"type\":\"snag-mode\",\"playInSnag\":true}" : "{\"type\":\"snag-mode\",\"playInSnag\":false}");
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't update the YouTube tab", ex);
        }
    }

    /// <summary>
    /// Looks up the video's sponsor segments and dislike count (whichever are switched on) and hands them to the
    /// page, which skips the segments and shows the count. The page itself never contacts those services.
    /// </summary>
    private async Task SendVideoInfoAsync()
    {
        if (_core is null || _vm is null || _videoUrl is not { } url || UrlGuard.VideoIdOf(new Uri(url)) is not { } id) return;
        var segments = _vm.BrowserSponsorBlock ? YouTubeExtras.SkipSegmentsAsync(id) : Task.FromResult<IReadOnlyList<SkipSegment>>([]);
        var dislikes = _vm.BrowserDislikes ? YouTubeExtras.DislikeCountAsync(id) : Task.FromResult<long?>(null);
        await Task.WhenAll(segments, dislikes);
        if (_core is null || _videoUrl != url) return;
        var message = new Dictionary<string, object?>
        {
            ["type"] = "snag-video",
            ["id"] = id,
            ["segments"] = segments.Result.Select(s => new[] { s.Start, s.End }).ToArray(),
            ["dislikes"] = dislikes.Result is { } count ? Fmt.Count(count) : null,
        };
        try
        {
            _core.PostWebMessageAsJson(JsonSerializer.Serialize(message));
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't send video info to the YouTube tab", ex);
        }
    }

    private void OnPlayInSnag(object sender, RoutedEventArgs e)
    {
        if (_videoUrl is { } url) PlayInSnag(url, CurrentTitle);
    }

    private void OnQueue(object sender, RoutedEventArgs e)
    {
        if (_videoUrl is { } url) _vm?.QueueStream(url, CurrentTitle, null);
    }

    private void OnDownloadMenu(object sender, RoutedEventArgs e)
    {
        if (_videoUrl is not { } url || _vm is null) return;
        var title = CurrentTitle;
        var menu = NewMenu(DownloadButton, PlacementMode.Bottom);
        menu.Items.Add(MenuEntry("Video (MP4)", null, () => _ = _vm.DownloadVideoAsync(url, title, null, audioOnly: false)));
        menu.Items.Add(MenuEntry("Audio only (MP3)", null, () => _ = _vm.DownloadVideoAsync(url, title, null, audioOnly: true)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Uses the quality and extras chosen on the Download page", null, null, enabled: false));
        menu.IsOpen = true;
    }

    /// <summary>Right-clicking a video (a thumbnail, a title, or the page of the video showing) offers Snag's actions first.</summary>
    private void OnContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        if (_core is null || _vm is null) return;
        var items = e.MenuItems;
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (HiddenMenuItems.Contains(items[i].Name)) items.RemoveAt(i);
        }

        var target = e.ContextMenuTarget;
        string? url = null, title = null;
        if (target.HasLinkUri)
        {
            url = UrlGuard.CanonicalVideoUrl(target.LinkUri);
            var text = target.HasLinkText ? YouTubeMeta.CleanText(target.LinkText) : null;
            // A thumbnail's link text is often just its length ("12:34"); that's no title.
            title = text is null || text.Length < 3 || DurationText().IsMatch(text) ? null : text;
        }
        else if (!target.IsEditable && target.Kind is CoreWebView2ContextMenuTargetKind.Page or CoreWebView2ContextMenuTargetKind.Video)
        {
            url = _videoUrl;
            title = CurrentTitle;
        }
        if (url is null) return;

        var environment = _core.Environment;
        var entries = new (string Label, Action Run)[]
        {
            ("Play in Snag", () => PlayInSnag(url, title)),
            ("Add to Snag's queue", () => _vm.QueueStream(url, title, null)),
            ("Download video (MP4)", () => _ = _vm.DownloadVideoAsync(url, title, null, audioOnly: false)),
            ("Download audio (MP3)", () => _ = _vm.DownloadVideoAsync(url, title, null, audioOnly: true)),
        };
        var index = 0;
        foreach (var (label, run) in entries)
        {
            var item = environment.CreateContextMenuItem(label, null, CoreWebView2ContextMenuItemKind.Command);
            item.CustomItemSelected += (_, _) => Dispatcher.BeginInvoke(run);
            items.Insert(index++, item);
        }
        if (items.Count > index) items.Insert(index, environment.CreateContextMenuItem("", null, CoreWebView2ContextMenuItemKind.Separator));
    }

    /// <summary>Messages from youtube-tab.js. Treated as untrusted: YouTube pages only, a few known types, links re-checked.</summary>
    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_vm is null || !Uri.TryCreate(e.Source, UriKind.Absolute, out var source) || source.Scheme != Uri.UriSchemeHttps ||
            !UrlGuard.IsBrowserHost(source.Host))
            return;
        var fromYouTube = ("." + source.Host).EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase);

        string json;
        try
        {
            json = e.WebMessageAsJson;
        }
        catch (Exception)
        {
            return;
        }
        if (json.Length > 4096) return;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            string? Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            var type = Text("type");
            if (type == "pagefocus")
            {
                // The page took keyboard focus (a click into it). Just remembered: the page already has focus, and
                // moving it now would only blur the box being clicked.
                _pageFocused = true;
                return;
            }
            if (!fromYouTube) return; // Google's sign-in pages only ever send the above

            switch (type)
            {
                case "hello":
                    SendMode();
                    _ = SendVideoInfoAsync();
                    break;
                case "sponsorskipped":
                    if (_vm.BrowserSponsorBlock) _vm.ShowToast("Skipped a sponsor segment");
                    break;
                case "adblocked":
                    if (_vm.BrowserAdBlock) _vm.CountBlockedAd();
                    break;
                case "webplay":
                    // A video started on the page: Snag's player pauses so they don't play over each other.
                    if (_vm.Player.IsPlaying) _vm.Player.TogglePlay();
                    break;
                case "play":
                    if (_vm.BrowserPlayInSnag && UrlGuard.CanonicalVideoUrl(Text("url")) is { } url) PlayInSnag(url, Text("title"));
                    break;
            }
        }
        catch (JsonException)
        {
            // Not something Snag sent; ignore it.
        }
    }

    // ---------------------------------------------------------------- account

    /// <summary>Signed in if YouTube's sign-in cookie exists. Only the cookie's name is looked at, never its value.</summary>
    private async Task RefreshSignInAsync()
    {
        if (_core is null) return;
        try
        {
            var cookies = await _core.CookieManager.GetCookiesAsync("https://www.youtube.com/");
            SetSignedIn(cookies.Any(c => c.Name is "LOGIN_INFO" or "SID" or "__Secure-3PSID"));
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't check the YouTube sign-in", ex);
        }
    }

    private void SetSignedIn(bool signedIn)
    {
        _signedIn = signedIn;
        AccountText.Text = signedIn ? "Account" : "Sign in";
        AccountGlyph.Text = signedIn ? "" : "";
        AccountButton.ToolTip = signedIn ? "Your YouTube account" : "Sign in to YouTube with your Google account";
    }

    private void OnAccount(object sender, RoutedEventArgs e)
    {
        if (!_signedIn)
        {
            Navigate(SignInUrl);
            return;
        }
        var menu = NewMenu(AccountButton, PlacementMode.Bottom);
        menu.Items.Add(MenuEntry("Switch account", null, () => Navigate(SwitchAccountUrl)));
        menu.Items.Add(MenuEntry("Google account settings", null, () => Navigate(GoogleAccountUrl)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Sign out", null, () => Navigate(SignOutUrl)));
        menu.IsOpen = true;
    }

    private void OnMore(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var menu = NewMenu(MoreButton, PlacementMode.Bottom);
        menu.Items.Add(MenuEntry("Block ads", _vm.BrowserAdBlock, () => OnToggleAdBlock(this, new RoutedEventArgs())));
        menu.Items.Add(MenuEntry("Play clicked videos in Snag's player", _vm.BrowserPlayInSnag, () =>
        {
            _vm.BrowserPlayInSnag = !_vm.BrowserPlayInSnag;
            SendMode();
            _vm.ShowToast(_vm.BrowserPlayInSnag
                ? "Clicking a video now plays it in Snag's player"
                : "Clicking a video now plays it here");
        }));
        menu.Items.Add(MenuEntry("Skip sponsor segments (SponsorBlock)", _vm.BrowserSponsorBlock, () =>
        {
            _vm.BrowserSponsorBlock = !_vm.BrowserSponsorBlock;
            _ = SendVideoInfoAsync();
            _vm.ShowToast(_vm.BrowserSponsorBlock ? "Sponsor segments will be skipped" : "Sponsor segments won't be skipped");
        }));
        menu.Items.Add(MenuEntry("Show dislikes (Return YouTube Dislike)", _vm.BrowserDislikes, () =>
        {
            _vm.BrowserDislikes = !_vm.BrowserDislikes;
            _ = SendVideoInfoAsync();
            _vm.ShowToast(_vm.BrowserDislikes ? "Dislike counts are on" : "Dislike counts are off");
        }));
        menu.Items.Add(new Separator());
        var pageUrl = _core?.Source;
        menu.Items.Add(MenuEntry("Copy link", false, () => CopyLink(_videoUrl ?? pageUrl), enabled: !string.IsNullOrEmpty(pageUrl)));
        menu.Items.Add(MenuEntry("Open in your browser", false, () =>
        {
            if (Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri)) OpenExternally(uri);
        }, enabled: !string.IsNullOrEmpty(pageUrl)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Clear browsing data…", false, () => _ = ClearDataAsync(), enabled: _core is not null));
        menu.IsOpen = true;
    }

    private void CopyLink(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            Clipboard.SetText(url);
            _vm?.ShowToast("Link copied");
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't copy a link", ex);
        }
    }

    private async Task ClearDataAsync()
    {
        if (_core is null) return;
        if (!Dialogs.Confirm("Clear the YouTube tab's data?",
                "This signs you out of YouTube here and clears the tab's cookies, history and cache. Your downloads, queue and playlists aren't touched.",
                "Clear data", tone: DialogTone.Danger, glyph: Dialogs.TrashGlyph))
            return;
        try
        {
            await _core.Profile.ClearBrowsingDataAsync();
            SetSignedIn(false);
            Navigate(HomeUrl);
            _vm?.ShowToast("The YouTube tab's data was cleared");
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't clear the YouTube tab's data", ex);
            _vm?.ShowToast("Couldn't clear the data. Try again.");
        }
    }

    // ---------------------------------------------------------------- full screen

    public void ExitFullscreen()
    {
        try
        {
            _core?.ExecuteScriptAsync("if (document.fullscreenElement) document.exitFullscreen();");
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't leave full screen", ex);
        }
    }

    private void ApplyFullscreen(bool on)
    {
        Toolbar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        Layout.Margin = on ? new Thickness(0) : new Thickness(22, 2, 22, 22);
        PageFrame.CornerRadius = new CornerRadius(on ? 0 : 12);
        PageFrame.BorderThickness = new Thickness(on ? 0 : 1);
        UpdateWebRegion();
    }

    // ---------------------------------------------------------------- the page window: corners, dialogs, theme

    /// <summary>
    /// The page is a native window, so WPF can't round its corners; a window region does it instead, matching
    /// the player's rounded stage.
    /// </summary>
    private void UpdateWebRegion()
    {
        if (_web is null || _web.Handle == IntPtr.Zero) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = (int)Math.Ceiling(_web.ActualWidth * dpi.DpiScaleX) + 1;
        var height = (int)Math.Ceiling(_web.ActualHeight * dpi.DpiScaleY) + 1;
        if (width <= 1 || height <= 1) return;
        var diameter = _vm?.IsBrowserFullscreen == true ? 0 : (int)Math.Round(22 * dpi.DpiScaleX);
        var region = diameter == 0 ? CreateRectRgn(0, 0, width, height) : CreateRoundRectRgn(0, 0, width, height, diameter, diameter);
        if (SetWindowRgn(_web.Handle, region, true) == 0) DeleteObject(region);
    }

    /// <summary>
    /// Snag's dialogs and the shutdown countdown are drawn by WPF, which the native page window would cover. While
    /// one is up the page steps aside and a still picture of it takes its place (blurred with the rest of the app).
    /// </summary>
    private async void UpdateCover()
    {
        var covered = _vm is not null && (_vm.IsDialogOpen || _vm.Shutdown.IsOpen);
        if (covered == _covered) return;
        _covered = covered;
        if (_web is null || _core is null) return;

        if (!covered)
        {
            _web.Visibility = Visibility.Visible;
            Snapshot.Visibility = Visibility.Collapsed;
            Snapshot.Source = null;
            // Typing was in the page before the dialog: it goes back there.
            if (_refocusAfterCover) _ = Dispatcher.BeginInvoke(FocusPage, DispatcherPriority.Input);
            return;
        }
        _refocusAfterCover = _pageFocused;

        try
        {
            var stream = new MemoryStream();
            var capture = _core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            await Task.WhenAny(capture, Task.Delay(150));
            if (_covered && capture.IsCompletedSuccessfully && stream.Length > 0)
            {
                stream.Position = 0;
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                Snapshot.Source = image;
                Snapshot.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            Log.Info($"No picture of the YouTube tab for the dialog: {ex.Message}");
        }
        if (_covered && _web is not null) _web.Visibility = Visibility.Hidden;
    }

    private void ApplyTheme()
    {
        if (_web is not null) _web.DefaultBackgroundColor = DrawingColor(ThemeManager.Current.Window);
        if (_core is not null)
            _core.Profile.PreferredColorScheme = ThemeManager.Current.IsLight ? CoreWebView2PreferredColorScheme.Light : CoreWebView2PreferredColorScheme.Dark;
    }

    private static System.Drawing.Color DrawingColor(Color c) => System.Drawing.Color.FromArgb(255, c.R, c.G, c.B);

    // ---------------------------------------------------------------- toast

    private void UpdateToast()
    {
        var text = _vm?.ToastText;
        var show = text is not null && IsVisible && _vm?.IsDialogOpen != true && _window?.IsActive == true;
        if (text is not null) ToastLabel.Text = text;
        if (!show)
        {
            ToastPopup.IsOpen = false;
            return;
        }
        // Reopened so it's placed for the new text's size.
        ToastPopup.IsOpen = false;
        ToastPopup.IsOpen = true;
    }

    private void RepositionToast()
    {
        if (!ToastPopup.IsOpen) return;
        ToastPopup.IsOpen = false;
        ToastPopup.IsOpen = true;
    }

    // ---------------------------------------------------------------- menus

    private ContextMenu NewMenu(UIElement target, PlacementMode placement) => new()
    {
        PlacementTarget = target,
        Placement = placement,
        // Menus inherit fonts from the button they open from; some of those use the icon font.
        FontFamily = FontFamily,
        FontSize = 13,
    };

    /// <summary>A menu entry with a tick column, like the player's captions menu.</summary>
    private static MenuItem MenuEntry(string text, bool? isChecked, Action? click, bool enabled = true)
    {
        var tick = new TextBlock
        {
            Text = isChecked == true ? "" : "",
            FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
            FontSize = 12,
            Width = isChecked is null ? 0 : 24,
            VerticalAlignment = VerticalAlignment.Center,
        };
        tick.SetResourceReference(TextBlock.ForegroundProperty, "BrandBrush");
        var header = new DockPanel();
        DockPanel.SetDock(tick, Dock.Left);
        header.Children.Add(tick);
        header.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });

        var item = new MenuItem { Header = header, IsEnabled = enabled };
        AutomationProperties.SetName(item, isChecked == true ? $"{text} (on)" : text);
        if (click is not null)
        {
            item.Click += (_, e) =>
            {
                e.Handled = true;
                click();
            };
        }
        return item;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr Active;
        public IntPtr Focus;
        public IntPtr Capture;
        public IntPtr MenuOwner;
        public IntPtr MoveSize;
        public IntPtr Caret;
        public NativeRect CaretRect;
    }

    /// <summary>Thread 0 = the thread in front, which owns Windows' keyboard focus.</summary>
    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

    [DllImport("user32.dll")]
    private static extern bool IsChild(IntPtr parent, IntPtr window);
}
