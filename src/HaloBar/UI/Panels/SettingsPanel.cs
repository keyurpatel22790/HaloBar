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
    /// <summary>Taller rows when Settings' text is bigger.</summary>
    private static float RowHeight => 30 * Math.Max(1, Prefs.TextScale);
    private Spring _scroll = new(0, 300, 1);

    public override string Title => "Settings";
    public override IEnumerable<(string Label, Action Click)> Actions => [("Open window", () => Hub.OpenSettings(null))];
    public override void Icon(SKCanvas c, float cx, float cy, float size, SKColor color) => Icons.Gear(c, cx, cy, size, color);
    public override double FrameInterval => _scroll.Settled ? 0.5 : 0;

    public override void Update(double dt) => _scroll.Step(dt);

    /// <summary>One option: a switch (On), a value chip (Value) or a plain action, with a line of explanation.</summary>
    internal sealed record Row(string Label, string? Hint, Func<string>? Value, Action Click, Func<bool>? On = null, string Suffix = "", Slide? Slider = null);

    /// <summary>A number set by dragging: its range, step, and how to read and change it (done = let go).</summary>
    internal sealed record Slide(double Min, double Max, double Step, Func<double> Get, Action<double, bool> Set)
    {
        public float Fraction => (float)((Get() - Min) / (Max - Min));
        public void SetFraction(float f, bool done) => Set(Math.Round((Min + Math.Clamp(f, 0, 1) * (Max - Min)) / Step) * Step, done);
    }

    /// <summary>Every option, in sections; the Settings window shows the same ones in pages.</summary>
    internal IEnumerable<(string Section, Row[] Rows)> Sections()
    {
        var s = Hub.Settings;
        Row Toggle(string label, string? hint, Func<bool> get, Action<bool> set) =>
            new(label, hint, null, () => { set(!get()); s.Save(); Prefs.Apply(s); }, get);
        Row Choice(string label, string? hint, string[] options, Func<string> get, Action<string> set, string suffix = "") =>
            new(label, hint, get, () => { set(options[(Array.IndexOf(options, get()) + 1) % options.Length]); s.Save(); Prefs.Apply(s); }, Suffix: suffix);
        // Options stored as ids ("auto") but shown as words ("Same as Windows").
        Row Named(string label, string? hint, (string Id, string Name)[] options, Func<string> get, Action<string> set) =>
            new(label, hint, () => options.FirstOrDefault(o => o.Id == get()).Name ?? get(), () =>
            {
                var i = Array.FindIndex(options, o => o.Id == get());
                set(options[(i + 1) % options.Length].Id);
                s.Save();
                Prefs.Apply(s);
            });
        (string, string)[] windowsOnOff = [("auto", "Same as Windows"), ("on", "On"), ("off", "Off")];
        // A number with a slider; settings that move windows apply when the slider is let go.
        Row Slider(string label, string? hint, double min, double max, double step, Func<int> get, Action<int> set, string suffix, Action? whenDone = null) =>
            new(label, hint, () => get().ToString(), () => { }, Suffix: suffix, Slider: new Slide(min, max, step, () => get(), (v, done) =>
            {
                set((int)v);
                Prefs.Apply(s);
                if (!done) return;
                s.Save();
                whenDone?.Invoke();
            }));

        yield return ("HUDs", [
            Toggle("Volume", "Show volume changes in the island", () => s.ShowVolumeHud, v => s.ShowVolumeHud = v),
            Toggle("Hide the Windows volume pop-up", "The island is the only read-out", () => s.ReplaceVolumePopup, v => { s.ReplaceVolumePopup = v; applyLayout(); }),
            Toggle("Brightness", "Laptop screens", () => s.ShowBrightnessHud, v => s.ShowBrightnessHud = v),
            Toggle("Battery", "Plugged in, unplugged, low battery", () => s.ShowBatteryHud, v => s.ShowBatteryHud = v),
            Toggle("Battery time left", "Windows' estimate, on the Battery tile", () => s.ShowBatteryEstimate, v => s.ShowBatteryEstimate = v),
            Toggle("Bluetooth devices", "Connected and disconnected", () => s.ShowBluetoothHud, v => s.ShowBluetoothHud = v),
            Toggle("Caps, Num and Scroll Lock", null, () => s.ShowLockKeysHud, v => s.ShowLockKeysHud = v),
            Toggle("Microphone mute", Keys("mic", "mutes and unmutes"), () => s.ShowMicHud, v => s.ShowMicHud = v),
            Toggle("Camera and microphone in use", "Green and orange dots on the pill", () => s.ShowPrivacyIndicator, v => s.ShowPrivacyIndicator = v),
        ]);
        yield return ("Music", [
            Toggle("Lyrics", "The line being sung, from LRCLIB (sends title, artist and length)", () => s.ShowLyrics, v => s.ShowLyrics = v),
            Choice("Visualizer", "Beside the artwork on the pill", ["bars", "pixels"], () => s.Visualizer, v => s.Visualizer = v),
            Choice("Progress bar", null, ["wave", "simple", "pixels", "comet", "hearts"], () => s.ProgressBar, v => s.ProgressBar = v),
            Toggle("Song title in the closed island", "Between the cover and the bars; the pill grows a little for it", () => s.TitleInPill, v => s.TitleInPill = v),
        ]);
        yield return ("Look and place", [
            Choice("Shape", "Island floats below the edge; Notch hangs from it", ["island", "notch"], () => s.Shape, v => { s.Shape = v; applyLayout(); }),
            Choice("Position", "Along the top edge", ["center", "left", "right"], () => s.Position, v => { s.Position = v; applyLayout(); }),
            Slider("Size", "The pill, the card and the text together", 80, 140, 1, () => s.Size, v => s.Size = v, "%", applyLayout),
            Slider("Corners", "From square to fully round", 0, 100, 1, () => s.CornerRoundness, v => s.CornerRoundness = v, "%"),
            Slider("Distance from top", null, 0, 24, 1, () => s.TopGap, v => s.TopGap = v, " px"),
            Choice("Animation speed", null, ["normal", "quick", "relaxed"], () => s.AnimationSpeed, v => { s.AnimationSpeed = v; applyLayout(); }),
            Slider("Open size", "Room in the open card, apart from Size", 90, 120, 1, () => s.OpenSize, v => s.OpenSize = v, "%", applyLayout),
            Slider("Glass blur", "Glass, Frost and Candy: lower is clearer, higher is frostier", 0, 100, 1, () => s.GlassBlur, v => s.GlassBlur = v, "%"),
            Named("Motion", "Battery saver keeps animation under 60 frames a second and refreshes the glass less often; Balanced does that on battery",
                [("fluid", "Fluid"), ("balanced", "Balanced"), ("saver", "Battery saver")], () => s.Motion, v => s.Motion = v),
            Toggle("Hidden until hover", "Nothing at the top until the pointer rests there", () => s.HiddenUntilHover, v => s.HiddenUntilHover = v),
            Toggle("Slim over maximised windows", "A thin line so it never covers browser tabs", () => s.SlimWhenCovered, v => s.SlimWhenCovered = v),
        ]);
        yield return ("Surface and colour", [
            new("Surface", "The island's material: dark, light, glass, aurora…", () => Hub.Theme.Surface.Name, () =>
            {
                var all = Looks.Surfaces;
                var i = Array.IndexOf(all, Hub.Theme.Surface);
                Hub.Theme.Choose(surface: all[(i + 1) % all.Length]);
            }),
            new("Accent", "Highlights, switches and links", () => Hub.Theme.AccentChoice.Name, () =>
            {
                var all = Looks.Accents;
                var i = Array.FindIndex(all, a => a.Id == Hub.Theme.AccentChoice.Id);
                Hub.Theme.Choose(accent: all[(i + 1) % all.Length]);
            }),
            new("Album colours on the music card", null, null, () => Hub.Theme.Choose(albumCard: !Hub.Theme.AlbumCard), () => Hub.Theme.AlbumCard),
            Named("Text colour", "Auto: the surface's own; on Glass lens, dark over bright backgrounds and light over dark ones",
                [("auto", "Auto"), ("white", "White"), ("black", "Black"), ("#FFF1D6", "Warm white"), ("#C8CCD4", "Soft grey")], () => s.TextColour, v => s.TextColour = v),
            Named("Text shadow", "A soft shadow under text, so it reads on busy backgrounds; Auto uses it on see-through surfaces",
                [("auto", "Auto"), ("on", "On"), ("off", "Off")], () => s.TextShadow, v => s.TextShadow = v),
        ]);
        yield return ("Accessibility", [
            Named("Text size", "Text in Settings; Same as Windows follows Windows' Text size",
                [("auto", "Same as Windows"), ("100", "100%"), ("115", "115%"), ("130", "130%")], () => s.TextSize, v => s.TextSize = v),
            Named("Higher contrast text", "Descriptions and other secondary text drawn brighter", windowsOnOff, () => s.HigherContrast, v => s.HigherContrast = v),
            Named("Reduce motion", "Opens and closes without bouncing", windowsOnOff, () => s.ReduceMotion, v => s.ReduceMotion = v),
            Toggle("Keep notices longer", "Notifications and read-outs stay twice as long", () => s.LongerNotices, v => s.LongerNotices = v),
        ]);
        yield return ("Stats and game strip", [
            Named("Network speed", "Megabytes as downloads show it, or megabits as speed tests do",
                [("bytes", "MB/s"), ("bits", "Mb/s")], () => s.NetworkInBits ? "bits" : "bytes", v => s.NetworkInBits = v == "bits"),
            Named("CPU and GPU temperature", null, [("c", "°C"), ("f", "°F")], () => s.StatsFahrenheit ? "f" : "c", v => s.StatsFahrenheit = v == "f"),
            Toggle("Game strip", "A slim strip instead of hiding while a game is full screen", () => s.GameStrip, v => s.GameStrip = v),
            Toggle("Show it everywhere", "The strip all the time, in every window; hover it to open the card", () => s.GameStripEverywhere, v => s.GameStripEverywhere = v),
            Toggle("Strip: CPU", null, () => s.StripCpu, v => s.StripCpu = v),
            Toggle("Strip: GPU", null, () => s.StripGpu, v => s.StripGpu = v),
            Toggle("Strip: memory", null, () => s.StripRam, v => s.StripRam = v),
            Toggle("Strip: network", null, () => s.StripNetwork, v => s.StripNetwork = v),
            Toggle("Strip: clock", null, () => s.StripClock, v => s.StripClock = v),
            Toggle("Strip: usage bars", "Small bars beside CPU and GPU", () => s.StripBars, v => s.StripBars = v),
        ]);
        yield return ("Integrations", [
            Toggle("OBS Studio", "Recording and streaming on the pill" + Keys("obs", "records", "; "), () => s.ObsEnabled, v => s.ObsEnabled = v),
            Toggle("Use OBS's own settings", "Reads the websocket port and password from OBS on this PC", () => s.ObsAuto, v => s.ObsAuto = v),
            Toggle("AI agents", "Claude Code and others: working, waiting, done", () => s.AgentsEnabled, v => s.AgentsEnabled = v),
            Toggle("Notify when an agent finishes", null, () => s.AgentsNotify, v => s.AgentsNotify = v),
        ]);
        yield return ("Panels", [
            Toggle("Notifications", null, () => s.NotificationsPanel, v => s.NotificationsPanel = v),
            Toggle("Reminders and to-dos", null, () => s.RemindersPanel, v => s.RemindersPanel = v),
            Toggle("Apps", "Six launcher slots", () => s.LauncherPanel, v => s.LauncherPanel = v),
            Toggle("Web", "Pinned sites in a drawer under the island", () => s.WebPanel, v => s.WebPanel = v),
            Toggle("Phone", "Send files to and from your phone with a QR code", () => s.PhoneShare, v => s.PhoneShare = v),
            Toggle("Call", "A camera mirror and microphone switch", () => s.CameraEnabled, v => s.CameraEnabled = v),
            Toggle("Chat", "Twitch or YouTube live chat", () => s.ChatPanel, v => s.ChatPanel = v),
            Toggle("Files", "The shelf: drop files on the island to keep them handy", () => s.ShelfEnabled, v => s.ShelfEnabled = v),
            Toggle("Controls", "Brightness, volume and switches for Wi-Fi, Bluetooth, Night light", () => s.ControlsPanel, v => s.ControlsPanel = v),
        ]);
        yield return ("Chat", [
            Toggle("Chat pop-ups", "New messages on the pill while the chat is closed", () => s.ChatPopups, v => s.ChatPopups = v),
            Toggle("Chat overlay", "The chat in a window of its own, above other windows; drag it, right-click for size", () => s.ChatOverlay, v => s.ChatOverlay = v),
            Named("Background", null, [("#0B0B0C", "Black"), ("#1C1C22", "Graphite"), ("#14213D", "Navy"), ("#2D1B3D", "Plum"), ("#F5F5F7", "White")],
                () => s.ChatBackground, v => s.ChatBackground = v),
            Slider("Background opacity", null, 20, 100, 1, () => s.ChatOpacity, v => s.ChatOpacity = v, "%"),
            Named("Text colour", "Names keep the colour each person picked", [("#F5F5F7", "White"), ("#1B1B1F", "Black"), ("#FFE27A", "Yellow"), ("#9FE2BF", "Mint")],
                () => s.ChatTextColor, v => s.ChatTextColor = v),
            Slider("Text size", null, 10, 24, 1, () => s.ChatTextSize, v => s.ChatTextSize = v, " px"),
        ]);
        yield return ("App rules", [.. AppRuleRows()]);
        yield return ("Island", [
            Toggle("Hide when something is full screen", null, () => s.HideInFullscreen, v => s.HideInFullscreen = v),
            Toggle("Hide from screenshots and sharing", null, () => s.HideFromCapture, v => { s.HideFromCapture = v; applyLayout(); }),
            Named("Show in screen sharing", "Glass needs the island hidden from captures; when shown, it uses Windows' blur instead",
                [("calls", "During calls"), ("always", "Always"), ("never", "Never (glass only)")], () => s.ShowWhenSharing, v => s.ShowWhenSharing = v),
            Toggle("Every monitor", "One island per screen", () => s.AllMonitors, v => { s.AllMonitors = v; applyLayout(); }),
            Slider("Open delay", "How long the pointer rests before it opens", 0, 800, 1, () => s.HoverDelayMs, v => s.HoverDelayMs = v, " ms", applyLayout),
            Slider("Notice time", "How long a notification stays on the pill", 2, 20, 1, () => s.NotificationSeconds, v => s.NotificationSeconds = v, " s", applyLayout),
            Named("Opens on", "Main: music while it plays, otherwise Home. Last tab: where you were, even after a restart",
                [("main", "Main"), ("last", "Last tab")], () => s.OpenOn, v => s.OpenOn = v),
            Named("Time format", "Every clock on the island", [("system", "Same as Windows"), ("12", "12-hour"), ("24", "24-hour")], () => s.TimeFormat, v => s.TimeFormat = v),
            Toggle("Meeting countdown", "On the pill before a calendar meeting", () => s.MeetingCountdown, v => s.MeetingCountdown = v),
            Named("Reminder sound", "Repeat chimes every 15 seconds for up to a minute until you open the island",
                [("repeat", "Repeat"), ("once", "Once"), ("off", "Off")], () => s.ReminderSound, v => s.ReminderSound = v),
            Toggle("Night light warning", "When Photoshop, Lightroom, Premiere, Resolve, GIMP, Krita or Blender is in front; one click turns it off",
                () => s.ColorWorkWarning, v => s.ColorWorkWarning = v),
            Toggle("Microphone in screen recordings", "Your voice, mixed with the PC's sound", () => s.RecordMicrophone, v => s.RecordMicrophone = v),
            Toggle("Sound in screen recordings", "What the PC plays; recordings go to Videos\\HaloBar", () => s.RecordSound, v => s.RecordSound = v),
            Toggle("Mirror Windows notifications", "Takes effect after a restart", () => s.MirrorNotifications, v => s.MirrorNotifications = v),
            Toggle("Local API", $"Scripts post notices to 127.0.0.1:{s.NotifyPort}; takes effect after a restart", () => s.NotifyEndpoint, v => s.NotifyEndpoint = v),
            Toggle("Do not disturb", "Notifications, chat and agent pings wait in the list", () => s.DoNotDisturb, v => s.DoNotDisturb = v),
            Toggle("Quiet during focus", "Do not disturb while the focus timer runs", () => s.QuietDuringFocus, v => s.QuietDuringFocus = v),
            Named("Block sites during focus", "Web windows won't open them while the timer runs",
                [("instagram.com, facebook.com, x.com, reddit.com", "Social sites"),
                 ("instagram.com, facebook.com, x.com, reddit.com, youtube.com", "Social sites and YouTube"), ("", "None")],
                () => s.FocusBlockedSites, v => s.FocusBlockedSites = v),
            Toggle("Watch the internet", "A ping every 20 seconds; says when it drops and comes back", () => s.NetworkWatch, v => s.NetworkWatch = v),
            Named("Charge reminder", "Says when to unplug, for a longer-lasting battery", [("80", "At 80%"), ("90", "At 90%"), ("0", "Off")],
                () => s.ChargeLimit.ToString(), v => s.ChargeLimit = int.Parse(v)),
            Toggle("Turn off Windows banners", "For apps shown here; they stay in the notification centre", () => s.QuietWindowsBanners, v =>
            {
                s.QuietWindowsBanners = v;
                if (v) Hub.Banners.QuietAll(Hub.Notices.Items.Select(n => n.AppId));
                else Hub.Banners.RestoreAll();
            }),
            Toggle("Draw on the graphics card", "Smoother, and lighter on the CPU; takes effect after a restart", () => s.GpuDrawing, v => s.GpuDrawing = v),
            new("Start with Windows", null, null, () => StartupRegistration.Set(!StartupRegistration.IsEnabled), () => StartupRegistration.IsEnabled),
            new("Language", s.Language == "auto" ? Lang.Name(Lang.Current) : null, () => Lang.Name(s.Language), () =>
            {
                var i = Array.FindIndex(Lang.Available, a => a.Code == s.Language);
                s.Language = Lang.Available[(i + 1) % Lang.Available.Length].Code;
                s.Save();
                Lang.Use(s.Language);
            }),
        ]);
        yield return ("Clipboard", [
            Toggle("Clipboard history", null, () => s.ClipboardEnabled, v => s.ClipboardEnabled = v),
            Named("Hours to keep them", "Pinned snippets always stay", [("0", "Until the list is full"), ("1", "1 hour"), ("10", "10 hours"), ("24", "1 day"), ("72", "3 days")],
                () => s.ClipboardKeepHours.ToString(), v => s.ClipboardKeepHours = int.Parse(v)),
            Toggle("Show as a list", "One line each instead of cards", () => s.ClipboardList, v => s.ClipboardList = v),
            Toggle("Copied text on the phone page", "Phone share shows what you last copied", () => s.PhoneShareCopiedText, v => s.PhoneShareCopiedText = v),
        ]);
        yield return ("Files and exports", [
            Toggle("Photo and video details", "Drop one on the shelf to see its resolution, length and size", () => s.MediaInfoOnDrop, v => s.MediaInfoOnDrop = v),
            .. FolderRows(s.ExportFolders, "Add an export folder", "Finished renders and exports here go on the shelf with a notice", null),
        ]);
        yield return ("Web drawer", [
            Toggle("Block ads", $"Ads and trackers on the drawer's sites, YouTube and YouTube Music included; for pages opened from now on ({Hub.Web.AdsBlocked} blocked so far)",
                () => s.WebAdBlock, v => s.WebAdBlock = v),
            Toggle("Skip ads that get through", "Muted, sped up and skipped the moment YouTube allows", () => s.WebAdFastForward, v => s.WebAdFastForward = v),
            Toggle("Glow around videos", "Light in the page around the player, in the video's colours, like ambient light strips; for pages opened from now on",
                () => s.WebGlow, v => s.WebGlow = v),
        ]);
        yield return ("Web extensions", [.. FolderRows(s.WebExtensions, "Add an unpacked extension",
            "A folder with a manifest.json; it loads the next time a site opens", f => File.Exists(Path.Combine(f, "manifest.json")))]);
        yield return ("Privacy dots", [.. PrivacyRows()]);
        if (s.MusicSeen.Count > 0) yield return ("Music sources", [.. MusicRows()]);
        yield return ("Keys", [.. KeyRows()]);
    }

    /// <summary>"Ctrl+Alt+M mutes and unmutes", or "" when the action has no shortcut.</summary>
    private string Keys(string action, string does, string before = "") =>
        Hub.Hotkeys.Text(action) is { Length: > 0 } keys ? $"{before}{keys} {does}" : "";

    // ───────────────────────────── shortcuts ─────────────────────────────

    private string? _captureFor;
    private bool _focusCapture;
    private string _captureNote = "";
    private TextField? _capture;

    /// <summary>
    /// One row per folder (click to take it off the list), then a row that opens Windows' folder
    /// dialog to add another. <paramref name="valid"/> rejects folders that won't work.
    /// </summary>
    private IEnumerable<Row> FolderRows(List<string> list, string addLabel, string addHint, Func<string, bool>? valid)
    {
        var s = Hub.Settings;
        foreach (var folder in list.ToList())
        {
            var chosen = folder;
            yield return new(Path.GetFileName(folder.TrimEnd('\\')), Directory.Exists(folder) ? folder : "not found: " + folder, () => "Remove", () =>
            {
                list.Remove(chosen);
                s.Save();
            });
        }
        yield return new(addLabel, addHint, () => "Choose…", () => Platform.FolderPicker.Pick(addLabel, null, folder => Hub.RunOnUi(() =>
        {
            if (valid != null && !valid(folder))
            {
                Hub.ShowHud(new Hud(HudIcon.Info, "No manifest.json in that folder", Detail: Path.GetFileName(folder)));
                return;
            }
            if (list.Contains(folder, StringComparer.OrdinalIgnoreCase)) return;
            list.Add(folder);
            s.Save();
        })));
    }

    /// <summary>A switch per app seen using the camera or microphone: off keeps it from lighting the dots.</summary>
    private IEnumerable<Row> PrivacyRows()
    {
        var s = Hub.Settings;
        var apps = Hub.Privacy.Seen.Concat(s.PrivacyIgnoredApps).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(a => a).ToList();
        if (apps.Count == 0)
        {
            yield return new("No apps yet", "Apps that use the camera or microphone show up here", null, () => { });
            yield break;
        }
        foreach (var app in apps)
        {
            var name = app;
            yield return new(name, null, null, () =>
            {
                if (s.PrivacyIgnoredApps.RemoveAll(a => a.Equals(name, StringComparison.OrdinalIgnoreCase)) == 0) s.PrivacyIgnoredApps.Add(name);
                s.Save();
            }, () => !s.PrivacyIgnoredApps.Contains(name, StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <summary>A switch for each app seen playing: off keeps its music off the island.</summary>
    private IEnumerable<Row> MusicRows()
    {
        var s = Hub.Settings;
        foreach (var (id, name) in s.MusicSeen.OrderBy(p => p.Value))
        {
            var app = id;
            var hint = s.MusicSource == app ? "the only app shown (click its name on the music card to change)" : null;
            yield return new(name, hint, null, () =>
            {
                if (!s.MusicHidden.Remove(app)) s.MusicHidden.Add(app);
                s.Save();
                Hub.Media.Refresh();
            }, () => !s.MusicHidden.Contains(app));
        }
    }

    /// <summary>One row per action: click it, then press the new keys.</summary>
    private IEnumerable<Row> KeyRows()
    {
        foreach (var action in HotkeyService.Actions)
        {
            var id = action.Id;
            var capturing = _captureFor == id;
            var hint = capturing ? (_captureNote.Length > 0 ? _captureNote : "Press the new keys · Backspace clears · Esc cancels")
                : Hub.Hotkeys.Taken(id) ? "Taken by another app; pick other keys" : null;
            yield return new(action.Label, hint, () => capturing ? "Press keys…" : Hub.Hotkeys.Text(id) is { Length: > 0 } t ? t : "None",
                () => StartCapture(id));
        }
        yield return new("Back to the usual shortcuts", null, () => "Reset", () =>
        {
            Hub.Hotkeys.Reset();
            Hub.Hotkeys.Register();
        });
    }

    private void StartCapture(string id)
    {
        _capture ??= new TextField("", _ => { }) { CaptureKey = OnCaptureKey, Ended = EndCapture };
        _captureFor = id;
        _captureNote = "";
        _focusCapture = true;
        // Let every shortcut go, so pressing one of them now reaches us instead of firing it.
        Hub.Hotkeys.Unregister();
    }

    private bool OnCaptureKey(int vk)
    {
        static bool Down(int key) => Platform.Native.GetKeyState(key) < 0;
        uint mods = 0;
        if (Down(0x11)) mods |= Platform.Native.MOD_CONTROL;
        if (Down(0x12)) mods |= Platform.Native.MOD_ALT;
        if (Down(0x10)) mods |= Platform.Native.MOD_SHIFT;
        if (Down(0x5B) || Down(0x5C)) mods |= Gesture.Win;
        if (_captureFor is not { } id) return true;
        if (mods == 0 && vk == 0x1B) return true; // Esc: keep what it was
        if (mods == 0 && vk is 0x08 or 0x2E) // Backspace or Delete: no shortcut
        {
            Hub.Hotkeys.Set(id, null);
            return true;
        }
        if (Gesture.IsModifierKey((uint)vk)) return false; // wait for the real key
        var gesture = new Gesture(mods, (uint)vk);
        if (Gesture.Parse(gesture.ToString()) == null)
        {
            _captureNote = $"{gesture} would get in the way of typing; add Ctrl, Alt or Win";
            return false;
        }
        Hub.Hotkeys.Set(id, gesture);
        return true;
    }

    private void EndCapture()
    {
        // Clicking another shortcut while choosing one ends the old capture after the new one
        // has started; that new one carries on.
        if (_focusCapture) return;
        _captureFor = null;
        _captureNote = "";
        Hub.Hotkeys.Register();
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

    /// <summary>"keys" (or any section's name) scrolls to that section.</summary>
    public override void GoTo(string place)
    {
        var y = 0f;
        foreach (var (section, rows) in Sections())
        {
            if (section.Equals(place, StringComparison.OrdinalIgnoreCase))
            {
                _scroll.Snap(Math.Clamp(y, 0, Math.Max(0, RowCount * RowHeight - 140)));
                return;
            }
            y += RowHeight * 0.8f + rows.Length * RowHeight + 4;
        }
    }

    /// <summary>The shortcut box that should take the keyboard now (a shortcut row was clicked), once.</summary>
    internal TextField? TakeFocus()
    {
        if (!_focusCapture || _capture == null) return null;
        _focusCapture = false;
        return _capture;
    }

    /// <summary>The shortcut being recorded, if any.</summary>
    internal bool Capturing => _captureFor != null;

    public override bool Wheel(float notches)
    {
        var max = Math.Max(0, RowCount * RowHeight - 140);
        _scroll.Target = Math.Clamp(_scroll.Target - notches * RowHeight * 1.5f, 0, max);
        return true;
    }

    public override void Draw(Ui ui, SKRect body)
    {
        if (_focusCapture && _capture != null)
        {
            _focusCapture = false;
            ui.Focus(_capture);
        }
        var area = new SKRect(body.Left + 8, body.Top, body.Right - 8, body.Bottom - 6);
        ui.Canvas.Save();
        ui.Canvas.ClipRect(area);
        var y = body.Top + 16 - (float)_scroll.Value;
        foreach (var (section, rows) in Sections())
        {
            ui.Text(Lang.T(section).ToUpper(System.Globalization.CultureInfo.CurrentCulture), body.Left + 20, y, 200, 10.5f, Theme.Weight.SemiBold, Theme.Accent);
            y += RowHeight * 0.8f;
            foreach (var row in rows)
            {
                var r = new SKRect(body.Left + 12, y - RowHeight * 0.6f, body.Right - 12, y + RowHeight * 0.27f);
                if (y > area.Top - 20 && y < area.Bottom + 20)
                {
                    if (ui.Hovering(r) && ui.Hovering(area)) ui.Fill(r, 10, Theme.Overlay(12));
                    var k = Prefs.TextScale;
                    ui.Text(row.Label, r.Left + 8, y, 260, 12.5f * k);
                    if (row.Hint != null)
                        ui.Text(row.Hint, r.Left + 16 + Math.Min(250, ui.Measure(row.Label, 12.5f * k)), y, r.Width - 330, 11 * k, Theme.Weight.Regular, Theme.Faint);
                    if (row.On != null) ui.Switch(r.Right - 44, y - 5, row.On(), row.Click);
                    else if (row.Slider is { } slide)
                    {
                        var label = row.Value!() + row.Suffix;
                        ui.Text(label, r.Right - 136, y, 50, 11, Theme.Weight.SemiBold, Theme.Dim, SKTextAlign.Right);
                        ui.Slider(new SKRect(r.Right - 124, y - 14, r.Right - 16, y + 4), slide.Fraction, slide.SetFraction);
                    }
                    else if (row.Value != null)
                    {
                        var label = row.Value() + row.Suffix;
                        ui.Chip(r.Right - 12 - ui.Measure(label, 11.5f) - 22, y - 5, label, row.Click, true, 11.5f);
                    }
                    if (ui.Hovering(area) && row.Slider == null) ui.Hit(SKRect.Intersect(r, area), row.Click);
                }
                y += RowHeight;
            }
            y += 4;
        }
        ui.Canvas.Restore();
    }
}
