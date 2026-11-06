using HaloBar.Services;
using SkiaSharp;

namespace HaloBar.UI.Panels;

/// <summary>
/// Reminders typed in plain words ("call mum at 6:30pm", "stretch in 45m") on the left,
/// the to-do list on the right.
/// </summary>
internal sealed class RemindersPanel : Panel
{
    private readonly TextField _reminder;
    private readonly TextField _todo;
    private ReminderRepeat _repeat;
    private Spring _scroll = new(0, 300, 1);

    public RemindersPanel(Hub hub) : base(hub)
    {
        _reminder = new TextField("Remind me to… (at 6pm, in 20m, tomorrow 9am)", text =>
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var r = Hub.Reminders.Add(text, _repeat);
            _reminder!.Clear();
            Hub.ShowHud(new Hud(HudIcon.Alarm, r.Text, Detail: When(r.Due)));
        });
        _todo = new TextField("Add a to-do", text =>
        {
            Hub.Reminders.AddTodo(text);
            _todo!.Clear();
        });
    }

    public override string Title => "Reminders";
    public override void Icon(SKCanvas c, float cx, float cy, float size, SKColor color) => Icons.Alarm(c, cx, cy, size, color);
    public override bool Available => Hub.Settings.RemindersPanel;
    public override double FrameInterval => 0.5;

    /// <summary>Focuses the reminder box (the new-reminder hotkey).</summary>
    public TextField ReminderField => _reminder;

    public override IEnumerable<(string Label, Action Click)> Actions =>
        Hub.Reminders.Todos.Any(t => t.Done) ? [("Clear done", Hub.Reminders.ClearDoneTodos)] : [];

    public override void Update(double dt) => _scroll.Step(dt);

    public override bool Wheel(float notches)
    {
        var max = Math.Max(0, Hub.Reminders.Reminders.Count * 26 - 90);
        _scroll.Target = Math.Clamp(_scroll.Target - notches * 26, 0, max);
        return true;
    }

    public override void Draw(Ui ui, SKRect body)
    {
        var split = body.Left + 340;

        // Reminders.
        ui.Field(_reminder, new SKRect(body.Left + 18, body.Top + 2, split - 92, body.Top + 28), 12);
        var repeatLabel = _repeat switch
        {
            ReminderRepeat.Daily => "Daily",
            ReminderRepeat.Weekdays => "Weekdays",
            ReminderRepeat.Weekly => "Weekly",
            _ => "Once",
        };
        ui.Chip(split - 86, body.Top + 15, repeatLabel, () => _repeat = (ReminderRepeat)(((int)_repeat + 1) % 4), _repeat != ReminderRepeat.None, 11.5f);

        // Quick timers: one tap starts a countdown (it shows on the pill and chimes when done).
        var tx = body.Left + 18;
        ui.Text("Timer", tx, body.Top + 44, 44, 11, Theme.Weight.SemiBold, Theme.Dim);
        tx += ui.Measure("Timer", 11, Theme.Weight.SemiBold) + 10;
        foreach (var (label, mins) in new[] { ("5m", 5), ("10m", 10), ("15m", 15), ("25m", 25), ("45m", 45) })
        {
            var m = mins;
            tx = ui.Chip(tx, body.Top + 40, label, () =>
            {
                var r = Hub.Reminders.AddIn(TimeSpan.FromMinutes(m), $"{m}-minute timer");
                Hub.ShowHud(new Hud(HudIcon.Timer, r.Text, Detail: When(r.Due)));
            }, size: 11).Right + 6;
        }

        var list = Hub.Reminders.Reminders;
        var area = new SKRect(body.Left + 12, body.Top + 58, split - 8, body.Bottom - 6);
        if (list.Count == 0)
            ui.Text("No reminders", area.MidX, area.MidY + 4, area.Width, 12, Theme.Weight.Regular, Theme.Faint, SKTextAlign.Center);
        ui.Canvas.Save();
        ui.Canvas.ClipRect(area);
        var y = area.Top + 16 - (float)_scroll.Value;
        foreach (var r in list)
        {
            var row = new SKRect(area.Left + 6, y - 14, area.Right - 4, y + 8);
            if (ui.Hovering(row)) ui.Fill(row, 8, Theme.Overlay(14));
            Icons.Alarm(ui.Canvas, row.Left + 12, y - 3, 13, r.Repeat == ReminderRepeat.None ? Theme.Dim : Theme.Accent);
            var when = When(r.Due) + (r.Repeat == ReminderRepeat.None ? "" : $" · {r.Repeat}");
            var whenWidth = ui.Measure(when, 11) + 8;
            ui.Text(r.Text, row.Left + 26, y, row.Width - 56 - whenWidth, 12.5f);
            ui.Text(when, row.Right - 26, y, whenWidth, 11, Theme.Weight.Regular, Theme.Dim, SKTextAlign.Right);
            var chosen = r;
            ui.IconButton(row.Right - 10, y - 3, 8, Icons.Close, () => Hub.Reminders.Remove(chosen));
            y += 26;
        }
        ui.Canvas.Restore();

        // To-do.
        using (var line = new SKPaint { Color = Theme.Overlay(24), StrokeWidth = 1 })
            ui.Canvas.DrawLine(split, body.Top + 4, split, body.Bottom - 10, line);
        ui.Field(_todo, new SKRect(split + 12, body.Top + 2, body.Right - 18, body.Top + 28), 12);
        y = body.Top + 50;
        foreach (var item in Hub.Reminders.Todos.Take(5))
        {
            var box = new SKRect(split + 16, y - 9, split + 28, y + 3);
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
            ui.Text(item.Text, split + 36, y, body.Right - split - 74, 12, Theme.Weight.Regular, item.Done ? Theme.Faint : Theme.Text);
            var chosen = item;
            ui.Hit(new SKRect(split + 10, y - 13, body.Right - 40, y + 7), () => Hub.Reminders.ToggleTodo(chosen));
            ui.IconButton(body.Right - 28, y - 3, 8, Icons.Close, () => Hub.Reminders.RemoveTodo(chosen));
            y += 22;
        }
        if (Hub.Reminders.Todos.Count > 5)
            ui.Text($"+{Hub.Reminders.Todos.Count - 5} more", split + 16, y, 120, 10.5f, Theme.Weight.Regular, Theme.Faint);
    }

    public static string When(DateTime due)
    {
        var now = DateTime.Now;
        var minutes = (due - now).TotalMinutes;
        if (minutes < 60) return $"in {Math.Max(1, Math.Ceiling(minutes))} min";
        if (due.Date == now.Date) return Prefs.Time(due);
        if (due.Date == now.Date.AddDays(1)) return "Tomorrow " + Prefs.Time(due);
        return due.ToString("ddd ") + Prefs.Time(due);
    }
}
