using HaloBar.Services;
using NAudio.CoreAudioApi;
using SkiaSharp;

namespace HaloBar.UI.Panels;

/// <summary>
/// Quick settings: Wi-Fi, Bluetooth, Night light, sound and microphone switches, volume and
/// brightness, the default speaker and microphone, and who is using the camera or microphone.
/// </summary>
internal sealed class ControlsPanel(Hub hub, ControlsService controls) : Panel(hub)
{
    private string? _output, _input;
    private DateTime _devicesReadAt;

    public override string Title => "Controls";
    public override void Icon(SKCanvas c, float cx, float cy, float size, SKColor color) => Icons.Sliders(c, cx, cy, size, color);
    public override double FrameInterval => 0.5;

    public override void Opened() => _devicesReadAt = default;

    public override void Draw(Ui ui, SKRect body)
    {
        // Device names are read at most every few seconds: enumerating endpoints isn't free.
        if (DateTime.Now - _devicesReadAt > TimeSpan.FromSeconds(3))
        {
            _output = ControlsService.DefaultName(DataFlow.Render);
            _input = ControlsService.DefaultName(DataFlow.Capture);
            _devicesReadAt = DateTime.Now;
        }

        // Switches.
        var tiles = new (string Label, Action<SKCanvas, float, float, float, SKColor> Icon, bool? On, Action Toggle)[]
        {
            ("Wi-Fi", Icons.Wifi, controls.WifiOn, controls.ToggleWifi),
            ("Bluetooth", Icons.Bluetooth, controls.BluetoothOn, controls.ToggleBluetooth),
            ("Night light", Icons.Moon, controls.NightLightOn, controls.ToggleNightLight),
            ("Sound", (c, x, y, s, col) => Icons.Speaker(c, x, y, s, col, Hub.Volume.Volume, Hub.Volume.Muted), !Hub.Volume.Muted, Hub.Volume.ToggleMute),
            ("Microphone", Hub.Microphone.Muted ? Icons.MicOff : Icons.Mic, Hub.Microphone.Available ? !Hub.Microphone.Muted : null, Hub.Microphone.ToggleMute),
            ("Record", Icons.Record, Hub.Recorder.Recording, Hub.ToggleRecording),
        };
        const float gap = 8;
        var w = (body.Width - 36 - gap * (tiles.Length - 1)) / tiles.Length;
        for (var i = 0; i < tiles.Length; i++)
        {
            var (label, icon, on, toggle) = tiles[i];
            var x = body.Left + 18 + i * (w + gap);
            var r = new SKRect(x, body.Top + 2, x + w, body.Top + 50);
            var active = on == true;
            ui.Fill(r, 14, active ? Theme.Accent : Theme.Overlay(ui.Hovering(r) ? (byte)26 : (byte)14));
            var color = on == null ? Theme.Faint : active ? Theme.OnAccent : Theme.Text;
            icon(ui.Canvas, r.Left + 20, r.MidY - 7, 17, color);
            ui.Text(label, r.Left + 10, r.Bottom - 9, r.Width - 16, 10.5f, Theme.Weight.SemiBold, color);
            ui.Text(on == null ? "—" : active ? "On" : "Off", r.Right - 10, r.Top + 18, 30, 10, Theme.Weight.Regular, color.WithAlpha(180), SKTextAlign.Right);
            if (on != null) ui.Hit(r, toggle);
        }

        // Sliders: click anywhere on the track to set; the wheel works too.
        var y = body.Top + 68;
        Slider(ui, new SKRect(body.Left + 18, y - 10, body.MidX - 10, y + 10), Hub.Volume.Volume,
            (c, x, yy, s, col) => Icons.Speaker(c, x, yy, s, col, Hub.Volume.Volume, Hub.Volume.Muted),
            v => Hub.Volume.Nudge(v - Hub.Volume.Volume));
        if (Hub.Brightness.Supported)
            Slider(ui, new SKRect(body.MidX + 10, y - 10, body.Right - 18, y + 10), Hub.Brightness.Level, Icons.Sun, Hub.Brightness.Set);
        else
            ui.Text("Brightness: this display isn't controllable", body.MidX + 10, y + 4, body.Width / 2 - 28, 11, Theme.Weight.Regular, Theme.Faint);

        // Devices.
        y = body.Top + 100;
        var chip = ui.Chip(body.Left + 18, y, $"Speaker  ·  {Short(_output)}", () =>
        {
            _output = ControlsService.CycleDefault(DataFlow.Render);
            if (_output != null) Hub.ShowHud(new Hud(HudIcon.Volume, _output, Detail: "Output"));
        }, size: 11.5f);
        ui.Chip(chip.Right + 8, y, $"Mic  ·  {Short(_input)}", () =>
        {
            _input = ControlsService.CycleDefault(DataFlow.Capture);
            if (_input != null) Hub.ShowHud(new Hud(HudIcon.Mic, _input, Detail: "Input"));
        }, size: 11.5f);

        // Privacy.
        y = body.Top + 132;
        var p = Hub.Privacy;
        var who = new List<string>();
        if (p.CameraInUse) who.Add("Camera: " + string.Join(", ", p.CameraApps));
        if (p.MicrophoneInUse) who.Add("Microphone: " + string.Join(", ", p.MicrophoneApps));
        if (who.Count == 0) who.Add("Camera and microphone not in use");
        var dot = p.CameraInUse ? new SKColor(0x34, 0xC7, 0x59) : p.MicrophoneInUse ? new SKColor(0xFF, 0x9F, 0x0A) : Theme.Faint;
        ui.Circle(body.Left + 22, y - 4, 3.5f, dot);
        ui.Text(string.Join("   ·   ", who), body.Left + 32, y, body.Width - 50, 11, Theme.Weight.Regular, Theme.Dim);
    }

    /// <summary>"Microphone Array (Intel® Smart Sound Technology for…)" → "Microphone Array".</summary>
    private static string Short(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "none";
        var paren = name.IndexOf(" (", StringComparison.Ordinal);
        var s = paren > 0 ? name[..paren] : name;
        return s.Length > 26 ? s[..25] + "…" : s;
    }

    private void Slider(Ui ui, SKRect r, float value, Action<SKCanvas, float, float, float, SKColor> icon, Action<float> set)
    {
        icon(ui.Canvas, r.Left + 10, r.MidY, 16, Theme.Text);
        var track = new SKRect(r.Left + 28, r.MidY - 3, r.Right - 38, r.MidY + 3);
        ui.Bar(track, value, Theme.Text);
        ui.Circle(track.Left + track.Width * value, track.MidY, 6, Theme.Text);
        ui.Text($"{Math.Round(value * 100)}%", r.Right, r.MidY + 4, 36, 11, Theme.Weight.SemiBold, Theme.Dim, SKTextAlign.Right);
        ui.Hit(new SKRect(track.Left - 6, r.Top, track.Right + 6, r.Bottom), () =>
        {
            if (ui.Cursor is { } c) set(Math.Clamp((c.X - track.Left) / track.Width, 0, 1));
        });
    }
}
