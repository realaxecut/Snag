using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Snag.Services;

namespace Snag.ViewModels;

public enum AppPage { Download, Player, History, Settings }

public enum PreviewState { Empty, Loading, Video, Playlist }

public sealed record QualityOption(string Value, string Label, string? Detail, bool IsEnabled = true);

public sealed record CodecOption(VideoCodec Codec, string Label, string? Detail, bool IsEnabled = true);

public sealed record ToolRow(string Name, string Description, string Glyph, bool Installed, string Version);

public sealed class MainViewModel : ObservableObject
{
    private static readonly QualityOption[] StandardVideoQualities =
    [
        new("best", "Best available", "Highest quality"),
        new("2160", "2160p", "4K"),
        new("1440", "1440p", "2K"),
        new("1080", "1080p", "Full HD"),
        new("720", "720p", "HD"),
        new("480", "480p", "SD"),
        new("360", "360p", "SD"),
        new("240", "240p", "Low"),
        new("144", "144p", "Lowest"),
    ];

    private static readonly (string Value, string Label, string Detail, int Kbps)[] AudioPresets =
    [
        ("best", "Best (VBR)", "~245 kbps", 245),
        ("320", "320 kbps", "Highest", 320),
        ("256", "256 kbps", "Very high", 256),
        ("192", "192 kbps", "Standard", 192),
        ("128", "128 kbps", "Smaller", 128),
    ];

    private readonly AppSettings _settings;
    private readonly ToolManager _tools = new();
    private readonly YtDlpService _ytdlp;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3.5) };
    private CancellationTokenSource? _analyzeCts;
    private CancellationTokenSource? _toolsCts;
    private CancellationTokenSource? _appUpdateCts;
    private MediaInfo? _info;
    private string? _focusVideoId;
    private bool _syncingQuality;
    private bool _bulkSelecting;
    private bool _exiting;

    public MainViewModel()
    {
        _settings = SettingsStore.Load();
        _ytdlp = new YtDlpService(_tools);

        Player = new PlayerViewModel(_settings.Playlist, _settings.Volume, SaveSoon, ShowToast)
        {
            Shuffle = _settings.Shuffle,
            Repeat = Enum.TryParse<RepeatMode>(_settings.Repeat, out var repeat) ? repeat : RepeatMode.Off,
        };
        Player.PlaylistFinished += OnPlaylistFinished;
        Shutdown = new ShutdownViewModel(() => ActiveCount, PrepareForShutdown, ShowToast);
        Shutdown.Canceled += () =>
        {
            Player.ShutdownWhenDone = false;
            ShowToast("Shutdown canceled");
        };

        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            Save();
        };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastText = null;
        };

        IsMp3 = _settings.Format == "mp3";
        SetCodecOptions(null);
        SetAudioQualities(null);

        AnalyzeCommand = new RelayCommand(() => _ = AnalyzeAsync(), () => ToolsReady && !ToolsBusy);
        PasteCommand = new RelayCommand(PasteAndAnalyze, () => ToolsReady && !ToolsBusy);
        CancelAnalyzeCommand = new RelayCommand(() => _analyzeCts?.Cancel());
        ClearPreviewCommand = new RelayCommand(ClearPreview);
        SelectAllCommand = new RelayCommand(() => SelectEntries(_ => true));
        SelectNoneCommand = new RelayCommand(() => SelectEntries(_ => false));
        OnlyFocusCommand = new RelayCommand(() => SelectEntries(e => e.Video.Id == _focusVideoId));
        DownloadCommand = new RelayCommand(EnqueueDownloads, () => CanDownload);
        BrowseFolderCommand = new RelayCommand(BrowseFolder);
        OpenFolderCommand = new RelayCommand(() =>
        {
            TryCreateDirectory(OutputFolder);
            PowerActions.ShowInExplorer(OutputFolder);
        });

        CancelDownloadCommand = new RelayCommand<DownloadItemViewModel>(CancelDownload, item => item.CanCancel);
        RetryDownloadCommand = new RelayCommand<DownloadItemViewModel>(item =>
        {
            item.Requeue();
            Pump();
        }, item => item.CanRetry);
        RemoveDownloadCommand = new RelayCommand<DownloadItemViewModel>(item =>
        {
            CancelDownload(item);
            Queue.Remove(item);
            RefreshQueue();
        });
        PlayDownloadCommand = new RelayCommand<DownloadItemViewModel>(PlayDownload, item => item.FilePath is not null);
        ShowDownloadCommand = new RelayCommand<DownloadItemViewModel>(item => PowerActions.ShowInExplorer(item.FilePath ?? item.Job.OutputDir));
        ClearFinishedCommand = new RelayCommand(() =>
        {
            foreach (var item in Queue.Where(q => q.IsFinished).ToList()) Queue.Remove(item);
            RefreshQueue();
        }, () => Queue.Any(q => q.IsFinished));
        CancelAllCommand = new RelayCommand(() =>
        {
            foreach (var item in Queue.ToList()) CancelDownload(item);
        }, () => Queue.Any(q => q.CanCancel));

        InstallToolsCommand = new RelayCommand(() => _ = InstallToolsAsync(), () => !ToolsBusy);
        CheckUpdatesCommand = new RelayCommand(() => _ = CheckUpdatesAsync(), () => !ToolsBusy && ToolsReady);
        ReinstallToolsCommand = new RelayCommand(() => _ = ReinstallToolsAsync(), () => !ToolsBusy);
        CancelToolsCommand = new RelayCommand(() => _toolsCts?.Cancel(), () => ToolsBusy);
        OpenToolsFolderCommand = new RelayCommand(() =>
        {
            TryCreateDirectory(_tools.Root);
            PowerActions.ShowInExplorer(_tools.Root);
        });

        CheckAppUpdateCommand = new RelayCommand(() => _ = CheckAppUpdateAsync(quiet: false), () => !AppUpdateBusy);
        InstallAppUpdateCommand = new RelayCommand(() => _ = InstallAppUpdateAsync(), () => AppUpdate is not null && !AppUpdateBusy);
        CancelAppUpdateCommand = new RelayCommand(() => _appUpdateCts?.Cancel(), () => AppUpdateBusy);
        OpenReleaseNotesCommand = new RelayCommand(() => AppUpdater.OpenInBrowser(AppUpdate?.PageUrl ?? AppUpdater.ReleasesPage));

        GoToPlayerCommand = new RelayCommand(() => Page = AppPage.Player);
        ToggleFullscreenCommand = new RelayCommand(() => IsFullscreen = !IsFullscreen);

        History = new ObservableCollection<HistoryItemViewModel>(HistoryStore.Load().Select(e => new HistoryItemViewModel(e)));
        HistoryView = CollectionViewSource.GetDefaultView(History);
        HistoryView.Filter = item => item is HistoryItemViewModel h && h.Matches(HistorySearch.Trim());
        History.CollectionChanged += (_, _) => RefreshHistorySummary();
        PlayHistoryCommand = new RelayCommand<HistoryItemViewModel>(PlayHistory, h => h.Exists);
        AddHistoryToPlaylistCommand = new RelayCommand<HistoryItemViewModel>(AddHistoryToPlaylist, h => h.Exists);
        ShowHistoryCommand = new RelayCommand<HistoryItemViewModel>(h => PowerActions.ShowInExplorer(h.Exists ? h.FilePath : Path.GetDirectoryName(h.FilePath) ?? ""));
        DeleteHistoryCommand = new RelayCommand<HistoryItemViewModel>(DeleteHistory);
        ClearHistoryCommand = new RelayCommand(ClearHistory, () => History.Count > 0);

        RefreshTools();
        RefreshQueue();
        RefreshHistorySummary();
        RefreshAppUpdateStatus();
    }

    // ---------------------------------------------------------------- shared state

    public PlayerViewModel Player { get; }
    public ShutdownViewModel Shutdown { get; }
    public ObservableCollection<DownloadItemViewModel> Queue { get; } = [];
    public IReadOnlyList<int> ParallelOptions { get; } = [1, 2, 3, 4];

    public ICommand AnalyzeCommand { get; }
    public ICommand PasteCommand { get; }
    public ICommand CancelAnalyzeCommand { get; }
    public ICommand ClearPreviewCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand SelectNoneCommand { get; }
    public ICommand OnlyFocusCommand { get; }
    public ICommand DownloadCommand { get; }
    public ICommand BrowseFolderCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand CancelDownloadCommand { get; }
    public ICommand RetryDownloadCommand { get; }
    public ICommand RemoveDownloadCommand { get; }
    public ICommand PlayDownloadCommand { get; }
    public ICommand ShowDownloadCommand { get; }
    public ICommand ClearFinishedCommand { get; }
    public ICommand CancelAllCommand { get; }
    public ICommand InstallToolsCommand { get; }
    public ICommand CheckUpdatesCommand { get; }
    public ICommand ReinstallToolsCommand { get; }
    public ICommand CancelToolsCommand { get; }
    public ICommand OpenToolsFolderCommand { get; }
    public ICommand CheckAppUpdateCommand { get; }
    public ICommand InstallAppUpdateCommand { get; }
    public ICommand CancelAppUpdateCommand { get; }
    public ICommand OpenReleaseNotesCommand { get; }
    public ICommand GoToPlayerCommand { get; }
    public ICommand ToggleFullscreenCommand { get; }
    public ICommand PlayHistoryCommand { get; }
    public ICommand AddHistoryToPlaylistCommand { get; }
    public ICommand ShowHistoryCommand { get; }
    public ICommand DeleteHistoryCommand { get; }
    public ICommand ClearHistoryCommand { get; }

    public AppPage Page
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            // Files may have been moved or deleted outside Snag since we last looked.
            if (value == AppPage.History)
            {
                foreach (var item in History) item.Refresh();
                RefreshHistorySummary();
            }
        }
    }

    public bool IsFullscreen
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            if (value) Page = AppPage.Player;
            OnPropertyChanged(nameof(FullscreenGlyph));
        }
    }

    public string FullscreenGlyph => IsFullscreen ? "" : "";

    public string? ToastText { get; private set => Set(ref field, value); }

    // ---------------------------------------------------------------- tools

    public bool ToolsReady
    {
        get;
        private set
        {
            if (Set(ref field, value)) RaiseCanDownload();
        }
    }

    public bool ToolsBusy
    {
        get;
        private set
        {
            if (Set(ref field, value)) RaiseCanDownload();
        }
    }

    public string? ToolsStatus { get; private set => Set(ref field, value); }
    public double ToolsProgress { get; private set => Set(ref field, value); }
    public string? ToolsError { get; private set => Set(ref field, value); }
    public IReadOnlyList<ToolRow> ToolRows { get; private set => Set(ref field, value); } = [];

    // ---------------------------------------------------------------- app updates

    public string AppVersionText { get; } = $"Snag {AppUpdater.CurrentVersionText}";

    /// <summary>A newer release found on GitHub, or null.</summary>
    public AppRelease? AppUpdate
    {
        get;
        private set
        {
            if (!Set(ref field, value)) return;
            OnPropertyChanged(nameof(HasAppUpdate));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasAppUpdate => AppUpdate is not null;

    public bool AppUpdateBusy
    {
        get;
        private set
        {
            if (Set(ref field, value)) CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool AppUpdateDownloading { get; private set => Set(ref field, value); }
    public double AppUpdateProgress { get; private set => Set(ref field, value); }
    public string? AppUpdateStatus { get; private set => Set(ref field, value); }
    public string? AppUpdateError { get; private set => Set(ref field, value); }

    public bool CheckAppUpdates
    {
        get => _settings.CheckAppUpdates;
        set => UpdateSetting(() => _settings.CheckAppUpdates = value);
    }

    // ---------------------------------------------------------------- link + preview

    public string UrlText
    {
        get;
        set
        {
            if (Set(ref field, value)) AnalyzeError = null;
        }
    } = "";

    public string? AnalyzeError { get; private set => Set(ref field, value); }

    public PreviewState Preview
    {
        get;
        private set
        {
            if (!Set(ref field, value)) return;
            OnPropertyChanged(nameof(IsPlaylistPreview));
            OnPropertyChanged(nameof(VideoQualityHeading));
            RaiseCanDownload();
        }
    }

    public bool IsPlaylistPreview => Preview == PreviewState.Playlist;
    public string VideoQualityHeading => IsPlaylistPreview ? "MAX RESOLUTION" : "RESOLUTION";

    public string? PreviewTitle { get; private set => Set(ref field, value); }
    public string? PreviewChannel { get; private set => Set(ref field, value); }
    public string? PreviewThumbnail { get; private set => Set(ref field, value); }
    public string? PreviewDuration { get; private set => Set(ref field, value); }
    public string? PreviewViews { get; private set => Set(ref field, value); }
    public string? PreviewDate { get; private set => Set(ref field, value); }
    public string? PreviewBest { get; private set => Set(ref field, value); }

    public ObservableCollection<PlaylistEntryViewModel> Entries { get; private set => Set(ref field, value); } = [];
    public string? PlaylistSummary { get; private set => Set(ref field, value); }

    public int SelectedCount
    {
        get;
        private set
        {
            if (Set(ref field, value)) RaiseCanDownload();
        }
    }

    public bool HasFocusVideo => _focusVideoId is not null;

    // ---------------------------------------------------------------- download options

    public bool IsMp3
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            OnPropertyChanged(nameof(IsMp4));
            OnPropertyChanged(nameof(DownloadLabel));
            _settings.Format = value ? "mp3" : "mp4";
            SaveSoon();
        }
    }

    public bool IsMp4
    {
        get => !IsMp3;
        set => IsMp3 = !value;
    }

    public IReadOnlyList<CodecOption> CodecOptions { get; private set => Set(ref field, value); } = [];

    public CodecOption? SelectedCodec
    {
        get;
        set
        {
            if (!Set(ref field, value) || value is null || _syncingQuality) return;
            _settings.VideoCodec = value.Codec.ToString();
            SaveSoon();
            RebuildVideoQualities();
        }
    }

    public IReadOnlyList<QualityOption> VideoQualities { get; private set => Set(ref field, value); } = [];

    public QualityOption? SelectedVideoQuality
    {
        get;
        set
        {
            if (!Set(ref field, value) || value is null || _syncingQuality) return;
            _settings.VideoQuality = value.Value;
            SaveSoon();
        }
    }

    public IReadOnlyList<QualityOption> AudioQualities { get; private set => Set(ref field, value); } = [];

    public QualityOption? SelectedAudioQuality
    {
        get;
        set
        {
            if (!Set(ref field, value) || value is null || _syncingQuality) return;
            _settings.AudioQuality = value.Value;
            SaveSoon();
        }
    }

    public string OutputFolder
    {
        get => _settings.OutputFolder;
        set
        {
            if (_settings.OutputFolder == value) return;
            _settings.OutputFolder = value;
            OnPropertyChanged();
            SaveSoon();
        }
    }

    public bool EmbedThumbnail
    {
        get => _settings.EmbedThumbnail;
        set => UpdateSetting(() => _settings.EmbedThumbnail = value);
    }

    public bool EmbedSubtitles
    {
        get => _settings.EmbedSubtitles;
        set => UpdateSetting(() => _settings.EmbedSubtitles = value);
    }

    public bool SponsorBlock
    {
        get => _settings.SponsorBlock;
        set => UpdateSetting(() => _settings.SponsorBlock = value);
    }

    public bool AddDownloadsToPlayer
    {
        get => _settings.AddDownloadsToPlayer;
        set => UpdateSetting(() => _settings.AddDownloadsToPlayer = value);
    }

    public bool AutoUpdateYtDlp
    {
        get => _settings.AutoUpdateYtDlp;
        set => UpdateSetting(() => _settings.AutoUpdateYtDlp = value);
    }

    public bool NumberPlaylistItems
    {
        get => _settings.NumberPlaylistItems;
        set => UpdateSetting(() => _settings.NumberPlaylistItems = value);
    }

    public int MaxParallel
    {
        get => _settings.MaxParallel;
        set
        {
            UpdateSetting(() => _settings.MaxParallel = Math.Clamp(value, 1, 4));
            Pump();
        }
    }

    public bool CanDownload =>
        ToolsReady && !ToolsBusy && (Preview == PreviewState.Video || (Preview == PreviewState.Playlist && SelectedCount > 0));

    public string DownloadLabel => Preview switch
    {
        PreviewState.Playlist when SelectedCount == 0 => "Select videos to download",
        PreviewState.Playlist => $"Download {SelectedCount} {(SelectedCount == 1 ? "video" : "videos")}{(IsMp3 ? " as MP3" : "")}",
        _ => IsMp3 ? "Download MP3" : "Download MP4",
    };

    // ---------------------------------------------------------------- queue

    public int ActiveCount { get; private set => Set(ref field, value); }
    public bool HasQueue { get; private set => Set(ref field, value); }
    public string? QueueSummary { get; private set => Set(ref field, value); }

    // ---------------------------------------------------------------- history

    public ObservableCollection<HistoryItemViewModel> History { get; }
    public ICollectionView HistoryView { get; }
    public string? HistorySummary { get; private set => Set(ref field, value); }
    public bool HasHistory { get; private set => Set(ref field, value); }

    public string HistorySearch
    {
        get;
        set
        {
            if (Set(ref field, value)) HistoryView.Refresh();
        }
    } = "";

    // ================================================================ lifecycle

    public async Task InitializeAsync()
    {
        CleanTempFolder();
        AppUpdater.ClearDownloads();
        if (_settings.CheckAppUpdates) _ = CheckAppUpdateAsync(quiet: true);

        // Snag set up before the VLC-based player existed: fetch just the engine, no questions asked.
        if (_tools.OnlyVlcMissing)
        {
            await RunToolsTaskAsync(async (progress, ct) =>
            {
                await _tools.InstallMissingAsync(progress, ct);
                ShowToast("Player upgraded: it now plays every video format");
            });
            return;
        }

        if (!ToolsReady || !_settings.AutoUpdateYtDlp) return;
        if (_tools.Versions.LastUpdateCheckUtc is { } last && DateTime.UtcNow - last < TimeSpan.FromHours(20)) return;

        await RunToolsTaskAsync(async (progress, ct) =>
        {
            var report = await _tools.UpdateAsync(includeDeno: false, progress, ct);
            if (report.Updated.Count > 0) ShowToast($"yt-dlp updated to {_tools.Versions.YtDlp}");
        }, quiet: true);
    }

    public bool ConfirmExit()
    {
        if (_exiting) return true;
        var pending = Queue.Count(q => q.CanCancel);
        if (pending == 0) return true;
        return MessageBox.Show(
            $"{pending} download{(pending == 1 ? " is" : "s are")} still running or waiting. Quit anyway?",
            "Snag", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    public void OnExit()
    {
        _exiting = true;
        foreach (var item in Queue) item.Cts?.Cancel();
        _analyzeCts?.Cancel();
        _toolsCts?.Cancel();
        Save();
    }

    public void ShowToast(string message)
    {
        ToastText = message;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void SaveSoon()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Save()
    {
        _settings.Volume = Player.Volume;
        _settings.Shuffle = Player.Shuffle;
        _settings.Repeat = Player.Repeat.ToString();
        _settings.Playlist = Player.Snapshot();
        SettingsStore.Save(_settings);
    }

    private void UpdateSetting(Action apply, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        apply();
        OnPropertyChanged(name);
        SaveSoon();
    }

    // ================================================================ analysis

    private async Task AnalyzeAsync()
    {
        if (!ToolsReady || ToolsBusy) return;
        if (!UrlGuard.TryParse(UrlText, out var uri, out var error))
        {
            AnalyzeError = error;
            return;
        }

        _analyzeCts?.Cancel();
        var cts = _analyzeCts = new CancellationTokenSource();
        AnalyzeError = null;
        Preview = PreviewState.Loading;
        try
        {
            var info = await _ytdlp.AnalyzeAsync(uri, UrlGuard.WantsPlaylist(uri), cts.Token);
            if (cts != _analyzeCts) return;
            if (info.IsLive) throw new YtDlpException("This is a live stream. You can download it once the stream has ended.");
            if (info.IsUpcoming) throw new YtDlpException("This video hasn't premiered yet.");
            if (!info.IsPlaylist && info.PageUrl is null) throw new YtDlpException("Couldn't find a downloadable link for this post.");
            ShowInfo(info, UrlGuard.VideoIdOf(uri));
        }
        catch (OperationCanceledException)
        {
            if (cts == _analyzeCts) ResetPreview();
        }
        catch (Exception ex)
        {
            if (cts != _analyzeCts) return;
            Log.Error("Analyze failed", ex);
            ResetPreview();
            AnalyzeError = ex is YtDlpException ? ex.Message : "Something went wrong: " + ex.Message;
        }
        finally
        {
            if (cts == _analyzeCts) _analyzeCts = null;
            cts.Dispose();
        }
    }

    private void ShowInfo(MediaInfo info, string? focusId)
    {
        _info = info;
        PreviewTitle = info.Title;
        PreviewChannel = info.Channel;

        if (info.IsPlaylist)
        {
            _bulkSelecting = true;
            Entries = new ObservableCollection<PlaylistEntryViewModel>(info.Entries.Select(v => new PlaylistEntryViewModel(v, OnEntrySelectionChanged)));
            _bulkSelecting = false;
            _focusVideoId = focusId is not null && info.Entries.Any(v => v.Id == focusId) ? focusId : null;
            PreviewThumbnail = null;
            SetCodecOptions(info);
            SetAudioQualities(null);
            Preview = PreviewState.Playlist;
            OnEntrySelectionChanged();
        }
        else
        {
            Entries = [];
            _focusVideoId = null;
            PreviewThumbnail = info.ThumbnailUrl;
            PreviewDuration = info.Duration is { } d ? Fmt.Duration(d) : null;
            PreviewViews = info.ViewCount is { } views ? $"{Fmt.Count(views)} views" : null;
            PreviewDate = info.UploadDate?.ToString("MMM d, yyyy");
            var top = info.Resolutions.OrderByDescending(r => r.Height).ThenByDescending(r => r.Fps).FirstOrDefault();
            PreviewBest = top is null ? null : $"Up to {ResolutionLabel(top)}";
            SetCodecOptions(info);
            SetAudioQualities(info.Duration);
            Preview = PreviewState.Video;
        }
        OnPropertyChanged(nameof(HasFocusVideo));
    }

    private void ResetPreview()
    {
        _info = null;
        _focusVideoId = null;
        Entries = [];
        Preview = PreviewState.Empty;
        SetCodecOptions(null);
        SetAudioQualities(null);
        OnPropertyChanged(nameof(HasFocusVideo));
    }

    private void ClearPreview()
    {
        _analyzeCts?.Cancel();
        ResetPreview();
        UrlText = "";
        AnalyzeError = null;
    }

    private void PasteAndAnalyze()
    {
        string text;
        try
        {
            text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        }
        catch (Exception ex)
        {
            Log.Error("Clipboard read failed", ex);
            text = "";
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            AnalyzeError = "There's no link on your clipboard.";
            return;
        }
        UrlText = text.Trim();
        _ = AnalyzeAsync();
    }

    private void OnEntrySelectionChanged()
    {
        if (_bulkSelecting) return;
        SelectedCount = Entries.Count(e => e.IsSelected);
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(_info?.Channel)) parts.Add(_info.Channel);
        parts.Add($"{Entries.Count} {(Entries.Count == 1 ? "video" : "videos")}");
        parts.Add($"{SelectedCount} selected");
        PlaylistSummary = string.Join("  ·  ", parts);
        OnPropertyChanged(nameof(DownloadLabel));
    }

    private void SelectEntries(Func<PlaylistEntryViewModel, bool> predicate)
    {
        _bulkSelecting = true;
        foreach (var entry in Entries) entry.IsSelected = predicate(entry);
        _bulkSelecting = false;
        OnEntrySelectionChanged();
    }

    /// <summary>
    /// Fills the codec picker: Auto plus, for a single video, each codec it actually comes in (or all
    /// four, as a preference, for playlists).
    /// </summary>
    private void SetCodecOptions(MediaInfo? info)
    {
        var single = info is { IsPlaylist: false, Resolutions.Count: > 0 };
        var options = new List<CodecOption> { new(VideoCodec.Auto, "Auto", "Best quality available") };
        foreach (var codec in CodecSupport.Choices)
        {
            string detail;
            if (single)
            {
                var top = info!.Resolutions.Where(r => r.Codec == codec).Select(r => r.Height).DefaultIfEmpty().Max();
                if (top == 0) continue; // this video doesn't come in that codec
                detail = $"Up to {top}p";
            }
            else
            {
                detail = CodecSupport.Blurb(codec);
            }
            options.Add(new CodecOption(codec, CodecSupport.DisplayName(codec), detail));
        }

        _syncingQuality = true;
        try
        {
            CodecOptions = options;
            SelectedCodec = options.FirstOrDefault(o => o.IsEnabled && o.Codec.ToString() == _settings.VideoCodec) ?? options[0];
        }
        finally
        {
            _syncingQuality = false;
        }
        RebuildVideoQualities();
    }

    private void RebuildVideoQualities()
    {
        var codec = SelectedCodec?.Codec ?? VideoCodec.Auto;
        SetVideoQualities(_info is { IsPlaylist: false, Resolutions.Count: > 0 } info ? BuildVideoOptions(info, codec) : StandardVideoQualities);
    }

    private void SetVideoQualities(IReadOnlyList<QualityOption> options)
    {
        _syncingQuality = true;
        try
        {
            VideoQualities = options;
            // Keep the saved resolution if it's available, else the nearest one below it, else the best.
            var wanted = _settings.VideoQuality;
            SelectedVideoQuality = options.FirstOrDefault(o => o.IsEnabled && o.Value == wanted)
                ?? (int.TryParse(wanted, out var cap)
                    ? options.Where(o => o.IsEnabled && int.TryParse(o.Value, out var h) && h <= cap).MaxBy(o => int.Parse(o.Value))
                    : null)
                ?? options.FirstOrDefault(o => o.IsEnabled);
        }
        finally
        {
            _syncingQuality = false;
        }
    }

    private void SetAudioQualities(double? duration)
    {
        var options = AudioPresets
            .Select(p => new QualityOption(p.Value, p.Label, duration is { } d ? $"{p.Detail} · ~{Fmt.Bytes(d * p.Kbps * 125)}" : p.Detail))
            .ToList();
        _syncingQuality = true;
        try
        {
            AudioQualities = options;
            SelectedAudioQuality = options.FirstOrDefault(o => o.Value == _settings.AudioQuality) ?? options[1];
        }
        finally
        {
            _syncingQuality = false;
        }
    }

    /// <summary>
    /// Every resolution the video comes in. With a codec chosen, the ones that codec doesn't have stay
    /// in the list but are greyed out.
    /// </summary>
    private static List<QualityOption> BuildVideoOptions(MediaInfo info, VideoCodec codec)
    {
        var pool = info.Resolutions;
        var heights = pool.Select(r => r.Height).Distinct().OrderByDescending(h => h).ToList();
        if (heights.Count == 0) return [.. StandardVideoQualities];

        // On Auto, the variant yt-dlp will pick at that size: H.264 first, then the leanest newer codec.
        VideoResolution? Pick(int height) => codec == VideoCodec.Auto
            ? pool.Where(r => r.Height == height).OrderBy(r => AutoRank(r.Codec)).FirstOrDefault()
            : pool.FirstOrDefault(r => r.Height == height && r.Codec == codec);

        string Size(VideoResolution r) => r.VideoBytes is { } bytes ? $" · ~{Fmt.Bytes(bytes + (info.AudioBytes ?? 0))}" : "";

        var best = heights.Select(Pick).FirstOrDefault(r => r is not null);
        var options = new List<QualityOption>
        {
            best is null
                ? new("best", "Best available", null, false)
                : new("best", "Best available", ResolutionLabel(best) + Size(best)),
        };
        foreach (var height in heights)
        {
            options.Add(Pick(height) is { } variant
                ? new QualityOption(height.ToString(), ResolutionLabel(variant), QualityTag(height) + Size(variant))
                : new QualityOption(height.ToString(), $"{height}p", $"Not in {CodecSupport.DisplayName(codec)}", false));
        }
        return options;
    }

    private static int AutoRank(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => 0,
        VideoCodec.Hevc => 1,
        VideoCodec.Vp9 => 2,
        VideoCodec.Av1 => 3,
        _ => 4,
    };

    private static string ResolutionLabel(VideoResolution r) => r.Fps > 30 ? $"{r.Height}p{r.Fps}" : $"{r.Height}p";

    private static string QualityTag(int height) => height switch
    {
        >= 2160 => "4K",
        >= 1440 => "2K",
        >= 1080 => "Full HD",
        >= 720 => "HD",
        >= 480 => "SD",
        _ => "Low",
    };

    private void RaiseCanDownload()
    {
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(DownloadLabel));
        CommandManager.InvalidateRequerySuggested();
    }

    // ================================================================ queue

    private void EnqueueDownloads()
    {
        if (_info is null || !CanDownload) return;

        var options = new DownloadOptions(
            AudioOnly: IsMp3,
            VideoQuality: SelectedVideoQuality?.Value ?? "best",
            Codec: SelectedCodec?.Codec ?? VideoCodec.Auto,
            AudioQuality: SelectedAudioQuality?.Value ?? "best",
            EmbedThumbnail: EmbedThumbnail,
            EmbedSubtitles: EmbedSubtitles && !IsMp3,
            SponsorBlock: SponsorBlock);

        var added = 0;
        if (Preview == PreviewState.Video && _info.PageUrl is { } pageUrl)
        {
            AddToQueue(new DownloadItemViewModel(new DownloadJob(pageUrl, OutputFolder, "", options), _info.Title, _info.Channel, _info.ThumbnailUrl));
            added = 1;
        }
        else if (Preview == PreviewState.Playlist)
        {
            var folder = Path.Combine(OutputFolder, PathSafe.FolderName(_info.Title));
            var digits = Math.Max(2, Entries.Count.ToString().Length);
            foreach (var entry in Entries.Where(e => e.IsSelected))
            {
                var prefix = NumberPlaylistItems ? entry.Index.ToString().PadLeft(digits, '0') + " - " : "";
                AddToQueue(new DownloadItemViewModel(new DownloadJob(entry.Video.Url, folder, prefix, options), entry.Title, entry.Channel, entry.ThumbnailUrl));
                added++;
            }
        }

        if (added == 0) return;
        ShowToast(added == 1 ? "Added to downloads" : $"Added {added} videos to downloads");
        Pump();
    }

    private void AddToQueue(DownloadItemViewModel item)
    {
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DownloadItemViewModel.State)) RefreshQueue();
        };
        Queue.Add(item);
    }

    private void Pump()
    {
        if (_exiting || !ToolsReady || ToolsBusy) return;
        while (Queue.Count(q => q.IsActive) < MaxParallel && Queue.FirstOrDefault(q => q.State == DownloadState.Queued) is { } next)
            _ = RunDownloadAsync(next);
        RefreshQueue();
    }

    private async Task RunDownloadAsync(DownloadItemViewModel item)
    {
        using var cts = new CancellationTokenSource();
        item.Cts = cts;
        item.Begin(); // flips to Downloading right away so Pump() counts it
        var tempDir = Path.Combine(AppPaths.TempDir, Guid.NewGuid().ToString("N")[..10]);
        try
        {
            Directory.CreateDirectory(item.Job.OutputDir);
            Directory.CreateDirectory(tempDir);
            var result = await _ytdlp.DownloadAsync(item.Job, tempDir, item, cts.Token);
            if (cts.IsCancellationRequested)
            {
                item.MarkCanceled();
            }
            else if (result.ExitCode == 0)
            {
                item.Complete();
                if (item.FilePath is { } path && File.Exists(path))
                {
                    // A local copy keeps the artwork working after TikTok/Instagram links expire.
                    var artwork = await ThumbnailCache.SaveAsync(item.ThumbnailUrl) ?? item.ThumbnailUrl;
                    RecordHistory(item, path, artwork);
                    if (AddDownloadsToPlayer) Player.AddDownloaded(path, item.Title, artwork);
                }
            }
            else
            {
                item.Fail(result.Error ?? $"yt-dlp stopped with exit code {result.ExitCode}.");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Download failed", ex);
            if (cts.IsCancellationRequested) item.MarkCanceled();
            else item.Fail(ex.Message);
        }
        finally
        {
            item.Cts = null;
            await DeleteDirectoryAsync(tempDir);
            Pump();
        }
    }

    private static void CancelDownload(DownloadItemViewModel item)
    {
        if (item.State == DownloadState.Queued) item.MarkCanceled();
        else item.Cts?.Cancel();
    }

    private void PlayDownload(DownloadItemViewModel item)
    {
        if (item.FilePath is not { } path || !File.Exists(path))
        {
            ShowToast("That file was moved or deleted.");
            return;
        }
        var entry = Player.AddDownloaded(path, item.Title, item.ThumbnailUrl);
        Page = AppPage.Player;
        Player.PlayItem(entry);
    }

    private void RefreshQueue()
    {
        int active = 0, waiting = 0, done = 0, failed = 0;
        foreach (var item in Queue)
        {
            switch (item.State)
            {
                case DownloadState.Downloading or DownloadState.Processing: active++; break;
                case DownloadState.Queued: waiting++; break;
                case DownloadState.Done: done++; break;
                case DownloadState.Failed: failed++; break;
            }
        }

        ActiveCount = active;
        HasQueue = Queue.Count > 0;
        var parts = new List<string>();
        if (active > 0) parts.Add($"{active} downloading");
        if (waiting > 0) parts.Add($"{waiting} waiting");
        if (done > 0) parts.Add($"{done} done");
        if (failed > 0) parts.Add($"{failed} failed");
        QueueSummary = string.Join("  ·  ", parts);
        CommandManager.InvalidateRequerySuggested();
    }

    // ================================================================ history

    private void RecordHistory(DownloadItemViewModel item, string path, string? artwork)
    {
        // Downloading the same file again replaces its old entry instead of duplicating it.
        foreach (var old in History.Where(h => string.Equals(h.FilePath, path, StringComparison.OrdinalIgnoreCase)).ToList())
            History.Remove(old);

        History.Insert(0, new HistoryItemViewModel(new HistoryEntry
        {
            Title = item.Title,
            FilePath = path,
            ThumbnailUrl = artwork,
            SourceUrl = item.Job.Url,
            Badge = item.Badge,
            IsAudio = item.IsAudio,
            SizeBytes = new FileInfo(path).Length,
            DownloadedUtc = DateTime.UtcNow,
        }));
        SaveHistory();
    }

    private void PlayHistory(HistoryItemViewModel item)
    {
        var entry = Player.AddDownloaded(item.FilePath, item.Title, item.ThumbnailUrl);
        Page = AppPage.Player;
        Player.PlayItem(entry);
    }

    private void AddHistoryToPlaylist(HistoryItemViewModel item)
    {
        var before = Player.Count;
        Player.AddDownloaded(item.FilePath, item.Title, item.ThumbnailUrl);
        ShowToast(Player.Count > before ? "Added to the player's playlist" : "It's already in the playlist");
    }

    private void DeleteHistory(HistoryItemViewModel item)
    {
        item.Refresh();
        if (!item.Exists)
        {
            History.Remove(item);
            SaveHistory();
            ShowToast("Removed from history (the file was already gone)");
            return;
        }

        var answer = MessageBox.Show(
            $"Delete \"{item.Title}\" from your computer?\n\nThe file goes to the Recycle Bin, so you can still restore it from there.",
            "Delete video", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        // Let go of the file first if the player has it open.
        Player.RemovePath(item.FilePath);
        if (!PowerActions.MoveToRecycleBin(item.FilePath))
        {
            ShowToast("Windows couldn't delete that file. Is it open in another app?");
            return;
        }

        History.Remove(item);
        SaveHistory();
        ThumbnailCache.Delete(item.ThumbnailUrl);
        ShowToast("Moved to the Recycle Bin");
    }

    private void ClearHistory()
    {
        var answer = MessageBox.Show(
            "Clear the download history list? Your files stay exactly where they are.",
            "Snag", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        History.Clear();
        SaveHistory();
    }

    private void SaveHistory() => HistoryStore.Save(History.Select(h => h.Entry));

    private void RefreshHistorySummary()
    {
        HasHistory = History.Count > 0;
        var onDisk = History.Where(h => h.Exists).ToList();
        HistorySummary = History.Count == 0
            ? null
            : $"{History.Count} {(History.Count == 1 ? "download" : "downloads")}  ·  {Fmt.Bytes(onDisk.Sum(h => h.Entry.SizeBytes))} on disk";
        CommandManager.InvalidateRequerySuggested();
    }

    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where downloads are saved",
            InitialDirectory = Directory.Exists(OutputFolder) ? OutputFolder : "",
        };
        if (dialog.ShowDialog() == true) OutputFolder = dialog.FolderName;
    }

    // ================================================================ tools

    private Task InstallToolsAsync() => RunToolsTaskAsync(async (progress, ct) =>
    {
        await _tools.InstallMissingAsync(progress, ct);
        ShowToast("All set. Paste a link to get started.");
    });

    private Task CheckUpdatesAsync() => RunToolsTaskAsync(async (progress, ct) =>
    {
        var report = await _tools.UpdateAsync(includeDeno: true, progress, ct);
        ShowToast(report.Updated.Count > 0 ? $"Updated {string.Join(" and ", report.Updated)}" : "Everything is up to date");
    });

    private Task ReinstallToolsAsync()
    {
        var answer = MessageBox.Show(
            "Download fresh copies of yt-dlp, FFmpeg, Deno and the VLC engine? That's about 230 MB.",
            "Snag", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return Task.CompletedTask;
        return RunToolsTaskAsync(async (progress, ct) =>
        {
            await _tools.ReinstallAllAsync(progress, ct);
            ShowToast("Tools downloaded and verified");
        });
    }

    private async Task RunToolsTaskAsync(Func<IProgress<ToolProgress>, CancellationToken, Task> work, bool quiet = false)
    {
        if (ToolsBusy) return;
        if (ActiveCount > 0)
        {
            ShowToast("Wait for the current downloads to finish first.");
            return;
        }

        var cts = _toolsCts = new CancellationTokenSource();
        ToolsBusy = true;
        ToolsError = null;
        ToolsProgress = 0;
        ToolsStatus = "Contacting GitHub…";
        var progress = new Progress<ToolProgress>(p =>
        {
            if (!ToolsBusy) return;
            ToolsStatus = p.Message;
            ToolsProgress = p.Percent;
        });

        try
        {
            await work(progress, cts.Token);
        }
        catch (OperationCanceledException)
        {
            ToolsError = quiet ? null : "Canceled.";
        }
        catch (Exception ex)
        {
            Log.Error("Tool operation failed", ex);
            ToolsError = quiet ? null : ex is HttpRequestException ? "Couldn't reach GitHub. Check your internet connection." : ex.Message;
        }
        finally
        {
            ToolsBusy = false;
            ToolsStatus = null;
            _toolsCts = null;
            cts.Dispose();
            RefreshTools();
            Pump();
        }
    }

    private void RefreshTools()
    {
        var v = _tools.Versions;
        static string Version(bool installed, string? version) => installed ? version ?? "Installed" : "Not installed";
        ToolRows =
        [
            new("yt-dlp", "The download engine", "", _tools.HasYtDlp, Version(_tools.HasYtDlp, v.YtDlp)),
            new("FFmpeg", "Merges video and audio, converts to MP3", "", _tools.HasFfmpeg, Version(_tools.HasFfmpeg, v.Ffmpeg)),
            new("Deno", "Sandboxed JavaScript runtime YouTube now requires", "", _tools.HasDeno, Version(_tools.HasDeno, v.Deno)),
            new("VLC engine", "Plays every video and audio format in Snag's player", "", ToolManager.HasVlc, Version(ToolManager.HasVlc, v.Vlc)),
        ];
        ToolsReady = _tools.IsReady;
    }

    // ================================================================ app updates

    private async Task CheckAppUpdateAsync(bool quiet)
    {
        if (AppUpdateBusy) return;
        var cts = _appUpdateCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        AppUpdateBusy = true;
        AppUpdateError = null;
        AppUpdateStatus = "Checking GitHub for a newer version…";
        try
        {
            AppUpdate = await AppUpdater.CheckAsync(cts.Token);
            _settings.LastAppUpdateCheckUtc = DateTime.UtcNow;
            SaveSoon();
            if (AppUpdate is { } release)
            {
                if (quiet) ShowToast($"Snag {release.VersionText} is available. Get it in Settings.");
            }
            else if (!quiet)
            {
                ShowToast("Snag is up to date");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't check for a Snag update", ex);
            if (!quiet)
                AppUpdateError = ex is HttpRequestException or OperationCanceledException
                    ? "Couldn't reach GitHub. Check your internet connection."
                    : ex.Message;
        }
        finally
        {
            AppUpdateBusy = false;
            _appUpdateCts = null;
            cts.Dispose();
            RefreshAppUpdateStatus();
        }
    }

    private async Task InstallAppUpdateAsync()
    {
        if (AppUpdate is not { } release || AppUpdateBusy) return;

        var cts = _appUpdateCts = new CancellationTokenSource();
        AppUpdateBusy = true;
        AppUpdateDownloading = true;
        AppUpdateError = null;
        AppUpdateProgress = 0;
        AppUpdateStatus = $"Downloading Snag {release.VersionText}…";
        IProgress<double> progress = new Progress<double>(fraction => AppUpdateProgress = fraction * 100);
        string? installer = null;
        try
        {
            installer = await AppUpdater.DownloadAsync(release, progress.Report, cts.Token);
        }
        catch (OperationCanceledException)
        {
            AppUpdateError = "Canceled.";
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't download the Snag update", ex);
            AppUpdateError = ex is HttpRequestException ? "Couldn't reach GitHub. Check your internet connection." : ex.Message;
        }
        finally
        {
            AppUpdateBusy = false;
            AppUpdateDownloading = false;
            _appUpdateCts = null;
            cts.Dispose();
            RefreshAppUpdateStatus();
        }
        if (installer is null) return;

        var running = Queue.Count(q => q.CanCancel);
        if (running > 0 && MessageBox.Show(
                $"{running} download{(running == 1 ? " is" : "s are")} still running or waiting, and will stop while Snag updates. Update now?",
                "Snag", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        if (!AppUpdater.StartInstaller(installer))
        {
            AppUpdateError = "Windows didn't get permission to install the update. Try again and choose Yes.";
            return;
        }

        // The installer replaces Snag's files, so get out of its way. It reopens Snag when it's done.
        _exiting = true;
        Application.Current.MainWindow?.Close();
    }

    private void RefreshAppUpdateStatus()
    {
        AppUpdateStatus = AppUpdate is { } release
            ? $"Version {release.VersionText} is available" + (release.InstallerSize > 0 ? $"  ·  {Fmt.Bytes(release.InstallerSize)} download" : "")
            : _settings.LastAppUpdateCheckUtc is { } last
                ? $"You're up to date  ·  checked {FriendlyTime(last)}"
                : "Snag can check GitHub for new versions of itself.";
    }

    private static string FriendlyTime(DateTime utc)
    {
        var local = utc.ToLocalTime();
        if (local.Date == DateTime.Today) return $"today at {local:t}";
        if (local.Date == DateTime.Today.AddDays(-1)) return $"yesterday at {local:t}";
        return $"on {local:MMM d}";
    }

    // ================================================================ shutdown

    private void OnPlaylistFinished()
    {
        if (Player.ShutdownWhenDone) Shutdown.Begin();
    }

    private void PrepareForShutdown()
    {
        Player.ShutdownWhenDone = false;
        if (App.DryRunShutdown) return;
        _exiting = true;
        foreach (var item in Queue) item.Cts?.Cancel();
        Save();
    }

    // ================================================================ housekeeping

    private static void CleanTempFolder()
    {
        try
        {
            if (!Directory.Exists(AppPaths.TempDir)) return;
            foreach (var dir in Directory.EnumerateDirectories(AppPaths.TempDir))
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch
                {
                    // Still in use by something; try again next launch.
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't clean the temp folder", ex);
        }
    }

    private static async Task DeleteDirectoryAsync(string path)
    {
        for (var attempt = 0; attempt < 5 && Directory.Exists(path); attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch
            {
                // ffmpeg may still be letting go of a .part file after a cancel.
                await Task.Delay(400);
            }
        }
    }

    private static void TryCreateDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't create {path}", ex);
        }
    }
}
