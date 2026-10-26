using System.Globalization;
using System.Net.NetworkInformation;
using HaloBar.Services;
using SkiaSharp;
using Windows.Networking.Connectivity;

namespace HaloBar.UI.Tiles;

/// <summary>Air quality, sunrise and sunset, UV and the moon, for the weather's place.</summary>
internal sealed class AirSunTile(Hub hub) : Tile(hub)
{
    public override string Id => "air";
    public override string Name => "Air & sun";

    public override void Draw(Ui ui, SKRect t)
    {
        var air = Hub.Weather.Air;
        var w = Hub.Weather.Snapshot;
        var phase = Moon.Phase(DateTime.UtcNow);
        Heading(ui, t, "Air quality");
        Moon.Draw(ui.Canvas, t.Right - 20, t.Top + 18, 8, phase, Theme.Text, Theme.Faint);
        var half = t.Width > 200 ? new SKRect(t.Left, t.Top, t.MidX, t.Bottom) : t;
        if (air == null)
        {
            Note(ui, half, w == null ? "No place yet" : "Loading…", w == null ? "Set a city on Weather" : "");
        }
        else
        {
            var color = air.Aqi switch { <= 50 => Good, <= 100 => new SKColor(0xFF, 0xD6, 0x0A), <= 150 => Warn, <= 200 => Bad, _ => new SKColor(0xAF, 0x52, 0xDE) };
            ui.Text(air.Aqi.ToString(), half.Left + 12, t.Top + 62, 70, 30, Theme.Weight.Bold, color);
            ui.Text("AQI", half.Left + 18 + ui.Measure(air.Aqi.ToString(), 30, Theme.Weight.Bold), t.Top + 62, 40, 10, Theme.Weight.SemiBold, Theme.Dim);
            ui.Text(air.Describe(), half.Left + 12, t.Top + 80, half.Width - 24, 11, Theme.Weight.SemiBold);
        }
        if (w?.Sunrise is { } rise && w.Sunset is { } set)
        {
            ui.Text($"Sunrise {rise:HH:mm}", half.Left + 12, t.Top + 102, half.Width - 24, 10.5f, Theme.Weight.Regular, Theme.Dim);
            ui.Text($"Sunset {set:HH:mm}", half.Left + 12, t.Bottom - 12, half.Width - 24, 10.5f, Theme.Weight.Regular, Theme.Dim);
        }
        if (t.Width <= 200) return;

        // Wide: the details beside it.
        var x = t.MidX + 4;
        var rows = new List<(string, string)>();
        if (air != null)
        {
            rows.Add(("PM2.5", $"{air.Pm25:0} µg/m³"));
            rows.Add(("PM10", $"{air.Pm10:0} µg/m³"));
        }
        if (w != null) rows.Add(("UV today", w.Uv switch { < 3 => $"{w.Uv:0} Low", < 6 => $"{w.Uv:0} Moderate", < 8 => $"{w.Uv:0} High", _ => $"{w.Uv:0} Very high" }));
        rows.Add(("Moon", $"{Moon.Name(phase)}, {Moon.Lit(phase):P0}"));
        var y = t.Top + 44;
        foreach (var (label, value) in rows)
        {
            ui.Text(label, x, y, 60, 10, Theme.Weight.SemiBold, Theme.Dim);
            ui.Text(value, t.Right - 12, y, t.Right - x - 66, 10.5f, Theme.Weight.Regular, Theme.Text, SKTextAlign.Right);
            y += 20;
        }
    }
}

/// <summary>The moon's phase, worked out from the date.</summary>
internal static class Moon
{
    private static readonly DateTime KnownNew = new(2000, 1, 6, 18, 14, 0, DateTimeKind.Utc);
    private const double Synodic = 29.530588853;

    /// <summary>0 is new, 0.5 full, back to 1.</summary>
    public static double Phase(DateTime utc)
    {
        var p = (utc - KnownNew).TotalDays / Synodic % 1;
        return p < 0 ? p + 1 : p;
    }

    public static double Lit(double phase) => (1 - Math.Cos(2 * Math.PI * phase)) / 2;

    public static string Name(double p) => p switch
    {
        < 0.03 or > 0.97 => "New moon",
        < 0.22 => "Waxing crescent",
        < 0.28 => "First quarter",
        < 0.47 => "Waxing gibbous",
        < 0.53 => "Full moon",
        < 0.72 => "Waning gibbous",
        < 0.78 => "Last quarter",
        _ => "Waning crescent",
    };

    /// <summary>The moon as seen from the north: lit from the right while waxing.</summary>
    public static void Draw(SKCanvas c, float cx, float cy, float r, double phase, SKColor lit, SKColor dark)
    {
        using var paint = new SKPaint { Color = dark, IsAntialias = true };
        c.DrawCircle(cx, cy, r, paint);
        var k = Math.Cos(2 * Math.PI * phase);
        var side = phase < 0.5 ? 1 : -1;
        using var path = new SKPathBuilder();
        const int steps = 24;
        for (var i = 0; i <= steps; i++)
        {
            var a = -Math.PI / 2 + Math.PI * i / steps;
            var x = (float)(cx + side * r * Math.Cos(a));
            var y = (float)(cy + r * Math.Sin(a));
            if (i == 0) path.MoveTo(x, y);
            else path.LineTo(x, y);
        }
        for (var i = steps; i >= 0; i--)
        {
            var a = -Math.PI / 2 + Math.PI * i / steps;
            path.LineTo((float)(cx + side * k * r * Math.Cos(a)), (float)(cy + r * Math.Sin(a)));
        }
        path.Close();
        paint.Color = lit;
        using var shape = path.Detach();
        c.DrawPath(shape, paint);
    }
}

/// <summary>This month as a grid, with dots on days that have calendar events.</summary>
internal sealed class MonthTile(Hub hub) : Tile(hub)
{
    private int _monthOffset;
    private DateTime? _picked;

    public override string Id => "month";
    public override string Name => "Month";
    public override bool WideByDefault => true;

    public override void Draw(Ui ui, SKRect t)
    {
        var today = ui.Now.Date;
        var month = new DateTime(today.Year, today.Month, 1).AddMonths(_monthOffset);
        var wide = t.Width > 200;
        var grid = wide ? new SKRect(t.Left, t.Top, t.Left + Math.Min(150, t.Width / 2 + 10), t.Bottom) : t;

        ui.Text(month.ToString(month.Year == today.Year ? "MMMM" : "MMM yyyy"), grid.Left + 12, t.Top + 20, grid.Width - 50, 11.5f, Theme.Weight.SemiBold, Theme.Accent);
        ui.IconButton(grid.Right - 34, t.Top + 16, 8, (c, x, y, s, col) => Chevron(c, x, y, s, col, -1), () => { _monthOffset--; _picked = null; });
        ui.IconButton(grid.Right - 16, t.Top + 16, 8, (c, x, y, s, col) => Chevron(c, x, y, s, col, 1), () => { _monthOffset++; _picked = null; });

        var first = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var lead = ((int)month.DayOfWeek - (int)first + 7) % 7;
        var days = DateTime.DaysInMonth(month.Year, month.Month);
        var rows = (lead + days + 6) / 7;
        var cellW = (grid.Width - 16) / 7;
        var top = t.Top + 40;
        var cellH = Math.Min(16, (t.Bottom - 6 - top) / rows);
        var names = CultureInfo.CurrentCulture.DateTimeFormat.ShortestDayNames;
        for (var i = 0; i < 7; i++)
            ui.Text(names[((int)first + i) % 7][..1], grid.Left + 8 + cellW * (i + 0.5f), t.Top + 33, cellW, 8.5f, Theme.Weight.SemiBold, Theme.Faint, SKTextAlign.Center);

        var busy = BusyDays(month);
        for (var d = 1; d <= days; d++)
        {
            var slot = lead + d - 1;
            var cx = grid.Left + 8 + cellW * (slot % 7 + 0.5f);
            var cy = top + cellH * (slot / 7 + 0.5f);
            var date = new DateTime(month.Year, month.Month, d);
            var isToday = date == today;
            if (isToday) ui.Circle(cx, cy, Math.Min(cellW, cellH) / 2 + 0.5f, Theme.Accent);
            else if (date == _picked) ui.Circle(cx, cy, Math.Min(cellW, cellH) / 2 + 0.5f, Theme.Overlay(36));
            var weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            ui.Text(d.ToString(), cx, cy + 3.2f, cellW, 9, isToday ? Theme.Weight.Bold : Theme.Weight.Regular,
                isToday ? Theme.OnAccent : weekend ? Theme.Dim : Theme.Text, SKTextAlign.Center);
            if (busy.Contains(d)) ui.Circle(cx, cy + 6.5f, 1.3f, isToday ? Theme.OnAccent : Theme.Accent);
            if (wide) ui.Hit(new SKRect(cx - cellW / 2, cy - cellH / 2, cx + cellW / 2, cy + cellH / 2), () => _picked = date);
        }
        if (!wide) return;

        // Wide: that day's events beside the grid.
        var day = _picked ?? (month.Month == today.Month && month.Year == today.Year ? today : month);
        var x = grid.Right + 6;
        var width = t.Right - 12 - x;
        ui.Text(day == today ? "Today" : day.ToString("ddd d MMM"), x, t.Top + 20, width, 11.5f, Theme.Weight.SemiBold);
        if (!Hub.Calendar.Configured)
        {
            ui.Text("Add a calendar on", x, t.Top + 44, width, 10.5f, Theme.Weight.Regular, Theme.Faint);
            ui.Text("the Next meeting tile", x, t.Top + 58, width, 10.5f, Theme.Weight.Regular, Theme.Faint);
            return;
        }
        var events = Hub.Calendar.Events.Where(e => e.Start.Date <= day && (e.AllDay ? e.End.Date > day : e.End.Date >= day)).Take(4).ToList();
        if (events.Count == 0)
        {
            ui.Text("Nothing on", x, t.Top + 44, width, 10.5f, Theme.Weight.Regular, Theme.Faint);
            return;
        }
        var y = t.Top + 42;
        foreach (var e in events)
        {
            ui.Fill(new SKRect(x, y - 9, x + 3, y + 13), 1.5f, Theme.Accent);
            ui.Text(e.Title, x + 9, y, width - 9, 10.5f, Theme.Weight.SemiBold);
            ui.Text(e.AllDay ? "All day" : $"{e.Start:HH:mm} – {e.End:HH:mm}", x + 9, y + 12, width - 9, 9.5f, Theme.Weight.Regular, Theme.Dim);
            y += 26;
        }
    }

    private HashSet<int> BusyDays(DateTime month)
    {
        var set = new HashSet<int>();
        var next = month.AddMonths(1);
        foreach (var e in Hub.Calendar.Events)
        {
            var end = e.AllDay ? e.End.Date.AddDays(-1) : e.End.Date;
            for (var d = e.Start.Date; d <= end && d < next; d = d.AddDays(1))
                if (d >= month) set.Add(d.Day);
        }
        return set;
    }

    private static void Chevron(SKCanvas c, float cx, float cy, float size, SKColor color, int dir)
    {
        using var p = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, StrokeCap = SKStrokeCap.Round };
        var h = size * 0.35f;
        c.DrawLine(cx - dir * h * 0.5f, cy - h, cx + dir * h * 0.5f, cy, p);
        c.DrawLine(cx + dir * h * 0.5f, cy, cx - dir * h * 0.5f, cy + h, p);
    }
}

/// <summary>Time at the screen today, since the last break, and the apps it went to.</summary>
internal sealed class ScreenTimeTile(Hub hub) : Tile(hub)
{
    public override string Id => "screentime";
    public override string Name => "Screen time";

    public override void Draw(Ui ui, SKRect t)
    {
        var st = Hub.ScreenTime;
        var half = t.Width > 200 ? new SKRect(t.Left, t.Top, t.MidX, t.Bottom) : t;
        Heading(ui, half, "Screen time");
        ui.Text(SystemInfo.Span(st.Today), half.Left + 12, t.Top + 58, half.Width - 24, 24, Theme.Weight.Bold);
        ui.Text("today", half.Left + 12, t.Top + 74, half.Width - 24, 10.5f, Theme.Weight.Regular, Theme.Dim);
        var over = st.Session >= st.BreakEvery;
        ui.Text(over ? "Take a break" : $"{SystemInfo.Span(st.Session)} since a break", half.Left + 12, t.Top + 100, half.Width - 24, 10.5f,
            Theme.Weight.SemiBold, over ? Warn : Theme.Text);
        ui.Bar(new SKRect(half.Left + 12, t.Top + 107, half.Right - 12, t.Top + 111), (float)(st.Session / st.BreakEvery), over ? Warn : Good);
        var apps = st.TopApps.ToList();
        if (t.Width <= 200)
        {
            if (apps.Count > 0)
                ui.Text($"{Pretty(apps[0].App)} {SystemInfo.Span(apps[0].Time)}", t.Left + 12, t.Bottom - 10, t.Width - 24, 10, Theme.Weight.Regular, Theme.Dim);
            return;
        }
        var x = t.MidX + 4;
        var width = t.Right - 12 - x;
        ui.Text("Most used", x, t.Top + 22, width, 11, Theme.Weight.SemiBold, Theme.Dim);
        var y = t.Top + 46;
        var most = apps.Count > 0 ? apps[0].Time.TotalSeconds : 1;
        foreach (var (app, time) in apps)
        {
            ui.Text(Pretty(app), x, y, width - 50, 10.5f, Theme.Weight.SemiBold);
            ui.Text(SystemInfo.Span(time), t.Right - 12, y, 50, 10, Theme.Weight.Regular, Theme.Dim, SKTextAlign.Right);
            ui.Bar(new SKRect(x, y + 5, t.Right - 12, y + 8), (float)(time.TotalSeconds / most), Theme.Accent);
            y += 26;
        }
    }

    private static string Pretty(string process) =>
        process.ToLowerInvariant() switch
        {
            "code" => "VS Code", "msedge" => "Edge", "chrome" => "Chrome", "firefox" => "Firefox", "explorer" => "Explorer",
            "windowsterminal" => "Terminal", "devenv" => "Visual Studio", "winword" => "Word", "excel" => "Excel",
            "powerpnt" => "PowerPoint", "olk" or "outlook" => "Outlook", "ms-teams" => "Teams", "claude" => "Claude",
            _ => process.Length > 0 ? char.ToUpperInvariant(process[0]) + process[1..] : process,
        };
}

/// <summary>Free space on each drive.</summary>
internal sealed class DiskTile(Hub hub) : Tile(hub)
{
    public override string Id => "disk";
    public override string Name => "Storage";

    public override void Draw(Ui ui, SKRect t)
    {
        Heading(ui, t, "Storage");
        var drives = SystemInfo.Drives();
        if (drives.Count == 0)
        {
            Note(ui, t, "Reading drives…", "");
            return;
        }
        var wide = t.Width > 200;
        var y = t.Top + 44;
        var gap = drives.Count <= 2 ? 36f : 28f;
        foreach (var d in drives.Take(wide ? 3 : 3))
        {
            var label = wide && d.Label.Length > 0 ? $"{d.Name} {d.Label}" : d.Name;
            ui.Text(label, t.Left + 12, y, t.Width / 2, 10.5f, Theme.Weight.SemiBold);
            var detail = wide ? $"{SystemInfo.Size(d.Free)} free of {SystemInfo.Size(d.Total)}" : $"{SystemInfo.Size(d.Free)} free";
            ui.Text(detail, t.Right - 12, y, t.Width - 50, 10, Theme.Weight.Regular, Theme.Dim, SKTextAlign.Right);
            ui.Bar(new SKRect(t.Left + 12, y + 5, t.Right - 12, y + 9), (float)d.Used, d.Used > 0.9 ? Bad : d.Used > 0.8 ? Warn : Theme.Accent);
            var root = d.Name + "\\";
            ui.Hit(new SKRect(t.Left, y - 14, t.Right, y + 12), () => Hub.Open(root));
            y += gap;
        }
    }
}

/// <summary>The connection: Wi-Fi name and bars, ping, speed now and data used this month.</summary>
internal sealed class NetworkTile(Hub hub) : Tile(hub)
{
    private string _name = "";
    private byte? _bars;
    private bool _wifi, _online = true;
    private long? _ping;
    private long? _month;
    private DateTime _infoAt, _pingAt, _monthAt;
    private volatile bool _pinging, _counting;

    public override string Id => "network";
    public override string Name => "Network";

    public override void Draw(Ui ui, SKRect t)
    {
        Refresh();
        Hub.Stats.Touch();
        var s = Hub.Stats.Snapshot;
        var wide = t.Width > 200;
        Icons.Wifi(ui.Canvas, t.Left + 20, t.Top + 18, 15, _online ? Theme.Accent : Theme.Faint);
        ui.Text(_online ? _name.Length > 0 ? _name : "Connected" : "Offline", t.Left + 34, t.Top + 23, t.Width - 70, 12, Theme.Weight.SemiBold, Theme.Accent);
        if (_wifi && _bars is { } bars) Bars(ui, t.Right - 26, t.Top + 23, bars);

        ui.Text(_ping is { } ms ? $"{ms} ms" : _online ? "…" : "—", t.Left + 12, t.Top + 58, t.Width / 2, 22, Theme.Weight.Bold,
            _ping is > 150 ? Warn : Theme.Text);
        ui.Text("ping", t.Left + 12, t.Top + 72, 60, 10, Theme.Weight.Regular, Theme.Dim);

        var col = wide ? t.MidX + 4 : t.Left + 12;
        var y = wide ? t.Top + 50 : t.Top + 98;
        if (wide)
        {
            ui.Text("This month", col, y, t.Right - col - 12, 10, Theme.Weight.SemiBold, Theme.Dim);
            ui.Text(_month is { } bytes ? SystemInfo.Size(bytes) : "…", col, y + 22, t.Right - col - 12, 20, Theme.Weight.Bold);
            y = t.Bottom - 12;
        }
        else ui.Text(_month is { } bytes ? $"{SystemInfo.Size(bytes)} this month" : "Counting…", col, y, t.Width - 24, 10.5f, Theme.Weight.SemiBold);

        var rateY = wide ? y : t.Bottom - 10;
        Icons.Arrow(ui.Canvas, col + 5, rateY - 4, 10, Theme.Dim, up: false);
        ui.Text(StatsService.Rate(s.DownBytesPerSec), col + 12, rateY, 50, 9.5f, Theme.Weight.Regular, Theme.Dim);
        var upX = wide ? col + 70 : t.MidX + 4;
        Icons.Arrow(ui.Canvas, upX + 5, rateY - 4, 10, Theme.Dim, up: true);
        ui.Text(StatsService.Rate(s.UpBytesPerSec), upX + 12, rateY, 50, 9.5f, Theme.Weight.Regular, Theme.Dim);
    }

    private void Bars(Ui ui, float x, float baseline, byte bars)
    {
        for (var i = 0; i < 4; i++)
        {
            var h = 3 + i * 2.5f;
            ui.Fill(new SKRect(x + i * 4.5f, baseline - h, x + i * 4.5f + 3, baseline), 1, i < Math.Max((byte)1, Math.Min(bars, (byte)4)) ? Theme.Text : Theme.Faint);
        }
    }

    private void Refresh()
    {
        var now = DateTime.UtcNow;
        if (now - _infoAt > TimeSpan.FromSeconds(10))
        {
            _infoAt = now;
            try
            {
                var profile = NetworkInformation.GetInternetConnectionProfile();
                _online = profile != null;
                _wifi = profile?.IsWlanConnectionProfile == true;
                _name = _wifi ? profile!.WlanConnectionProfileDetails.GetConnectedSsid() : profile?.ProfileName ?? "";
                _bars = profile?.GetSignalBars();
            }
            catch (Exception ex)
            {
                Log.Write($"Network tile: {ex.Message}");
            }
        }
        if (!_pinging && now - _pingAt > TimeSpan.FromSeconds(5))
        {
            _pingAt = now;
            _pinging = true;
            Task.Run(async () =>
            {
                try
                {
                    using var ping = new Ping();
                    var reply = await ping.SendPingAsync("1.1.1.1", 1500);
                    _ping = reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
                }
                catch (PingException) { _ping = null; }
                finally { _pinging = false; }
            });
        }
        if (!_counting && now - _monthAt > TimeSpan.FromMinutes(5))
        {
            _monthAt = now;
            _counting = true;
            Task.Run(async () =>
            {
                try
                {
                    var start = new DateTimeOffset(DateTime.Today.Year, DateTime.Today.Month, 1, 0, 0, 0, DateTimeOffset.Now.Offset);
                    var states = new NetworkUsageStates { Roaming = TriStates.DoNotCare, Shared = TriStates.DoNotCare };
                    long total = 0;
                    foreach (var profile in NetworkInformation.GetConnectionProfiles())
                    {
                        var usage = await profile.GetNetworkUsageAsync(start, DateTimeOffset.Now, DataUsageGranularity.Total, states);
                        foreach (var u in usage) total += (long)(u.BytesReceived + u.BytesSent);
                    }
                    _month = total;
                }
                catch (Exception ex)
                {
                    Log.Write($"Network tile: usage unavailable: {ex.Message}");
                }
                finally { _counting = false; }
            });
        }
    }
}

/// <summary>Uptime, the hungriest app and battery health.</summary>
internal sealed class SystemTile(Hub hub) : Tile(hub)
{
    public override string Id => "system";
    public override string Name => "This PC";

    public override void Draw(Ui ui, SKRect t)
    {
        Heading(ui, t, Environment.MachineName.Length > 0 && t.Width > 200 ? Environment.MachineName : "This PC");
        var rows = new List<(string Label, string Value)> { ("Up for", SystemInfo.Span(SystemInfo.Uptime)) };
        if (SystemInfo.Processes() is { } p)
        {
            rows.Add(("Most memory", $"{p.Name} · {SystemInfo.Size(p.Bytes)}"));
            rows.Add(("Processes", p.Count.ToString()));
        }
        if (SystemInfo.BatteryHealth() is { } health) rows.Add(("Battery health", $"{health:P0}"));
        var wide = t.Width > 200;
        for (var i = 0; i < rows.Count; i++)
        {
            var (label, value) = rows[i];
            var x = wide && i % 2 == 1 ? t.MidX + 4 : t.Left + 12;
            var y = wide ? t.Top + 48 + i / 2 * 40 : t.Top + 42 + i * 30;
            if (!wide && y > t.Bottom - 6) break;
            var width = wide ? t.Width / 2 - 16 : t.Width - 24;
            ui.Text(label, x, y, width, 9.5f, Theme.Weight.Regular, Theme.Dim);
            ui.Text(value, x, y + 14, width, 11.5f, Theme.Weight.SemiBold);
        }
    }
}
