using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace HaloBar.Services;

internal sealed record StatsSnapshot(float Cpu, float Gpu, float Ram, double RamUsedGb, double RamTotalGb, double DownBytesPerSec, double UpBytesPerSec)
{
    /// <summary>In °C, or null where the PC doesn't say (CPU from the ACPI thermal zones, GPU from NVIDIA's NVML).</summary>
    public double? CpuTemp { get; init; }
    public double? GpuTemp { get; init; }
}

/// <summary>
/// CPU, GPU, memory and network use, sampled once a second while something on screen shows them.
/// CPU from GetSystemTimes, memory from GlobalMemoryStatusEx, GPU from the "GPU Engine" performance
/// counters (3D engines), network from the interfaces' byte counters. Temperatures where the PC
/// offers them without admin rights: the ACPI thermal zones (close to the CPU's) and NVML on
/// NVIDIA graphics.
/// </summary>
internal sealed class StatsService : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private long _idle, _kernel, _user;
    private long _rx, _tx;
    private DateTime _netAt;
    private IntPtr _gpuQuery, _gpuCounter, _thermalQuery, _thermalCounter;
    private IntPtr _nvmlDevice;
    private bool _nvmlTried;
    private int _tick;
    private double? _cpuTemp, _gpuTemp;

    public StatsSnapshot Snapshot { get; private set; } = new(0, 0, 0, 0, 0, 0, 0);
    private DateTime _wantedAt;

    /// <summary>Call while something on screen shows the numbers; sampling pauses a few seconds after the last call.</summary>
    public void Touch() => _wantedAt = DateTime.UtcNow;

    private bool Active => DateTime.UtcNow - _wantedAt < TimeSpan.FromSeconds(3);
    public event Action? Changed;

    public void Start()
    {
        OpenGpuCounter();
        OpenThermalCounter();
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
        // Memory is sold and shown in binary gigabytes (1024³ bytes), as Task Manager does.
        const double GiB = 1024.0 * 1024 * 1024;
        var total = mem.ullTotalPhys / GiB;
        var used = (mem.ullTotalPhys - mem.ullAvailPhys) / GiB;

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

        // Temperatures change slowly; read them every few seconds.
        if (_tick++ % 3 == 0)
        {
            _cpuTemp = ReadThermalZone();
            _gpuTemp = ReadNvidiaTemp();
        }
        return new StatsSnapshot(cpu, ReadGpu(), (float)(used / total), used, total, down, up) { CpuTemp = _cpuTemp, GpuTemp = _gpuTemp };
    }

    private void OpenThermalCounter()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out _thermalQuery) != 0) return;
        if (PdhAddEnglishCounterW(_thermalQuery, @"\Thermal Zone Information(*)\High Precision Temperature", IntPtr.Zero, out _thermalCounter) != 0)
        {
            PdhCloseQuery(_thermalQuery);
            _thermalQuery = IntPtr.Zero;
            Log.Write("Stats: no thermal zone counters");
            return;
        }
        PdhCollectQueryData(_thermalQuery);
    }

    /// <summary>The hottest ACPI thermal zone in °C (the counter reads tenths of a kelvin).</summary>
    private double? ReadThermalZone()
    {
        if (_thermalQuery == IntPtr.Zero || PdhCollectQueryData(_thermalQuery) != 0) return null;
        var values = ReadArray(_thermalCounter);
        if (values.Count == 0) return null;
        var celsius = values.Max() / 10 - 273.15;
        return celsius is > 5 and < 125 ? celsius : null;
    }

    /// <summary>The NVIDIA GPU's core temperature through the driver's nvml.dll; null without one.</summary>
    private double? ReadNvidiaTemp()
    {
        try
        {
            if (!_nvmlTried)
            {
                _nvmlTried = true;
                if (nvmlInit_v2() != 0 || nvmlDeviceGetHandleByIndex_v2(0, out _nvmlDevice) != 0) _nvmlDevice = IntPtr.Zero;
            }
            if (_nvmlDevice == IntPtr.Zero) return null;
            return nvmlDeviceGetTemperature(_nvmlDevice, 0, out var t) == 0 ? t : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _nvmlDevice = IntPtr.Zero;
            return null;
        }
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
        return (float)Math.Clamp(ReadArray(_gpuCounter).Sum() / 100, 0, 1);
    }

    /// <summary>Every instance's value of a wildcard counter.</summary>
    private static List<double> ReadArray(IntPtr counter)
    {
        var values = new List<double>();
        uint size = 0, count;
        const uint PDH_MORE_DATA = 0x800007D2, PDH_FMT_DOUBLE = 0x200;
        if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, out count, IntPtr.Zero) != PDH_MORE_DATA) return values;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, out count, buffer) != 0) return values;
            // PDH_FMT_COUNTERVALUE_ITEM_W: name pointer, then {status, pad, double}.
            var stride = IntPtr.Size + 16;
            for (var i = 0; i < count; i++)
            {
                var item = buffer + i * stride;
                if (Marshal.ReadInt32(item + IntPtr.Size) != 0) continue;
                values.Add(BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item + IntPtr.Size + 8)));
            }
            return values;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// A network speed short enough for half a stats tile: at most three digits and no space,
    /// so "49MB/s" and "4.9MB/s" rather than "49.1 MB/s".
    /// </summary>
    public static string Rate(double bytesPerSec) => Rate(bytesPerSec, UI.Prefs.Bits);

    /// <summary>In bytes ("4.9MB/s", as downloads show it) or bits ("39Mb/s", as speed tests do).</summary>
    public static string Rate(double bytesPerSec, bool bits)
    {
        var (n, unit) = bits ? (bytesPerSec * 8, "b") : (bytesPerSec, "B");
        return n switch
        {
            >= 1e9 => $"{n / 1e9:0.0}G{unit}/s",
            >= 1e7 => $"{n / 1e6:0}M{unit}/s",
            >= 1e6 => $"{n / 1e6:0.0}M{unit}/s",
            >= 1e3 => $"{n / 1e3:0}K{unit}/s",
            _ => $"{n:0}{unit}/s",
        };
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_gpuQuery != IntPtr.Zero) PdhCloseQuery(_gpuQuery);
        if (_thermalQuery != IntPtr.Zero) PdhCloseQuery(_thermalQuery);
        if (_nvmlDevice != IntPtr.Zero) nvmlShutdown();
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
    [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] private static extern int nvmlShutdown();
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temperature);
}
