using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Snag.Services;

namespace Snag.ViewModels;

/// <summary>A download offered in the "Add from downloads" list, with its tick box.</summary>
public sealed class PickItem(HistoryItemViewModel history, bool alreadyIn) : ObservableObject
{
    public HistoryItemViewModel History { get; } = history;
    public bool AlreadyIn { get; } = alreadyIn;
    public bool CanPick => History.Exists && !AlreadyIn;
    public string? Note => AlreadyIn ? "Already in this playlist" : History.Exists ? null : "File not found";
    public bool IsChecked { get; set => Set(ref field, value && CanPick); }
}

/// <summary>
/// The Playlists page: saved playlists of videos and songs, built from files on the PC or from downloads,
/// that can be played (replacing the player's queue) or added to the player.
/// </summary>
public sealed class PlaylistsViewModel : ObservableObject
{
    private readonly PlayerViewModel _player;
    private readonly Func<IEnumerable<HistoryItemViewModel>> _history;
    private readonly Action<string> _toast;
    private readonly Action<AppPage> _goTo;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private bool _loading;

    public PlaylistsViewModel(PlayerViewModel player, Func<IEnumerable<HistoryItemViewModel>> history, Action<string> toast, Action<AppPage> goTo)
    {
        _player = player;
        _history = history;
        _toast = toast;
        _goTo = goTo;
        _saveTimer.Tick += (_, _) => SaveNow();

        _loading = true;
        foreach (var dto in PlaylistStore.Load()) Playlists.Add(new SavedPlaylistViewModel(dto, SaveSoon));
        _loading = false;
        Playlists.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasPlaylists));
            SaveSoon();
        };
        Selected = Playlists.FirstOrDefault();

        PickerView = CollectionViewSource.GetDefaultView(PickerItems);
        PickerView.Filter = o => o is PickItem p && p.History.Matches(PickerSearch.Trim());

        NewPlaylistCommand = new RelayCommand(() => Create([], select: true));
        SaveFromPlayerCommand = new RelayCommand(SaveFromPlayer, () => _player.Count > 0);
        DeleteCommand = new RelayCommand(Delete, () => Selected is not null);
        PlayCommand = new RelayCommand(() => SendToPlayer(replace: true), () => Selected is { IsEmpty: false });
        AddToPlayerCommand = new RelayCommand(() => SendToPlayer(replace: false), () => Selected is { IsEmpty: false });
        RemoveItemCommand = new RelayCommand<PlaylistItem>(item => Selected?.Items.Remove(item));
        MoveUpCommand = new RelayCommand<PlaylistItem>(item => Selected?.Move(item, -1), item => Selected?.Items.IndexOf(item) > 0);
        MoveDownCommand = new RelayCommand<PlaylistItem>(item => Selected?.Move(item, 1),
            item => Selected is { } s && s.Items.IndexOf(item) is var i && i >= 0 && i < s.Items.Count - 1);
        StartPickCommand = new RelayCommand(StartPick, () => Selected is not null);
        ConfirmPickCommand = new RelayCommand(ConfirmPick, () => PickedCount > 0);
        CancelPickCommand = new RelayCommand(() => IsPicking = false);
        PickAllCommand = new RelayCommand(() => SetPicked(true));
        PickNoneCommand = new RelayCommand(() => SetPicked(false));
    }

    public ObservableCollection<SavedPlaylistViewModel> Playlists { get; } = [];
    public bool HasPlaylists => Playlists.Count > 0;

    public SavedPlaylistViewModel? Selected
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            IsPicking = false;
            OnPropertyChanged(nameof(HasSelection));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasSelection => Selected is not null;

    /// <summary>Raised after a playlist is made, so the page can put the cursor in its name.</summary>
    public event Action? RenameRequested;

    public ICommand NewPlaylistCommand { get; }
    public ICommand SaveFromPlayerCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand PlayCommand { get; }
    public ICommand AddToPlayerCommand { get; }
    public ICommand RemoveItemCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand StartPickCommand { get; }
    public ICommand ConfirmPickCommand { get; }
    public ICommand CancelPickCommand { get; }
    public ICommand PickAllCommand { get; }
    public ICommand PickNoneCommand { get; }

    // ---------------------------------------------------------------- picking from downloads

    public bool IsPicking { get; private set => Set(ref field, value); }
    public ObservableCollection<PickItem> PickerItems { get; } = [];
    public ICollectionView PickerView { get; }

    public string PickerSearch
    {
        get;
        set
        {
            if (Set(ref field, value)) PickerView.Refresh();
        }
    } = "";

    public int PickedCount => PickerItems.Count(p => p.IsChecked);
    public string PickLabel => PickedCount switch { 0 => "Tick downloads to add", 1 => "Add 1 to the playlist", var n => $"Add {n} to the playlist" };

    private void StartPick()
    {
        if (Selected is not { } playlist) return;
        foreach (var item in PickerItems) item.PropertyChanged -= OnPickChanged;
        PickerItems.Clear();
        foreach (var history in _history())
        {
            history.Refresh();
            var item = new PickItem(history, playlist.Contains(history.FilePath));
            item.PropertyChanged += OnPickChanged;
            PickerItems.Add(item);
        }
        PickerSearch = "";
        RaisePicked();
        IsPicking = true;
    }

    private void OnPickChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PickItem.IsChecked)) RaisePicked();
    }

    private void SetPicked(bool value)
    {
        foreach (var item in PickerView.Cast<PickItem>()) item.IsChecked = value;
    }

    private void ConfirmPick()
    {
        if (Selected is not { } playlist) return;
        var picked = PickerItems.Where(p => p.IsChecked).Select(p => p.History).ToList();
        var added = playlist.Add(picked.Select(h => new PlaylistEntryDto { Path = h.FilePath, Title = h.Title, ArtworkUrl = h.ThumbnailUrl }));
        IsPicking = false;
        _toast(added == 1 ? $"Added 1 download to \"{playlist.DisplayName}\"" : $"Added {added} downloads to \"{playlist.DisplayName}\"");
    }

    private void RaisePicked()
    {
        OnPropertyChanged(nameof(PickedCount));
        OnPropertyChanged(nameof(PickLabel));
        CommandManager.InvalidateRequerySuggested();
    }

    // ---------------------------------------------------------------- playlists

    /// <summary>Files from the PC (folders become their videos and songs). Makes a playlist first if there's none selected.</summary>
    public void AddFiles(IEnumerable<string> paths)
    {
        var files = MediaFiles.Expand(paths).Where(p => MediaFiles.IsMedia(p) && File.Exists(p)).ToList();
        if (files.Count == 0)
        {
            _toast("No playable files were found.");
            return;
        }
        var playlist = Selected ?? Create([], select: true);
        var added = playlist.Add(files.Select(f => new PlaylistEntryDto { Path = f }));
        _toast(added switch
        {
            0 => "Those files are already in this playlist.",
            1 => $"Added 1 file to \"{playlist.DisplayName}\"",
            _ => $"Added {added} files to \"{playlist.DisplayName}\"",
        });
    }

    private SavedPlaylistViewModel Create(IEnumerable<PlaylistEntryDto> items, bool select)
    {
        var playlist = new SavedPlaylistViewModel(new SavedPlaylistDto
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = NextName(),
            CreatedUtc = DateTime.UtcNow,
            Items = items.Take(PlaylistStore.MaxItems).ToList(),
        }, SaveSoon);
        Playlists.Insert(0, playlist);
        if (select)
        {
            Selected = playlist;
            RenameRequested?.Invoke();
        }
        return playlist;
    }

    private void SaveFromPlayer()
    {
        if (_player.Count == 0) return;
        var playlist = Create(_player.Snapshot(), select: true);
        _goTo(AppPage.Playlists);
        _toast($"Saved the player's {playlist.Count} items as \"{playlist.DisplayName}\"");
        RenameRequested?.Invoke();
    }

    private void Delete()
    {
        if (Selected is not { } playlist) return;
        var answer = MessageBox.Show($"Delete the playlist \"{playlist.DisplayName}\"? The videos and songs themselves stay where they are.",
            "Snag", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        var index = Playlists.IndexOf(playlist);
        Playlists.Remove(playlist);
        Selected = Playlists.Count == 0 ? null : Playlists[Math.Min(index, Playlists.Count - 1)];
        _toast($"Deleted \"{playlist.DisplayName}\"");
    }

    private void SendToPlayer(bool replace)
    {
        if (Selected is not { IsEmpty: false } playlist) return;
        var entries = playlist.ToDto().Items;
        var added = _player.Enqueue(entries, replace);
        if (replace)
        {
            _goTo(AppPage.Player);
            _toast($"Playing \"{playlist.DisplayName}\"");
        }
        else
        {
            _toast(added == 0 ? "Everything in this playlist is already in the player" : $"Added {added} to the player");
        }
    }

    private string NextName()
    {
        for (var n = 1; ; n++)
        {
            var name = $"Playlist {n}";
            if (Playlists.All(p => !string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))) return name;
        }
    }

    // ---------------------------------------------------------------- saving

    private void SaveSoon()
    {
        if (_loading) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow()
    {
        _saveTimer.Stop();
        PlaylistStore.Save(Playlists.Select(p => p.ToDto()));
    }
}
