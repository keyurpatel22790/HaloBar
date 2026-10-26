using HaloBar.Services;
using SkiaSharp;

namespace HaloBar.UI.Tiles;

internal sealed class DateTile(Hub hub) : Tile(hub)
{
    public override string Id => "date";
    public override string Name => "Date";

    public override void Draw(Ui ui, SKRect t)
    {
        var now = ui.Now;
        ui.Text(now.ToString("dddd"), t.Left + 14, t.Top + 24, t.Width - 28, 12, Theme.Weight.SemiBold, Theme.Accent);
        ui.Text(now.Day.ToString(), t.Left + 14, t.Top + 72, t.Width - 28, 42, Theme.Weight.Bold);
        ui.Text(now.ToString("MMMM yyyy"), t.Left + 14, t.Top + 92, t.Width - 28, 11.5f, Theme.Weight.Regular, Theme.Dim);
        if (Hub.Calendar.Next is { } e && e.Start.Date == now.Date)
            ui.Text($"{e.Start:HH:mm} {e.Title}", t.Left + 14, t.Bottom - 12, t.Width - 28, 10.5f, Theme.Weight.Regular, Theme.Dim);
    }
}

internal sealed class WeatherTile : Tile
{
    private readonly TextField _city;

    public WeatherTile(Hub hub) : base(hub) => _city = new TextField("e.g. Ahmedabad", text => _ = Hub.Weather.SetCityAsync(text));

    public override string Id => "weather";
    public override string Name => "Weather";

    public override void Draw(Ui ui, SKRect t)
    {
        if (ui.Focused == _city)
        {
            DrawAsk(ui, t, _city, "City");
            return;
        }
        var w = Hub.Weather.Snapshot;
        var colourful = Settings.WeatherColourful;
        if (w != null && colourful)
        {
            // Tint the tile like the sky.
            var (top, bottom) = w.Code switch
            {
                0 or 1 when w.IsDay => (new SKColor(0x3A, 0x8D, 0xE0), new SKColor(0x7B, 0xC4, 0xF5)),
                2 or 3 when w.IsDay => (new SKColor(0x6C, 0x82, 0x9C), new SKColor(0x9F, 0xB3, 0xC8)),
                >= 51 and <= 67 or >= 80 and <= 82 or >= 95 => (new SKColor(0x3C, 0x4A, 0x5E), new SKColor(0x5D, 0x70, 0x88)),
                >= 71 and <= 77 or 85 or 86 => (new SKColor(0x8C, 0xA0, 0xB8), new SKColor(0xC8, 0xD6, 0xE4)),
                _ when !w.IsDay => (new SKColor(0x14, 0x1E, 0x3C), new SKColor(0x2A, 0x3A, 0x66)),
                _ => (new SKColor(0x5A, 0x6E, 0x88), new SKColor(0x88, 0x9C, 0xB4)),
            };
            using var sky = SKShader.CreateLinearGradient(new SKPoint(t.MidX, t.Top), new SKPoint(t.MidX, t.Bottom), [top, bottom], SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = sky, IsAntialias = true };
            ui.Canvas.DrawRect(t, paint);
        }
        ui.Hit(t, () => Ask(ui, _city, Settings.WeatherCity));
        if (w == null)
        {
            Icons.Cloud(ui.Canvas, t.MidX, t.Top + 46, 30, Theme.Faint);
            ui.Text(Hub.Weather.Status, t.MidX, t.Top + 92, t.Width - 16, 12, Theme.Weight.SemiBold, Theme.Dim, SKTextAlign.Center);
            ui.Text("Click to set a city", t.MidX, t.Top + 110, t.Width - 16, 10.5f, Theme.Weight.Regular, Theme.Faint, SKTextAlign.Center);
            return;
        }
        var text = colourful ? SKColors.White : Theme.Text;
        var dim = colourful ? SKColors.White.WithAlpha(200) : Theme.Dim;
        Icons.Weather(ui.Canvas, t.Right - 28, t.Top + 28, 30, w.Code, !w.IsDay, text, colourful);
        ui.Text($"{Math.Round(w.Temperature)}°", t.Left + 14, t.Top + 66, t.Width - 28, 34, Theme.Weight.Bold, text);
        ui.Text(w.Describe(), t.Left + 14, t.Top + 86, t.Width - 28, 11.5f, Theme.Weight.SemiBold, text);
        ui.Text($"H {Math.Round(w.High)}°  L {Math.Round(w.Low)}°", t.Left + 14, t.Top + 103, t.Width - 28, 10.5f, Theme.Weight.Regular, dim);
        ui.Text(w.Place, t.Left + 14, t.Bottom - 12, t.Width - 28, 10.5f, Theme.Weight.Regular, dim);
    }
}

internal sealed class StatsTile(Hub hub) : Tile(hub)
{
    public override string Id => "stats";
    public override string Name => "Stats";

    public override void Draw(Ui ui, SKRect t)
    {
        Hub.Stats.Touch();
        var s = Hub.Stats.Snapshot;
        var y = t.Top + 22;
        foreach (var (label, value, detail) in new[]
                 {
                     ("CPU", s.Cpu, $"{s.Cpu:P0}"),
                     ("GPU", s.Gpu, $"{s.Gpu:P0}"),
                     ("RAM", s.Ram, $"{s.RamUsedGb:0.0}/{s.RamTotalGb:0} GB"),
                 })
        {
            ui.Text(label, t.Left + 12, y, 40, 10.5f, Theme.Weight.SemiBold, Theme.Dim);
            ui.Text(detail, t.Right - 12, y, t.Width - 50, 10.5f, Theme.Weight.SemiBold, Theme.Text, SKTextAlign.Right);
            ui.Bar(new SKRect(t.Left + 12, y + 5, t.Right - 12, y + 9), value, value > 0.85f ? Bad : Theme.Accent);
            y += 27;
        }
        Icons.Arrow(ui.Canvas, t.Left + 17, t.Bottom - 16, 11, Theme.Dim, up: false);
        ui.Text(StatsService.Rate(s.DownBytesPerSec), t.Left + 25, t.Bottom - 12, t.Width / 2 - 20, 10, Theme.Weight.Regular, Theme.Dim);
        Icons.Arrow(ui.Canvas, t.MidX + 8, t.Bottom - 16, 11, Theme.Dim, up: true);
        ui.Text(StatsService.Rate(s.UpBytesPerSec), t.MidX + 16, t.Bottom - 12, t.Width / 2 - 20, 10, Theme.Weight.Regular, Theme.Dim);
    }
}

internal sealed class TimerTile(Hub hub) : Tile(hub)
{
    public override string Id => "timer";
    public override string Name => "Focus timer";
    public override double FrameInterval => Hub.Timer.Running ? 0.25 : 1;

    public override void Draw(Ui ui, SKRect t)
    {
        var timer = Hub.Timer;
        var cx = t.MidX;
        var cy = t.Top + 52;
        var color = timer.Phase == TimerPhase.Break ? Good : Theme.Accent;
        ui.Arc(cx, cy, 34, timer.Progress, color, 4);
        ui.Text(Ui.Clock(timer.Remaining), cx, cy + 6, 64, 17, Theme.Weight.Bold, Theme.Text, SKTextAlign.Center);
        var label = timer.Phase switch
        {
            TimerPhase.Focus => timer.Paused ? "Paused" : "Focus",
            TimerPhase.Break => timer.Paused ? "Paused" : "Break",
            _ => "Focus timer",
        };
        ui.Text(label, cx, cy - 13, 70, 9.5f, Theme.Weight.SemiBold, Theme.Dim, SKTextAlign.Center);

        var by = t.Bottom - 22;
        if (timer.Phase == TimerPhase.Idle)
        {
            ui.IconButton(cx - 34, by, 11, (c, x, y, s, col) => ui.Text("−", x, y + 5, 20, 15, Theme.Weight.Bold, col, SKTextAlign.Center), () => timer.Step(-5));
            ui.IconButton(cx, by, 13, (c, x, y, s, col) => Icons.Play(c, x + 1, y, s * 0.8f, col), timer.Toggle, filled: true);
            ui.IconButton(cx + 34, by, 11, Icons.Plus, () => timer.Step(5));
            return;
        }
        ui.IconButton(cx - 34, by, 11, Icons.Reset, timer.Reset);
        ui.IconButton(cx, by, 13, timer.Paused ? (c, x, y, s, col) => Icons.Play(c, x + 1, y, s * 0.8f, col) : Icons.Pause, timer.Toggle, filled: true);
        ui.IconButton(cx + 34, by, 11, Icons.Next, timer.Skip);
    }
}

internal sealed class ClockTile(Hub hub) : Tile(hub)
{
    /// <summary>Cities for the world clock; click the tile to move to the next.</summary>
    public static readonly (string City, string Zone)[] Cities =
    [
        ("London", "GMT Standard Time"), ("New York", "Eastern Standard Time"), ("San Francisco", "Pacific Standard Time"),
        ("Chicago", "Central Standard Time"), ("São Paulo", "E. South America Standard Time"), ("Paris", "Romance Standard Time"),
        ("Berlin", "W. Europe Standard Time"), ("Cairo", "Egypt Standard Time"), ("Moscow", "Russian Standard Time"),
        ("Dubai", "Arabian Standard Time"), ("Mumbai", "India Standard Time"), ("Singapore", "Singapore Standard Time"),
        ("Beijing", "China Standard Time"), ("Seoul", "Korea Standard Time"), ("Tokyo", "Tokyo Standard Time"),
        ("Sydney", "AUS Eastern Standard Time"), ("Auckland", "New Zealand Standard Time"),
    ];

    public override string Id => "clock";
    public override string Name => "World clock";

    public override void Draw(Ui ui, SKRect t)
    {
        var (city, zone) = Cities.FirstOrDefault(c => c.City == Settings.WorldClockCity);
        if (city == null) (city, zone) = Cities[0];
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(zone, out var tz)) tz = TimeZoneInfo.Utc;
        var there = TimeZoneInfo.ConvertTime(DateTime.Now, tz);
        var diff = tz.GetUtcOffset(DateTime.UtcNow) - TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
        if (there.Hour is >= 6 and < 18) Icons.Sun(ui.Canvas, t.Right - 24, t.Top + 24, 18, new SKColor(0xFF, 0xC8, 0x3D));
        else Icons.Moon(ui.Canvas, t.Right - 24, t.Top + 24, 18, new SKColor(0xC8, 0xD0, 0xF0));
        ui.Text(city, t.Left + 14, t.Top + 26, t.Width - 50, 12, Theme.Weight.SemiBold, Theme.Accent);
        ui.Text(there.ToShortTimeString(), t.Left + 14, t.Top + 68, t.Width - 20, 26, Theme.Weight.Bold);
        var sign = diff < TimeSpan.Zero ? "−" : "+";
        var offset = diff == TimeSpan.Zero ? "Same time" : $"{sign}{Math.Abs(diff.TotalHours):0.#} h";
        var dayWord = there.Date == DateTime.Now.Date ? "Today" : there.Date > DateTime.Now.Date ? "Tomorrow" : "Yesterday";
        ui.Text($"{dayWord}, {offset}", t.Left + 14, t.Top + 88, t.Width - 28, 10.5f, Theme.Weight.Regular, Theme.Dim);
        ui.Text("Click for next city", t.Left + 14, t.Bottom - 12, t.Width - 28, 9.5f, Theme.Weight.Regular, Theme.Faint);
        ui.Hit(t, () =>
        {
            var i = Array.FindIndex(Cities, c => c.City == city);
            Settings.WorldClockCity = Cities[(i + 1) % Cities.Length].City;
            Settings.Save();
        });
    }
}

internal sealed class TodoTile : Tile
{
    private readonly TextField _new;

    public TodoTile(Hub hub) : base(hub) => _new = new TextField("Something to do", text => Hub.Reminders.AddTodo(text));

    public override string Id => "todo";
    public override string Name => "To-do";

    public override void Draw(Ui ui, SKRect t)
    {
        if (ui.Focused == _new)
        {
            DrawAsk(ui, t, _new, "New to-do");
            return;
        }
        Heading(ui, t, "To-do");
        ui.IconButton(t.Right - 18, t.Top + 18, 10, Icons.Plus, () => Ask(ui, _new));
        var todos = Hub.Reminders.Todos;
        if (todos.Count == 0)
        {
            ui.Text("Nothing to do", t.MidX, t.MidY + 8, t.Width - 16, 11.5f, Theme.Weight.Regular, Theme.Faint, SKTextAlign.Center);
            return;
        }
        var y = t.Top + 44;
        foreach (var item in todos.Take(4))
        {
            var box = new SKRect(t.Left + 12, y - 9, t.Left + 24, y + 3);
            if (item.Done)
            {
                ui.Fill(box, 3.5f, Theme.Accent);
                Icons.Check(ui.Canvas, box.MidX, box.MidY, 10, Theme.OnAccent);
            }
            else
            {
                using var stroke = new SKPaint { Color = Theme.Dim, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.3f };
                ui.Canvas.DrawRoundRect(box, 3.5f, 3.5f, stroke);
            }
            ui.Text(item.Text, t.Left + 30, y, t.Width - 42, 11.5f, Theme.Weight.Regular, item.Done ? Theme.Faint : Theme.Text);
            var chosen = item;
            ui.Hit(new SKRect(t.Left + 6, y - 13, t.Right - 6, y + 7), () => Hub.Reminders.ToggleTodo(chosen));
            y += 22;
        }
    }
}

internal sealed class BatteryTile(Hub hub) : Tile(hub)
{
    public override string Id => "battery";
    public override string Name => "Battery";

    public override void Draw(Ui ui, SKRect t)
    {
        var p = Hub.Power;
        if (!p.HasBattery)
        {
            Icons.Bolt(ui.Canvas, t.MidX, t.Top + 46, 30, Theme.Accent);
            ui.Text("Plugged in", t.MidX, t.Top + 92, t.Width - 16, 12.5f, Theme.Weight.SemiBold, Theme.Text, SKTextAlign.Center);
            ui.Text("No battery", t.MidX, t.Top + 110, t.Width - 16, 10.5f, Theme.Weight.Regular, Theme.Dim, SKTextAlign.Center);
            return;
        }
        Icons.Battery(ui.Canvas, t.Left + 30, t.Top + 26, 30, Theme.Text, p.Percent / 100f, p.Charging);
        ui.Text($"{p.Percent}%", t.Left + 14, t.Top + 76, t.Width - 28, 34, Theme.Weight.Bold);
        var status = p.Charging ? "Charging" : p.PluggedIn ? "Plugged in" : "On battery";
        ui.Text(status, t.Left + 14, t.Top + 96, t.Width - 28, 11.5f, Theme.Weight.SemiBold, Theme.Dim);
        if (p.TimeLeft is { } left && Settings.ShowBatteryEstimate)
            ui.Text($"{(int)left.TotalHours}h {left.Minutes}m left", t.Left + 14, t.Bottom - 12, t.Width - 28, 10.5f, Theme.Weight.Regular, Theme.Dim);
        else if (SystemInfo.BatteryHealth() is { } health)
            ui.Text($"Health {health:P0}", t.Left + 14, t.Bottom - 12, t.Width - 28, 10.5f, Theme.Weight.Regular, Theme.Dim);
    }
}

internal sealed class NextUpTile : Tile
{
    private readonly TextField _url;

    public NextUpTile(Hub hub) : base(hub) => _url = new TextField("https://…/basic.ics", text => _ = Hub.Calendar.SetUrlAsync(text));

    public override string Id => "calendar";
    public override string Name => "Next meeting";

    public override void Draw(Ui ui, SKRect t)
    {
        if (ui.Focused == _url)
        {
            DrawAsk(ui, t, _url, "Calendar link (ICS)");
            return;
        }
        Heading(ui, t, "Next up");
        if (!Hub.Calendar.Configured)
        {
            Note(ui, t, "Add a calendar", "Paste its iCal (ICS) link");
            ui.Hit(t, () => Ask(ui, _url));
            return;
        }
        if (Hub.Calendar.Next is not { } e)
        {
            ui.Text(Hub.Calendar.Status.Length > 0 ? Hub.Calendar.Status : "Nothing coming up", t.MidX, t.MidY + 8, t.Width - 16, 11.5f,
                Theme.Weight.Regular, Theme.Faint, SKTextAlign.Center);
            return;
        }
        ui.Wrapped(e.Title, t.Left + 12, t.Top + 30, t.Width - 24, 2, 12.5f);
        var when = e.Start <= ui.Now ? "Now" : e.Start.Date == ui.Now.Date ? e.Start.ToShortTimeString() : e.Start.ToString("ddd HH:mm");
        ui.Text($"{when} – {e.End.ToShortTimeString()}", t.Left + 12, t.Top + 82, t.Width - 24, 11, Theme.Weight.Regular, Theme.Dim);
        var minutes = (e.Start - ui.Now).TotalMinutes;
        if (minutes > 0 && minutes < 120)
            ui.Text($"in {Math.Ceiling(minutes)} min", t.Left + 12, t.Top + 98, t.Width - 24, 11, Theme.Weight.SemiBold, Theme.Accent);
        if (e.JoinUrl is { } url) ui.Chip(t.Left + 10, t.Bottom - 18, "Join", () => Hub.Open(url), accent: true, size: 11.5f);
    }
}

internal sealed class TickerTile : Tile
{
    private readonly TextField _symbols;

    public TickerTile(Hub hub) : base(hub) => _symbols = new TextField("BTC, ETH, AAPL, ^NSEI", text => Hub.Ticker.SetSymbols(text));

    public override string Id => "ticker";
    public override string Name => "Markets";

    public override void Draw(Ui ui, SKRect t)
    {
        if (ui.Focused == _symbols)
        {
            DrawAsk(ui, t, _symbols, "Symbols, comma-separated");
            return;
        }
        Hub.Ticker.Touch();
        Icons.Chart(ui.Canvas, t.Left + 20, t.Top + 18, 15, Theme.Accent);
        ui.Text("Markets", t.Left + 34, t.Top + 23, t.Width - 46, 12, Theme.Weight.SemiBold, Theme.Accent);
        ui.Hit(t, () => Ask(ui, _symbols, Settings.TickerSymbols));
        var quotes = Hub.Ticker.Quotes;
        if (quotes.Count == 0)
        {
            ui.Text(Hub.Ticker.Status.Length > 0 ? Hub.Ticker.Status : "Loading…", t.MidX, t.MidY + 8, t.Width - 16, 11.5f,
                Theme.Weight.Regular, Theme.Faint, SKTextAlign.Center);
            return;
        }
        var y = t.Top + 46;
        foreach (var q in quotes.Take(4))
        {
            ui.Text(q.Symbol.TrimStart('^'), t.Left + 12, y, 54, 11.5f, Theme.Weight.SemiBold);
            var price = q.Price >= 1000 ? q.Price.ToString("#,0") : q.Price >= 1 ? q.Price.ToString("#,0.00") : q.Price.ToString("0.0000");
            ui.Text(price, t.Right - 12, y, t.Width - 70, 11.5f, Theme.Weight.Regular, Theme.Text, SKTextAlign.Right);
            var up = q.ChangePercent >= 0;
            ui.Text($"{(up ? "▲" : "▼")} {Math.Abs(q.ChangePercent):0.00}%", t.Right - 12, y + 13, t.Width - 24, 9.5f, Theme.Weight.SemiBold,
                up ? Good : Bad, SKTextAlign.Right);
            y += 24;
        }
    }
}

/// <summary>A slow slideshow from a folder of photos.</summary>
internal sealed class PhotosTile : Tile
{
    private readonly TextField _folder;
    private string[]? _photos;
    private int _index = -1;
    private SKImage? _photo;
    private DateTime _shownAt;
    private volatile bool _loading;

    public PhotosTile(Hub hub) : base(hub) => _folder = new TextField("", text =>
    {
        Settings.PhotosFolder = text.Trim().Trim('"');
        Settings.Save();
        _photos = null;
    });

    public override string Id => "photos";
    public override string Name => "Photos";

    public override void Draw(Ui ui, SKRect t)
    {
        if (ui.Focused == _folder)
        {
            DrawAsk(ui, t, _folder, "Photos folder");
            return;
        }
        var folder = Settings.PhotosFolder;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            Icons.Folder(ui.Canvas, t.MidX, t.Top + 48, 30, Theme.Faint);
            ui.Text("Pick a photos folder", t.MidX, t.Top + 92, t.Width - 16, 12, Theme.Weight.SemiBold, Theme.Dim, SKTextAlign.Center);
            ui.Text("Click and paste its path", t.MidX, t.Top + 110, t.Width - 16, 10.5f, Theme.Weight.Regular, Theme.Faint, SKTextAlign.Center);
            ui.Hit(t, () => Ask(ui, _folder, Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)));
            return;
        }
        _photos ??= Directory.EnumerateFiles(folder)
            .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            .OrderBy(_ => Random.Shared.Next()).Take(200).ToArray();
        if (_photos.Length > 0 && !_loading && (_photo == null || DateTime.Now - _shownAt > TimeSpan.FromSeconds(12)))
        {
            _loading = true;
            _index = (_index + 1) % _photos.Length;
            var file = _photos[_index];
            Task.Run(() =>
            {
                try { _photo = Rendering.ImageTools.DecodeSmall(File.ReadAllBytes(file), 420) ?? _photo; }
                catch (IOException) { }
                _shownAt = DateTime.Now;
                _loading = false;
            });
        }
        if (_photo != null) ui.Image(_photo, t, 16);
        else ui.Text(_photos.Length == 0 ? "No photos here" : "Loading…", t.MidX, t.MidY, t.Width, 11.5f, Theme.Weight.Regular, Theme.Faint, SKTextAlign.Center);
        ui.Hit(t, () => _shownAt = default); // click for the next photo
    }
}
