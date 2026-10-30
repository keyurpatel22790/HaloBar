using HaloBar.Services;
using NAudio.CoreAudioApi;
using SkiaSharp;

namespace HaloBar.UI.Panels;

/// <summary>
/// Quick settings: Wi-Fi, Bluetooth, Night light, sound and microphone switches, volume and
/// brightness, the default speaker and microphone, and who is using the camera or microphone.
/// </summary>
internal sealed class ControlsPanel : Panel
{
    private readonly ControlsService controls;
    private string? _output, _input;
    private DateTime _devicesReadAt;

    /// <summary>The main page, or the list of Wi-Fi networks or Bluetooth devices.</summary>
    private enum Page { Main, Wifi, Bluetooth }
    private Page _page;
    private Spring _scroll = new(0, 300, 1);
    private float _listHeight;
    private readonly TextField _password;

    public ControlsPanel(Hub hub, ControlsService controls) : base(hub)
    {
        this.controls = controls;
        _password = new TextField("Password", text =>
        {
            if (Hub.Wifi.Networks.FirstOrDefault(n => n.Ssid == Hub.Wifi.NeedsPassword) is { } network) Hub.Wifi.Connect(network, text);
            _password!.Clear();
        }) { Secret = true };
    }

    public override string Title => "Controls";
    public override bool Available => Hub.Settings.ControlsPanel;
    public override void Icon(SKCanvas c, float cx, float cy, float size, SKColor color) => Icons.Sliders(c, cx, cy, size, color);
    public override double FrameInterval => _page == Page.Main ? 0.5 : 0.25;

    public override void Opened()
    {
        _devicesReadAt = default;
        Hub.Wifi.Refresh();
    }

    public override void Closed()
    {
        if (_page == Page.Bluetooth) Hub.Bluetooth.StopSearch();
        _page = Page.Main;
    }

    /// <summary>"controls:wifi" and "controls:bluetooth" open those lists.</summary>
    public override void GoTo(string place)
    {
        if (place.Equals("wifi", StringComparison.OrdinalIgnoreCase)) Show(Page.Wifi);
        else if (place.Equals("bluetooth", StringComparison.OrdinalIgnoreCase)) Show(Page.Bluetooth);
        else Show(Page.Main);
    }

    private void Show(Page page)
    {
        if (_page == Page.Bluetooth && page != Page.Bluetooth) Hub.Bluetooth.StopSearch();
        _page = page;
        _scroll = new(0, 300, 1);
        if (page == Page.Wifi) Hub.Wifi.ScanIfStale();
        if (page == Page.Bluetooth) Hub.Bluetooth.ClearStatus();
    }

    public override IEnumerable<(string Label, Action Click)> Actions => _page switch
    {
        Page.Wifi or Page.Bluetooth => [("Back", () => Show(Page.Main))],
        _ => [],
    };

    private static void Open(string uri)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write($"Controls: couldn't open {uri}: {ex.Message}"); }
    }

    public override void Update(double dt) => _scroll.Step(dt);

    public override bool Wheel(float notches)
    {
        if (_page == Page.Main) return false;
        _scroll.Target = Math.Clamp(_scroll.Target - notches * 28, 0, Math.Max(0, _listHeight));
        return true;
    }

    public override void Draw(Ui ui, SKRect body)
    {
        switch (_page)
        {
            case Page.Wifi:
                DrawWifi(ui, body);
                return;
            case Page.Bluetooth:
                DrawBluetooth(ui, body);
                return;
        }

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
            ("Mic", Hub.Microphone.Muted ? Icons.MicOff : Icons.Mic, Hub.Microphone.Available ? !Hub.Microphone.Muted : null, Hub.Microphone.ToggleMute),
            ("Record", Icons.Record, Hub.Recorder.Recording, Hub.ToggleRecording),
            ("Quiet", Icons.Bell, Hub.Settings.DoNotDisturb, Hub.ToggleDoNotDisturb),
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
        var wifiChip = ui.Chip(body.Left + 18, y, $"Wi-Fi · {Short(Hub.Wifi.ConnectedSsid ?? (controls.WifiOn == false ? "off" : "networks"), 14)} ›", () => Show(Page.Wifi), size: 11.5f);
        var connected = Hub.Bluetooth.Connected.Count;
        var btChip = ui.Chip(wifiChip.Right + 8, y, $"Bluetooth · {(controls.BluetoothOn == false ? "off" : connected == 0 ? "devices" : $"{connected} on")} ›", () => Show(Page.Bluetooth), size: 11.5f);
        var chip = ui.Chip(btChip.Right + 8, y, $"Speaker · {Short(_output, 14)}", () =>
        {
            _output = ControlsService.CycleDefault(DataFlow.Render);
            if (_output != null) Hub.ShowHud(new Hud(HudIcon.Volume, _output, Detail: "Output"));
        }, size: 11.5f);
        ui.Chip(chip.Right + 8, y, $"Mic · {Short(_input, 14)}", () =>
        {
            _input = ControlsService.CycleDefault(DataFlow.Capture);
            if (_input != null) Hub.ShowHud(new Hud(HudIcon.Mic, _input, Detail: "Input"));
        }, size: 11.5f);

        // Profiles: the first tap saves how things are now; later taps switch back to it.
        y = body.Top + 132;
        var px = body.Left + 18;
        ui.Text("Profile", px, y + 4, 60, 11, Theme.Weight.SemiBold, Theme.Dim);
        px += ui.Measure("Profile", 11, Theme.Weight.SemiBold) + 10;
        var profiles = Hub.Profiles;
        foreach (var name in ProfileService.Names)
        {
            var label = profiles.IsSaved(name) ? name : name + " +";
            var chosen = name;
            px = ui.Chip(px, y, label, () =>
            {
                var what = profiles.Use(chosen);
                Hub.ShowHud(new Hud(HudIcon.Info, $"{chosen} profile", Detail: what == "Saved" ? "Saved as it is now" : null));
            }, on: profiles.Active == name, size: 11).Right + 6;
        }
        if (profiles.Active.Length > 0)
            px = ui.Chip(px + 4, y, "Update", () =>
            {
                profiles.SaveAs(profiles.Active);
                Hub.ShowHud(new Hud(HudIcon.Info, $"{profiles.Active} profile", Detail: "Saved as it is now"));
            }, size: 11).Right + 6;

        // Privacy, on the right of the same line.
        var p = Hub.Privacy;
        var who = new List<string>();
        if (p.CameraInUse) who.Add("Camera: " + string.Join(", ", p.CameraApps));
        if (p.MicrophoneInUse) who.Add("Microphone: " + string.Join(", ", p.MicrophoneApps));
        if (who.Count == 0) who.Add("Camera and microphone not in use");
        if (controls.Problem is { } problem) who.Insert(0, problem);
        var dot = p.CameraInUse ? new SKColor(0x34, 0xC7, 0x59) : p.MicrophoneInUse ? new SKColor(0xFF, 0x9F, 0x0A) : Theme.Faint;
        var line = string.Join("  ·  ", who);
        var room = body.Right - 18 - (px + 24);
        var width = Math.Min(room, ui.Measure(line, 10.5f));
        ui.Circle(body.Right - 18 - width - 9, y, 3.5f, dot);
        ui.Text(line, body.Right - 18, y + 4, room, 10.5f, Theme.Weight.Regular, Theme.Dim, SKTextAlign.Right);
    }

    /// <summary>"Microphone Array (Intel® Smart Sound Technology for…)" → "Microphone Array".</summary>
    private static string Short(string? name, int max = 26)
    {
        if (string.IsNullOrWhiteSpace(name)) return "none";
        var paren = name.IndexOf(" (", StringComparison.Ordinal);
        var s = paren > 0 ? name[..paren] : name;
        return s.Length > max ? s[..(max - 1)] + "…" : s;
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

    // ───────────────────────────── Wi-Fi networks ─────────────────────────────

    private void DrawWifi(Ui ui, SKRect body)
    {
        var wifi = Hub.Wifi;
        var on = controls.WifiOn;
        var net = Hub.Network;
        var health = net.Online == false ? " · no internet"
            : (net.PingMs is { } ms ? $" · {ms} ms" : "") + (net.DownloadMbps is { } mbps ? $" · {mbps:0} Mbps down" : "");
        var x = body.Right - 18;
        if (on != false)
        {
            var speedLabel = net.Testing ? "Testing…" : "Speed test";
            x = ui.Chip(x - Width(ui, speedLabel), body.Top + 14, speedLabel, net.TestSpeed, on: net.Testing, size: 11.5f).Left - 8;
            x = ui.Chip(x - Width(ui, "Rescan"), body.Top + 14, "Rescan", wifi.Scan, size: 11.5f).Left - 8;
            if (wifi.ConnectedSsid != null) x = ui.Chip(x - Width(ui, "Disconnect"), body.Top + 14, "Disconnect", wifi.Disconnect, size: 11.5f).Left - 8;
        }
        x = ui.Chip(x - Width(ui, "Settings"), body.Top + 14, "Settings", () => Open("ms-settings:network-wifi"), size: 11.5f).Left - 8;
        Header(ui, body, Icons.Wifi, "Wi-Fi", on, controls.ToggleWifi,
            wifi.Scanning ? "Looking for networks…" : wifi.Status ?? controls.Problem ?? (net.Online == false ? "No internet" : wifi.ConnectedSsid != null ? "Connected" : "Not connected"), x);

        var area = new SKRect(body.Left + 12, body.Top + 32, body.Right - 12, body.Bottom - 4);
        if (wifi.NeedsPassword is { } asking)
        {
            // Password for a new network.
            var y = area.Top + 22;
            Icons.Lock(ui.Canvas, area.Left + 16, y - 4, 13, Theme.Dim);
            ui.Text(asking, area.Left + 30, y, 160, 12.5f, Theme.Weight.SemiBold);
            var field = new SKRect(area.Left + 196, y - 15, area.Right - 170, y + 9);
            ui.Field(_password, field, 12.5f);
            var join = ui.Chip(field.Right + 10, y - 3, "Connect", () => _password.Submit(_password.Text), accent: true, size: 11.5f);
            ui.Chip(join.Right + 8, y - 3, "Cancel", () => { _password.Clear(); wifi.CancelPassword(); }, size: 11.5f);
            ui.Text("Click the box and type the password, then press Enter. Windows saves it with the network.",
                area.Left + 30, y + 28, area.Width - 40, 10.5f, Theme.Weight.Regular, Theme.Faint);
            return;
        }
        if (on == false)
        {
            ui.Empty(area, "Wi-Fi is off", "Turn it on with the switch above");
            return;
        }
        var list = wifi.Networks;
        if (list.Count == 0)
        {
            ui.Empty(area, wifi.Scanning ? "Looking for networks…" : "No networks found", "Rescan, or check that Wi-Fi is on");
            return;
        }
        List(ui, area, list.Count, (i, row, y) =>
        {
            var n = list[i];
            var hover = ui.Hovering(row);
            if (n.Connected) ui.Fill(row, 9, Theme.Accent.WithAlpha(40));
            else if (hover) ui.Fill(row, 9, Theme.Overlay(16));
            Bars(ui, row.Left + 14, y - 3, n.Bars, n.Connected ? Theme.Accent : Theme.Text);
            ui.Text(n.Ssid, row.Left + 34, y, row.Width - 200, 12.5f, n.Connected ? Theme.Weight.SemiBold : Theme.Weight.Regular);
            var note = n.Connected ? "Connected" + health : n.Saved ? "Saved" : n.Secured ? "Secured" : "Open";
            if (hover && !n.Connected) note = n.Secured && !n.Saved ? "Connect…" : "Connect";
            if (n.Secured) Icons.Lock(ui.Canvas, row.Right - (n.Connected ? 26 + ui.Measure(note, 11) : 150), y - 4, 11, Theme.Faint);
            ui.Text(note, row.Right - 12, y, n.Connected ? 230 : 120, 11, Theme.Weight.Regular, n.Connected ? Theme.Accent : hover ? Theme.Text : Theme.Dim, SKTextAlign.Right);
            if (!n.Connected) ui.Hit(row, () => wifi.Connect(n));
        });
    }

    /// <summary>Signal strength as four rising bars, the strong ones lit.</summary>
    private static void Bars(Ui ui, float x, float midY, int bars, SKColor color)
    {
        for (var i = 0; i < 4; i++)
        {
            var h = 4 + i * 3f;
            ui.Fill(new SKRect(x - 8 + i * 4.5f, midY + 7 - h, x - 5 + i * 4.5f, midY + 7), 1.2f, i < Math.Max(1, bars) ? color : color.WithAlpha(60));
        }
    }

    // ───────────────────────────── Bluetooth devices ─────────────────────────────

    private void DrawBluetooth(Ui ui, SKRect body)
    {
        var bt = Hub.Bluetooth;
        var on = controls.BluetoothOn;
        var status = bt.Pin is { } pin ? $"Check that {pin} shows on the device, then accept there"
            : bt.Status ?? controls.Problem ?? (bt.Searching ? "Looking for devices in pairing mode…" : $"{bt.Connected.Count} connected");
        var right = body.Right - 18;
        if (on != false)
        {
            var label = bt.Searching ? "Stop" : "Add device";
            var add = ui.Chip(body.Right - 18 - Width(ui, label), body.Top + 14, label, () => { if (bt.Searching) bt.StopSearch(); else bt.Search(); },
                on: bt.Searching, size: 11.5f);
            right = ui.Chip(add.Left - 8 - Width(ui, "Settings"), body.Top + 14, "Settings", () => Open("ms-settings:bluetooth"), size: 11.5f).Left - 8;
        }
        Header(ui, body, Icons.Bluetooth, "Bluetooth", on, controls.ToggleBluetooth, status, right);

        var area = new SKRect(body.Left + 12, body.Top + 32, body.Right - 12, body.Bottom - 4);
        if (on == false)
        {
            ui.Empty(area, "Bluetooth is off", "Turn it on with the switch above");
            return;
        }
        var paired = bt.Paired;
        var nearby = bt.Nearby;
        var searchRows = bt.Searching || nearby.Count > 0 ? 1 + Math.Max(1, nearby.Count) : 0;
        // Rows: paired devices, then (while searching) a heading and the devices found.
        var rows = paired.Count + searchRows;
        if (rows == 0)
        {
            ui.Empty(area, "No paired devices", "Put a device in pairing mode, then Add device");
            return;
        }
        var busy = bt.Busy;
        List(ui, area, rows, (i, row, y) =>
        {
            if (i < paired.Count)
            {
                var d = paired[i];
                if (d.Connected) ui.Fill(row, 9, Theme.Accent.WithAlpha(40));
                else if (ui.Hovering(row)) ui.Fill(row, 9, Theme.Overlay(12));
                Icons.Bluetooth(ui.Canvas, row.Left + 14, y - 4, 13, d.Connected ? Theme.Accent : Theme.Dim);
                ui.Text(d.Name, row.Left + 34, y, row.Width - 260, 12.5f, d.Connected ? Theme.Weight.SemiBold : Theme.Weight.Regular);
                if (busy.Contains(d.Id))
                {
                    ui.Text("Working…", row.Right - 12, y, 120, 11, Theme.Weight.Regular, Theme.Dim, SKTextAlign.Right);
                    return;
                }
                var forget = ui.Chip(row.Right - 6 - Width(ui, "Forget"), y - 4, "Forget", () => bt.Forget(d), size: 11);
                var act = d.Connected ? "Disconnect" : "Connect";
                ui.Chip(forget.Left - 6 - Width(ui, act), y - 4, act, () => bt.SetConnected(d, !d.Connected), accent: !d.Connected, size: 11);
                return;
            }
            var j = i - paired.Count;
            if (j == 0)
            {
                ui.Text(bt.Searching ? "Nearby devices in pairing mode" : "Found", row.Left + 12, y, row.Width, 11, Theme.Weight.SemiBold, Theme.Dim);
                return;
            }
            if (nearby.Count == 0)
            {
                ui.Text("Searching… put your device in pairing mode", row.Left + 34, y, row.Width - 40, 11.5f, Theme.Weight.Regular, Theme.Faint);
                return;
            }
            var found = nearby[j - 1];
            if (ui.Hovering(row)) ui.Fill(row, 9, Theme.Overlay(12));
            Icons.Plus(ui.Canvas, row.Left + 14, y - 4, 11, Theme.Dim);
            ui.Text(found.Name, row.Left + 34, y, row.Width - 160, 12.5f);
            if (busy.Contains(found.Id)) ui.Text("Pairing…", row.Right - 12, y, 120, 11, Theme.Weight.Regular, Theme.Dim, SKTextAlign.Right);
            else ui.Chip(row.Right - 6 - Width(ui, "Pair"), y - 4, "Pair", () => bt.Pair(found), accent: true, size: 11);
        });
    }

    // ───────────────────────────── shared ─────────────────────────────

    /// <summary>A list page's top line: icon, name, on/off switch and what's going on.</summary>
    private void Header(Ui ui, SKRect body, Action<SKCanvas, float, float, float, SKColor> icon, string name, bool? on, Action toggle, string status, float right)
    {
        var y = body.Top + 14;
        icon(ui.Canvas, body.Left + 28, y, 15, Theme.Text);
        ui.Text(name, body.Left + 44, y + 5, 90, 14, Theme.Weight.SemiBold);
        var x = body.Left + 44 + Math.Min(90, ui.Measure(name, 14, Theme.Weight.SemiBold)) + 12;
        if (on != null) ui.Switch(x, y, on == true, toggle);
        ui.Text(status, x + 44, y + 4, right - (x + 44) - 4, 11, Theme.Weight.Regular, Theme.Dim);
    }

    /// <summary>A scrolling list of rows 28 high in <paramref name="area"/>.</summary>
    private void List(Ui ui, SKRect area, int count, Action<int, SKRect, float> row)
    {
        const float height = 28;
        _listHeight = count * height - area.Height + 6;
        if (_scroll.Target > Math.Max(0, _listHeight)) _scroll.Target = Math.Max(0, _listHeight);
        ui.Canvas.Save();
        ui.Canvas.ClipRect(area);
        var clip = ui.HitClip;
        ui.HitClip = area;
        var top = area.Top + 2 - (float)_scroll.Value;
        for (var i = 0; i < count; i++)
        {
            var r = new SKRect(area.Left + 4, top + i * height, area.Right - 4, top + i * height + height - 3);
            if (r.Bottom < area.Top || r.Top > area.Bottom) continue;
            row(i, r, r.MidY + 4);
        }
        ui.HitClip = clip;
        ui.Canvas.Restore();
    }

    private static float Width(Ui ui, string label) => ui.Measure(label, 11.5f) + 22;
}
