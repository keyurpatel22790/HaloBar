using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace HaloBar.Services;

internal sealed record StatsSnapshot(float Cpu, float Gpu, float Ram, double RamUsedGb, double RamTotalGb, double DownBytesPerSec, double UpBytesPerSec);

/// <summary>
/// CPU, GPU, memory and network use, sampled once a second while something on screen shows them.
/// CPU from GetSystemTimes, memory from GlobalMemoryStatusEx, GPU from the "GPU Engine" performance
/// counters (3D engines), network from the interfaces' byte counters.
/// </summary>
internal sealed class StatsService : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private long _idle, _kernel, _user;
    private long _rx, _tx;
    private DateTime _netAt;
    private IntPtr _gpuQuery, _gpuCounter;

    public StatsSnapshot Snapshot { get; private set; } = new(0, 0, 0, 0, 0, 0, 0);
    private DateTime _wantedAt;

    /// <summary>Call while something on screen shows the numbers; sampling pauses a few seconds after the last call.</summary>
    public void Touch() => _wantedAt = DateTime.UtcNow;

    private bool Active => DateTime.UtcNow - _wantedAt < TimeSpan.FromSeconds(3);
    public event Action? Changed;

    public void Start()
    {
        OpenGpuCounter();
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                if (!Active) continue;
                try
                {
                    Snapshot = Sample();
                    Changed?.Invoke();
                }
                catch (Exception ex)
                {
                    Log.Write($"Stats: {ex.Message}");
                }
            }
        });
    }

    private StatsSnapshot Sample()
    {
        // CPU: busy share of the time since the last sample. Kernel time includes idle time.
        GetSystemTimes(out var idle, out var kernel, out var user);
        var dIdle = idle - _idle;
        var dTotal = kernel - _kernel + (user - _user);
        var cpu = _kernel == 0 || dTotal <= 0 ? 0 : (float)Math.Clamp(1 - (double)dIdle / dTotal, 0, 1);
        (_idle, _kernel, _user) = (idle, kernel, user);

        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref mem);
        var total = mem.ullTotalPhys / 1e9;
        var used = (mem.ullTotalPhys - mem.ullAvailPhys) / 1e9;

        long rx = 0, tx = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var s = nic.GetIPStatistics();
            rx += s.BytesReceived;
            tx += s.BytesSent;
        }
        var now = DateTime.UtcNow;
        double down = 0, up = 0;
        if (_netAt != default)
        {
            var secs = Math.Max(0.2, (now - _netAt).TotalSeconds);
            down = Math.Max(0, rx - _rx) / secs;
            up = Math.Max(0, tx - _tx) / secs;
        }
        (_rx, _tx, _netAt) = (rx, tx, now);

        return new StatsSnapshot(cpu, ReadGpu(), (float)(used / total), used, total, down, up);
    }

    private void OpenGpuCounter()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out _gpuQuery) != 0) return;
        if (PdhAddEnglishCounterW(_gpuQuery, @"\GPU Engine(*engtype_3D)\Utilization Percentage", IntPtr.Zero, out _gpuCounter) != 0)
        {
            PdhCloseQuery(_gpuQuery);
            _gpuQuery = IntPtr.Zero;
            Log.Write("Stats: GPU counters unavailable");
            return;
        }
        PdhCollectQueryData(_gpuQuery);
    }

    /// <summary>Sum of every process's 3D-engine use, which is what Task Manager shows as GPU.</summary>
    private float ReadGpu()
    {
        if (_gpuQuery == IntPtr.Zero || PdhCollectQueryData(_gpuQuery) != 0) return 0;
        uint size = 0, count;
        const uint PDH_MORE_DATA = 0x800007D2, PDH_FMT_DOUBLE = 0x200;
        if (PdhGetFormattedCounterArrayW(_gpuCounter, PDH_FMT_DOUBLE, ref size, out count, IntPtr.Zero) != PDH_MORE_DATA) return 0;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(_gpuCounter, PDH_FMT_DOUBLE, ref size, out count, buffer) != 0) return 0;
            double sum = 0;
            // PDH_FMT_COUNTERVALUE_ITEM_W: name pointer, then {status, pad, double}.
            var stride = IntPtr.Size + 16;
            for (var i = 0; i < count; i++)
            {
                var item = buffer + i * stride;
                if (Marshal.ReadInt32(item + IntPtr.Size) != 0) continue;
                sum += BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item + IntPtr.Size + 8));
            }
            return (float)Math.Clamp(sum / 100, 0, 1);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static string Rate(double bytesPerSec) => bytesPerSec switch
    {
        >= 1e6 => $"{bytesPerSec / 1e6:0.0} MB/s",
        >= 1e3 => $"{bytesPerSec / 1e3:0} KB/s",
        _ => $"{bytesPerSec:0} B/s",
    };

    public void Dispose()
    {
        _cts.Cancel();
        if (_gpuQuery != IntPtr.Zero) PdhCloseQuery(_gpuQuery);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint size, out uint count, IntPtr items);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}
