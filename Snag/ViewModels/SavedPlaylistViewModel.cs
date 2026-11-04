using System.Collections.ObjectModel;
using Snag.Services;

namespace Snag.ViewModels;

/// <summary>One saved playlist: its name and its videos/songs, in order.</summary>
public sealed class SavedPlaylistViewModel : ObservableObject
{
    private readonly Action _changed;

    public SavedPlaylistViewModel(SavedPlaylistDto dto, Action changed)
    {
        _changed = changed;
        Id = dto.Id;
        CreatedUtc = dto.CreatedUtc;
        Name = dto.Name;
        foreach (var entry in dto.Items)
            Items.Add(new PlaylistItem(entry.Path, entry.Title, entry.ArtworkUrl) { Missing = !File.Exists(entry.Path) });
        Renumber();
        foreach (var item in Items) _ = LoadArtworkAsync(item);

        Items.CollectionChanged += (_, _) =>
        {
            Renumber();
            RaiseSummary();
            _changed();
        };
    }

    public string Id { get; }
    public DateTime CreatedUtc { get; }
    public ObservableCollection<PlaylistItem> Items { get; } = [];

    /// <summary>As typed; cleaned up (and never empty) when saved.</summary>
    public string Name
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            OnPropertyChanged(nameof(DisplayName));
            _changed();
        }
    } = "";

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "Untitled playlist" : Name;
    public int Count => Items.Count;
    public bool IsEmpty => Items.Count == 0;

    public string Summary
    {
        get
        {
            var text = Count == 1 ? "1 item" : $"{Count} items";
            var missing = Items.Count(i => i.Missing);
            return missing > 0 ? $"{text}  ·  {missing} not found" : text;
        }
    }

    /// <summary>The first picture in the playlist, for its tile.</summary>
    public string? CoverArt => Items.Select(i => i.ArtworkUrl).FirstOrDefault(a => a is not null);

    /// <summary>Adds files (skipping ones already in it) and returns how many were new.</summary>
    public int Add(IEnumerable<PlaylistEntryDto> entries)
    {
        var added = 0;
        foreach (var entry in entries)
        {
            if (Items.Count >= PlaylistStore.MaxItems) break;
            if (Contains(entry.Path)) continue;
            var item = new PlaylistItem(entry.Path, entry.Title, entry.ArtworkUrl) { Missing = !File.Exists(entry.Path) };
            Items.Add(item);
            _ = LoadArtworkAsync(item);
            added++;
        }
        return added;
    }

    public bool Contains(string path) => Items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));

    public void Move(PlaylistItem item, int offset)
    {
        var from = Items.IndexOf(item);
        var to = from + offset;
        if (from < 0 || to < 0 || to >= Items.Count) return;
        Items.Move(from, to);
    }

    public SavedPlaylistDto ToDto() => new()
    {
        Id = Id,
        Name = PlaylistStore.CleanName(Name),
        CreatedUtc = CreatedUtc,
        Items = Items.Select(i => i.ToDto()).ToList(),
    };

    private async Task LoadArtworkAsync(PlaylistItem item)
    {
        if (!await item.LoadArtworkAsync() || !Items.Contains(item)) return;
        OnPropertyChanged(nameof(CoverArt));
        _changed(); // remembered with the playlist
    }

    private void Renumber()
    {
        for (var i = 0; i < Items.Count; i++) Items[i].Number = i + 1;
    }

    private void RaiseSummary()
    {
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(CoverArt));
    }
}
