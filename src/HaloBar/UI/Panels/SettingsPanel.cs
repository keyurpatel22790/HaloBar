using HaloBar.Platform;
using HaloBar.Services;
using SkiaSharp;

namespace HaloBar.UI.Panels;

/// <summary>
/// Every switch in one scrolling list: HUDs, music, island behaviour, startup. Changes apply
/// immediately and are saved.
/// </summary>
internal sealed class SettingsPanel(Hub hub, Action applyLayout) : Panel(hub)
{
    private const float RowHeight = 30;
    private Spring _scroll = new(0, 300, 1);

    public override string Title => "Settings";
    public override void Icon(SKCanvas c, float cx, float cy, float size, SKColor color) => Icons.Gear(c, cx, cy, size, color);
    public override double FrameInterval => _scroll.Settled ? 0.5 : 0;

    public override void Update(double dt) => _scroll.Step(dt);

    private sealed record Row(string Label, string? Hint, Func<string>? Value, Action Click, Func<bool>? On = null, string Suffix = "");

    private IEnumerable<(string Section, Row[] Rows)> Sections()
    {
        var s = Hub.Settings;
        Row Toggle(string label, string? hint, Func<bool> get, Action<bool> set) =>
            new(label, hint, null, () => { set(!get()); s.Save(); }, get);
        Row Choice(string label, string? hint, string[] options, Func<string> get, Action<string> set, string suffix = "") =>
            new(label, hint, get, () => { set(options[(Array.IndexOf(options, get()) + 1) % options.Length]); s.Save(); }, Suffix: suffix);

        yield return ("HUDs", [
            Toggle("Volume", "Show volume changes in the island", () => s.ShowVolumeHud, v => s.ShowVolumeHud = v),
            Toggle("Hide the Windows volume pop-up", "The island is the only read-out", () => s.ReplaceVolumePopup, v => { s.ReplaceVolumePopup = v; applyLayout(); }),
            Toggle("Brightness", "Laptop screens", () => s.ShowBrightnessHud, v => s.ShowBrightnessHud = v),
            Toggle("Battery", "Plugged in, unplugged, low battery", () => s.ShowBatteryHud, v => s.ShowBatteryHud = v),
            Toggle("Battery time left", "Windows' estimate, on the Battery tile", () => s.ShowBatteryEstimate, v => s.ShowBatteryEstimate = v),
            Toggle("Bluetooth devices", "Connected and disconnected", () => s.ShowBluetoothHud, v => s.ShowBluetoothHud = v),
            Toggle("Caps, Num and Scroll Lock", null, () => s.ShowLockKeysHud, v => s.ShowLockKeysHud = v),
            Toggle("Microphone mute", "Ctrl+Alt+M mutes and unmutes", () => s.ShowMicHud, v => s.ShowMicHud = v),
            Toggle("Camera and microphone in use", "Green and orange dots on the pill", () => s.ShowPrivacyIndicator, v => s.ShowPrivacyIndicator = v),
        ]);
        yield return ("Music", [
            Toggle("Lyrics", "The line being sung, from LRCLIB (sends title, artist and length)", () => s.ShowLyrics, v => s.ShowLyrics = v),
            Choice("Visualizer", "Beside the artwork on the pill", ["bars", "pixels"], () => s.Visualizer, v => s.Visualizer = v),
            Choice("Progress bar", null, ["wave", "simple", "pixels", "comet", "hearts"], () => s.ProgressBar, v => s.ProgressBar = v),
        ]);
        yield return ("Look and place", [
            Choice("Shape", "Island floats below the edge; Notch hangs from it", ["island", "notch"], () => s.Shape, v => { s.Shape = v; applyLayout(); }),
            Choice("Position", "Along the top edge", ["center", "left", "right"], () => s.Position, v => { s.Position = v; applyLayout(); }),
            Choice("Size", "The pill, the card and the text together", ["100", "110", "120", "90"], () => s.Size.ToString(), v => { s.Size = int.Parse(v); applyLayout(); }, "%"),
            Choice("Corners", "From square to fully round", ["100", "75", "50", "25", "0"], () => s.CornerRoundness.ToString(), v => s.CornerRoundness = int.Parse(v), "%"),
            Choice("Distance from top", null, ["6", "0", "10", "16"], () => s.TopGap.ToString(), v => s.TopGap = int.Parse(v), " px"),
            Choice("Animation speed", null, ["normal", "quick", "relaxed"], () => s.AnimationSpeed, v => { s.AnimationSpeed = v; applyLayout(); }),
            Toggle("Hidden until hover", "Nothing at the top until the pointer rests there", () => s.HiddenUntilHover, v => s.HiddenUntilHover = v),
            Toggle("Slim over maximised windows", "A thin line so it never covers browser tabs", () => s.SlimWhenCovered, v => s.SlimWhenCovered = v),
            Toggle("Game strip", "CPU, GPU, memory and time while a game is full screen", () => s.GameStrip, v => s.GameStrip = v),
        ]);
        yield return ("Integrations", [
            Toggle("OBS Studio", "Recording and streaming on the pill; Ctrl+Alt+R records", () => s.ObsEnabled, v => s.ObsEnabled = v),
            Toggle("Use OBS's own settings", "Reads the websocket port and password from OBS on this PC", () => s.ObsAuto, v => s.ObsAuto = v),
            Toggle("AI agents", "Claude Code and others: working, waiting, done", () => s.AgentsEnabled, v => s.AgentsEnabled = v),
            Toggle("Notify when an agent finishes", null, () => s.AgentsNotify, v => s.AgentsNotify = v),
        ]);
        yield return ("Panels", [
            Toggle("Notifications", null, () => s.NotificationsPanel, v => s.NotificationsPanel = v),
            Toggle("Reminders and to-dos", null, () => s.RemindersPanel, v => s.RemindersPanel = v),
            Toggle("Apps", "Six launcher slots", () => s.LauncherPanel, v => s.LauncherPanel = v),
            Toggle("Phone", "Send files to and from your phone with a QR code", () => s.PhoneShare, v => s.PhoneShare = v),
            Toggle("Call", "A camera mirror and microphone switch", () => s.CameraEnabled, v => s.CameraEnabled = v),
            Toggle("Chat", "A Twitch channel's live chat", () => s.ChatPanel, v => s.ChatPanel = v),
            Toggle("Chat pop-ups", "New messages on the pill while the chat is closed", () => s.ChatPopups, v => s.ChatPopups = v),
        ]);
        yield return ("App rules", [.. AppRuleRows()]);
        yield return ("Island", [
            Toggle("Hide when something is full screen", null, () => s.HideInFullscreen, v => s.HideInFullscreen = v),
            Toggle("Hide from screenshots and sharing", null, () => s.HideFromCapture, v => { s.HideFromCapture = v; applyLayout(); }),
            Toggle("Every monitor", "One island per screen", () => s.AllMonitors, v => { s.AllMonitors = v; applyLayout(); }),
            Choice("Open delay", "How long the pointer rests before it opens", ["0", "150", "300", "600"],
                () => s.HoverDelayMs.ToString(), v => { s.HoverDelayMs = int.Parse(v); applyLayout(); }, " ms"),
            Toggle("Meeting countdown", "On the pill before a calendar meeting", () => s.MeetingCountdown, v => s.MeetingCountdown = v),
            Toggle("Clipboard history", null, () => s.ClipboardEnabled, v => s.ClipboardEnabled = v),
            Toggle("Mirror Windows notifications", "Takes effect after a restart", () => s.MirrorNotifications, v => s.MirrorNotifications = v),
            new("Start with Windows", null, null, () => StartupRegistration.Set(!StartupRegistration.IsEnabled), () => StartupRegistration.IsEnabled),
        ]);
    }

    /// <summary>A rule for the app in front, then the rules already made.</summary>
    private IEnumerable<Row> AppRuleRows()
    {
        var s = Hub.Settings;
        string[] options = ["normal", "slim", "hide"];
        Row RuleRow(string process, string? hint) => new(process, hint, () =>
            s.AppRules.FirstOrDefault(r => r.Process.Equals(process, StringComparison.OrdinalIgnoreCase))?.Behaviour ?? "normal", () =>
        {
            var rule = s.AppRules.FirstOrDefault(r => r.Process.Equals(process, StringComparison.OrdinalIgnoreCase));
            var next = options[(Array.IndexOf(options, rule?.Behaviour ?? "normal") + 1) % options.Length];
            if (next == "normal") s.AppRules.RemoveAll(r => r.Process.Equals(process, StringComparison.OrdinalIgnoreCase));
            else if (rule != null) rule.Behaviour = next;
            else s.AppRules.Add(new AppRule { Process = process, Behaviour = next });
            s.Save();
        });
        var front = Hub.FrontApp;
        if (front.Length > 0 && !s.AppRules.Any(r => r.Process.Equals(front, StringComparison.OrdinalIgnoreCase)))
            yield return RuleRow(front, "the app in front: click to slim or hide the island over it");
        foreach (var rule in s.AppRules.ToList()) yield return RuleRow(rule.Process, null);
    }

    private int RowCount => Sections().Sum(s => s.Rows.Length + 1);

    public override bool Wheel(float notches)
    {
        var max = Math.Max(0, RowCount * RowHeight - 140);
        _scroll.Target = Math.Clamp(_scroll.Target - notches * RowHeight * 1.5f, 0, max);
        return true;
    }

    public override void Draw(Ui ui, SKRect body)
    {
        var area = new SKRect(body.Left + 8, body.Top, body.Right - 8, body.Bottom - 6);
        ui.Canvas.Save();
        ui.Canvas.ClipRect(area);
        var y = body.Top + 16 - (float)_scroll.Value;
        foreach (var (section, rows) in Sections())
        {
            ui.Text(section.ToUpperInvariant(), body.Left + 20, y, 200, 10.5f, Theme.Weight.SemiBold, Theme.Accent);
            y += RowHeight * 0.8f;
            foreach (var row in rows)
            {
                var r = new SKRect(body.Left + 12, y - 18, body.Right - 12, y + 8);
                if (y > area.Top - 20 && y < area.Bottom + 20)
                {
                    if (ui.Hovering(r) && ui.Hovering(area)) ui.Fill(r, 10, Theme.Overlay(12));
                    ui.Text(row.Label, r.Left + 8, y, 260, 12.5f);
                    if (row.Hint != null)
                        ui.Text(row.Hint, r.Left + 16 + Math.Min(250, ui.Measure(row.Label, 12.5f)), y, r.Width - 330, 11, Theme.Weight.Regular, Theme.Faint);
                    if (row.On != null) ui.Switch(r.Right - 44, y - 5, row.On(), row.Click);
                    else if (row.Value != null)
                    {
                        var label = row.Value() + row.Suffix;
                        ui.Chip(r.Right - 12 - ui.Measure(label, 11.5f) - 22, y - 5, label, row.Click, true, 11.5f);
                    }
                    if (ui.Hovering(area)) ui.Hit(SKRect.Intersect(r, area), row.Click);
                }
                y += RowHeight;
            }
            y += 4;
        }
        ui.Canvas.Restore();
    }
}
