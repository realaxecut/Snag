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

    public const string UntitledStream = "YouTube video";

    /// <summary>
    /// <paramref name="path"/> is a file on the PC, or (for a video streamed from YouTube without downloading
    /// it) its canonical watch link; see <see cref="UrlGuard.CanonicalVideoUrl"/>.
    /// </summary>
    public PlaylistItem(string path, string? title = null, string? artworkUrl = null)
    {
        Path = path;
        IsStream = IsStreamPath(path);
        ArtworkUrl = artworkUrl;
        if (IsStream)
        {
            Title = string.IsNullOrWhiteSpace(title) ? UntitledStream : title;
            Kind = "Streaming from YouTube";
            if (artworkUrl is null && UrlGuard.VideoIdOf(new Uri(path)) is { } id) ArtworkUrl = UrlGuard.ThumbnailFor(id);
            return;
        }
        Title = string.IsNullOrWhiteSpace(title) ? System.IO.Path.GetFileNameWithoutExtension(path) : title;
        var extension = System.IO.Path.GetExtension(path);
        IsAudio = AudioExtensions.Contains(extension);
        Kind = $"{(IsAudio ? "Audio" : "Video")} · {extension.TrimStart('.').ToUpperInvariant()}";
    }

    /// <summary>A canonical YouTube watch link (as opposed to a file path).</summary>
    public static bool IsStreamPath(string? path) =>
        path is not null && path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && UrlGuard.CanonicalVideoUrl(path) == path;

    /// <summary>Streams are always "there"; files have to still exist.</summary>
    public static bool IsAvailable(string path) => IsStreamPath(path) || File.Exists(path);

    public string Path { get; }

    /// <summary>Streams start with what the YouTube tab knew and get the real title once it's looked up.</summary>
    public string Title { get; set => Set(ref field, value); }

    /// <summary>Played straight from YouTube, not from a file.</summary>
    public bool IsStream { get; }

    public string ErrorHeading => IsStream ? "Couldn't play this video" : "Couldn't play this file";

    /// <summary>The "where is it" action: a file is shown in Explorer, a stream opens in the YouTube tab.</summary>
    public string LocateText => IsStream ? "Open in YouTube tab" : "Show in folder";

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
        if (Missing || IsStream) return false;
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
        foreach (var entry in saved.Where(IsUsable))
            Items.Add(new PlaylistItem(entry.Path, entry.Title, entry.ArtworkUrl) { Missing = !PlaylistItem.IsAvailable(entry.Path) });
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
        foreach (var entry in entries.Where(IsUsable))
        {
            if (Find(entry.Path) is not null) continue;
            Items.Add(new PlaylistItem(entry.Path, entry.Title, entry.ArtworkUrl) { Missing = !PlaylistItem.IsAvailable(entry.Path) });
            added++;
        }
        if (replace) PlayFromStart();
        return added;
    }

    /// <summary>A local path, or a canonical YouTube link; anything else from disk is dropped.</summary>
    private static bool IsUsable(PlaylistEntryDto entry) =>
        !string.IsNullOrWhiteSpace(entry.Path) && (PlaylistItem.IsStreamPath(entry.Path) || Path.IsPathFullyQualified(entry.Path));

    /// <summary>
    /// A YouTube video to stream (nothing is downloaded). <paramref name="afterCurrent"/> puts it right after
    /// what's playing; otherwise at the end. A video already queued is returned as it is.
    /// </summary>
    public PlaylistItem AddStream(string canonicalUrl, string? title, string? artworkUrl, bool afterCurrent)
    {
        if (Find(canonicalUrl) is { } existing) return existing;
        var item = new PlaylistItem(canonicalUrl, title, artworkUrl);
        var index = afterCurrent && Current is { } current ? Items.IndexOf(current) + 1 : Items.Count;
        Items.Insert(Math.Clamp(index, 0, Items.Count), item);
        return item;
    }

    /// <summary>Gives a stream its real title once it's known (and remembers it with the queue).</summary>
    public void SetTitle(PlaylistItem item, string title)
    {
        if (string.IsNullOrWhiteSpace(title) || item.Title == title) return;
        item.Title = title;
        if (Items.Contains(item)) _changed();
    }

    public bool HasUntitledStream(string canonicalUrl) =>
        Find(canonicalUrl) is { IsStream: true, Title: PlaylistItem.UntitledStream };

    public PlaylistItem? FindStream(string canonicalUrl) => Find(canonicalUrl) is { IsStream: true } item ? item : null;

    /// <summary>The item that plays after this one if it's a streamed video (with shuffle on there's no telling, so none).</summary>
    public PlaylistItem? UpcomingStream()
    {
        if (Shuffle || Current is null || Repeat == RepeatMode.One) return null;
        var index = Items.IndexOf(Current);
        var next = index >= 0 && index + 1 < Items.Count ? Items[index + 1]
            : Repeat == RepeatMode.All && Items.Count > 1 ? Items[0]
            : null;
        return next is { IsStream: true, Missing: false } ? next : null;
    }

    private readonly Dictionary<PlaylistItem, PlaylistItem> _pendingSwaps = [];

    /// <summary>
    /// A streamed video was downloaded: the queue gets the file in the stream's place. If the stream is playing
    /// right now it's swapped as soon as playback moves on, so nothing is interrupted. False if it isn't queued.
    /// </summary>
    public bool SwapStreamForFile(string canonicalUrl, string path, string title, string? artworkUrl)
    {
        if (FindStream(canonicalUrl) is not { } stream) return false;
        if (Find(path) is not null)
        {
            if (stream != Current) Items.Remove(stream);
            return true;
        }
        var file = new PlaylistItem(path, title, artworkUrl);
        if (stream == Current) _pendingSwaps[stream] = file;
        else Items[Items.IndexOf(stream)] = file;
        return true;
    }

    private void ApplyPendingSwap(PlaylistItem? leaving)
    {
        if (leaving is null || !_pendingSwaps.Remove(leaving, out var file)) return;
        var index = Items.IndexOf(leaving);
        if (index >= 0 && Find(file.Path) is null) Items[index] = file;
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
            if (previous != item) ApplyPendingSwap(previous);
        }
        IsFinished = false;
        Current = item;
        item.IsCurrent = true;
        item.Error = null;
        _playedThisRound.Add(item);

        if (!PlaylistItem.IsAvailable(item.Path))
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
        _pendingSwaps.Remove(item);
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
        _pendingSwaps.Clear();
        Items.Clear();
    }

    public List<PlaylistEntryDto> Snapshot() => Items.Select(i => i.ToDto()).ToList();

    private PlaylistItem? Find(string path)
    {
        // YouTube video IDs are case-sensitive; Windows paths aren't.
        var comparison = PlaylistItem.IsStreamPath(path) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return Items.FirstOrDefault(i => string.Equals(i.Path, path, comparison));
    }

    private void Renumber()
    {
        for (var i = 0; i < Items.Count; i++) Items[i].Number = i + 1;
    }
}
