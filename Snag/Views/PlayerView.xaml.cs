using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;
using Snag.Services;
using Snag.ViewModels;
using VlcMedia = LibVLCSharp.Shared.Media;
using VlcPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Snag.Views;

/// <summary>
/// The player page. Audio and video are decoded by the VLC engine (libvlc), which plays every codec
/// Snag downloads (H.264, HEVC, VP9, AV1) and just about any file from disk. Frames arrive as an
/// ordinary WPF image (see <see cref="VlcVideoSurface"/>), so everything else on the page can sit on top.
/// </summary>
public partial class PlayerView : UserControl, IMediaHost
{
    private enum Surface { Empty, Loading, Video, Audio, Finished, Error }

    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly DispatcherTimer _loadingDelay = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _singleClick = new() { Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime()) };
    private MainViewModel? _vm;
    private PlaylistItem? _watchedItem;
    private LibVLC? _libVlc;
    private VlcPlayer? _player;
    private VlcMedia? _media;
    private VlcVideoSurface? _surface;
    private PlaylistItem? _loaded;
    private bool _seeking;
    private bool _fullscreen;
    private bool _loadingVisible;
    private Surface _surfaceShown = Surface.Empty;
    private Point _lastMouse = new(double.NaN, double.NaN);
    private Point _dragStart;
    private PlaylistItem? _dragCandidate;

    public PlayerView()
    {
        InitializeComponent();
        _clock.Tick += (_, _) => UpdateClock();
        _idle.Tick += (_, _) => HideOverlay();
        _loadingDelay.Tick += (_, _) =>
        {
            _loadingDelay.Stop();
            _loadingVisible = true;
            UpdateSurface();
        };
        _singleClick.Tick += (_, _) =>
        {
            _singleClick.Stop();
            Player?.TogglePlay();
        };
        PreviewMouseMove += OnAnyMouseMove;
        // The slider marks click-to-seek as handled itself, so listen to handled events too.
        SeekBar.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnSeekStart), handledEventsToo: true);
        SeekBar.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnSeekEnd), handledEventsToo: true);
        DataContextChanged += OnDataContextChanged;
        IsVisibleChanged += OnVisibleChanged;
        Stage.SizeChanged += (_, _) =>
        {
            UpdateStageClip();
            StyleCaptions();
        };
        VideoImage.SizeChanged += (_, _) => StyleCaptions();
        _captionClock.Tick += (_, _) => UpdateCaption();
    }

    private PlayerViewModel? Player => _vm?.Player;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnMainPropertyChanged;
            _vm.Player.PropertyChanged -= OnPlayerPropertyChanged;
        }
        _vm = DataContext as MainViewModel;
        if (_vm is null) return;
        _vm.PropertyChanged += OnMainPropertyChanged;
        _vm.Player.PropertyChanged += OnPlayerPropertyChanged;
        _vm.Player.Host = this;
        WatchCurrentItem();
        UpdateSurface();
    }

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsFullscreen)) ApplyFullscreen(_vm!.IsFullscreen);
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.Current):
                WatchCurrentItem();
                UpdateSurface();
                UpdateCaptionsButton();
                break;
            case nameof(PlayerViewModel.IsPlaying):
                UpdateSurface();
                // Pausing brings the full-screen controls back; resuming starts the hide timer again.
                if (_fullscreen) ShowOverlay();
                break;
            case nameof(PlayerViewModel.IsFinished):
                UpdateSurface();
                break;
            case nameof(PlayerViewModel.Volume) or nameof(PlayerViewModel.IsMuted):
                ApplyVolume();
                break;
        }
    }

    /// <summary>Follows the current item so an error on it updates the stage straight away.</summary>
    private void WatchCurrentItem()
    {
        if (_watchedItem is not null) _watchedItem.PropertyChanged -= OnCurrentItemChanged;
        _watchedItem = Player?.Current;
        if (_watchedItem is not null) _watchedItem.PropertyChanged += OnCurrentItemChanged;
    }

    private void OnCurrentItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaylistItem.HasError)) UpdateSurface();
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // No point drawing frames nobody can see; catch up with the latest one when the page comes back.
        if (_surface is null) return;
        _surface.IsOnScreen = IsVisible;
        if (IsVisible) _surface.Refresh();
    }

    // ---------------------------------------------------------------- the stage

    /// <summary>Picks the one layer the stage should show right now. Every state change ends up here.</summary>
    private void UpdateSurface()
    {
        var player = Player;
        var item = player?.Current;
        var hasFrame = VideoImage.Source is not null;
        var surface = item is null ? Surface.Empty
            : item.HasError ? Surface.Error
            : player!.IsFinished ? Surface.Finished
            : item.IsAudio ? Surface.Audio
            : hasFrame ? Surface.Video
            : Surface.Loading;

        EmptyState.Visibility = Show(surface == Surface.Empty);
        ErrorState.Visibility = Show(surface == Surface.Error);
        FinishedState.Visibility = Show(surface == Surface.Finished);
        AudioArt.Visibility = Show(surface == Surface.Audio);
        VideoImage.Visibility = Show(surface == Surface.Video);
        LoadingState.Visibility = Show(surface == Surface.Loading && _loadingVisible);
        PausedBadge.Visibility = Show(surface is Surface.Video or Surface.Audio && player?.IsPlaying == false);

        if (surface != Surface.Loading)
        {
            _loadingDelay.Stop();
            _loadingVisible = false;
        }
        _surfaceShown = surface;
        UpdateCaptionVisibility();
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void ShowFrame(BitmapSource? frame)
    {
        VideoImage.Source = frame;
        UpdateSurface();
    }

    private void OnStageMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Buttons on the stage (Add files, Play again, …) handle their own clicks.
        if (FindAncestor<ButtonBase>(e.OriginalSource as DependencyObject) is not null || Player?.HasCurrent != true || _vm is null) return;

        if (e.ClickCount >= 2)
        {
            // Double-click: full screen, and cancel the pause the first click was about to do.
            _singleClick.Stop();
            _vm.IsFullscreen = !_vm.IsFullscreen;
        }
        else if (_surfaceShown is Surface.Video or Surface.Audio or Surface.Loading)
        {
            // Wait a moment in case this turns into a double-click.
            _singleClick.Stop();
            _singleClick.Start();
        }
        e.Handled = true;
    }

    private void OnRemoveCurrent(object sender, RoutedEventArgs e)
    {
        if (Player?.Current is { } item) Player.Remove(item);
    }

    private void OnShowCurrent(object sender, RoutedEventArgs e)
    {
        if (Player?.Current is { } item) Locate(item);
    }

    /// <summary>A file is shown in Explorer; a streamed video opens on the Browser tab.</summary>
    private void Locate(PlaylistItem item)
    {
        if (item.IsStream) _vm?.OpenInBrowser(item.Path);
        else PowerActions.ShowInExplorer(item.Path);
    }

    // ---------------------------------------------------------------- the VLC engine

    /// <summary>Starts libvlc the first time something plays. False if the engine isn't installed (yet).</summary>
    private bool EnsureEngine()
    {
        if (_player is not null) return true;
        if (!ToolManager.HasVlc) return false;
        try
        {
            Core.Initialize(ToolManager.VlcDir);
            // Snag draws captions itself (see Captions.cs), so VLC's own subtitles stay off: no subtitle
            // pictures, and no picking up .srt files next to videos on its own.
            _libVlc = new LibVLC("--no-video-title-show", "--no-snapshot-preview", "--no-stats", "--quiet", "--no-spu", "--no-sub-autodetect-file");
            // Software decoding on purpose: VLC's GPU decoders crash (heap corruption) when frames are
            // copied back to memory for an app and files are switched quickly. Software decoding was
            // stable under the same stress test and handles 1080p AV1 with a few percent of the CPU.
            _player = new VlcPlayer(_libVlc) { EnableHardwareDecoding = false };

            // Frames go into a WPF image no bigger than the screen.
            var screenWidth = Math.Max(GetSystemMetrics(0), 1280);
            var screenHeight = Math.Max(GetSystemMetrics(1), 720);
            _surface = new VlcVideoSurface(Dispatcher, ShowFrame, screenWidth, screenHeight) { IsOnScreen = IsVisible };
            _surface.Attach(_player);

            // libvlc raises these on its own threads; hop to the UI thread before touching anything.
            // The state checks drop late events from a file that has already been replaced.
            _player.Playing += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                Player?.OnMediaOpened();
                ApplyVolume();
                UpdateClock();
                // If a streamed video is up next, look it up now so it starts without a wait.
                _vm?.PrefetchNextStream();
            });
            _player.EndReached += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (_player?.State == VLCState.Ended) Player?.OnMediaEnded();
            });
            _player.EncounteredError += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (_player?.State != VLCState.Error) return;
                if (_loaded is { IsStream: true } stream)
                {
                    // The links may have expired or been refused: look them up afresh next time.
                    _vm?.ForgetStream(stream.Path);
                    Player?.OnMediaFailed("YouTube stopped sending this video. Check your connection, then try playing it again.");
                }
                else
                {
                    Player?.OnMediaFailed("It may be damaged, incomplete, or not a video or audio file.");
                }
            });
            ApplyVolume();
            Log.Info($"VLC engine started ({_libVlc.Version})");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't start the VLC engine", ex);
            return false;
        }
    }

    // ---------------------------------------------------------------- captions (drawn by Snag, YouTube-style)

    private static readonly (double Scale, string Name)[] CaptionSizes = [(0.75, "Small"), (1.0, "Normal"), (1.5, "Large"), (2.0, "Huge")];
    private static readonly (string Id, string Name)[] CaptionBackgrounds = [("box", "Box (like YouTube)"), ("solid", "Solid"), ("none", "None (shadow only)")];
    private static readonly FontFamily CaptionFont = new("Segoe UI");

    private readonly DispatcherTimer _captionClock = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private CancellationTokenSource? _captionsCts;
    private string? _captionsFor;              // the file the tracks below belong to
    private List<CaptionTrack> _captionTracks = [];
    private bool _captionTracksReady;          // false while still looking
    private CaptionTrack? _captionTrack;       // the one showing; null = off
    private List<CaptionCue>? _cues;
    private string? _shownCaption;
    private string? _preferredCaption;         // the track name last picked (e.g. "English"), chosen again next time
    private ContextMenu? _captionsMenu;

    private bool CaptionsShowing => _captionTrack is not null && Player?.Current is { IsAudio: false };

    /// <summary>A new file: forget the old captions and look for this one's (inside it and next to it).</summary>
    private void LoadCaptionsFor(PlaylistItem item)
    {
        if (string.Equals(_captionsFor, item.Path, StringComparison.OrdinalIgnoreCase)) return; // replaying the same file
        _captionsCts?.Cancel();
        _captionsFor = item.Path;
        _captionTracks = [];
        _captionTrack = null;
        _cues = null;
        _captionClock.Stop();
        ShowCaption(null);
        // Streams have no file to look in (yet): captions come with downloading the video.
        _captionTracksReady = item.IsAudio || item.IsStream;
        UpdateCaptionsButton();
        if (item.IsAudio || item.IsStream) return;
        var cts = _captionsCts = new CancellationTokenSource();
        _ = FindCaptionsAsync(item.Path, cts.Token);
    }

    private void ForgetCaptions()
    {
        _captionsCts?.Cancel();
        _captionsFor = null;
        _captionTracks = [];
        _captionTrack = null;
        _cues = null;
        _captionClock.Stop();
        ShowCaption(null);
        UpdateCaptionsButton();
    }

    private async Task FindCaptionsAsync(string path, CancellationToken ct)
    {
        try
        {
            var tracks = await Captions.FindTracksAsync(path, ct);
            if (ct.IsCancellationRequested) return;
            _captionTracks = tracks;
            _captionTracksReady = true;
            if (_vm?.CaptionsOn == true && tracks.Count > 0) await ShowTrackAsync(PreferredTrack(tracks), ct);
            UpdateCaptionsButton();
        }
        catch (OperationCanceledException)
        {
            // A newer file took over.
        }
    }

    private CaptionTrack PreferredTrack(List<CaptionTrack> tracks) =>
        tracks.FirstOrDefault(t => string.Equals(t.Name, _preferredCaption, StringComparison.OrdinalIgnoreCase))
        ?? tracks.FirstOrDefault(t => t.Name.StartsWith("English", StringComparison.OrdinalIgnoreCase))
        ?? tracks[0];

    /// <summary>Reads a track and starts showing it (null hides captions).</summary>
    private async Task ShowTrackAsync(CaptionTrack? track, CancellationToken ct)
    {
        if (track is null)
        {
            _captionTrack = null;
            _cues = null;
            _captionClock.Stop();
            ShowCaption(null);
            UpdateCaptionsButton();
            return;
        }
        var cues = await Captions.LoadAsync(track, ct);
        if (ct.IsCancellationRequested || !string.Equals(_captionsFor, track.MediaPath, StringComparison.OrdinalIgnoreCase)) return;
        if (cues is not { Count: > 0 })
        {
            _vm?.ShowToast($"Couldn't read the captions in \"{track.Name}\".");
            return;
        }
        _captionTrack = track;
        _cues = cues;
        _captionClock.Start();
        UpdateCaption();
        UpdateCaptionsButton();
    }

    /// <summary>The user picked a track (or Off) from the menu or with the C key.</summary>
    private async Task ChooseTrackAsync(CaptionTrack? track)
    {
        if (_vm is null) return;
        _vm.CaptionsOn = track is not null;
        if (track is not null) _preferredCaption = track.Name;
        try
        {
            await ShowTrackAsync(track, _captionsCts?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Moved on to another file meanwhile.
        }
    }

    /// <summary>The C key: captions off, or back on with the preferred track.</summary>
    private void ToggleCaptions()
    {
        if (_vm is null || Player?.Current is not { IsAudio: false } current) return;
        if (current.IsStream)
        {
            _vm.ShowToast("Captions work on downloaded videos. Right-click it in the queue to download it.");
            return;
        }
        if (CaptionsShowing)
        {
            _ = ChooseTrackAsync(null);
            _vm.ShowToast("Captions off");
        }
        else if (_captionTracks.Count > 0)
        {
            var track = PreferredTrack(_captionTracks);
            _ = ChooseTrackAsync(track);
            _vm.ShowToast($"Captions: {track.Name}");
        }
        else
        {
            _vm.ShowToast(_captionTracksReady
                ? "This video has no captions. The CC button can add a subtitle file."
                : "Still looking for captions in this video…");
        }
    }

    /// <summary>The + and - keys: one caption size up or down.</summary>
    private void StepCaptionSize(int step)
    {
        if (_vm is null) return;
        var index = Array.FindIndex(CaptionSizes, s => s.Scale >= _vm.CaptionScale - 0.01);
        index = Math.Clamp((index < 0 ? 1 : index) + step, 0, CaptionSizes.Length - 1);
        SetCaptionSize(CaptionSizes[index].Scale);
        _vm.ShowToast($"Caption size: {CaptionSizes[index].Name}");
    }

    private void SetCaptionSize(double scale)
    {
        if (_vm is null) return;
        _vm.CaptionScale = scale;
        StyleCaptions();
    }

    private void SetCaptionBackground(string id)
    {
        if (_vm is null) return;
        _vm.CaptionBackground = id;
        StyleCaptions();
    }

    private void UpdateCaption()
    {
        if (_player is null || _cues is null)
        {
            ShowCaption(null);
            return;
        }
        ShowCaption(Captions.TextAt(_cues, TimeSpan.FromMilliseconds(Math.Max(0, _player.Time))));
    }

    /// <summary>One box per line, like YouTube; nothing when there's no caption right now.</summary>
    private void ShowCaption(string? text)
    {
        if (text == _shownCaption) return;
        _shownCaption = text;
        CaptionPanel.Children.Clear();
        if (!string.IsNullOrEmpty(text))
        {
            foreach (var line in text.Split('\n'))
            {
                CaptionPanel.Children.Add(new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Child = new TextBlock { Text = line, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap },
                });
            }
            StyleCaptions();
        }
        UpdateCaptionVisibility();
    }

    private void UpdateCaptionVisibility() =>
        CaptionPanel.Visibility = _surfaceShown == Surface.Video && CaptionPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Sizes the captions to the picture (about 4.5% of its height at Normal, as on YouTube) and keeps them
    /// inside it, a little above its bottom edge, or above the controls while they float over the video.
    /// </summary>
    private void StyleCaptions()
    {
        if (CaptionPanel.Children.Count == 0) return;
        var videoHeight = VideoImage.ActualHeight > 0 ? VideoImage.ActualHeight : Stage.ActualHeight;
        var videoWidth = VideoImage.ActualWidth > 0 ? VideoImage.ActualWidth : Stage.ActualWidth;
        var size = Math.Clamp(videoHeight * 0.045, 13, 60) * (_vm?.CaptionScale ?? 1);
        var background = _vm?.CaptionBackground ?? "box";
        Brush box = background switch
        {
            "solid" => new SolidColorBrush(Color.FromRgb(8, 8, 8)),
            "none" => Brushes.Transparent,
            _ => new SolidColorBrush(Color.FromArgb(0xBF, 8, 8, 8)),
        };
        Effect? shadow = background == "none" ? new DropShadowEffect { ShadowDepth = 1.5, BlurRadius = 5, Opacity = 0.95 } : null;

        var letterbox = Math.Max(0, (Stage.ActualHeight - videoHeight) / 2);
        var overControls = _fullscreen && Controls.IsHitTestVisible ? Math.Max(0, Controls.ActualHeight - Controls.Padding.Top) : 0;
        CaptionPanel.Margin = new Thickness(0, 0, 0, Math.Max(letterbox + videoHeight * 0.05, overControls + 12));
        CaptionPanel.MaxWidth = Math.Max(200, videoWidth * 0.9);

        foreach (var child in CaptionPanel.Children)
        {
            if (child is not Border { Child: TextBlock line } border) continue;
            border.Background = box;
            border.Padding = new Thickness(size * 0.28, size * 0.04, size * 0.28, size * 0.08);
            line.FontFamily = CaptionFont;
            line.FontSize = size;
            line.Foreground = Brushes.White;
            line.Effect = shadow;
        }
    }

    /// <summary>The CC button: Off, each track, size, background, and adding a subtitle file.</summary>
    private void OnCaptionsClick(object sender, RoutedEventArgs e)
    {
        // A menu inherits fonts from the button it opens from, and that button uses the icon font, which
        // has no letters (every word came out as boxes). So it gets the page's own text font.
        var menu = new ContextMenu
        {
            PlacementTarget = CaptionsButton,
            Placement = PlacementMode.Top,
            FontFamily = FontFamily,
            FontSize = FontSize,
        };
        var showing = CaptionsShowing;
        menu.Items.Add(MenuEntry("Off", !showing, () => _ = ChooseTrackAsync(null)));
        if (!_captionTracksReady) menu.Items.Add(MenuEntry("Looking for captions…", null, null, enabled: false));
        else if (_captionTracks.Count == 0) menu.Items.Add(MenuEntry("No captions in this video", null, null, enabled: false));
        foreach (var track in _captionTracks)
            menu.Items.Add(MenuEntry(track.Name, showing && _captionTrack?.Key == track.Key, () => _ = ChooseTrackAsync(track)));

        menu.Items.Add(new Separator());
        var sizes = MenuEntry("Size", null, null);
        foreach (var (scale, name) in CaptionSizes)
            sizes.Items.Add(MenuEntry(name, Math.Abs((_vm?.CaptionScale ?? 1) - scale) < 0.01, () => SetCaptionSize(scale)));
        menu.Items.Add(sizes);
        var backgrounds = MenuEntry("Background", null, null);
        foreach (var (id, name) in CaptionBackgrounds)
            backgrounds.Items.Add(MenuEntry(name, (_vm?.CaptionBackground ?? "box") == id, () => SetCaptionBackground(id)));
        menu.Items.Add(backgrounds);

        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Add subtitle file…", null, AddSubtitleFile));

        menu.Closed += (_, _) =>
        {
            _captionsMenu = null;
            if (_fullscreen) ShowOverlay();
        };
        _captionsMenu = menu;
        menu.IsOpen = true;
    }

    /// <summary>
    /// A menu entry. <paramref name="isChecked"/> true puts a tick before it (the current choice). The tick is
    /// drawn here, in a column every entry has, so ticked entries, plain ones and submenus all line up.
    /// The text is a TextBlock so a track name's underscores aren't read as keyboard shortcuts.
    /// </summary>
    private static MenuItem MenuEntry(string text, bool? isChecked, Action? click, bool enabled = true)
    {
        var tick = new TextBlock
        {
            Text = isChecked == true ? "" : "",
            FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
            FontSize = 12,
            Width = 24,
            VerticalAlignment = VerticalAlignment.Center,
        };
        tick.SetResourceReference(TextBlock.ForegroundProperty, "BrandBrush");
        var header = new DockPanel();
        DockPanel.SetDock(tick, Dock.Left);
        header.Children.Add(tick);
        header.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });

        var item = new MenuItem { Header = header, IsEnabled = enabled };
        AutomationProperties.SetName(item, isChecked == true ? $"{text} (selected)" : text);
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

    private void AddSubtitleFile()
    {
        if (_vm is null || Player?.Current is not { IsAudio: false } current) return;
        var dialog = new OpenFileDialog
        {
            Title = "Add a subtitle file",
            Filter = "Subtitles|*.srt;*.vtt;*.ass;*.ssa|All files|*.*",
            InitialDirectory = Path.GetDirectoryName(current.Path) ?? "",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var track = _captionTracks.FirstOrDefault(t => string.Equals(t.FilePath, dialog.FileName, StringComparison.OrdinalIgnoreCase))
                    ?? Captions.FromFile(current.Path, dialog.FileName);
        if (!_captionTracks.Contains(track)) _captionTracks = [.. _captionTracks, track];
        _captionTracksReady = true;
        _ = ChooseTrackAsync(track);
    }

    private void UpdateCaptionsButton()
    {
        var current = Player?.Current;
        var isStream = current is { IsStream: true };
        var isVideo = current is { IsAudio: false, IsStream: false };
        var showing = CaptionsShowing;
        CaptionsButton.IsEnabled = isVideo;
        CaptionsDot.Visibility = showing ? Visibility.Visible : Visibility.Collapsed;
        if (showing) CaptionsButton.SetResourceReference(ForegroundProperty, "BrandBrush");
        else CaptionsButton.ClearValue(ForegroundProperty);
        CaptionsButton.ToolTip = isStream ? "Captions work on downloaded videos"
            : !isVideo ? "Captions (videos only)"
            : showing ? "Captions: on (C)" : "Captions: off (C)";
    }

    private void ApplyVolume()
    {
        if (_player is null || Player is null) return;
        _player.Volume = (int)Math.Round(Player.Volume * 100);
        _player.Mute = Player.IsMuted;
    }

    /// <summary>Shuts the engine down cleanly when Snag closes.</summary>
    public void Shutdown()
    {
        try
        {
            _clock.Stop();
            _player?.Stop();
            _player?.Dispose();
            _media?.Dispose();
            _libVlc?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("VLC shutdown", ex);
        }
        _player = null;
        _libVlc = null;
    }

    // ---------------------------------------------------------------- IMediaHost

    public TimeSpan Position
    {
        get => TimeSpan.FromMilliseconds(Math.Max(0, _player?.Time ?? 0));
        set
        {
            if (_player is { IsSeekable: true }) _player.Time = (long)value.TotalMilliseconds;
            UpdateClock();
        }
    }

    public void Open(PlaylistItem item)
    {
        _loaded = item;
        ResetClock();
        LoadCaptionsFor(item);
        if (!EnsureEngine())
        {
            Player?.OnMediaFailed("The VLC engine isn't installed yet. Open Settings and choose Re-download all.");
            return;
        }

        _streamCts?.Cancel();
        _streamCts = null;
        _skipSegments = [];
        if (item.IsStream)
        {
            // Looking the video up takes a few seconds: stop what was playing and say so straight away.
            _player!.Stop();
            VideoImage.Source = null;
            LoadingText.Text = "Loading from YouTube…";
            _loadingDelay.Stop();
            _loadingVisible = true;
            UpdateSurface();
            var cts = _streamCts = new CancellationTokenSource();
            _ = OpenStreamAsync(item, cts.Token);
            return;
        }

        // A new video keeps the previous picture until its first frame; a song or a new start doesn't.
        LoadingText.Text = "Opening…";
        if (item.IsAudio) VideoImage.Source = null;
        if (!item.IsAudio && VideoImage.Source is null) _loadingDelay.Start();
        UpdateSurface();
        PlayMedia(new VlcMedia(_libVlc!, item.Path, FromType.FromPath));
    }

    private CancellationTokenSource? _streamCts;
    private IReadOnlyList<SkipSegment> _skipSegments = [];
    private long _lastSkipToast;

    /// <summary>SponsorBlock for streamed videos: where the sponsor reads are, skipped as they come up (see <see cref="SkipSponsors"/>).</summary>
    private async Task LoadSkipSegmentsAsync(PlaylistItem item)
    {
        _skipSegments = [];
        if (_vm?.BrowserSponsorBlock != true || UrlGuard.VideoIdOf(new Uri(item.Path)) is not { } id) return;
        var segments = await YouTubeExtras.SkipSegmentsAsync(id);
        if (_loaded == item) _skipSegments = segments;
    }

    private void SkipSponsors()
    {
        if (_skipSegments.Count == 0 || _seeking || _player is not { IsPlaying: true, IsSeekable: true } player || _vm?.BrowserSponsorBlock != true) return;
        var t = player.Time / 1000.0;
        foreach (var segment in _skipSegments)
        {
            if (t < segment.Start || t >= segment.End - 0.5) continue;
            player.Time = (long)(segment.End * 1000);
            if (Environment.TickCount64 - _lastSkipToast > 2000) _vm.ShowToast("Skipped a sponsor segment");
            _lastSkipToast = Environment.TickCount64;
            return;
        }
    }

    private void PlayMedia(VlcMedia media)
    {
        var previous = _media;
        _media = media;
        _player!.Play(_media);
        previous?.Dispose();
        _clock.Start();
    }

    /// <summary>
    /// A video streamed from YouTube: yt-dlp finds its media links (nothing is downloaded), then VLC plays the
    /// picture and the sound from YouTube's servers, joined together like a downloaded file.
    /// </summary>
    private async Task OpenStreamAsync(PlaylistItem item, CancellationToken ct)
    {
        if (_vm is null) return;
        try
        {
            var source = await _vm.ResolveStreamAsync(item.Path, ct);
            if (ct.IsCancellationRequested || _loaded != item || _libVlc is null || _player is null) return;

            var media = new VlcMedia(_libVlc, new Uri(source.VideoUrl));
            if (source.AudioUrl is { } audio) media.AddSlave(MediaSlaveType.Audio, 4, audio);
            if (source.UserAgent is { } agent) media.AddOption(":http-user-agent=" + agent);
            if (source.Referer is { } referer) media.AddOption(":http-referrer=" + referer);
            media.AddOption(":network-caching=" + (source.IsLive ? "4000" : "2000"));
            PlayMedia(media);
            _ = LoadSkipSegmentsAsync(item);
        }
        catch (OperationCanceledException)
        {
            // Another item took over.
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested || _loaded != item) return;
            Log.Error("Couldn't start a stream", ex);
            Player?.OnMediaFailed(ex is YtDlpException ? ex.Message : "Couldn't load this video from YouTube.");
        }
    }

    public void Play()
    {
        if (_player is null || _player.State is VLCState.Stopped or VLCState.Ended or VLCState.NothingSpecial)
        {
            if (_loaded is not null) Open(_loaded);
            return;
        }
        _player.SetPause(false);
        _clock.Start();
    }

    public void Pause() => _player?.SetPause(true);

    public void Restart()
    {
        if (_loaded is not null) Open(_loaded);
    }

    public void Stop()
    {
        _streamCts?.Cancel();
        _streamCts = null;
        _player?.Stop();
        _clock.Stop();
        ResetClock();
    }

    public void Close()
    {
        _streamCts?.Cancel();
        _streamCts = null;
        _player?.Stop();
        _loaded = null;
        _clock.Stop();
        ForgetCaptions();
        VideoImage.Source = null;
        ResetClock();
        UpdateSurface();
    }

    private void ResetClock()
    {
        SeekBar.Value = 0;
        ElapsedText.Text = TotalText.Text = "0:00";
    }

    private void UpdateClock()
    {
        if (_player is null) return;
        var length = _player.Length;
        if (length <= 0) return;
        var total = length / 1000.0;
        var position = Math.Clamp(_player.Time / 1000.0, 0, total);
        SeekBar.Maximum = Math.Max(total, 0.1);
        if (!_seeking) SeekBar.Value = position;
        ElapsedText.Text = Fmt.Duration(_seeking ? SeekBar.Value : position);
        TotalText.Text = Fmt.Duration(total);
        SkipSponsors();
    }

    private void OnSeekStart(object sender, MouseButtonEventArgs e) => _seeking = true;

    private void OnSeekEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_seeking) return;
        _seeking = false;
        if (_player is { IsSeekable: true }) _player.Time = (long)(SeekBar.Value * 1000);
        UpdateClock();
    }

    private void OnSeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_seeking) ElapsedText.Text = Fmt.Duration(SeekBar.Value);
    }

    /// <summary>Keyboard shortcuts, called by the window while the Player page is showing.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (Player is not { } p || _vm is null) return false;
        var listHasFocus = PlaylistBox.IsKeyboardFocusWithin;
        switch (e.Key)
        {
            case Key.Space: p.TogglePlay(); return true;
            case Key.Left when !listHasFocus: Nudge(-5); return true;
            case Key.Right when !listHasFocus: Nudge(5); return true;
            case Key.Up when !listHasFocus: p.Volume += 0.05; return true;
            case Key.Down when !listHasFocus: p.Volume -= 0.05; return true;
            case Key.M: p.IsMuted = !p.IsMuted; return true;
            case Key.N: p.Next(); return true;
            case Key.P: p.Previous(); return true;
            case Key.S: p.Shuffle = !p.Shuffle; return true;
            case Key.R: p.CycleRepeat(); return true;
            case Key.C: ToggleCaptions(); return true;
            case Key.OemPlus or Key.Add: StepCaptionSize(1); return true;
            case Key.OemMinus or Key.Subtract: StepCaptionSize(-1); return true;
            case Key.F when p.HasCurrent: _vm.IsFullscreen = !_vm.IsFullscreen; return true;
            default: return false;
        }
    }

    private void Nudge(double seconds)
    {
        if (_player is not { Length: > 0 } player) return;
        var target = Math.Clamp(player.Time / 1000.0 + seconds, 0, player.Length / 1000.0);
        Position = TimeSpan.FromSeconds(target);
    }

    // ---------------------------------------------------------------- full screen

    private void ApplyFullscreen(bool on)
    {
        _fullscreen = on;
        Layout.Margin = on ? new Thickness(0) : new Thickness(22, 2, 22, 22);
        GapColumn.Width = new GridLength(on ? 0 : 18);
        ListColumn.Width = new GridLength(on ? 0 : 370);
        ListPanel.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        Stage.CornerRadius = new CornerRadius(on ? 0 : 12);

        // The video takes both rows and the controls float over its bottom edge.
        Grid.SetRowSpan(Stage, on ? 2 : 1);
        Controls.Padding = on ? new Thickness(36, 90, 36, 26) : new Thickness(4, 14, 4, 0);
        Controls.Background = on ? (Brush)Controls.Resources["OverlayScrim"] : null;
        // Over the video the controls are white-on-dark in every theme.
        ThemeManager.UseDarkColors(Controls.Resources, on);
        UpdateStageClip();

        if (on)
        {
            ShowOverlay();
        }
        else
        {
            _idle.Stop();
            Controls.BeginAnimation(OpacityProperty, null);
            Controls.Opacity = 1;
            Controls.IsHitTestVisible = true;
            Cursor = null;
        }
    }

    private void OnAnyMouseMove(object sender, MouseEventArgs e)
    {
        if (!_fullscreen) return;
        // WPF also raises MouseMove when what's under a still cursor changes; only real movement counts.
        var position = e.GetPosition(this);
        if (!double.IsNaN(_lastMouse.X) && (position - _lastMouse).Length < 3) return;
        _lastMouse = position;
        ShowOverlay();
    }

    private void ShowOverlay()
    {
        if (Controls.Opacity < 1 || Controls.HasAnimatedProperties)
            Controls.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
        Controls.IsHitTestVisible = true;
        Cursor = null;
        _idle.Stop();
        _idle.Start();
        StyleCaptions(); // captions move up out of the controls' way
    }

    private void HideOverlay()
    {
        _idle.Stop();
        // Stay visible while paused, while the pointer is on the controls, or while the captions menu is open.
        if (!_fullscreen || Player?.IsPlaying != true || Controls.IsMouseOver || _captionsMenu is { IsOpen: true }) return;
        Controls.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(350)));
        Controls.IsHitTestVisible = false;
        Cursor = Cursors.None;
        StyleCaptions(); // and back down once they've gone
    }

    private void UpdateStageClip()
    {
        var radius = Stage.CornerRadius.TopLeft;
        Stage.Clip = new RectangleGeometry(new Rect(Stage.RenderSize), radius, radius);
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    // ---------------------------------------------------------------- adding files

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add to playlist",
            Multiselect = true,
            Filter = MediaFiles.DialogFilter,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) AddPaths(dialog.FileNames, null);
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Add every video and song in a folder" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) AddPaths([dialog.FolderName], null);
    }

    private void OnClearPlaylist(object sender, RoutedEventArgs e)
    {
        if (Player is not { Count: > 0 } p) return;
        if (Dialogs.Confirm("Clear the queue?",
                $"All {(p.Count == 1 ? "1 item" : $"{p.Count} items")} will be removed from the queue. Your files stay where they are.",
                "Clear queue", glyph: Dialogs.TrashGlyph))
            p.Clear();
    }

    private void AddPaths(IEnumerable<string> paths, int? insertAt)
    {
        if (Player is null || _vm is null) return;
        var added = Player.AddFiles(MediaFiles.Expand(paths), insertAt);
        _vm.ShowToast(added.Count switch
        {
            0 => "No new playable files were found.",
            1 => "Added 1 file to the queue",
            _ => $"Added {added.Count} files to the queue",
        });
    }

    private void OnFilesDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFilesDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddPaths(paths, null);
        e.Handled = true;
    }

    // ---------------------------------------------------------------- playlist: reorder by dragging

    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(PlaylistBox);
        var source = e.OriginalSource as DependencyObject;
        _dragCandidate = FindAncestor<ButtonBase>(source) is null ? FindAncestor<ListBoxItem>(source)?.DataContext as PlaylistItem : null;
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is null) return;
        var delta = e.GetPosition(PlaylistBox) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var item = _dragCandidate;
        _dragCandidate = null;
        DragDrop.DoDragDrop(PlaylistBox, new DataObject(typeof(PlaylistItem), item), DragDropEffects.Move);
        ClearDropHints();
    }

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        var isMove = e.Data.GetDataPresent(typeof(PlaylistItem));
        var isFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = isMove ? DragDropEffects.Move : isFiles ? DragDropEffects.Copy : DragDropEffects.None;

        if (Player is { } p && (isMove || isFiles))
        {
            var (target, below) = DropTarget(e);
            foreach (var item in p.Items) item.DropHint = item == target ? (below ? DropHint.Below : DropHint.Above) : DropHint.None;
            AutoScroll(e);
        }
        e.Handled = true;
    }

    private void OnListDragLeave(object sender, DragEventArgs e) => ClearDropHints();

    private void OnListDrop(object sender, DragEventArgs e)
    {
        if (Player is not { } p) return;
        var (target, below) = DropTarget(e);
        var index = target is null ? p.Items.Count : p.Items.IndexOf(target) + (below ? 1 : 0);

        if (e.Data.GetData(typeof(PlaylistItem)) is PlaylistItem moved)
        {
            if (p.Items.IndexOf(moved) < index) index--;
            p.Move(moved, index);
            PlaylistBox.SelectedItem = moved;
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            AddPaths(paths, index);
        }

        ClearDropHints();
        e.Handled = true;
    }

    private (PlaylistItem? Item, bool Below) DropTarget(DragEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is { DataContext: PlaylistItem item } container)
            return (item, e.GetPosition(container).Y > container.ActualHeight / 2);
        // Empty space under the last row means "at the end".
        return Player is { Count: > 0 } p ? (p.Items[^1], true) : (null, false);
    }

    private void AutoScroll(DragEventArgs e)
    {
        if (FindDescendant<ScrollViewer>(PlaylistBox) is not { } scroller) return;
        var y = e.GetPosition(PlaylistBox).Y;
        if (y < 28) scroller.ScrollToVerticalOffset(scroller.VerticalOffset - 10);
        else if (y > PlaylistBox.ActualHeight - 28) scroller.ScrollToVerticalOffset(scroller.VerticalOffset + 10);
    }

    private void ClearDropHints()
    {
        if (Player is null) return;
        foreach (var item in Player.Items) item.DropHint = DropHint.None;
    }

    // ---------------------------------------------------------------- playlist: clicks, keys, menu

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is PlaylistItem item) Player?.PlayItem(item);
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (PlaylistBox.SelectedItem is not PlaylistItem item || Player is not { } p) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Delete)
        {
            p.Remove(item);
            e.Handled = true;
        }
        else if (key == Key.Enter)
        {
            p.PlayItem(item);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && key is Key.Up or Key.Down)
        {
            p.Move(item, p.Items.IndexOf(item) + (key == Key.Up ? -1 : 1));
            PlaylistBox.SelectedItem = item;
            PlaylistBox.ScrollIntoView(item);
            e.Handled = true;
        }
    }

    private void OnMoveUp(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item && Player is { } p) p.Move(item, p.Items.IndexOf(item) - 1);
    }

    private void OnMoveDown(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item && Player is { } p) p.Move(item, p.Items.IndexOf(item) + 1);
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) Player?.Remove(item);
    }

    private void OnListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Right-click picks the row under the pointer first, like Explorer.
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is { DataContext: PlaylistItem under }) PlaylistBox.SelectedItem = under;
        if (PlaylistBox.SelectedItem is not PlaylistItem item)
        {
            e.Handled = true;
            return;
        }
        LocateMenuItem.Header = item.LocateText;
        var download = item.IsStream ? Visibility.Visible : Visibility.Collapsed;
        DownloadMenuSeparator.Visibility = DownloadVideoMenuItem.Visibility = DownloadAudioMenuItem.Visibility = download;
    }

    /// <summary>The download button on a streamed video's row: MP4 or MP3, whichever the Download page is set to.</summary>
    private void OnDownloadItem(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { IsStream: true } item && _vm is not null)
            _ = _vm.DownloadVideoAsync(item.Path, item.Title, item.ArtworkUrl, fromQueue: true);
    }

    private void OnMenuClick(object sender, RoutedEventArgs e)
    {
        if (PlaylistBox.SelectedItem is not PlaylistItem item || Player is not { } p) return;
        switch ((sender as MenuItem)?.Tag as string)
        {
            case "play": p.PlayItem(item); break;
            case "show": Locate(item); break;
            case "dl-mp4" when item.IsStream && _vm is not null:
                _ = _vm.DownloadVideoAsync(item.Path, item.Title, item.ArtworkUrl, audioOnly: false, fromQueue: true);
                break;
            case "dl-mp3" when item.IsStream && _vm is not null:
                _ = _vm.DownloadVideoAsync(item.Path, item.Title, item.ArtworkUrl, audioOnly: true, fromQueue: true);
                break;
            case "top": p.Move(item, 0); break;
            case "up": p.Move(item, p.Items.IndexOf(item) - 1); break;
            case "down": p.Move(item, p.Items.IndexOf(item) + 1); break;
            case "bottom": p.Move(item, p.Items.Count - 1); break;
            case "remove": p.Remove(item); break;
        }
    }

    private static PlaylistItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as PlaylistItem;

    // ---------------------------------------------------------------- tree helpers

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match) return match;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject node) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } deeper) return deeper;
        }
        return null;
    }
}
