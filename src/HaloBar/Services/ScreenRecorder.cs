// NAudio 3 marks WasapiCapture obsolete in favour of its new recorder builder; the classic
// capture works, matches the rest of HaloBar, and is what this recorder was checked with.
#pragma warning disable CS0618
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
/// the PC is playing (WASAPI loopback), the microphone, or both mixed; a pump writes it every
/// 20 ms and pads what hasn't arrived with silence, so sound stays in step with the picture.
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
    private readonly List<SoundSource> _sources = [];
    private int _pumping;
    private int _videoStream, _audioStream = -1;
    private int _width, _height, _audioRate, _audioChannels;
    private long _audioFrames, _lastVideoTicks = -1;
    private readonly Stopwatch _clock = new();
    private Timer? _pump;

    public bool Recording { get; private set; }
    public DateTime Since { get; private set; }
    public string? File { get; private set; }
    public event Action? Changed;

    /// <summary>Starts recording <paramref name="monitor"/> into a new file in Videos\HaloBar. Returns an error, or null.</summary>
    public string? Start(IntPtr monitor, bool sound, bool microphone = false)
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
            CreateWriter(sound, microphone);

            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice!, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _item.Size);
            _pool.FrameArrived += OnFrame;
            _session = _pool.CreateCaptureSession(_item);
            _session.IsCursorCaptureEnabled = true;

            _writer!.BeginWriting();
            _clock.Restart();
            _session.StartCapture();
            foreach (var source in _sources) source.Capture.StartRecording();
            if (_audioStream >= 0) _pump = new Timer(_ => Pump(), null, 20, 20);
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
        foreach (var source in _sources) source.Capture.StopRecording();
        _pump?.Dispose();
        _pump = null;
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

    /// <summary>
    /// One input (loopback or microphone): its captures become stereo float at the recording's
    /// rate and wait in a queue for the pump to mix them.
    /// </summary>
    private sealed class SoundSource(WasapiCapture capture, int rate, float gain)
    {
        private readonly Queue<float> _queue = new();
        public WasapiCapture Capture { get; } = capture;

        public void Add(WaveInEventArgs e)
        {
            var format = Capture.WaveFormat;
            var frames = e.BytesRecorded / format.BlockAlign;
            if (frames == 0) return;
            var isFloat = format.BitsPerSample == 32;
            float Read(int frame, int channel)
            {
                var at = frame * format.BlockAlign + Math.Min(channel, format.Channels - 1) * (format.BitsPerSample / 8);
                return isFloat ? BitConverter.ToSingle(e.Buffer, at) : BitConverter.ToInt16(e.Buffer, at) / 32768f;
            }
            // Linear resampling when the device runs at another rate (a 44.1 kHz mic into 48 kHz).
            var step = (double)format.SampleRate / rate;
            var outFrames = (int)(frames / step);
            lock (_queue)
            {
                for (var i = 0; i < outFrames; i++)
                {
                    var pos = i * step;
                    var a = (int)pos;
                    var b = Math.Min(a + 1, frames - 1);
                    var t = (float)(pos - a);
                    for (var c = 0; c < 2; c++) _queue.Enqueue((Read(a, c) * (1 - t) + Read(b, c) * t) * gain);
                }
                // More than a second waiting means the pump fell behind; drop the oldest to catch up.
                while (_queue.Count > rate * 2) _queue.Dequeue();
            }
        }

        /// <summary>Adds up to <paramref name="frames"/> waiting frames into <paramref name="mix"/>.</summary>
        public void MixInto(float[] mix, int frames)
        {
            lock (_queue)
            {
                var n = Math.Min(frames * 2, _queue.Count);
                for (var i = 0; i < n; i++) mix[i] += _queue.Dequeue();
            }
        }
    }

    /// <summary>Writes the sound up to about 100 ms ago: what each source sent, mixed, and silence for the rest.</summary>
    private void Pump()
    {
        if (!Recording || Interlocked.Exchange(ref _pumping, 1) == 1) return;
        try
        {
            var due = (long)(_clock.Elapsed.TotalSeconds * _audioRate) - Interlocked.Read(ref _audioFrames) - _audioRate / 10;
            if (due <= 0) return;
            var frames = (int)Math.Min(due, _audioRate);
            var mix = new float[frames * 2];
            foreach (var source in _sources) source.MixInto(mix, frames);
            var pcm = new short[mix.Length];
            for (var i = 0; i < mix.Length; i++) pcm[i] = (short)Math.Clamp(mix[i] * 32767f, short.MinValue, short.MaxValue);
            WriteSound(pcm, frames);
        }
        finally
        {
            Volatile.Write(ref _pumping, 0);
        }
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

    private void CreateWriter(bool sound, bool microphone)
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
        if (!sound && !microphone) return;

        try
        {
            // The PC's own output rate when it is one AAC takes; the other inputs are resampled to it.
            using (var probe = new MMDeviceEnumerator())
            {
                using var client = probe.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia).CreateAudioClient();
                var mix = client.MixFormat.SampleRate;
                _audioRate = mix is 44100 or 48000 ? mix : 48000;
            }
            _audioChannels = 2;
            if (sound) AddSource(new WasapiLoopbackCapture(), 1f);
            if (microphone)
            {
                try { AddSource(new WasapiCapture(), 1.4f); } // voices come in quieter than the PC's own sound
                catch (Exception ex) { Log.Write($"Recorder: no microphone: {ex.Message}"); }
            }
            if (_sources.Count == 0) throw new InvalidOperationException("no sound input");
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
        }
        catch (Exception ex)
        {
            // A video without sound beats no video.
            Log.Write($"Recorder: recording without sound: {ex.Message}");
            foreach (var source in _sources) source.Capture.Dispose();
            _sources.Clear();
            _audioStream = -1;
        }
    }

    private void AddSource(WasapiCapture capture, float gain)
    {
        var format = capture.WaveFormat;
        if (format.BitsPerSample is not (32 or 16)) throw new NotSupportedException($"sound format {format}");
        var source = new SoundSource(capture, _audioRate, gain);
        capture.DataAvailable += (_, e) => { if (Recording) source.Add(e); };
        _sources.Add(source);
    }

    private void Cleanup(bool deleteFile)
    {
        lock (_gate)
        {
            _session?.Dispose();
            _pool?.Dispose();
            foreach (var source in _sources) source.Capture.Dispose();
            _sources.Clear();
            _writer?.Dispose();
            _staging?.Dispose();
            _context?.Dispose();
            _device?.Dispose();
            (_session, _pool, _writer, _staging, _context, _device, _winrtDevice, _item) = (null, null, null, null, null, null, null, null);
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
