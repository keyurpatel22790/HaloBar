using SkiaSharp;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct3D12;
using Vortice.DirectComposition;
using Vortice.DXGI;

namespace HaloBar.Rendering;

/// <summary>
/// The GPU objects every island shares: one D3D11 device and one DirectComposition device,
/// however many monitors there are. A device costs far more memory than a swap chain.
/// </summary>
internal sealed class GraphicsDevice : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public IDXGIFactory2 Factory { get; }
    public IDCompositionDevice Composition { get; }
    public string AdapterName { get; }
    /// <summary>Skia on the graphics card, or null to draw on the CPU.</summary>
    public GpuSkia? Gpu { get; }
    /// <summary>Set before the first device is made (from settings): false keeps drawing on the CPU.</summary>
    public static bool UseGpu { get; set; } = true;

    public GraphicsDevice()
    {
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
        // On a laptop with two GPUs, the one that drives the main screen: frames made on the other
        // would cross between GPUs on every present.
        using var display = MainScreenAdapter();
        ID3D11Device device;
        ID3D11DeviceContext context;
        var made = display != null
            ? D3D11.D3D11CreateDevice(display, DriverType.Unknown, DeviceCreationFlags.BgraSupport, levels, out device, out context)
            : D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out device, out context);
        if (made.Failure)
        {
            // No usable GPU (remote desktop, broken driver): WARP is a software rasteriser that still composes.
            D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Warp, DeviceCreationFlags.BgraSupport,
                levels, out device, out context).CheckError();
        }
        Device = device!;
        Context = context!;

        using var dxgiDevice = Device.QueryInterface<IDXGIDevice1>();
        // Windows' usual three queued frames: the device is shared by every island and widget, and
        // with one, each present waited for the last window's frame to finish.
        dxgiDevice.MaximumFrameLatency = 3;
        using var adapter = dxgiDevice.GetAdapter();
        AdapterName = adapter.Description.Description;
        Factory = adapter.GetParent<IDXGIFactory2>();
        Composition = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
        if (UseGpu) Gpu = GpuSkia.Create(adapter);
    }

    /// <summary>The GPU whose outputs include the primary monitor, or null to let Windows choose.</summary>
    private static IDXGIAdapter1? MainScreenAdapter()
    {
        try
        {
            var primary = Platform.Monitors.All().FirstOrDefault(m => m.IsPrimary)?.Handle ?? IntPtr.Zero;
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
            {
                for (uint j = 0; adapter.EnumOutputs(j, out var output).Success; j++)
                {
                    var here = output.Description.Monitor == primary;
                    output.Dispose();
                    if (here) return adapter;
                }
                adapter.Dispose();
            }
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or InvalidOperationException)
        {
            Log.Write($"GPU: couldn't find the main screen's adapter: {ex.Message}");
        }
        return null;
    }

    public bool IsLost => Device.DeviceRemovedReason.Failure;

    public void Dispose()
    {
        Gpu?.Dispose();
        Composition.Dispose();
        Factory.Dispose();
        Context.Dispose();
        Device.Dispose();
    }
}

/// <summary>
/// A premultiplied-alpha swap chain bound to one window through DirectComposition, so the island
/// can have soft, transparent edges with no GDI involvement. Where the GPU allows (see
/// <see cref="GpuSkia"/>), Skia draws each frame straight into the swap chain's buffer on the
/// graphics card; otherwise on the CPU into <see cref="Bitmap"/>, which is then copied up and presented.
/// A frame starts with <see cref="BeginFrame"/> and is shown with <see cref="Present"/>.
/// </summary>
internal sealed class CompositionSurface : IDisposable
{
    private readonly GraphicsDevice _gfx;
    private readonly GpuSkia? _gpu;
    private readonly IDXGISwapChain1 _swapChain;
    private readonly IDCompositionTarget _target;
    private readonly IDCompositionVisual _visual;

    public int Width { get; private set; }
    public int Height { get; private set; }
    /// <summary>The CPU frame (CPU drawing only; on the GPU it is just the size, unused).</summary>
    public SKBitmap Bitmap { get; private set; }
    public bool OnGpu => _gpu != null;

    /// <summary>Set before a frame to also get it as a picture (<see cref="Captured"/>), for snapshots.</summary>
    public bool CaptureNext { get; set; }
    /// <summary>The last frame drawn while <see cref="CaptureNext"/> was set.</summary>
    public SKBitmap? Captured { get; private set; }

    /// <param name="scale">Above 1, the picture is that many times smaller than the window and the
    /// compositor stretches it smoothly.</param>
    public CompositionSurface(GraphicsDevice gfx, IntPtr hwnd, int width, int height, float scale = 1)
    {
        _gfx = gfx;
        _gpu = gfx.Gpu;
        _swapChain3 = new(() => _swapChain!.QueryInterface<IDXGISwapChain3>());
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);

        var desc = new SwapChainDescription1
        {
            Width = (uint)Width,
            Height = (uint)Height,
            Format = Format.B8G8R8A8_UNorm,
            // Three buffers: with two, Present waits for the compositor to let go of the last frame,
            // and with an island on each monitor those waits add up to every other frame missed.
            BufferCount = Buffers,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = AlphaMode.Premultiplied,
        };
        // On the GPU the swap chain belongs to the D3D12 queue Skia draws with.
        _swapChain = _gpu != null
            ? gfx.Factory.CreateSwapChainForComposition(_gpu.Queue, desc)
            : gfx.Factory.CreateSwapChainForComposition(gfx.Device, desc);
        gfx.Composition.CreateTargetForHwnd(hwnd, true, out _target!).CheckError();
        _visual = gfx.Composition.CreateVisual();
        _visual.SetContent(_swapChain).CheckError();
        if (scale > 1)
        {
            _visual.SetTransform(System.Numerics.Matrix3x2.CreateScale(scale)).CheckError();
            _visual.SetBitmapInterpolationMode(BitmapInterpolationMode.Linear).CheckError();
        }
        _target.SetRoot(_visual).CheckError();
        gfx.Composition.Commit().CheckError();
        Bitmap = NewBitmap();
        if (_gpu != null) StartGpu();
        else _upload = NewUpload();
    }

    private SKBitmap NewBitmap() => new(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));

    private const uint Buffers = 3;

    // ───────────────────────────── frames ─────────────────────────────

    private SKCanvas? _cpuCanvas;
    private SKBitmap? _cpuCanvasBitmap;
    private SKBitmap? _capture;
    private SKCanvas? _captureCanvas;

    /// <summary>The canvas for this frame (cleared and drawn by the caller, then <see cref="Present"/>).</summary>
    public SKCanvas BeginFrame()
    {
        if (CaptureNext)
        {
            // Drawn on the CPU too, so the picture never has to be read back from the graphics card.
            _capture?.Dispose();
            _capture = NewBitmap();
            _captureCanvas = new SKCanvas(_capture);
            return _captureCanvas;
        }
        if (_gpu != null) return BeginGpuFrame();
        if (!ReferenceEquals(_cpuCanvasBitmap, Bitmap))
        {
            _cpuCanvas?.Dispose();
            _cpuCanvasBitmap = Bitmap;
            _cpuCanvas = new SKCanvas(Bitmap);
        }
        return _cpuCanvas!;
    }

    /// <summary>Shows the frame without waiting; false when the graphics device was lost.</summary>
    public bool Present()
    {
        if (_captureCanvas != null)
        {
            // A snapshot frame: kept, not shown (the screen keeps the last frame until the next).
            _captureCanvas.Flush();
            _captureCanvas.Dispose();
            _captureCanvas = null;
            Captured?.Dispose();
            Captured = _capture;
            _capture = null;
            CaptureNext = false;
            return true;
        }
        return _gpu != null ? PresentGpu() : PresentCpu();
    }

    public void Resize(int width, int height)
    {
        if (width == Width && height == Height) return;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        if (_gpu != null)
        {
            WaitForGpu();
            ReleaseBuffers();
        }
        _swapChain.ResizeBuffers(Buffers, (uint)Width, (uint)Height, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
        Bitmap.Dispose();
        Bitmap = NewBitmap();
        if (_gpu != null) GetBuffers();
        else
        {
            _upload!.Dispose();
            _upload = NewUpload();
        }
    }

    // ───────────────────────────── CPU drawing ─────────────────────────────

    /// <summary>
    /// The frame goes here first. Mapping it with WriteDiscard hands back fresh memory every time,
    /// so the copy never waits for the screen to finish with the last frame, as writing straight
    /// into the swap chain's buffer did.
    /// </summary>
    private ID3D11Texture2D? _upload;

    private ID3D11Texture2D NewUpload() => _gfx.Device.CreateTexture2D(new Texture2DDescription
    {
        Width = (uint)Width,
        Height = (uint)Height,
        MipLevels = 1,
        ArraySize = 1,
        Format = Format.B8G8R8A8_UNorm,
        SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Dynamic,
        BindFlags = BindFlags.ShaderResource,
        CPUAccessFlags = CpuAccessFlags.Write,
    });

    private bool PresentCpu()
    {
        try
        {
            var mapped = _gfx.Context.Map(_upload!, 0, MapMode.WriteDiscard);
            unsafe
            {
                var rowBytes = Bitmap.RowBytes;
                var source = (byte*)Bitmap.GetPixels();
                var target = (byte*)mapped.DataPointer;
                for (var y = 0; y < Height; y++)
                    Buffer.MemoryCopy(source + y * rowBytes, target + y * mapped.RowPitch, mapped.RowPitch, rowBytes);
            }
            _gfx.Context.Unmap(_upload!, 0);
            using (var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0))
                _gfx.Context.CopyResource(backBuffer, _upload!);
            return Shown(_swapChain.Present(0, PresentFlags.None));
        }
        catch (SharpGen.Runtime.SharpGenException ex)
        {
            Log.Write($"Graphics: present failed: {ex.Message}");
            return false;
        }
    }

    private static bool Shown(SharpGen.Runtime.Result hr) =>
        hr.Code != Vortice.DXGI.ResultCode.DeviceRemoved.Code && hr.Code != Vortice.DXGI.ResultCode.DeviceReset.Code;

    // ───────────────────────────── GPU drawing ─────────────────────────────

    // Skia is told each buffer starts in the PRESENT state; it moves it to RENDER_TARGET to draw.
    // After its work, a one-barrier command list puts the buffer back to PRESENT for the swap.
    // Each buffer's slot remembers the fence value of its last frame, so a buffer (and its command
    // allocator) is only reused once the graphics card is done with it.
    private const uint FormatBgra = 87; // DXGI_FORMAT_B8G8R8A8_UNORM
    private ID3D12Resource[] _buffers = [];
    private ID3D12CommandAllocator[] _allocators = [];
    private ulong[] _slotFence = [];
    private ID3D12GraphicsCommandList? _list;
    private ID3D12Fence? _fence;
    private ulong _fenceValue;
    private readonly AutoResetEvent _fenceEvent = new(false);
    private SKSurface? _frame;
    private GRBackendRenderTarget? _frameTarget;
    private GRD3DTextureResourceInfo? _frameInfo;
    private uint _frameIndex;

    private void StartGpu()
    {
        var device = _gpu!.Device;
        _allocators = new ID3D12CommandAllocator[Buffers];
        for (var i = 0; i < Buffers; i++) _allocators[i] = device.CreateCommandAllocator(CommandListType.Direct);
        _list = device.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, _allocators[0], null);
        _list.Close();
        _fence = device.CreateFence(0);
        _slotFence = new ulong[Buffers];
        GetBuffers();
    }

    private void GetBuffers()
    {
        _buffers = new ID3D12Resource[Buffers];
        for (uint i = 0; i < Buffers; i++) _buffers[i] = _swapChain.GetBuffer<ID3D12Resource>(i);
    }

    private void ReleaseBuffers()
    {
        foreach (var b in _buffers) b.Dispose();
        _buffers = [];
    }

    private void WaitFor(ulong value)
    {
        if (_fence == null || _fence.CompletedValue >= value) return;
        _fence.SetEventOnCompletion(value, _fenceEvent);
        _fenceEvent.WaitOne(1000);
    }

    private void WaitForGpu()
    {
        if (_fence == null) return;
        _gpu!.Queue.Signal(_fence, ++_fenceValue);
        WaitFor(_fenceValue);
    }

    private SKCanvas BeginGpuFrame()
    {
        _frameIndex = _swapChain3.Value.CurrentBackBufferIndex;
        WaitFor(_slotFence[_frameIndex]);
        _frameInfo = new GRD3DTextureResourceInfo
        {
            Resource = _buffers[_frameIndex].NativePointer,
            ResourceState = (uint)ResourceStates.Present,
            Format = FormatBgra,
            LevelCount = 1,
            SampleCount = 1,
        };
        _frameTarget = new GRBackendRenderTarget(Width, Height, _frameInfo);
        _frame = SKSurface.Create(_gpu!.Context, _frameTarget, GRSurfaceOrigin.TopLeft, SKColorType.Bgra8888);
        if (_frame == null) throw new InvalidOperationException("Skia couldn't draw into the swap chain");
        return _frame.Canvas;
    }

    private readonly Lazy<IDXGISwapChain3> _swapChain3;

    private bool PresentGpu()
    {
        try
        {
            _frame!.Flush(true, false);
            var allocator = _allocators[_frameIndex];
            allocator.Reset();
            _list!.Reset(allocator, null);
            _list.ResourceBarrierTransition(_buffers[_frameIndex], ResourceStates.RenderTarget, ResourceStates.Present);
            _list.Close();
            _gpu!.Queue.ExecuteCommandList(_list);
            var hr = _swapChain.Present(0, PresentFlags.None);
            _gpu.Queue.Signal(_fence!, ++_fenceValue);
            _slotFence[_frameIndex] = _fenceValue;
            return Shown(hr);
        }
        catch (SharpGen.Runtime.SharpGenException ex)
        {
            Log.Write($"Graphics: GPU present failed: {ex.Message}");
            return false;
        }
        finally
        {
            _frame?.Dispose();
            _frame = null;
            _frameTarget?.Dispose();
            _frameTarget = null;
            _frameInfo?.Dispose();
            _frameInfo = null;
        }
    }

    public void Dispose()
    {
        if (_gpu != null)
        {
            try { WaitForGpu(); } catch (SharpGen.Runtime.SharpGenException) { }
            ReleaseBuffers();
            foreach (var a in _allocators) a.Dispose();
            _list?.Dispose();
            _fence?.Dispose();
            if (_swapChain3.IsValueCreated) _swapChain3.Value.Dispose();
        }
        _cpuCanvas?.Dispose();
        _captureCanvas?.Dispose();
        _capture?.Dispose();
        Captured?.Dispose();
        _visual.Dispose();
        _target.Dispose();
        _swapChain.Dispose();
        _upload?.Dispose();
        Bitmap.Dispose();
        _fenceEvent.Dispose();
    }
}
