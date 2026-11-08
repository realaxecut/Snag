using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Snag.Services;
using Snag.ViewModels;

namespace Snag;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private WindowState _restoreState;
    private WindowStyle _restoreStyle;
    private ResizeMode _restoreResize;
    private Rect _restoreBounds;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.Shutdown.Opened += OnShutdownCountdown;
        Loaded += async (_, _) => await _vm.InitializeAsync();
        Closing += OnClosing;
        SourceInitialized += (_, _) => ApplyCaptionColor();
        ThemeManager.Changed += ApplyCaptionColor;
        Closed += (_, _) => ThemeManager.Changed -= ApplyCaptionColor;
        SizeChanged += (_, _) =>
        {
            if (_vm.IsOptionsPopupOpen) SizeOptionsCard();
        };
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.HideChrome)) ApplyFullscreen(_vm.HideChrome);
        else if (e.PropertyName == nameof(MainViewModel.IsOptionsPopupOpen)) ShowOptionsPopup(_vm.IsOptionsPopupOpen);
    }

    // ---------------------------------------------------------------- Snag's dialogs (see Dialogs.cs)

    private DispatcherFrame? _dialogFrame;
    private bool _dialogResult;

    /// <summary>One dialog at a time; a second one asked for meanwhile falls back to Windows' message box.</summary>
    public bool CanShowSnagDialog => _dialogFrame is null;

    /// <summary>
    /// Shows a dialog over the blurred app and waits for the answer, like MessageBox.Show does (the app keeps
    /// running meanwhile: playback, downloads and the clock carry on). True if the main button was chosen.
    /// </summary>
    public bool ShowSnagDialog(DialogRequest request)
    {
        if (_dialogFrame is not null) return false;
        var danger = request.Tone == DialogTone.Danger;

        DialogTitle.Text = request.Title;
        DialogMessage.Text = request.Message;
        DialogIcon.Text = request.Glyph;
        DialogIconTile.SetResourceReference(Border.BackgroundProperty, danger ? "DangerSoftBrush" : "BrandSoftBrush");
        DialogIcon.SetResourceReference(TextBlock.ForegroundProperty, danger ? "DangerBrush" : "BrandBrush");
        DialogConfirm.Content = request.ConfirmText;
        DialogConfirm.Style = (Style)FindResource(danger ? "DangerButton" : "PrimaryButton");
        DialogCancel.Content = request.CancelText;
        DialogCancel.Visibility = request.CancelText is null ? Visibility.Collapsed : Visibility.Visible;

        var previousFocus = Keyboard.FocusedElement as UIElement;
        _dialogResult = false;
        _vm.IsDialogOpen = true;
        AnimateDialog(open: true);
        // Something that deletes or stops things starts on the safe button.
        var first = danger && request.CancelText is not null ? DialogCancel : DialogConfirm;
        Dispatcher.BeginInvoke(() => first.Focus(), DispatcherPriority.Input);

        var frame = _dialogFrame = new DispatcherFrame();
        try
        {
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            _dialogFrame = null;
            _vm.IsDialogOpen = false;
            AnimateDialog(open: false);
            if (previousFocus is { IsVisible: true, Focusable: true }) previousFocus.Focus();
        }
        return _dialogResult;
    }

    private void CloseDialog(bool result)
    {
        if (_dialogFrame is not { } frame) return;
        _dialogResult = result;
        frame.Continue = false;
    }

    private void OnDialogConfirm(object sender, RoutedEventArgs e) => CloseDialog(true);
    private void OnDialogCancel(object sender, RoutedEventArgs e) => CloseDialog(false);
    private void OnDialogScrimClick(object sender, MouseButtonEventArgs e) => CloseDialog(false);

    /// <summary>The same entrance as the download options pop-up: fade and scale in while the app blurs.</summary>
    private void AnimateDialog(bool open)
    {
        var duration = TimeSpan.FromMilliseconds(open ? 220 : 150);
        if (open)
        {
            DialogOverlay.Visibility = Visibility.Visible;
            if (AppContent.Effect is not BlurEffect)
                AppContent.Effect = new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
        }

        var fade = open
            ? new DoubleAnimation(0, 1, duration) { EasingFunction = PopupEase }
            : new DoubleAnimation(0, duration) { EasingFunction = PopupEase };
        if (!open)
        {
            fade.Completed += (_, _) =>
            {
                if (_dialogFrame is not null) return; // another one opened meanwhile
                DialogOverlay.Visibility = Visibility.Collapsed;
                if (!_vm.IsOptionsPopupOpen) AppContent.Effect = null;
            };
        }
        DialogOverlay.BeginAnimation(OpacityProperty, fade);
        // The options pop-up keeps its own blur if it's open underneath.
        if (AppContent.Effect is BlurEffect blur && !_vm.IsOptionsPopupOpen)
            blur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(open ? 22 : 0, duration) { EasingFunction = PopupEase });
        var scale = new DoubleAnimation(open ? 0.94 : 1, open ? 1 : 0.96, duration) { EasingFunction = PopupEase };
        DialogCardScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        DialogCardScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
    }

    // ---------------------------------------------------------------- download options pop-up

    private static readonly IEasingFunction PopupEase = new CubicEase { EasingMode = EasingMode.EaseOut };

    /// <summary>Fades the pop-up in or out while the rest of the window blurs (and sharpens again).</summary>
    private void ShowOptionsPopup(bool open)
    {
        var duration = TimeSpan.FromMilliseconds(open ? 240 : 170);
        if (open)
        {
            SizeOptionsCard();
            OptionsOverlay.Visibility = Visibility.Visible;
            if (AppContent.Effect is not BlurEffect)
                AppContent.Effect = new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
        }
        if (AppContent.Effect is not BlurEffect blur) return;

        var fade = open
            ? new DoubleAnimation(0, 1, duration) { EasingFunction = PopupEase }
            : new DoubleAnimation(0, duration) { EasingFunction = PopupEase };
        if (!open)
        {
            fade.Completed += (_, _) =>
            {
                if (_vm.IsOptionsPopupOpen) return; // opened again meanwhile
                OptionsOverlay.Visibility = Visibility.Collapsed;
                AppContent.Effect = null; // a blur costs GPU time even at radius 0
            };
        }
        OptionsOverlay.BeginAnimation(OpacityProperty, fade);
        blur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(open ? 22 : 0, duration) { EasingFunction = PopupEase });
        var scale = new DoubleAnimation(open ? 0.94 : 1, open ? 1 : 0.96, duration) { EasingFunction = PopupEase };
        OptionsCardScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        OptionsCardScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);

        if (open) Dispatcher.BeginInvoke(() => OptionsCard.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)), DispatcherPriority.Input);
    }

    private void SizeOptionsCard()
    {
        OptionsCard.Width = Math.Clamp(ActualWidth - 96, 700, 1020);
        OptionsCard.Height = Math.Clamp(ActualHeight - 96, 460, 680);
    }

    private void OnOptionsScrimClick(object sender, MouseButtonEventArgs e) => _vm.CloseOptionsPopupCommand.Execute(null);

    // ---------------------------------------------------------------- theme

    /// <summary>Colours the title bar like the window (Windows 11; older versions ignore it).</summary>
    private void ApplyCaptionColor()
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

    private bool _fullscreenApplied;

    private void ApplyFullscreen(bool on)
    {
        if (on == _fullscreenApplied) return;
        _fullscreenApplied = on;
        if (on)
        {
            _restoreState = WindowState;
            _restoreStyle = WindowStyle;
            _restoreResize = ResizeMode;
            _restoreBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;

            // Size the window to the whole monitor ourselves (rather than Maximized) so it always
            // covers the taskbar and leaves no frame, whatever the theme does with the window chrome.
            var screen = CurrentMonitorBounds();
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;
            Left = screen.Left;
            Top = screen.Top;
            Width = screen.Width;
            Height = screen.Height;
            Topmost = true;
        }
        else
        {
            Topmost = false;
            WindowStyle = _restoreStyle;
            ResizeMode = _restoreResize;
            Left = _restoreBounds.Left;
            Top = _restoreBounds.Top;
            Width = _restoreBounds.Width;
            Height = _restoreBounds.Height;
            WindowState = _restoreState;
        }
    }

    /// <summary>The full bounds (taskbar included) of the monitor the window is on, in WPF units.</summary>
    private Rect CurrentMonitorBounds()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2 /* nearest */), ref info))
            return new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);

        var fromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = fromDevice.Transform(new Point(info.Monitor.Left, info.Monitor.Top));
        var bottomRight = fromDevice.Transform(new Point(info.Monitor.Right, info.Monitor.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    private void OnShutdownCountdown()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Dispatcher.BeginInvoke(() => CancelShutdownButton.Focus(), DispatcherPriority.Input);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e) => HandleKey(e);

    /// <summary>App-wide keyboard shortcuts; also called by the player's video overlay, which is its own window.</summary>
    public void HandleKey(KeyEventArgs e)
    {
        if (_dialogFrame is not null)
        {
            // Esc answers "no"; Tab, Enter and Space work the dialog's buttons; nothing reaches the app behind.
            if (e.Key == Key.Escape)
            {
                CloseDialog(false);
                e.Handled = true;
            }
            else if (e.Key is not (Key.Tab or Key.Enter or Key.Space or Key.Left or Key.Right))
            {
                e.Handled = true;
            }
            return;
        }

        if (_vm.Shutdown.IsOpen)
        {
            if (e.Key == Key.Escape)
            {
                _vm.Shutdown.Cancel();
                e.Handled = true;
            }
            return;
        }

        if (_vm.IsOptionsPopupOpen)
        {
            // Esc closes the pop-up, unless it's closing a drop-down list inside it.
            if (e.Key == Key.Escape && Keyboard.FocusedElement is not (ComboBoxItem or ComboBox { IsDropDownOpen: true }))
            {
                _vm.CloseOptionsPopupCommand.Execute(null);
                e.Handled = true;
            }
            return; // the rest of the app is behind the pop-up
        }

        if (e.Key == Key.Escape && _vm.IsFullscreen)
        {
            _vm.IsFullscreen = false;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _vm.IsBrowserFullscreen)
        {
            BrowserPane.ExitFullscreen();
            e.Handled = true;
            return;
        }

        if (Keyboard.FocusedElement is TextBoxBase or ComboBox or ComboBoxItem) return;

        if (_vm.Page == AppPage.Player && Keyboard.Modifiers == ModifierKeys.None && PlayerPane.HandleKey(e))
        {
            e.Handled = true;
            return;
        }

        if (_vm.Page == AppPage.Download && e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _vm.PasteCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = _vm.Page == AppPage.Download && e.Data.GetDataPresent(DataFormats.UnicodeText)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (_vm.Page != AppPage.Download || e.Data.GetData(DataFormats.UnicodeText) is not string text || string.IsNullOrWhiteSpace(text)) return;
        _vm.UrlText = text.Trim();
        _vm.AnalyzeCommand.Execute(null);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_dialogFrame is not null)
        {
            // A dialog is waiting for an answer; that comes first.
            e.Cancel = true;
            return;
        }
        if (_vm.PendingDownloadsOnExit > 0)
        {
            // Ask outside the Closing event (a dialog's message loop doesn't belong in here), then close for real.
            e.Cancel = true;
            Dispatcher.BeginInvoke(() =>
            {
                if (_vm.ConfirmExit()) Close();
            });
            return;
        }
        _vm.OnExit();
        PlayerPane.Shutdown();
        BrowserPane.Shutdown();
    }
}
