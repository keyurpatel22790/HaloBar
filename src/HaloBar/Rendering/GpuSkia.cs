using SkiaSharp;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace HaloBar.Rendering;

/// <summary>
/// Skia drawing on the graphics card: a Direct3D 12 device and queue on the same adapter as the
/// D3D11 device, and Skia's GPU context on top. Every window's swap chain is made on the queue and
/// drawn straight into, so a frame is neither rasterised on the CPU nor copied up to the GPU.
/// </summary>
internal sealed class GpuSkia : IDisposable
{
    public ID3D12Device Device { get; }
    public ID3D12CommandQueue Queue { get; }
    public GRContext Context { get; }
    private readonly IDXGIAdapter1 _adapter;
    private readonly GRD3DBackendContext _backend;

    private GpuSkia(IDXGIAdapter1 adapter, ID3D12Device device, ID3D12CommandQueue queue, GRD3DBackendContext backend, GRContext context)
    {
        (_adapter, Device, Queue, _backend, Context) = (adapter, device, queue, backend, context);
    }

    /// <summary>Null where the GPU can't (no D3D12 feature level 11, or Skia refused): drawing stays on the CPU.</summary>
    public static GpuSkia? Create(IDXGIAdapter adapter)
    {
        IDXGIAdapter1? adapter1 = null;
        ID3D12Device? device = null;
        ID3D12CommandQueue? queue = null;
        try
        {
            adapter1 = adapter.QueryInterface<IDXGIAdapter1>();
            if (D3D12.D3D12CreateDevice(adapter1, FeatureLevel.Level_11_0, out device).Failure || device == null)
            {
                Log.Write("GPU: Direct3D 12 isn't available; drawing on the CPU");
                adapter1.Dispose();
                return null;
            }
            queue = device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct));
            var backend = new GRD3DBackendContext { Adapter = adapter1.NativePointer, Device = device.NativePointer, Queue = queue.NativePointer };
            var context = GRContext.CreateDirect3D(backend);
            if (context == null)
            {
                Log.Write("GPU: Skia couldn't start on Direct3D 12; drawing on the CPU");
                backend.Dispose();
                queue.Dispose();
                device.Dispose();
                adapter1.Dispose();
                return null;
            }
            // The island and widgets are small: a modest texture cache is plenty (Skia's default is 256 MB).
            context.SetResourceCacheLimit(48 * 1024 * 1024);
            Log.Write($"GPU: drawing with Direct3D 12 on {adapter1.Description1.Description}");
            return new GpuSkia(adapter1, device, queue, backend, context);
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Write($"GPU: {ex.Message}; drawing on the CPU");
            queue?.Dispose();
            device?.Dispose();
            adapter1?.Dispose();
            return null;
        }
    }

    public void Dispose()
    {
        Context.AbandonContext(false);
        Context.Dispose();
        _backend.Dispose();
        Queue.Dispose();
        Device.Dispose();
        _adapter.Dispose();
    }
}
