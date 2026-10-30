using SkiaSharp;
using Vortice.Direct3D;
using Vortice.Direct3D11;
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
        Composition.Dispose();
        Factory.Dispose();
        Context.Dispose();
        Device.Dispose();
    }
}

/// <summary>
/// A premultiplied-alpha swap chain bound to one window through DirectComposition, so the
/// island can have soft, transparent edges with no GDI involvement. Skia draws each frame on
/// the CPU into <see cref="Bitmap"/>, which is then copied into the back buffer and presented.
/// The island is small, so a CPU raster is cheap and avoids GPU-interop fragility.
/// </summary>
internal sealed class CompositionSurface : IDisposable
{
    private readonly GraphicsDevice _gfx;
    private readonly IDXGISwapChain1 _swapChain;
    private readonly IDCompositionTarget _target;
    private readonly IDCompositionVisual _visual;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public SKBitmap Bitmap { get; private set; }

    public CompositionSurface(GraphicsDevice gfx, IntPtr hwnd, int width, int height)
    {
        _gfx = gfx;
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
        _swapChain = gfx.Factory.CreateSwapChainForComposition(gfx.Device, desc);
        gfx.Composition.CreateTargetForHwnd(hwnd, true, out _target!).CheckError();
        _visual = gfx.Composition.CreateVisual();
        _visual.SetContent(_swapChain).CheckError();
        _target.SetRoot(_visual).CheckError();
        gfx.Composition.Commit().CheckError();
        Bitmap = NewBitmap();
        _upload = NewUpload();
    }

    /// <summary>
    /// The frame goes here first. Mapping it with WriteDiscard hands back fresh memory every time,
    /// so the copy never waits for the screen to finish with the last frame, as writing straight
    /// into the swap chain's buffer did.
    /// </summary>
    private ID3D11Texture2D _upload;

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

    private SKBitmap NewBitmap() => new(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));

    public void Resize(int width, int height)
    {
        if (width == Width && height == Height) return;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        _swapChain.ResizeBuffers(Buffers, (uint)Width, (uint)Height, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
        Bitmap.Dispose();
        Bitmap = NewBitmap();
        _upload.Dispose();
        _upload = NewUpload();
    }

    /// <summary>
    /// Copies <see cref="Bitmap"/> to the screen without waiting; the caller paces frames with
    /// DwmFlush once for all monitors. Returns false when the GPU device was lost.
    /// </summary>
    private const uint Buffers = 3;


    public bool Present()
    {
        try
        {
            var mapped = _gfx.Context.Map(_upload, 0, MapMode.WriteDiscard);
            unsafe
            {
                var rowBytes = Bitmap.RowBytes;
                var source = (byte*)Bitmap.GetPixels();
                var target = (byte*)mapped.DataPointer;
                for (var y = 0; y < Height; y++)
                    Buffer.MemoryCopy(source + y * rowBytes, target + y * mapped.RowPitch, mapped.RowPitch, rowBytes);
            }
            _gfx.Context.Unmap(_upload, 0);
            using (var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0))
                _gfx.Context.CopyResource(backBuffer, _upload);
            var hr = _swapChain.Present(0, PresentFlags.None);
            return hr.Code != Vortice.DXGI.ResultCode.DeviceRemoved.Code && hr.Code != Vortice.DXGI.ResultCode.DeviceReset.Code;
        }
        catch (SharpGen.Runtime.SharpGenException ex)
        {
            Log.Write($"Graphics: present failed: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        _visual.Dispose();
        _target.Dispose();
        _swapChain.Dispose();
        _upload.Dispose();
        Bitmap.Dispose();
    }
}
