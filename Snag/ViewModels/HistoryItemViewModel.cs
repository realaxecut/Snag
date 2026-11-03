using Snag.Services;

namespace Snag.ViewModels;

public sealed class HistoryItemViewModel(HistoryEntry entry) : ObservableObject
{
    public HistoryEntry Entry { get; } = entry;
    public string Title => Entry.Title;
    public string FilePath => Entry.FilePath;
    public string? ThumbnailUrl => Entry.ThumbnailUrl;
    public string Badge => Entry.Badge;
    public bool IsAudio => Entry.IsAudio;

    public string Details
    {
        get
        {
            var when = Entry.DownloadedUtc.ToLocalTime();
            var date = when.Date == DateTime.Today ? $"Today {when:t}"
                : when.Date == DateTime.Today.AddDays(-1) ? $"Yesterday {when:t}"
                : when.ToString("MMM d, yyyy");
            var folder = Path.GetFileName(Path.GetDirectoryName(Entry.FilePath)) ?? "";
            return $"{date}   ·   {Fmt.Bytes(Entry.SizeBytes)}   ·   {folder}";
        }
    }

    public bool Exists
    {
        get;
        private set => Set(ref field, value);
    } = File.Exists(entry.FilePath);

    public void Refresh() => Exists = File.Exists(Entry.FilePath);

    public bool Matches(string query) =>
        query.Length == 0 ||
        Entry.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        Entry.FilePath.Contains(query, StringComparison.CurrentCultureIgnoreCase);
}
