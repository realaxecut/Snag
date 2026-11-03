using Snag.Services;

namespace Snag.ViewModels;

/// <summary>A video inside a YouTube playlist the user is choosing from (download side, not the player).</summary>
public sealed class PlaylistEntryViewModel(PlaylistVideo video, Action onSelectionChanged) : ObservableObject
{
    public PlaylistVideo Video { get; } = video;
    public int Index => Video.Index;
    public string Title => Video.Title;
    public string? Channel => Video.Channel;
    public string? DurationText => Video.Duration is { } d ? Fmt.Duration(d) : null;
    public string? ThumbnailUrl => Video.ThumbnailUrl;
    public bool Unavailable => Video.Unavailable;

    public bool IsSelected
    {
        get;
        set
        {
            if (Set(ref field, value && !Unavailable)) onSelectionChanged();
        }
    } = !video.Unavailable;
}
