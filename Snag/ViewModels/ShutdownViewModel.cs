using System.Windows.Input;
using System.Windows.Threading;
using Snag.Services;

namespace Snag.ViewModels;

/// <summary>
/// The always-cancellable countdown shown before Snag turns the PC off: 60 seconds after a playlist, 10 at the end
/// of nuclear mode.
/// </summary>
public sealed class ShutdownViewModel : ObservableObject
{
    public const int CountdownSeconds = 60;
    private int _total = CountdownSeconds;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Func<int> _activeDownloads;
    private readonly Action _beforeShutdown;
    private readonly Action<string> _notify;

    public ShutdownViewModel(Func<int> activeDownloads, Action beforeShutdown, Action<string> notify)
    {
        _activeDownloads = activeDownloads;
        _beforeShutdown = beforeShutdown;
        _notify = notify;
        _timer.Tick += (_, _) => Tick();
        CancelCommand = new RelayCommand(Cancel);
        ShutDownNowCommand = new RelayCommand(Execute);
    }

    public ICommand CancelCommand { get; }
    public ICommand ShutDownNowCommand { get; }

    public event Action? Opened;
    public event Action? Canceled;

    public bool IsOpen { get; private set => Set(ref field, value); }

    public int SecondsLeft
    {
        get;
        private set
        {
            if (!Set(ref field, value)) return;
            OnPropertyChanged(nameof(Fraction));
            OnPropertyChanged(nameof(Message));
        }
    }

    public double Fraction => SecondsLeft / (double)_total;
    public string Message => $"Your PC will shut down in {SecondsLeft} second{(SecondsLeft == 1 ? "" : "s")}.";
    public string? Warning { get; private set => Set(ref field, value); }

    /// <summary>What just ended: "Playlist finished", or "Video finished" for nuclear mode.</summary>
    public string Heading { get; private set => Set(ref field, value); } = "Playlist finished";

    public void Begin(int seconds = CountdownSeconds, string heading = "Playlist finished")
    {
        if (IsOpen) return;
        _total = Math.Max(1, seconds);
        Heading = heading;
        SecondsLeft = _total;
        RefreshWarning();
        IsOpen = true;
        _timer.Start();
        Log.Info("Shutdown countdown started");
        Opened?.Invoke();
    }

    public void Cancel()
    {
        if (!IsOpen) return;
        _timer.Stop();
        IsOpen = false;
        Log.Info("Shutdown canceled by the user");
        Canceled?.Invoke();
    }

    private void Tick()
    {
        SecondsLeft--;
        RefreshWarning();
        if (SecondsLeft <= 0) Execute();
    }

    private void RefreshWarning()
    {
        var n = _activeDownloads();
        Warning = n > 0 ? $"{n} download{(n == 1 ? " is" : "s are")} still running and will be stopped." : null;
    }

    private void Execute()
    {
        if (!IsOpen) return;
        _timer.Stop();
        IsOpen = false;
        _beforeShutdown();

        if (App.DryRunShutdown)
        {
            _notify("Dry run: this is where Snag would shut down your PC.");
            return;
        }

        try
        {
            PowerActions.ShutDownNow();
        }
        catch (Exception ex)
        {
            Log.Error("Shutdown failed", ex);
            _notify("Windows didn't accept the shutdown request.");
        }
    }
}
