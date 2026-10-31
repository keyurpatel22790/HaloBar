using System.Diagnostics;
using System.Xml;

namespace HaloBar.Services;

/// <summary>
/// Warns before the PC slows down: the CPU or GPU hot for a minute, memory nearly full for a
/// minute (naming the app using the most), or the Windows drive nearly full. Checks every 30
/// seconds (the disk once a day) and repeats an alert at most every 30 minutes.
/// </summary>
internal sealed class HealthWatch(AppSettings settings, StatsService stats) : IDisposable
{
    public const double CpuHot = 90, GpuHot = 87, MemoryFull = 0.92;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, DateTime> _lastAlert = [];
    private int _cpuHot, _gpuHot, _memoryFull;
    private DateTime _diskCheckedAt;

    /// <summary>(title, detail) for a notice.</summary>
    public event Action<string, string>? Alert;

    public void Start() => _ = Task.Run(async () =>
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                if (settings.PcHealthAlerts) await Check();
                await Task.Delay(TimeSpan.FromSeconds(30), _stop.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log.Write($"Health: check failed: {ex.Message}");
            }
        }
    });

    private async Task Check()
    {
        // The stats sample only while something asks; ask for a couple of seconds' worth.
        stats.Touch();
        await Task.Delay(TimeSpan.FromSeconds(2.2), _stop.Token);
        stats.Touch();
        var s = stats.Snapshot;

        _cpuHot = s.CpuTemp >= CpuHot ? _cpuHot + 1 : 0;
        _gpuHot = s.GpuTemp >= GpuHot ? _gpuHot + 1 : 0;
        _memoryFull = s.RamTotalGb > 0 && s.RamUsedGb / s.RamTotalGb >= MemoryFull ? _memoryFull + 1 : 0;

        // Two checks in a row (about a minute), so a short spike isn't news.
        if (_cpuHot == 2) Raise("cpu", "CPU is running hot", $"{s.CpuTemp:0}°C. Check what's busy, and that the vents aren't blocked");
        if (_gpuHot == 2) Raise("gpu", "Graphics card is running hot", $"{s.GpuTemp:0}°C. A game or video app may be pushing it hard");
        if (_memoryFull == 2)
        {
            var top = SystemInfo.Processes() is { } p ? $" · {p.Name} uses {p.Bytes / 1_073_741_824.0:0.0} GB" : "";
            Raise("memory", "Memory is nearly full", $"{s.RamUsedGb:0.0} of {s.RamTotalGb:0} GB in use{top}");
        }

        if (DateTime.Now - _diskCheckedAt > TimeSpan.FromHours(24))
        {
            _diskCheckedAt = DateTime.Now;
            var system = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
            double free = system.AvailableFreeSpace, total = system.TotalSize;
            if (total > 0 && (free < 10L * 1_073_741_824 || free / total < 0.05))
                Raise("disk", $"Drive {system.Name.TrimEnd('\\')} is nearly full", $"{free / 1_073_741_824:0.0} GB free of {total / 1_073_741_824:0} GB");
        }
    }

    private void Raise(string kind, string title, string detail)
    {
        if (_lastAlert.TryGetValue(kind, out var last) && DateTime.Now - last < TimeSpan.FromMinutes(30)) return;
        _lastAlert[kind] = DateTime.Now;
        Log.Write($"Health: {title} ({detail})");
        Alert?.Invoke(title, detail);
    }

    public void Dispose() => _stop.Cancel();
}

/// <summary>A laptop battery's wear: design and current full-charge capacity, charge cycles, and how capacity went over time.</summary>
internal sealed record BatteryWear(int DesignMwh, int FullMwh, int? Cycles, IReadOnlyList<(DateTime Day, int FullMwh)> History)
{
    public double Health => DesignMwh > 0 ? Math.Min(1, (double)FullMwh / DesignMwh) : 0;
}

/// <summary>
/// Reads Windows' battery report (powercfg /batteryreport, no admin needed) twice a day:
/// design capacity, full-charge capacity, cycle count and the capacity history.
/// </summary>
internal static class BatteryReport
{
    private static BatteryWear? _wear;
    private static DateTime _readAt;
    private static bool _reading;

    /// <summary>The last reading (null until the first finishes, or without a battery); starts a fresh one when it's old.</summary>
    public static BatteryWear? Wear
    {
        get
        {
            if (!_reading && DateTime.Now - _readAt > TimeSpan.FromHours(12))
            {
                _reading = true;
                _ = Task.Run(Read);
            }
            return _wear;
        }
    }

    private static void Read()
    {
        var file = Path.Combine(Path.GetTempPath(), $"halobar-battery-{Environment.ProcessId}.xml");
        try
        {
            using (var p = Process.Start(new ProcessStartInfo("powercfg", $"/batteryreport /xml /output \"{file}\"")
                   { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true }))
            {
                if (p == null || !p.WaitForExit(20000)) return;
            }
            if (!File.Exists(file)) return;
            var xml = new XmlDocument();
            xml.Load(file);
            var ns = new XmlNamespaceManager(xml.NameTable);
            ns.AddNamespace("b", xml.DocumentElement!.NamespaceURI);
            var battery = xml.SelectSingleNode("//b:Batteries/b:Battery", ns);
            if (battery == null) return;
            static int? Number(XmlNode? n) => int.TryParse(n?.InnerText, out var v) ? v : null;
            var design = Number(battery.SelectSingleNode("b:DesignCapacity", ns)) ?? 0;
            var full = Number(battery.SelectSingleNode("b:FullChargeCapacity", ns)) ?? 0;
            var cycles = Number(battery.SelectSingleNode("b:CycleCount", ns));
            var history = new List<(DateTime, int)>();
            foreach (XmlNode entry in xml.SelectNodes("//b:History/b:HistoryEntry", ns)!)
            {
                if (DateTime.TryParse(entry.Attributes?["StartDate"]?.Value ?? entry.SelectSingleNode("b:StartDate", ns)?.InnerText, out var day)
                    && Number(entry.Attributes?["FullChargeCapacity"] is { } a ? a : entry.SelectSingleNode("b:FullChargeCapacity", ns)) is { } f && f > 0)
                    history.Add((day, f));
            }
            if (design > 0 && full > 0) _wear = new BatteryWear(design, full, cycles, history);
        }
        catch (Exception ex) when (ex is IOException or XmlException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Write($"Battery report: {ex.Message}");
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { }
            _readAt = DateTime.Now;
            _reading = false;
        }
    }
}
