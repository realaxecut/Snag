using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
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
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsFullscreen)) ApplyFullscreen(_vm.IsFullscreen);
    }

    private void ApplyFullscreen(bool on)
    {
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
        if (_vm.Shutdown.IsOpen)
        {
            if (e.Key == Key.Escape)
            {
                _vm.Shutdown.Cancel();
                e.Handled = true;
            }
            return;
        }

        if (e.Key == Key.Escape && _vm.IsFullscreen)
        {
            _vm.IsFullscreen = false;
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
        if (!_vm.ConfirmExit())
        {
            e.Cancel = true;
            return;
        }
        _vm.OnExit();
        PlayerPane.Shutdown();
    }
}
