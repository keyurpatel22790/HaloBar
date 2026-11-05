using SkiaSharp;

namespace HaloBar.UI;

internal enum HudIcon
{
    Volume, Brightness, Battery, Charging, Bluetooth, LockKey, Mic, MicOff, Camera, Timer, Alarm, Copied, Download, Color, Info,
    Record, Agent, Game, Chat,
}

/// <summary>
/// A quick read-out that takes over the pill for a moment: a level (volume, brightness) with a bar,
/// or a short message (device connected, Caps Lock on, timer done).
/// </summary>
internal sealed record Hud(HudIcon Icon, string Text, float? Level = null, string? Detail = null, SKColor? Tint = null)
{
    public static readonly TimeSpan DefaultTime = TimeSpan.FromSeconds(1.8);

    /// <summary>The pill's width for this HUD, in DIPs.</summary>
    public float Width(Ui ui)
    {
        if (Level != null) return 300;
        var w = 62 + ui.Measure(Text, 13, Theme.Weight.SemiBold) + (Detail == null ? 0 : 14 + ui.Measure(Detail, 12));
        return Math.Clamp(w, 200, 440);
    }

    public void Draw(Ui ui, SKRect r, Action? onIcon = null)
    {
        var t = ui.Theme;
        var tint = Tint ?? t.Text;
        DrawIcon(ui.Canvas, r.Left + 24, r.MidY, 20, tint);
        if (onIcon != null) ui.Hit(new SKRect(r.Left, r.Top, r.Left + 46, r.Bottom), onIcon);
        if (Level is { } level)
        {
            var track = new SKRect(r.Left + 46, r.MidY - 2.5f, r.Right - 58, r.MidY + 2.5f);
            ui.Bar(track, level, Text == "Muted" ? t.Dim : t.Text);
            ui.Text(Text, r.Right - 18, r.MidY + 4.5f, 44, 12.5f, Theme.Weight.SemiBold, t.Text, SKTextAlign.Right);
            return;
        }
        var x = r.Left + 46;
        ui.Text(Text, x, r.MidY + 4.6f, r.Width - 62, 13, Theme.Weight.SemiBold, t.Text);
        if (Detail != null)
            ui.Text(Detail, r.Right - 18, r.MidY + 4.4f, r.Width - 62 - ui.Measure(Text, 13, Theme.Weight.SemiBold) - 14, 12,
                Theme.Weight.Regular, t.Dim, SKTextAlign.Right);
    }

    public void DrawIcon(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        switch (Icon)
        {
            case HudIcon.Volume: Icons.Speaker(c, cx, cy, size, color, Level ?? 1, Text == "Muted"); break;
            case HudIcon.Brightness: Icons.Sun(c, cx, cy, size, color); break;
            case HudIcon.Battery: Icons.Battery(c, cx, cy, size, color, Level ?? 0.5f, false); break;
            case HudIcon.Charging: Icons.Battery(c, cx, cy, size, color, Level ?? 0.5f, true); break;
            case HudIcon.Bluetooth: Icons.Bluetooth(c, cx, cy, size, color); break;
            case HudIcon.LockKey: Icons.Lock(c, cx, cy, size, color); break;
            case HudIcon.Mic: Icons.Mic(c, cx, cy, size, color); break;
            case HudIcon.MicOff: Icons.MicOff(c, cx, cy, size, color); break;
            case HudIcon.Camera: Icons.Camera(c, cx, cy, size, color); break;
            case HudIcon.Timer: Icons.Timer(c, cx, cy, size, color); break;
            case HudIcon.Alarm: Icons.Alarm(c, cx, cy, size, color); break;
            case HudIcon.Copied: Icons.Check(c, cx, cy, size, color); break;
            case HudIcon.Download: Icons.Download(c, cx, cy, size, color); break;
            case HudIcon.Color: Icons.Drop(c, cx, cy, size, color); break;
            case HudIcon.Record: Icons.Record(c, cx, cy, size, color); break;
            case HudIcon.Agent: Icons.Spark(c, cx, cy, size, color); break;
            case HudIcon.Game: Icons.Gamepad(c, cx, cy, size, color); break;
            case HudIcon.Chat: Icons.Bubble(c, cx, cy, size, color); break;
            default: Icons.Info(c, cx, cy, size, color); break;
        }
    }
}

/// <summary>
/// Something going on that deserves the resting pill: a running timer, a meeting about to start,
/// a recording. The highest-priority activity replaces the clock or the song on the pill.
/// </summary>
internal sealed record Activity(int Priority, HudIcon Icon, string Text, SKColor Color, float? Progress = null, string? Right = null,
    string? ButtonLabel = null, Action? Button = null)
{
    /// <summary>The pill's button (a meeting's Join), drawn at the right end; clicking it doesn't open the island.</summary>
    private float ButtonWidth(Ui ui) => Button == null || ButtonLabel == null ? 0 : ui.Measure(ButtonLabel, 11.5f) + 22 + 8;

    // A running clock keeps one width ("1:11" as wide as "0:00"), so the pill doesn't resize every second.
    public float Width(Ui ui) =>
        Math.Clamp(58 + ui.Measure(Text, 12.5f, Theme.Weight.SemiBold) + (Right == null ? 0 : 12 + ui.Measure(SteadyDigits(Right), 12.5f, Theme.Weight.SemiBold)) + ButtonWidth(ui), 170, 420);

    private static string SteadyDigits(string s) => new(s.Select(c => char.IsAsciiDigit(c) ? '0' : c).ToArray());

    public void Draw(Ui ui, SKRect r)
    {
        var t = ui.Theme;
        if (Progress is { } p) ui.Arc(r.Left + 19, r.MidY, 9, p, Color, 2.4f);
        else new Hud(Icon, Text).DrawIcon(ui.Canvas, r.Left + 19, r.MidY, 15, Color);
        if (Progress != null) new Hud(Icon, Text).DrawIcon(ui.Canvas, r.Left + 19, r.MidY, 9, Color);
        var button = ButtonWidth(ui);
        if (Button != null && ButtonLabel != null)
            ui.Chip(r.Right - 8 - (button - 8), r.MidY, ButtonLabel, Button, accent: true, size: 11.5f);
        var right = r.Right - button;
        var rightWidth = Right == null ? 0 : ui.Measure(SteadyDigits(Right), 12.5f, Theme.Weight.SemiBold) + 12;
        ui.Text(Text, r.Left + 36, r.MidY + 4.5f, right - r.Left - 50 - rightWidth, 12.5f, Theme.Weight.SemiBold, t.Text);
        if (Right != null) ui.Text(Right, right - 14, r.MidY + 4.5f, rightWidth, 12.5f, Theme.Weight.SemiBold, t.Readable(Color), SKTextAlign.Right);
    }
}
