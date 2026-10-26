using System.Globalization;
using HaloBar.Services;
using SkiaSharp;

namespace HaloBar.UI.Tiles;

/// <summary>A sticky note: one line you keep in view.</summary>
internal sealed class NoteTile : Tile
{
    private readonly TextField _text;

    public NoteTile(Hub hub) : base(hub) => _text = new TextField("Jot something down", text =>
    {
        Settings.NoteText = text.Trim();
        Settings.Save();
    });

    public override string Id => "note";
    public override string Name => "Note";

    public override void Draw(Ui ui, SKRect t)
    {
        if (ui.Focused == _text)
        {
            DrawAsk(ui, t, _text, "Note");
            return;
        }
        // A paper-yellow card so it reads as a note on any surface.
        ui.Fill(t, 0, new SKColor(0xFF, 0xD6, 0x0A, 36));
        Heading(ui, t, "Note");
        if (Settings.NoteText.Length == 0) Note(ui, t, "Empty", "Click to write");
        else ui.Wrapped(Settings.NoteText, t.Left + 12, t.Top + 30, t.Width - 24, (int)((t.Height - 40) / (12.5f * 1.3f)), 12.5f);
        ui.Hit(t, () => Ask(ui, _text, Settings.NoteText));
    }
}

/// <summary>Days left until a date you name ("Diwali 1 Nov").</summary>
internal sealed class CountdownTile : Tile
{
    private readonly TextField _text;

    public CountdownTile(Hub hub) : base(hub) => _text = new TextField("Trip 20 Dec", text =>
    {
        if (Parse(text, DateTime.Today) is not { } parsed) return;
        (Settings.CountdownTitle, Settings.CountdownDate) = (parsed.Title, parsed.Date);
        Settings.Save();
    });

    public override string Id => "countdown";
    public override string Name => "Countdown";

    public override void Draw(Ui ui, SKRect t)
    {
        if (ui.Focused == _text)
        {
            DrawAsk(ui, t, _text, "What and when");
            return;
        }
        var initial = Settings.CountdownDate is { } d0 ? $"{Settings.CountdownTitle} {d0:d MMM yyyy}".Trim() : "";
        ui.Hit(t, () => Ask(ui, _text, initial));
        if (Settings.CountdownDate is not { } date)
        {
            Heading(ui, t, "Countdown");
            Note(ui, t, "Count down to…", "e.g. “Trip 20 Dec”");
            return;
        }
        Heading(ui, t, Settings.CountdownTitle.Length > 0 ? Settings.CountdownTitle : "Countdown");
        var days = (date.Date - ui.Now.Date).Days;
        var big = days switch { 0 => "Today", _ => Math.Abs(days).ToString() };
        ui.Text(big, t.Left + 12, t.Top + 72, t.Width - 24, days == 0 ? 28 : 40, Theme.Weight.Bold, days == 0 ? Theme.Accent : Theme.Text);
        if (days != 0)
            ui.Text(days > 0 ? Math.Abs(days) == 1 ? "day to go" : "days to go" : Math.Abs(days) == 1 ? "day ago" : "days ago",
                t.Left + 12, t.Top + 92, t.Width - 24, 11.5f, Theme.Weight.SemiBold, Theme.Dim);
        ui.Text(date.ToString("ddd d MMM yyyy"), t.Left + 12, t.Bottom - 12, t.Width - 24, 10.5f, Theme.Weight.Regular, Theme.Dim);
        if (days is > 0 and <= 60 && t.Width > 200)
            ui.Text($"{days / 7} weeks {days % 7} days", t.Right - 12, t.Top + 72, t.Width / 2 - 20, 11, Theme.Weight.Regular, Theme.Dim, SKTextAlign.Right);
    }

    private static readonly string[] Formats =
        ["d MMM yyyy", "d MMMM yyyy", "MMM d yyyy", "MMMM d yyyy", "yyyy-MM-dd", "d/M/yyyy", "d-M-yyyy", "d.M.yyyy",
         "d MMM", "d MMMM", "MMM d", "MMMM d", "d/M", "d-M"];

    /// <summary>
    /// Splits "Trip to Goa 20 Dec" into a title and the next 20 December. A date without a year
    /// that has already passed this year means next year. Day-first, as most of the world writes.
    /// </summary>
    public static (string Title, DateTime Date)? Parse(string text, DateTime today)
    {
        var words = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var take = Math.Min(3, words.Length); take >= 1; take--)
        {
            var tail = string.Join(' ', words[^take..]).TrimEnd(',', '.');
            foreach (var culture in new[] { CultureInfo.CurrentCulture, CultureInfo.InvariantCulture })
            {
                if (!DateTime.TryParseExact(tail, Formats, culture, DateTimeStyles.AllowWhiteSpaces, out var date)) continue;
                var hasYear = tail.Any(char.IsDigit) && System.Text.RegularExpressions.Regex.IsMatch(tail, @"\d{4}");
                if (!hasYear && date.Date < today.Date) date = date.AddYears(1);
                return (string.Join(' ', words[..^take]), date.Date);
            }
        }
        return null;
    }
}

/// <summary>A tally: glasses of water, push-ups, cups of chai.</summary>
internal sealed class CounterTile : Tile
{
    private readonly TextField _name;

    public CounterTile(Hub hub) : base(hub) => _name = new TextField("What are you counting?", text =>
    {
        Settings.CounterName = text.Trim().Length > 0 ? text.Trim() : "Count";
        Settings.Save();
    });

    public override string Id => "counter";
    public override string Name => "Counter";

    public override void Draw(Ui ui, SKRect t)
    {
        if (ui.Focused == _name)
        {
            DrawAsk(ui, t, _name, "Name");
            return;
        }
        Heading(ui, t, Settings.CounterName);
        ui.Hit(new SKRect(t.Left, t.Top, t.Right - 30, t.Top + 30), () => Ask(ui, _name, Settings.CounterName));
        ui.IconButton(t.Right - 18, t.Top + 18, 9, Icons.Reset, () => Change(-Settings.CounterValue));
        ui.Text(Settings.CounterValue.ToString(), t.MidX, t.Top + 78, t.Width - 24, 40, Theme.Weight.Bold, Theme.Text, SKTextAlign.Center);
        var by = t.Bottom - 22;
        ui.IconButton(t.MidX - 26, by, 13, (c, x, y, s, col) => ui.Text("−", x, y + 6, 20, 18, Theme.Weight.Bold, col, SKTextAlign.Center), () => Change(-1));
        ui.IconButton(t.MidX + 26, by, 13, Icons.Plus, () => Change(1), filled: true);
    }

    private void Change(int by)
    {
        Settings.CounterValue += by;
        Settings.Save();
    }
}

/// <summary>A stopwatch with laps.</summary>
internal sealed class StopwatchTile(Hub hub) : Tile(hub)
{
    private DateTime? _since;
    private TimeSpan _banked;
    private readonly List<TimeSpan> _laps = [];

    public override string Id => "stopwatch";
    public override string Name => "Stopwatch";
    public override double FrameInterval => _since != null ? 0.05 : 1;

    private TimeSpan Elapsed => _banked + (_since is { } s ? DateTime.Now - s : TimeSpan.Zero);

    public override void Draw(Ui ui, SKRect t)
    {
        var wide = t.Width > 200;
        var face = wide ? new SKRect(t.Left, t.Top, t.MidX + 10, t.Bottom) : t;
        Heading(ui, face, "Stopwatch");
        var e = Elapsed;
        var text = e.TotalHours >= 1 ? e.ToString(@"h\:mm\:ss") : e.ToString(@"m\:ss\.f");
        ui.Text(text, face.MidX, t.Top + 68, face.Width - 16, 26, Theme.Weight.Bold, Theme.Text, SKTextAlign.Center);
        if (!wide && _laps.Count > 0)
            ui.Text($"Lap {_laps.Count}  {Format(_laps[^1])}", face.MidX, t.Top + 86, face.Width - 16, 10, Theme.Weight.Regular, Theme.Dim, SKTextAlign.Center);

        var by = t.Bottom - 22;
        var running = _since != null;
        if (running || e > TimeSpan.Zero)
            ui.IconButton(face.MidX - 30, by, 11, running ? Icons.Flag : Icons.Reset, running ? Lap : Reset);
        ui.IconButton(face.MidX, by, 13, running ? Icons.Pause : (c, x, y, s, col) => Icons.Play(c, x + 1, y, s * 0.8f, col), Toggle, filled: true);
        if (!wide) return;

        var x = t.MidX + 18;
        var y = t.Top + 24;
        if (_laps.Count == 0) ui.Text("Laps show here", x, y, t.Right - x - 12, 10.5f, Theme.Weight.Regular, Theme.Faint);
        for (var i = _laps.Count - 1; i >= 0 && y < t.Bottom - 4; i--)
        {
            ui.Text($"Lap {i + 1}", x, y, 50, 10.5f, Theme.Weight.Regular, Theme.Dim);
            ui.Text(Format(_laps[i]), t.Right - 12, y, t.Right - x - 60, 10.5f, Theme.Weight.SemiBold, Theme.Text, SKTextAlign.Right);
            y += 19;
        }
    }

    private static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss\.ff");

    private void Toggle()
    {
        if (_since is { } s)
        {
            _banked += DateTime.Now - s;
            _since = null;
        }
        else _since = DateTime.Now;
    }

    private void Lap() => _laps.Add(Elapsed - _laps.Aggregate(TimeSpan.Zero, (a, b) => a + b));

    private void Reset()
    {
        _since = null;
        _banked = TimeSpan.Zero;
        _laps.Clear();
    }
}
