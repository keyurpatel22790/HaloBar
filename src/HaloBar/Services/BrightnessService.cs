using System.Management;

namespace HaloBar.Services;

/// <summary>
/// The built-in display's brightness, through the WMI monitor-brightness classes that laptop
/// panels expose. Desktop monitors don't (they speak DDC/CI instead), so <see cref="Supported"/>
/// is false there.
/// </summary>
internal sealed class BrightnessService : IDisposable
{
    private ManagementEventWatcher? _watcher;

    public bool Supported { get; private set; }
    /// <summary>0..1.</summary>
    public float Level { get; private set; }
    public event Action? Changed;

    public void Start()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (var mo in searcher.Get())
            {
                using (mo)
                {
                    Level = Convert.ToByte(mo["CurrentBrightness"]) / 100f;
                    Supported = true;
                    break;
                }
            }
            if (!Supported)
            {
                Log.Write("Brightness: no WMI-controllable display (desktop monitor?)");
                return;
            }
            _watcher = new ManagementEventWatcher(new ManagementScope(@"root\WMI"), new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            _watcher.EventArrived += (_, e) =>
            {
                Level = Convert.ToByte(e.NewEvent["Brightness"]) / 100f;
                Changed?.Invoke();
            };
            _watcher.Start();
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            Supported = false;
            Log.Write($"Brightness: unavailable: {ex.Message}");
        }
    }

    /// <summary>Sets brightness 0..1. Runs off the calling thread: WMI calls take tens of milliseconds.</summary>
    public void Set(float level)
    {
        if (!Supported) return;
        var value = (byte)Math.Round(Math.Clamp(level, 0, 1) * 100);
        Level = value / 100f;
        Task.Run(() =>
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
                foreach (var mo in searcher.Get())
                    using (var o = (ManagementObject)mo)
                        o.InvokeMethod("WmiSetBrightness", [(uint)1, value]);
            }
            catch (Exception ex)
            {
                Log.Write($"Brightness: set failed: {ex.Message}");
            }
        });
    }

    public void Dispose()
    {
        try { _watcher?.Stop(); } catch { }
        _watcher?.Dispose();
    }
}
