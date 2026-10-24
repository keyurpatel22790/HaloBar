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
        if (D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                levels, out var device, out var context).Failure)
        {
            // No usable GPU (remote desktop, broken driver): WARP is a software rasteriser that still composes.
            D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Warp, DeviceCreationFlags.BgraSupport,
                levels, out device, out context).CheckError();
        }
        Device = device!;
        Context = context!;

        using var dxgiDevice = Device.QueryInterface<IDXGIDevice1>();
        // Frames are tiny and paced by DWM; one queued frame is plenty and keeps latency down.
        dxgiDevice.MaximumFrameLatency = 1;
        using var adapter = dxgiDevice.GetAdapter();
        AdapterName = adapter.Description.Description;
        Factory = adapter.GetParent<IDXGIFactory2>();
        Composition = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
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
            BufferCount = 2,
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
    }

    private SKBitmap NewBitmap() => new(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));

    public void Resize(int width, int height)
    {
        if (width == Width && height == Height) return;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        _swapChain.ResizeBuffers(2, (uint)Width, (uint)Height, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
        Bitmap.Dispose();
        Bitmap = NewBitmap();
    }

    /// <summary>
    /// Copies <see cref="Bitmap"/> to the screen without waiting; the caller paces frames with
    /// DwmFlush once for all monitors. Returns false when the GPU device was lost.
    /// </summary>
    public bool Present()
    {
        try
        {
            using (var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0))
                _gfx.Context.UpdateSubresource(backBuffer, 0, null, Bitmap.GetPixels(), (uint)Bitmap.RowBytes, 0);
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
        Bitmap.Dispose();
    }
}
