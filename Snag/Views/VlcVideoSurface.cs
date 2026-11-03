using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Snag.Services;
using VlcPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Snag.Views;

/// <summary>
/// Gets VLC's decoded frames into WPF. libvlc writes each frame into shared memory that also backs a
/// WPF <see cref="InteropBitmap"/>, so the video is an ordinary image in the window: overlays, rounded
/// corners, full screen and drag-and-drop all work on top of it, with no separate native video window.
///
/// Rules this class sticks to:
/// • Every VLC video output gets its own buffer, identified by the "opaque" value VLC hands back to each
///   callback. When switching files the old output can still be closing while the new one starts, so
///   nothing is shared between them.
/// • Callbacks run on VLC's threads and never wait on the UI thread (BeginInvoke only), so stopping
///   playback from the UI can't deadlock.
/// • Buffers are only freed after VLC says it's done with them (cleanup), on the UI thread.
/// </summary>
internal sealed class VlcVideoSurface
{
    private static readonly byte[] Rv32 = "RV32"u8.ToArray(); // 32-bit BGRX, which is WPF's Bgr32

    private readonly Dispatcher _dispatcher;
    private readonly Action<BitmapSource?> _show;
    private readonly int _maxWidth;
    private readonly int _maxHeight;
    private readonly ConcurrentDictionary<IntPtr, FrameBuffer> _buffers = new();
    private long _nextId;

    // Kept in fields so the garbage collector never frees a callback VLC still holds.
    private readonly VlcPlayer.LibVLCVideoFormatCb _formatCb;
    private readonly VlcPlayer.LibVLCVideoCleanupCb _cleanupCb;
    private readonly VlcPlayer.LibVLCVideoLockCb _lockCb;
    private readonly VlcPlayer.LibVLCVideoDisplayCb _displayCb;

    private volatile FrameBuffer? _latest; // the newest output's buffer: the one on screen
    private int _refreshQueued;

    /// <param name="show">Called on the UI thread with the image to display (null when there's no video).</param>
    /// <param name="maxWidth">Frames bigger than this (in pixels) are scaled down by VLC; there's no point copying 4K to a 1080p screen.</param>
    public VlcVideoSurface(Dispatcher dispatcher, Action<BitmapSource?> show, int maxWidth, int maxHeight)
    {
        _dispatcher = dispatcher;
        _show = show;
        _maxWidth = Math.Max(320, maxWidth);
        _maxHeight = Math.Max(240, maxHeight);
        _formatCb = OnFormat;
        _cleanupCb = OnCleanup;
        _lockCb = OnLock;
        _displayCb = OnDisplay;
    }

    /// <summary>True while new frames should be drawn (false while the Player page is hidden).</summary>
    public bool IsOnScreen { get; set; } = true;

    /// <summary>UI thread: redraw the latest frame now (e.g. after coming back to the Player page while paused).</summary>
    public void Refresh() => _latest?.Bitmap?.Invalidate();

    public void Attach(VlcPlayer player)
    {
        player.SetVideoFormatCallbacks(_formatCb, _cleanupCb);
        player.SetVideoCallbacks(_lockCb, null, _displayCb);
    }

    private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        try
        {
            // Scale big videos down to the screen, keeping the shape; even sizes keep the converters happy.
            var scale = Math.Min(1.0, Math.Min(_maxWidth / (double)width, _maxHeight / (double)height));
            var w = Math.Max(2u, (uint)(width * scale) & ~1u);
            var h = Math.Max(2u, (uint)(height * scale) & ~1u);

            // VLC's converters work in 32-byte blocks and may write past the end of a row or the last
            // line, so the row size and line count are rounded up to 32 (as VLC recommends).
            var pitch = Align32(w * 4);
            var rows = Align32(h);

            Marshal.Copy(Rv32, 0, chroma, Rv32.Length);
            width = w;
            height = h;
            pitches = pitch;
            lines = rows;

            // If this output is being reformatted, its old buffer is released once the new one is up.
            _buffers.TryRemove(opaque, out var replaced);

            var buffer = new FrameBuffer((int)w, (int)h, (int)pitch, (int)rows);
            var id = new IntPtr(Interlocked.Increment(ref _nextId));
            _buffers[id] = buffer;
            opaque = id;
            _latest = buffer;

            _dispatcher.BeginInvoke(() =>
            {
                if (_latest == buffer) _show(buffer.CreateBitmap());
                replaced?.Dispose();
            });
            return 1;
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't set up the video surface", ex);
            return 0; // tells VLC there's nowhere to draw; it keeps playing the sound
        }
    }

    private void OnCleanup(ref IntPtr opaque)
    {
        if (!_buffers.TryRemove(opaque, out var buffer)) return;
        _dispatcher.BeginInvoke(() =>
        {
            // Only clear the picture if no newer video has taken over the screen.
            if (_latest == buffer)
            {
                _latest = null;
                _show(null);
            }
            buffer.Dispose();
        });
    }

    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        // Each output only ever draws into its own buffer, which stays alive until its cleanup.
        _buffers.TryGetValue(opaque, out var buffer);
        Marshal.WriteIntPtr(planes, buffer?.Pixels ?? IntPtr.Zero);
        return IntPtr.Zero;
    }

    private void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        if (!IsOnScreen || !_buffers.TryGetValue(opaque, out var buffer) || buffer != _latest) return;
        // One redraw queued at a time: if the UI is busy, newer frames simply replace older ones.
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (buffer == _latest) buffer.Bitmap?.Invalidate();
        });
    }

    private static uint Align32(uint value) => (value + 31) & ~31u;

    /// <summary>A block of shared memory sized for one frame, plus the WPF bitmap that reads it.</summary>
    private sealed class FrameBuffer : IDisposable
    {
        private const int Slack = 64 * 1024; // extra room at the end, in case a converter overshoots anyway

        private readonly int _width;
        private readonly int _height;
        private readonly int _pitch;
        private IntPtr _section;

        public FrameBuffer(int width, int height, int pitch, int rows)
        {
            _width = width;
            _height = height;
            _pitch = pitch;
            var size = (uint)(pitch * rows + Slack);
            _section = CreateFileMapping(new IntPtr(-1), IntPtr.Zero, PageReadWrite, 0, size, null);
            if (_section == IntPtr.Zero) throw new InvalidOperationException("Couldn't allocate video memory.");
            Pixels = MapViewOfFile(_section, FileMapAllAccess, 0, 0, (UIntPtr)size);
            if (Pixels == IntPtr.Zero)
            {
                CloseHandle(_section);
                throw new InvalidOperationException("Couldn't map video memory.");
            }
        }

        public IntPtr Pixels { get; private set; }
        public InteropBitmap? Bitmap { get; private set; }

        /// <summary>UI thread only.</summary>
        public InteropBitmap? CreateBitmap()
        {
            if (_section == IntPtr.Zero) return null;
            Bitmap = (InteropBitmap)Imaging.CreateBitmapSourceFromMemorySection(_section, _width, _height, PixelFormats.Bgr32, _pitch, 0);
            return Bitmap;
        }

        /// <summary>UI thread only, and only after VLC has finished with this buffer.</summary>
        public void Dispose()
        {
            // The WPF bitmap keeps its own view of the memory, so it's safe to let go of ours.
            if (Pixels != IntPtr.Zero) UnmapViewOfFile(Pixels);
            if (_section != IntPtr.Zero) CloseHandle(_section);
            Pixels = IntPtr.Zero;
            _section = IntPtr.Zero;
        }

        private const uint PageReadWrite = 0x04;
        private const uint FileMapAllAccess = 0xF001F;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileMapping(IntPtr file, IntPtr attributes, uint protect, uint sizeHigh, uint sizeLow, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr MapViewOfFile(IntPtr section, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UnmapViewOfFile(IntPtr address);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
