using System.Runtime.InteropServices.WindowsRuntime;
using SkiaSharp;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;

namespace HaloBar.Services;

/// <summary>
/// A small camera mirror for checking how you look before or during a call. The camera is only
/// on while you've turned it on; frames are shrunk and kept in memory, never saved.
/// </summary>
internal sealed class CameraService : IDisposable
{
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private DateTime _lastFrame;
    private int _busy;

    public bool On { get; private set; }
    public string Status { get; private set; } = "";
    public SKImage? Frame { get; private set; }
    public event Action? Changed;

    public async Task ToggleAsync()
    {
        if (On) await StopAsync();
        else await StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            Status = "Starting camera…";
            Changed?.Invoke();
            var groups = await MediaFrameSourceGroup.FindAllAsync();
            var (group, source) = groups
                .SelectMany(g => g.SourceInfos.Select(s => (Group: g, Source: s)))
                .FirstOrDefault(x => x.Source.SourceKind == MediaFrameSourceKind.Color &&
                                     x.Source.MediaStreamType is Windows.Media.Capture.MediaStreamType.VideoPreview or Windows.Media.Capture.MediaStreamType.VideoRecord);
            if (group == null)
            {
                Status = "No camera found";
                Changed?.Invoke();
                return;
            }
            _capture = new MediaCapture();
            await _capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                SourceGroup = group,
                // Shared so a call app that already has the camera keeps it.
                SharingMode = MediaCaptureSharingMode.SharedReadOnly,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
            });
            _reader = await _capture.CreateFrameReaderAsync(_capture.FrameSources[source.Id], Windows.Media.MediaProperties.MediaEncodingSubtypes.Bgra8);
            _reader.FrameArrived += OnFrame;
            await _reader.StartAsync();
            On = true;
            Status = "";
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Status = ex is UnauthorizedAccessException ? "Windows blocked the camera for desktop apps" : "Camera unavailable";
            Log.Write($"Camera: {ex.Message}");
            await StopAsync();
        }
    }

    private void OnFrame(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        // About 15 frames a second is plenty for a mirror; drop frames while one is being converted.
        if (DateTime.Now - _lastFrame < TimeSpan.FromMilliseconds(66) || Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (bitmap == null) return;
            using var bgra = bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8 && bitmap.BitmapAlphaMode != BitmapAlphaMode.Straight
                ? SoftwareBitmap.Copy(bitmap)
                : SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var w = bgra.PixelWidth;
            var h = bgra.PixelHeight;
            var buffer = new Windows.Storage.Streams.Buffer((uint)(w * h * 4));
            bgra.CopyToBuffer(buffer);
            var bytes = buffer.ToArray();
            using var full = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
            System.Runtime.InteropServices.Marshal.Copy(bytes, 0, full.GetPixels(), Math.Min(bytes.Length, full.ByteCount));
            using var image = SKImage.FromBitmap(full);
            Frame = Rendering.ImageTools.Shrink(image, 360);
            _lastFrame = DateTime.Now;
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Write($"Camera frame: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    public async Task StopAsync()
    {
        On = false;
        if (_reader != null)
        {
            _reader.FrameArrived -= OnFrame;
            try { await _reader.StopAsync(); } catch { }
            _reader.Dispose();
            _reader = null;
        }
        _capture?.Dispose();
        _capture = null;
        Frame = null;
        Changed?.Invoke();
    }

    public void Dispose() => _ = StopAsync();
}
