using Snag.Services;

namespace Snag.ViewModels;

public enum DownloadState { Queued, Downloading, Processing, Done, Failed, Canceled }

/// <summary>One row in the download queue. yt-dlp's machine-readable output lands here.</summary>
public sealed class DownloadItemViewModel : ObservableObject, IDownloadSink
{
    private long _lastProgressTick;
    private bool _twoStreams;
    private int _streamIndex;

    public DownloadItemViewModel(DownloadJob job, string title, string? channel, string? thumbnailUrl)
    {
        Job = job;
        Title = title;
        Channel = channel;
        ThumbnailUrl = thumbnailUrl;
        Badge = job.Options.AudioOnly
            ? "MP3 · " + (job.Options.AudioQuality == "best" ? "Best" : job.Options.AudioQuality + " kbps")
            : "MP4 · " + (job.Options.VideoQuality == "best" ? "Best" : job.Options.VideoQuality + "p") +
              (job.Options.Codec is VideoCodec.Auto ? "" : " · " + CodecSupport.DisplayName(job.Options.Codec));
        StatusText = "Waiting…";
    }

    public DownloadJob Job { get; }
    public string Title { get; }
    public string? Channel { get; }
    public string? ThumbnailUrl { get; }
    public bool IsAudio => Job.Options.AudioOnly;

    public string Badge { get; private set => Set(ref field, value); }

    public DownloadState State
    {
        get;
        private set
        {
            if (!Set(ref field, value)) return;
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(CanRetry));
            OnPropertyChanged(nameof(IsDone));
            OnPropertyChanged(nameof(IsFinished));
        }
    }

    public bool IsActive => State is DownloadState.Downloading or DownloadState.Processing;
    public bool CanCancel => State is DownloadState.Queued or DownloadState.Downloading or DownloadState.Processing;
    public bool CanRetry => State is DownloadState.Failed or DownloadState.Canceled;
    public bool IsDone => State == DownloadState.Done;
    public bool IsFinished => State is DownloadState.Done or DownloadState.Failed or DownloadState.Canceled;

    public double Progress { get; private set => Set(ref field, value); }
    public bool IsIndeterminate { get; private set => Set(ref field, value); }
    public string StatusText { get; private set => Set(ref field, value); }
    public string? DetailText { get; private set => Set(ref field, value); }
    public string? FilePath { get; private set => Set(ref field, value); }

    internal CancellationTokenSource? Cts { get; set; }

    public void Begin()
    {
        _twoStreams = false;
        _streamIndex = 0;
        FilePath = null;
        Progress = 0;
        IsIndeterminate = true;
        StatusText = "Starting…";
        DetailText = null;
        State = DownloadState.Downloading;
    }

    public void Complete()
    {
        IsIndeterminate = false;
        Progress = 100;
        DetailText = null;
        StatusText = FilePath is { } path && File.Exists(path) ? $"Saved · {Fmt.Bytes(new FileInfo(path).Length)}" : "Saved";
        State = DownloadState.Done;
    }

    public void Fail(string message)
    {
        IsIndeterminate = false;
        StatusText = message;
        DetailText = null;
        State = DownloadState.Failed;
    }

    public void MarkCanceled()
    {
        IsIndeterminate = false;
        StatusText = "Canceled";
        DetailText = null;
        State = DownloadState.Canceled;
    }

    public void Requeue()
    {
        Progress = 0;
        IsIndeterminate = false;
        StatusText = "Waiting…";
        DetailText = null;
        State = DownloadState.Queued;
    }

    void IDownloadSink.OnFormat(string formatId, int? height, int? width, int? fps, string? vcodec)
    {
        _twoStreams = formatId.Contains('+');
        if (IsAudio || height is not > 0) return;
        var res = width is > 0 ? Math.Min(width.Value, height.Value) : height.Value;
        var codec = CodecSupport.Family(vcodec);
        Badge = $"MP4 · {res}p{(fps > 30 ? fps.ToString() : "")}{(codec is VideoCodec.Other ? "" : " · " + CodecSupport.DisplayName(codec))}";
    }

    void IDownloadSink.OnProgress(DownloadProgress p)
    {
        if (p.Status == "finished")
        {
            _streamIndex++;
            return;
        }
        if (p.Status != "downloading") return;

        var now = Environment.TickCount64;
        if (now - _lastProgressTick < 120) return;
        _lastProgressTick = now;

        double? fraction = p.Total > 0 && p.Downloaded is { } got ? Math.Clamp(got / p.Total.Value, 0, 1) : null;
        var overall = fraction ?? 0;
        // Video and audio download one after the other; the video is the bulk of the bytes.
        if (_twoStreams) overall = _streamIndex == 0 ? overall * 0.85 : 0.85 + overall * 0.15;

        State = DownloadState.Downloading;
        IsIndeterminate = fraction is null;
        Progress = Math.Max(Progress, overall * 100);

        var what = !_twoStreams ? "Downloading" : p.IsAudioStream ? "Downloading audio" : "Downloading video";
        StatusText = fraction is { } f ? $"{what} · {f * 100:0}%" : what;

        var parts = new List<string>(3);
        if (p.Downloaded is { } bytes) parts.Add(p.Total is { } total ? $"{Fmt.Bytes(bytes)} of {Fmt.Bytes(total)}" : Fmt.Bytes(bytes));
        if (p.Speed is > 0) parts.Add($"{Fmt.Bytes(p.Speed.Value)}/s");
        if (p.Eta is > 0) parts.Add($"{Fmt.Duration(p.Eta.Value)} left");
        DetailText = string.Join("   ·   ", parts);
    }

    void IDownloadSink.OnPostProcess(string status, string processor)
    {
        if (status != "started") return;

        // These two run before the download starts, so they don't move the item into "processing".
        if (processor == "SponsorBlock")
        {
            StatusText = "Checking SponsorBlock…";
            return;
        }
        if (processor == "ThumbnailsConvertor") return;

        var label = processor switch
        {
            "Merger" => "Merging video and audio…",
            "ExtractAudio" => "Converting to MP3…",
            "VideoRemuxer" or "VideoConvertor" => "Finalizing MP4…",
            "ModifyChapters" => "Cutting sponsor segments…",
            "EmbedSubtitle" => "Embedding subtitles…",
            "Metadata" => "Writing metadata…",
            "EmbedThumbnail" => "Adding cover art…",
            "MoveFiles" => "Finishing up…",
            _ => "Processing…",
        };
        State = DownloadState.Processing;
        IsIndeterminate = true;
        StatusText = label;
        DetailText = null;
    }

    void IDownloadSink.OnFile(string path) => FilePath = path;
}
