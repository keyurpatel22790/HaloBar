using System.Runtime.InteropServices;

namespace HaloBar.Services;

/// <summary>
/// Counts time at the PC today: a second counts when there was keyboard or mouse input in the
/// last two minutes. Keeps per-app totals by the app in front, and how long since the last
/// break (five idle minutes). Ticked once a second from the window thread.
/// </summary>
internal sealed class ScreenTimeService(AppSettings settings)
{
    private static readonly TimeSpan Active = TimeSpan.FromMinutes(2), BreakLength = TimeSpan.FromMinutes(5);
    private int _sinceSave;
    private bool _nudged;

    /// <summary>Time on screen since the last break.</summary>
    public TimeSpan Session { get; private set; }
    public TimeSpan Today => TimeSpan.FromSeconds(settings.ScreenTimeSeconds);
    public IEnumerable<(string App, TimeSpan Time)> TopApps =>
        settings.ScreenTimeApps.OrderByDescending(p => p.Value).Take(3).Select(p => (p.Key, TimeSpan.FromSeconds(p.Value)));
    public TimeSpan BreakEvery => TimeSpan.FromMinutes(Math.Max(10, settings.BreakEveryMinutes));

    /// <summary>Raised once per session when it runs past <see cref="BreakEvery"/>.</summary>
    public event Action? BreakDue;

    public void Tick(string frontApp)
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        if (settings.ScreenTimeDay != today)
        {
            settings.ScreenTimeDay = today;
            settings.ScreenTimeSeconds = 0;
            settings.ScreenTimeApps.Clear();
        }
        var idle = IdleTime();
        if (idle >= BreakLength)
        {
            Session = TimeSpan.Zero;
            _nudged = false;
        }
        if (idle >= Active) return;

        settings.ScreenTimeSeconds++;
        Session += TimeSpan.FromSeconds(1);
        if (frontApp.Length > 0)
            settings.ScreenTimeApps[frontApp] = settings.ScreenTimeApps.GetValueOrDefault(frontApp) + 1;
        if (!_nudged && settings.BreakReminder && Session >= BreakEvery)
        {
            _nudged = true;
            BreakDue?.Invoke();
        }
        if (++_sinceSave >= 300)
        {
            _sinceSave = 0;
            settings.Save();
        }
    }

    public static TimeSpan IdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
}
