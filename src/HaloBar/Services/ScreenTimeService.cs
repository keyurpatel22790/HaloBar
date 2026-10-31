using System.Runtime.InteropServices;

namespace HaloBar.Services;

/// <summary>One earlier day: time on screen, the apps used most (seconds), and focus minutes.</summary>
internal sealed class ScreenDay
{
    public string Day { get; set; } = "";
    public int Seconds { get; set; }
    public Dictionary<string, int> Apps { get; set; } = [];
    public int FocusMinutes { get; set; }
}

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
    /// <summary>(title, summary) of last week, once on the first day of a new week.</summary>
    public event Action<string, string>? WeeklyReport;
    private bool _weekChecked;

    /// <summary>Screen time so far this week (Monday on), today included.</summary>
    public TimeSpan ThisWeek
    {
        get
        {
            var monday = Monday(DateTime.Today).ToString("yyyy-MM-dd");
            var earlier = settings.ScreenTimeHistory.Where(d => string.CompareOrdinal(d.Day, monday) >= 0 && d.Day != settings.ScreenTimeDay).Sum(d => d.Seconds);
            return TimeSpan.FromSeconds(earlier + settings.ScreenTimeSeconds);
        }
    }

    public static DateTime Monday(DateTime day) => day.Date.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    public void Tick(string frontApp)
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        if (settings.ScreenTimeDay != today)
        {
            Archive();
            settings.ScreenTimeDay = today;
            settings.ScreenTimeSeconds = 0;
            settings.ScreenTimeApps.Clear();
        }
        if (!_weekChecked)
        {
            _weekChecked = true;
            CheckWeek();
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

    /// <summary>Keeps the day that just ended (its top ten apps), and only the last five weeks.</summary>
    private void Archive()
    {
        if (settings.ScreenTimeDay.Length == 0 || settings.ScreenTimeSeconds == 0) return;
        settings.ScreenTimeHistory.RemoveAll(d => d.Day == settings.ScreenTimeDay);
        settings.ScreenTimeHistory.Add(new ScreenDay
        {
            Day = settings.ScreenTimeDay,
            Seconds = settings.ScreenTimeSeconds,
            Apps = settings.ScreenTimeApps.OrderByDescending(p => p.Value).Take(10).ToDictionary(p => p.Key, p => p.Value),
            FocusMinutes = settings.FocusDay == settings.ScreenTimeDay ? settings.FocusMinutesToday : 0,
        });
        var oldest = DateTime.Today.AddDays(-35).ToString("yyyy-MM-dd");
        settings.ScreenTimeHistory.RemoveAll(d => string.CompareOrdinal(d.Day, oldest) < 0);
        _weekChecked = false; // a new day may be a new week
    }

    private void CheckWeek()
    {
        var monday = Monday(DateTime.Today).ToString("yyyy-MM-dd");
        if (!settings.WeeklyReport || settings.LastWeeklyReport == monday) return;
        if (Summarise(settings.ScreenTimeHistory, Monday(DateTime.Today)) is not { } report) return;
        settings.LastWeeklyReport = monday;
        settings.Save();
        WeeklyReport?.Invoke(report.Title, report.Body);
    }

    /// <summary>Last week (the seven days before <paramref name="thisMonday"/>) in a line, against the week before; null without data.</summary>
    public static (string Title, string Body)? Summarise(IReadOnlyList<ScreenDay> history, DateTime thisMonday)
    {
        IEnumerable<ScreenDay> Week(DateTime from)
        {
            string a = from.ToString("yyyy-MM-dd"), b = from.AddDays(7).ToString("yyyy-MM-dd");
            return history.Where(d => string.CompareOrdinal(d.Day, a) >= 0 && string.CompareOrdinal(d.Day, b) < 0);
        }
        var last = Week(thisMonday.AddDays(-7)).ToList();
        if (last.Count == 0) return null;
        var total = TimeSpan.FromSeconds(last.Sum(d => d.Seconds));
        var before = TimeSpan.FromSeconds(Week(thisMonday.AddDays(-14)).Sum(d => d.Seconds));
        var parts = new List<string> { $"{SystemInfo.Span(total / last.Count)} a day" };
        if (before > TimeSpan.Zero)
        {
            var change = (total - before) / before;
            if (Math.Abs(change) >= 0.05) parts.Add($"{Math.Abs(change):P0} {(change > 0 ? "more" : "less")} than the week before");
        }
        var top = last.SelectMany(d => d.Apps).GroupBy(p => p.Key).Select(g => (App: g.Key, Seconds: g.Sum(p => p.Value)))
            .OrderByDescending(a => a.Seconds).Take(3).Select(a => $"{Pretty(a.App)} {SystemInfo.Span(TimeSpan.FromSeconds(a.Seconds))}");
        var body = string.Join(" · ", parts) + " · " + string.Join(", ", top);
        var focus = last.Sum(d => d.FocusMinutes);
        if (focus > 0) body += $" · {SystemInfo.Span(TimeSpan.FromMinutes(focus))} focused";
        return ($"Last week: {SystemInfo.Span(total)} on screen", body);
    }

    /// <summary>"chrome" → "Chrome", "code" → "VS Code".</summary>
    public static string Pretty(string process) =>
        process.ToLowerInvariant() switch
        {
            "code" => "VS Code", "msedge" => "Edge", "chrome" => "Chrome", "firefox" => "Firefox", "explorer" => "Explorer",
            "windowsterminal" => "Terminal", "devenv" => "Visual Studio", "winword" => "Word", "excel" => "Excel",
            "powerpnt" => "PowerPoint", "olk" or "outlook" => "Outlook", "ms-teams" => "Teams", "claude" => "Claude",
            _ => process.Length > 0 ? char.ToUpperInvariant(process[0]) + process[1..] : process,
        };

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
