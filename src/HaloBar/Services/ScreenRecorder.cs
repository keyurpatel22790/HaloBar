using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace HaloBar.Services;

/// <summary>
/// Records a screen to an MP4 (H.264 video, AAC sound). Frames come from Windows.Graphics.Capture
/// on a D3D11 device of its own (not the one drawing the island), are copied to memory, and go to
/// Media Foundation's sink writer, which picks the GPU encoder when there is one. Sound is what
/// the PC is playing (WASAPI loopback); gaps while nothing plays are filled with silence so the
/// sound stays in step with the picture.
/// </summary>
internal sealed class ScreenRecorder : IDisposable
{
    private const int Fps = 30;
    private readonly object _gate = new();
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Texture2D? _staging;
    private IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private IMFSinkWriter? _writer;
    private WasapiLoopbackCapture? _loopback;
    private int _videoStream, _audioStream = -1;
    private int _width, _height, _audioRate, _audioChannels;
    private long _audioFrames, _lastVideoTicks = -1;
    private readonly Stopwatch _clock = new();
    private Timer? _silence;

    public bool Recording { get; private set; }
    public DateTime Since { get; private set; }
    public string? File { get; private set; }
    public event Action? Changed;

    /// <summary>Starts recording <paramref name="monitor"/> into a new file in Videos\HaloBar. Returns an error, or null.</summary>
    public string? Start(IntPtr monitor, bool sound)
    {
        if (Recording) return null;
        try
        {
            MediaFactory.MFStartup().CheckError();
            CreateDevice();
            _item = CaptureItemForMonitor(monitor);
            _width = _item.Size.Width & ~1; // encoders want even sizes
            _height = _item.Size.Height & ~1;

            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "HaloBar");
            Directory.CreateDirectory(folder);
            File = Path.Combine(folder, $"HaloBar {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");
            CreateWriter(sound);

            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice!, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _item.Size);
            _pool.FrameArrived += OnFrame;
            _session = _pool.CreateCaptureSession(_item);
            _session.IsCursorCaptureEnabled = true;

            _writer!.BeginWriting();
            _clock.Restart();
            _session.StartCapture();
            _loopback?.StartRecording();
            if (_loopback != null) _silence = new Timer(_ => FillSilence(), null, 100, 100);
            Recording = true;
            Since = DateTime.Now;
            Log.Write($"Recorder: recording {_width}x{_height} to {File}");
            Changed?.Invoke();
            return null;
        }
        catch (Exception ex)
        {
            Log.Write($"Recorder: couldn't start: {ex}");
            Cleanup(deleteFile: true);
            return ex.Message;
        }
    }

    /// <summary>Stops and finishes the file. Returns its path, or null if nothing was saved.</summary>
    public string? Stop()
    {
        if (!Recording) return null;
        Recording = false;
        _session?.Dispose();
        _session = null;
        _loopback?.StopRecording();
        _silence?.Dispose();
        _silence = null;
        string? saved = null;
        lock (_gate)
        {
            try
            {
                if (_lastVideoTicks >= 0)
                {
                    _writer?.Finalize();
                    saved = File;
                }
            }
            catch (Exception ex)
            {
                Log.Write($"Recorder: couldn't finish the file: {ex.Message}");
            }
        }
        Cleanup(deleteFile: saved == null);
        Log.Write(saved != null ? $"Recorder: saved {saved}" : "Recorder: nothing recorded");
        Changed?.Invoke();
        return saved;
    }

    // ───────────────────────────── video ─────────────────────────────

    private void CreateDevice()
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out _device, out _context).CheckError();
        using var dxgi = _device!.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var inspectable));
        _winrtDevice = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        Marshal.Release(inspectable);
        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)(_item?.Size.Width ?? 1), Height = 1, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
        });
    }

    private void OnFrame(Direct3D11CaptureFramePool pool, object _)
    {
        using var frame = pool.TryGetNextFrame();
        if (frame == null || !Recording) return;
        var now = _clock.Elapsed.Ticks; // 100 ns units, as Media Foundation counts
        if (_lastVideoTicks >= 0 && now - _lastVideoTicks < TimeSpan.TicksPerSecond / Fps) return;
        lock (_gate)
        {
            if (_writer == null) return;
            try
            {
                using var texture = TextureOf(frame.Surface);
                var desc = texture.Description;
                if (_staging == null || _staging.Description.Width != desc.Width || _staging.Description.Height != desc.Height)
                {
                    _staging?.Dispose();
                    _staging = _device!.CreateTexture2D(desc with
                    {
                        Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read, BindFlags = BindFlags.None,
                        MiscFlags = ResourceOptionFlags.None, MipLevels = 1, ArraySize = 1,
                    });
                }
                _context!.CopyResource(_staging, texture);
                var mapped = _context.Map(_staging, 0, MapMode.Read);
                var stride = _width * 4;
                using var buffer = MediaFactory.MFCreateMemoryBuffer(stride * _height);
                buffer.Lock(out var dest, out int _, out int _);
                var rows = Math.Min(_height, (int)desc.Height);
                var bytes = Math.Min(stride, (int)desc.Width * 4);
                unsafe
                {
                    for (var y = 0; y < rows; y++)
                        Buffer.MemoryCopy((byte*)mapped.DataPointer + y * mapped.RowPitch, (byte*)dest + y * stride, stride, bytes);
                }
                buffer.Unlock();
                buffer.CurrentLength = stride * _height;
                _context.Unmap(_staging, 0);

                using var sample = MediaFactory.MFCreateSample();
                sample.AddBuffer(buffer);
                sample.SampleTime = now;
                sample.SampleDuration = TimeSpan.TicksPerSecond / Fps;
                _writer.WriteSample(_videoStream, sample);
                _lastVideoTicks = now;
            }
            catch (Exception ex)
            {
                Log.Write($"Recorder: frame dropped: {ex.Message}");
            }
        }
    }

    // ───────────────────────────── sound ─────────────────────────────

    private void OnSound(object? sender, WaveInEventArgs e)
    {
        if (!Recording || e.BytesRecorded == 0) return;
        var format = _loopback!.WaveFormat;
        var frames = e.BytesRecorded / format.BlockAlign;
        var pcm = new short[frames * 2];
        // Loopback hands over 32-bit float; the AAC encoder takes 16-bit stereo.
        for (var i = 0; i < frames; i++)
        for (var c = 0; c < 2; c++)
        {
            var channel = Math.Min(c, format.Channels - 1);
            var value = BitConverter.ToSingle(e.Buffer, i * format.BlockAlign + channel * 4);
            pcm[i * 2 + c] = (short)Math.Clamp(value * 32767f, short.MinValue, short.MaxValue);
        }
        WriteSound(pcm, frames);
    }

    /// <summary>Loopback sends nothing while the PC is silent; write silence up to now so sound and picture stay in step.</summary>
    private void FillSilence()
    {
        if (!Recording) return;
        var expected = (long)(_clock.Elapsed.TotalSeconds * _audioRate);
        var behind = expected - Interlocked.Read(ref _audioFrames) - _audioRate / 20; // allow 50 ms for data in flight
        if (behind <= 0) return;
        WriteSound(new short[behind * 2], (int)behind);
    }

    private void WriteSound(short[] pcm, int frames)
    {
        lock (_gate)
        {
            if (_writer == null || _audioStream < 0) return;
            try
            {
                var bytes = frames * 4;
                using var buffer = MediaFactory.MFCreateMemoryBuffer(bytes);
                buffer.Lock(out var dest, out int _, out int _);
                Marshal.Copy(pcm, 0, dest, frames * 2);
                buffer.Unlock();
                buffer.CurrentLength = bytes;
                using var sample = MediaFactory.MFCreateSample();
                sample.AddBuffer(buffer);
                sample.SampleTime = _audioFrames * TimeSpan.TicksPerSecond / _audioRate;
                sample.SampleDuration = frames * TimeSpan.TicksPerSecond / _audioRate;
                _writer.WriteSample(_audioStream, sample);
                Interlocked.Add(ref _audioFrames, frames);
            }
            catch (Exception ex)
            {
                Log.Write($"Recorder: sound dropped: {ex.Message}");
            }
        }
    }

    // ───────────────────────────── file ─────────────────────────────

    private void CreateWriter(bool sound)
    {
        using var attributes = MediaFactory.MFCreateAttributes(2);
        attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u);
        _writer = MediaFactory.MFCreateSinkWriterFromURL(File!, null, attributes);

        static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;
        using (var output = MediaFactory.MFCreateMediaType())
        {
            output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            output.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)Math.Clamp(_width * _height * Fps / 8, 2_000_000, 16_000_000));
            output.Set(MediaTypeAttributeKeys.InterlaceMode, 2u); // progressive
            output.Set(MediaTypeAttributeKeys.FrameSize, Pack(_width, _height));
            output.Set(MediaTypeAttributeKeys.FrameRate, Pack(Fps, 1));
            output.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            _videoStream = _writer.AddStream(output);
        }
        using (var input = MediaFactory.MFCreateMediaType())
        {
            input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
            input.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
            input.Set(MediaTypeAttributeKeys.FrameSize, Pack(_width, _height));
            input.Set(MediaTypeAttributeKeys.FrameRate, Pack(Fps, 1));
            input.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            input.Set(MediaTypeAttributeKeys.DefaultStride, (uint)(_width * 4)); // top-down rows
            _writer.SetInputMediaType(_videoStream, input, null);
        }
        if (!sound) return;

        try
        {
            _loopback = new WasapiLoopbackCapture();
            _audioRate = _loopback.WaveFormat.SampleRate is 44100 or 48000 ? _loopback.WaveFormat.SampleRate : 48000;
            if (_loopback.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat && _loopback.WaveFormat.Encoding != WaveFormatEncoding.Extensible
                || _loopback.WaveFormat.BitsPerSample != 32 || _audioRate != _loopback.WaveFormat.SampleRate)
                throw new NotSupportedException($"sound format {_loopback.WaveFormat}");
            _audioChannels = 2;
            using (var output = MediaFactory.MFCreateMediaType())
            {
                output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                output.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
                output.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)_audioRate);
                output.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)_audioChannels);
                output.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
                output.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, 24000u); // 192 kbit/s
                _audioStream = _writer.AddStream(output);
            }
            using (var input = MediaFactory.MFCreateMediaType())
            {
                input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                input.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
                input.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)_audioRate);
                input.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)_audioChannels);
                input.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
                input.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)(_audioChannels * 2));
                input.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(_audioRate * _audioChannels * 2));
                _writer.SetInputMediaType(_audioStream, input, null);
            }
            _loopback.DataAvailable += OnSound;
        }
        catch (Exception ex)
        {
            // A video without sound beats no video.
            Log.Write($"Recorder: recording without sound: {ex.Message}");
            _loopback?.Dispose();
            _loopback = null;
            _audioStream = -1;
        }
    }

    private void Cleanup(bool deleteFile)
    {
        lock (_gate)
        {
            _session?.Dispose();
            _pool?.Dispose();
            _loopback?.Dispose();
            _writer?.Dispose();
            _staging?.Dispose();
            _context?.Dispose();
            _device?.Dispose();
            (_session, _pool, _loopback, _writer, _staging, _context, _device, _winrtDevice, _item) = (null, null, null, null, null, null, null, null, null);
            _audioStream = -1;
            _audioFrames = 0;
            _lastVideoTicks = -1;
        }
        if (deleteFile && File != null)
        {
            try { System.IO.File.Delete(File); }
            catch (IOException) { }
        }
        try { MediaFactory.MFShutdown(); }
        catch (SharpGen.Runtime.SharpGenException) { }
    }

    public void Dispose()
    {
        Stop();
    }

    // ───────────────────────────── interop ─────────────────────────────

    /// <summary>A capture item for a whole monitor, through IGraphicsCaptureItemInterop.</summary>
    private static unsafe GraphicsCaptureItem CaptureItemForMonitor(IntPtr monitor)
    {
        var factory = WinRT.ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interopId = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(factory.ThisPtr, in interopId, out var interop));
        try
        {
            var itemId = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760"); // IGraphicsCaptureItem
            IntPtr result;
            // vtable: IUnknown (3 slots), CreateForWindow, CreateForMonitor
            var createForMonitor = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)(*(IntPtr**)interop)[4];
            Marshal.ThrowExceptionForHR(createForMonitor(interop, monitor, &itemId, &result));
            var item = GraphicsCaptureItem.FromAbi(result);
            Marshal.Release(result);
            return item;
        }
        finally
        {
            Marshal.Release(interop);
        }
    }

    /// <summary>The D3D11 texture behind a capture frame, through IDirect3DDxgiInterfaceAccess.</summary>
    private static unsafe ID3D11Texture2D TextureOf(IDirect3DSurface surface)
    {
        var unknown = WinRT.MarshalInspectable<IDirect3DSurface>.FromManaged(surface);
        try
        {
            var accessId = new Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in accessId, out var access));
            try
            {
                var textureId = typeof(ID3D11Texture2D).GUID;
                IntPtr texture;
                var getInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(IntPtr**)access)[3];
                Marshal.ThrowExceptionForHR(getInterface(access, &textureId, &texture));
                return new ID3D11Texture2D(texture);
            }
            finally
            {
                Marshal.Release(access);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
}
