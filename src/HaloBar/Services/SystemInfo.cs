using System.Diagnostics;
using Windows.Devices.Power;

namespace HaloBar.Services;

/// <summary>
/// Slow-changing facts about the PC for the Home tiles. Each is read on a worker thread at most
/// every so often and cached, so tiles can ask on every frame.
/// </summary>
internal static class SystemInfo
{
    private static double? _batteryHealth;
    private static DateTime _batteryAt;
    private static (string Name, long Bytes, int Count)? _processes;
    private static DateTime _processesAt;
    private static IReadOnlyList<Drive> _drives = [];
    private static DateTime _drivesAt;
    private static int _busy;

    public sealed record Drive(string Name, string Label, long Total, long Free)
    {
        public double Used => Total == 0 ? 0 : 1 - (double)Free / Total;
    }

    public static TimeSpan Uptime => TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>Full-charge capacity over design capacity, or null without a battery.</summary>
    public static double? BatteryHealth()
    {
        Refresh(ref _batteryAt, TimeSpan.FromMinutes(30), () =>
        {
            var report = Battery.AggregateBattery.GetReport();
            if (report.FullChargeCapacityInMilliwattHours is int full && report.DesignCapacityInMilliwattHours is int design && design > 0)
                _batteryHealth = Math.Min(1, (double)full / design);
        });
        return _batteryHealth;
    }

    /// <summary>The app using the most memory (all its processes together), and how many processes run.</summary>
    public static (string Name, long Bytes, int Count)? Processes()
    {
        Refresh(ref _processesAt, TimeSpan.FromSeconds(10), () =>
        {
            var all = Process.GetProcesses();
            var top = all.Where(p => p.ProcessName is not ("Idle" or "System" or "Registry" or "Memory Compression")).GroupBy(p => p.ProcessName)
                .Select(g => (Name: g.Key, Bytes: g.Sum(p => { try { return p.WorkingSet64; } catch (InvalidOperationException) { return 0L; } })))
                .MaxBy(g => g.Bytes);
            _processes = (top.Name, top.Bytes, all.Length);
            foreach (var p in all) p.Dispose();
        });
        return _processes;
    }

    public static IReadOnlyList<Drive> Drives()
    {
        Refresh(ref _drivesAt, TimeSpan.FromSeconds(30), () =>
        {
            _drives = DriveInfo.GetDrives()
                .Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable && d.IsReady)
                .Select(d => new Drive(d.Name.TrimEnd('\\'), d.VolumeLabel, d.TotalSize, d.AvailableFreeSpace))
                .ToList();
        });
        return _drives;
    }

    public static string Size(long bytes)
    {
        const double GiB = 1024.0 * 1024 * 1024;
        var gb = bytes / GiB;
        return gb >= 1000 ? $"{gb / 1024:0.#} TB" : gb >= 10 ? $"{gb:0} GB" : gb >= 1 ? $"{gb:0.#} GB" : $"{bytes / (1024.0 * 1024):0} MB";
    }

    public static string Span(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{t.Minutes}m";

    /// <summary>Runs <paramref name="read"/> on a worker when the cached value is older than <paramref name="every"/>.</summary>
    private static void Refresh(ref DateTime at, TimeSpan every, Action read)
    {
        if (DateTime.UtcNow - at < every) return;
        at = DateTime.UtcNow;
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            at = default; // try again next frame
            return;
        }
        Task.Run(() =>
        {
            try { read(); }
            catch (Exception ex) { Log.Write($"System info: {ex.Message}"); }
            finally { Volatile.Write(ref _busy, 0); }
        });
    }
}
