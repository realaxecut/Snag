using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;
using Snag.Services;

namespace Snag.ViewModels;

public enum DropHint { None, Above, Below }

public enum RepeatMode { Off, All, One }

/// <summary>What the view does with the actual MediaElement.</summary>
public interface IMediaHost
{
    TimeSpan Position { get; set; }
    void Open(PlaylistItem item);
    void Play();
    void Pause();
    void Restart();
    void Stop();
    void Close();
}

public sealed class PlaylistItem : ObservableObject
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".aac", ".wav", ".wma", ".flac", ".ogg", ".opus",
    };

    public PlaylistItem(string path, string? title = null, string? artworkUrl = null)
    {
        Path = path;
        Title = string.IsNullOrWhiteSpace(title) ? System.IO.Path.GetFileNameWithoutExtension(path) : title;
        ArtworkUrl = artworkUrl;
        var extension = System.IO.Path.GetExtension(path);
        IsAudio = AudioExtensions.Contains(extension);
        Kind = $"{(IsAudio ? "Audio" : "Video")} · {extension.TrimStart('.').ToUpperInvariant()}";
    }

    public string Path { get; }
    public string Title { get; }

    /// <summary>The download's thumbnail, or (for files from the PC) a frame from the video or the song's cover art.</summary>
    public string? ArtworkUrl { get; set => Set(ref field, value); }
    public bool IsAudio { get; }
    public string Kind { get; }

    public int Number { get; set => Set(ref field, value); }
    public bool IsCurrent { get; set => Set(ref field, value); }
    public bool Missing { get; set => Set(ref field, value); }
    public DropHint DropHint { get; set => Set(ref field, value); }

    public string? Error
    {
        get;
        set
        {
            if (Set(ref field, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => Error is not null;

    /// <summary>
    /// Files from the PC have no thumbnail: gives them a frame from halfway through the video (or the song's
    /// cover art) instead of a placeholder. Also redoes pictures whose cached file has gone. True if it changed.
    /// </summary>
    public async Task<bool> LoadArtworkAsync()
    {
        if (Missing) return false;
        if (ArtworkUrl is { } art && !(ThumbnailCache.IsCachedPath(art) && !File.Exists(art))) return false;
        var picture = await FrameGrabber.GetAsync(Path, IsAudio);
        if (picture is null) return false;
        ArtworkUrl = picture;
        return true;
    }

    public PlaylistEntryDto ToDto() => new() { Path = Path, Title = Title, ArtworkUrl = ArtworkUrl };
}

/// <summary>
/// The in-app player's playlist, with Spotify-style shuffle and repeat (off / playlist / one item).
/// "Shut down when playlist ends" fires once the playlist runs out, which can only happen with repeat off,
/// so the two settings switch each other off.
/// </summary>
public sealed class PlayerViewModel : ObservableObject
{
    private readonly Action _changed;
    private readonly Action<string> _notify;
    private readonly Random _random = new();
    private readonly HashSet<PlaylistItem> _playedThisRound = [];
    private readonly Stack<PlaylistItem> _history = new();
    private int _consecutiveFailures;

    public PlayerViewModel(IEnumerable<PlaylistEntryDto> saved, double volume, Action changed, Action<string> notify)
    {
        _changed = changed;
        _notify = notify;
        Volume = volume;
        foreach (var entry in saved.Where(e => !string.IsNullOrWhiteSpace(e.Path)))
            Items.Add(new PlaylistItem(entry.Path, entry.Title, entry.ArtworkUrl) { Missing = !File.Exists(entry.Path) });
        Renumber();
        foreach (var item in Items) _ = EnsureArtworkAsync(item);

        Items.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
            {
                foreach (PlaylistItem item in e.NewItems) _ = EnsureArtworkAsync(item);
            }
            Renumber();
            OnPropertyChanged(nameof(Count));
            OnPropertyChanged(nameof(PositionText));
            _changed();
        };

        TogglePlayCommand = new RelayCommand(TogglePlay, () => Items.Count > 0);
        NextCommand = new RelayCommand(Next, () => Current is not null && Items.Count > 0);
        PreviousCommand = new RelayCommand(Previous, () => Current is not null);
        ToggleMuteCommand = new RelayCommand(() => IsMuted = !IsMuted);
        ToggleShuffleCommand = new RelayCommand(() => Shuffle = !Shuffle);
        CycleRepeatCommand = new RelayCommand(CycleRepeat);
        ReplayCommand = new RelayCommand(PlayFromStart, () => Items.Count > 0);
        ClearCommand = new RelayCommand(Clear, () => Items.Count > 0);
    }

    public ObservableCollection<PlaylistItem> Items { get; } = [];
    public IMediaHost? Host { get; set; }

    public ICommand TogglePlayCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand ToggleMuteCommand { get; }
    public ICommand ToggleShuffleCommand { get; }
    public ICommand CycleRepeatCommand { get; }
    public ICommand ReplayCommand { get; }
    public ICommand ClearCommand { get; }

    /// <summary>True after the last item played through (or nothing playable was left).</summary>
    public bool IsFinished { get; private set => Set(ref field, value); }

    public event Action? PlaylistFinished;

    public PlaylistItem? Current
    {
        get;
        private set
        {
            if (!Set(ref field, value)) return;
            OnPropertyChanged(nameof(HasCurrent));
            OnPropertyChanged(nameof(PositionText));
            // Playback can move on by itself (next item, end of playlist); refresh Next/Previous right away.
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasCurrent => Current is not null;
    public int Count => Items.Count;

    public string PositionText => Current is { } c
        ? $"{Items.IndexOf(c) + 1} of {Items.Count}"
        : Items.Count == 1 ? "1 item" : $"{Items.Count} items";

    public bool IsPlaying
    {
        get;
        set
        {
            if (Set(ref field, value)) OnPropertyChanged(nameof(PlayGlyph));
        }
    }

    public string PlayGlyph => IsPlaying ? "" : "";

    public double Volume
    {
        get;
        set
        {
            if (!Set(ref field, Math.Clamp(value, 0, 1))) return;
            OnPropertyChanged(nameof(VolumeGlyph));
            _changed?.Invoke();
        }
    }

    public bool IsMuted
    {
        get;
        set
        {
            if (Set(ref field, value)) OnPropertyChanged(nameof(VolumeGlyph));
        }
    }

    public string VolumeGlyph => IsMuted || Volume == 0 ? "" : Volume < 0.34 ? "" : Volume < 0.67 ? "" : "";

    public bool Shuffle
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            _playedThisRound.Clear();
            if (value && Current is { } current) _playedThisRound.Add(current);
            OnPropertyChanged(nameof(ShuffleTip));
            _changed?.Invoke();
        }
    }

    public string ShuffleTip => Shuffle ? "Shuffle: on (S)" : "Shuffle: off (S)";

    public RepeatMode Repeat
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            OnPropertyChanged(nameof(IsRepeatOn));
            OnPropertyChanged(nameof(RepeatGlyph));
            OnPropertyChanged(nameof(RepeatTip));
            if (value != RepeatMode.Off && ShutdownWhenDone)
            {
                ShutdownWhenDone = false;
                _notify("Shut down when playlist ends is off: with repeat on, the playlist never ends");
            }
            _changed?.Invoke();
        }
    }

    public bool IsRepeatOn => Repeat != RepeatMode.Off;
    public string RepeatGlyph => Repeat == RepeatMode.One ? "" : "";

    public string RepeatTip => Repeat switch
    {
        RepeatMode.All => "Repeat: playlist (R)",
        RepeatMode.One => "Repeat: this item (R)",
        _ => "Repeat: off (R)",
    };

    /// <summary>Off at every launch on purpose: shutting the PC down must be a fresh choice.</summary>
    public bool ShutdownWhenDone
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            if (value && Repeat != RepeatMode.Off)
            {
                Repeat = RepeatMode.Off;
                _notify("Repeat turned off so the playlist can end");
            }
        }
    }

    public List<PlaylistItem> AddFiles(IEnumerable<string> paths, int? insertAt = null)
    {
        var added = new List<PlaylistItem>();
        var index = Math.Clamp(insertAt ?? Items.Count, 0, Items.Count);
        foreach (var path in paths)
        {
            if (!MediaFiles.IsMedia(path) || !File.Exists(path) || Find(path) is not null) continue;
            var item = new PlaylistItem(path);
            Items.Insert(index++, item);
            added.Add(item);
        }
        return added;
    }

    public PlaylistItem AddDownloaded(string path, string title, string? artworkUrl)
    {
        if (Find(path) is { } existing) return existing;
        var item = new PlaylistItem(path, title, artworkUrl);
        Items.Add(item);
        return item;
    }

    /// <summary>
    /// Puts a saved playlist in the player. <paramref name="replace"/>: instead of what's queued, and starts
    /// playing it; otherwise added to the end. Files already queued aren't added twice. Returns how many were added.
    /// </summary>
    public int Enqueue(IEnumerable<PlaylistEntryDto> entries, bool replace)
    {
        if (replace) Clear();
        var added = 0;
        foreach (var entry in entries)
        {
            if (Find(entry.Path) is not null) continue;
            Items.Add(new PlaylistItem(entry.Path, entry.Title, entry.ArtworkUrl) { Missing = !File.Exists(entry.Path) });
            added++;
        }
        if (replace) PlayFromStart();
        return added;
    }

    private async Task EnsureArtworkAsync(PlaylistItem item)
    {
        if (await item.LoadArtworkAsync() && Items.Contains(item)) _changed(); // remembered with the playlist
    }

    public void PlayItem(PlaylistItem item) => PlayItem(item, rememberPrevious: true);

    private void PlayItem(PlaylistItem item, bool rememberPrevious)
    {
        if (Current is { } previous)
        {
            previous.IsCurrent = false;
            if (rememberPrevious && previous != item) _history.Push(previous);
        }
        IsFinished = false;
        Current = item;
        item.IsCurrent = true;
        item.Error = null;
        _playedThisRound.Add(item);

        if (!File.Exists(item.Path))
        {
            item.Missing = true;
            OnMediaFailed("The file was moved or deleted.");
            return;
        }

        item.Missing = false;
        IsPlaying = true;
        Host?.Open(item);
    }

    public void TogglePlay()
    {
        if (Current is null || IsFinished)
        {
            PlayFromStart();
            return;
        }
        if (IsPlaying)
        {
            Host?.Pause();
            IsPlaying = false;
        }
        else
        {
            Host?.Play();
            IsPlaying = true;
        }
    }

    /// <summary>Starts the playlist over: the first item, or a random one with shuffle on.</summary>
    public void PlayFromStart()
    {
        _playedThisRound.Clear();
        var first = Shuffle && Items.Count > 0 ? Items[_random.Next(Items.Count)] : Items.FirstOrDefault();
        if (first is not null) PlayItem(first);
    }

    public void Next()
    {
        if (NextItem(wrap: Repeat != RepeatMode.Off) is { } next) PlayItem(next);
    }

    public void Previous()
    {
        if (Current is null || Host is null) return;
        if (Host.Position > TimeSpan.FromSeconds(3))
        {
            Host.Position = TimeSpan.Zero;
            return;
        }

        while (_history.Count > 0)
        {
            var previous = _history.Pop();
            if (Items.Contains(previous))
            {
                PlayItem(previous, rememberPrevious: false);
                return;
            }
        }

        var index = Items.IndexOf(Current);
        if (index > 0) PlayItem(Items[index - 1], rememberPrevious: false);
        else Host.Position = TimeSpan.Zero;
    }

    public void CycleRepeat() => Repeat = Repeat switch
    {
        RepeatMode.Off => RepeatMode.All,
        RepeatMode.All => RepeatMode.One,
        _ => RepeatMode.Off,
    };

    public void OnMediaOpened() => _consecutiveFailures = 0;

    public void OnMediaEnded()
    {
        _consecutiveFailures = 0;
        if (Repeat == RepeatMode.One && Current is not null)
        {
            Host?.Restart();
            return;
        }
        Advance();
    }

    public void OnMediaFailed(string message)
    {
        if (Current is { } item) item.Error = message;
        // If every item fails, stop instead of looping through broken files forever.
        if (++_consecutiveFailures >= Math.Max(1, Items.Count))
        {
            _consecutiveFailures = 0;
            Finish();
            return;
        }
        Advance();
    }

    private void Advance()
    {
        if (NextItem(wrap: Repeat == RepeatMode.All) is { } next) PlayItem(next);
        else Finish();
    }

    private void Finish()
    {
        IsPlaying = false;
        Host?.Stop();
        _playedThisRound.Clear();
        IsFinished = true;
        PlaylistFinished?.Invoke();
    }

    /// <summary>
    /// The item after the current one. Shuffle picks randomly among items not yet played this round;
    /// <paramref name="wrap"/> starts a new round (or goes back to the top) instead of returning null.
    /// </summary>
    private PlaylistItem? NextItem(bool wrap)
    {
        if (Items.Count == 0) return null;

        if (Shuffle)
        {
            var remaining = Items.Where(i => i != Current && !_playedThisRound.Contains(i)).ToList();
            if (remaining.Count == 0)
            {
                if (!wrap) return null;
                _playedThisRound.Clear();
                remaining = Items.Where(i => i != Current).ToList();
                if (remaining.Count == 0) return Current;
            }
            return remaining[_random.Next(remaining.Count)];
        }

        var index = Current is null ? -1 : Items.IndexOf(Current);
        if (index < Items.Count - 1) return Items[index + 1];
        return wrap ? Items[0] : null;
    }

    public void Move(PlaylistItem item, int newIndex)
    {
        var oldIndex = Items.IndexOf(item);
        if (oldIndex < 0 || Items.Count == 0) return;
        newIndex = Math.Clamp(newIndex, 0, Items.Count - 1);
        if (newIndex != oldIndex) Items.Move(oldIndex, newIndex);
    }

    public void Remove(PlaylistItem item)
    {
        if (item == Current)
        {
            Host?.Close();
            IsPlaying = false;
            IsFinished = false;
            Current = null;
        }
        Items.Remove(item);
    }

    public void RemovePath(string path)
    {
        if (Find(path) is { } item) Remove(item);
    }

    public void Clear()
    {
        Host?.Close();
        IsPlaying = false;
        IsFinished = false;
        Current = null;
        _history.Clear();
        _playedThisRound.Clear();
        Items.Clear();
    }

    public List<PlaylistEntryDto> Snapshot() => Items.Select(i => i.ToDto()).ToList();

    private PlaylistItem? Find(string path) =>
        Items.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));

    private void Renumber()
    {
        for (var i = 0; i < Items.Count; i++) Items[i].Number = i + 1;
    }
}
