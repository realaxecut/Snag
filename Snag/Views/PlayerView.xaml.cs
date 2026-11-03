using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
        Stage.SizeChanged += (_, _) => UpdateStageClip();
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
        if (Player?.Current is { } item) PowerActions.ShowInExplorer(item.Path);
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
            _libVlc = new LibVLC("--no-video-title-show", "--no-snapshot-preview", "--no-stats", "--quiet");
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
            });
            _player.EndReached += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (_player?.State == VLCState.Ended) Player?.OnMediaEnded();
            });
            _player.EncounteredError += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (_player?.State == VLCState.Error) Player?.OnMediaFailed("It may be damaged, incomplete, or not a video or audio file.");
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
        if (!EnsureEngine())
        {
            Player?.OnMediaFailed("The VLC engine isn't installed yet. Open Settings and choose Re-download all.");
            return;
        }

        // A new video keeps the previous picture until its first frame; a song or a new start doesn't.
        if (item.IsAudio) VideoImage.Source = null;
        if (!item.IsAudio && VideoImage.Source is null) _loadingDelay.Start();
        UpdateSurface();

        var previous = _media;
        _media = new VlcMedia(_libVlc!, item.Path, FromType.FromPath);
        _player!.Play(_media);
        previous?.Dispose();
        _clock.Start();
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
        _player?.Stop();
        _clock.Stop();
        ResetClock();
    }

    public void Close()
    {
        _player?.Stop();
        _loaded = null;
        _clock.Stop();
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
    }

    private void HideOverlay()
    {
        _idle.Stop();
        // Stay visible while paused or while the pointer is on the controls.
        if (!_fullscreen || Player?.IsPlaying != true || Controls.IsMouseOver) return;
        Controls.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(350)));
        Controls.IsHitTestVisible = false;
        Cursor = Cursors.None;
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
        var pattern = string.Join(';', PlayerViewModel.MediaExtensions.Select(x => "*" + x));
        var dialog = new OpenFileDialog
        {
            Title = "Add to playlist",
            Multiselect = true,
            Filter = $"Video and audio|{pattern}|All files|*.*",
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
        var answer = MessageBox.Show(Window.GetWindow(this)!, $"Remove all {p.Count} items from the playlist? Your files stay where they are.",
            "Snag", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes) p.Clear();
    }

    private void AddPaths(IEnumerable<string> paths, int? insertAt)
    {
        if (Player is null || _vm is null) return;
        var added = Player.AddFiles(ExpandFolders(paths), insertAt);
        _vm.ShowToast(added.Count switch
        {
            0 => "No new playable files were found.",
            1 => "Added 1 file to the playlist",
            _ => $"Added {added.Count} files to the playlist",
        });
    }

    /// <summary>Folders become their media files (sorted, up to 2,000); files pass through.</summary>
    private static IEnumerable<string> ExpandFolders(IEnumerable<string> paths)
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
                        .Where(PlayerViewModel.IsMedia)
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
        if (PlaylistBox.SelectedItem is null) e.Handled = true;
    }

    private void OnMenuClick(object sender, RoutedEventArgs e)
    {
        if (PlaylistBox.SelectedItem is not PlaylistItem item || Player is not { } p) return;
        switch ((sender as MenuItem)?.Tag as string)
        {
            case "play": p.PlayItem(item); break;
            case "show": PowerActions.ShowInExplorer(item.Path); break;
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
