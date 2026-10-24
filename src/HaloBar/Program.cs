using System.Collections.Concurrent;
using System.Diagnostics;
using HaloBar.Platform;
using HaloBar.Rendering;
using HaloBar.Services;
using HaloBar.UI;
using SkiaSharp;
using static HaloBar.Platform.Native;
using Activity = HaloBar.UI.Activity;

namespace HaloBar;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // For installers and scripts: HaloBar.exe --startup on|off
        if (args is ["--startup", var mode])
        {
            StartupRegistration.Set(mode.Equals("on", StringComparison.OrdinalIgnoreCase));
            return StartupRegistration.IsEnabled == mode.Equals("on", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        }

        using var single = new Mutex(true, "HaloBar.SingleInstance", out var first);
        if (!first) return 0;

        Log.Start();
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"Crash: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Write($"Unobserved task error: {e.Exception.GetBaseException().Message}");
            e.SetObserved();
        };

        try
        {
            using var app = new IslandApp(AppSettings.Load());
            app.Run();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Write($"Fatal: {ex}");
            return 1;
        }
    }
}

/// <summary>One monitor's island: its window, its swap chain and its view.</summary>
internal sealed class IslandScreen : IDisposable
{
    private SKCanvas? _canvas;
    private SKBitmap? _canvasBitmap;

    public OverlayWindow Window { get; }
    public CompositionSurface Surface { get; private set; }
    public IslandView View { get; }
    public MonitorInfo Monitor => Window.Monitor;
    public double Interval { get; set; }
    public double LastFrame { get; set; } = double.NegativeInfinity;
    private AcrylicBackdrop? _backdrop;
    private bool _backdropFailed;

    public IslandScreen(OverlayWindow window, GraphicsDevice gfx, IslandView view)
    {
        Window = window;
        View = view;
        Surface = new CompositionSurface(gfx, window.Handle, window.Width, window.Height);
    }

    public void ReplaceSurface(GraphicsDevice gfx)
    {
        DropCanvas();
        Surface.Dispose();
        Surface = new CompositionSurface(gfx, Window.Handle, Window.Width, Window.Height);
    }

    public void Place(MonitorInfo monitor)
    {
        Window.Place(monitor);
        Surface.Resize(Window.Width, Window.Height);
    }

    /// <summary>Draws and presents one frame. Returns false when the GPU device was lost.</summary>
    public bool Render()
    {
        if (!ReferenceEquals(_canvasBitmap, Surface.Bitmap))
        {
            DropCanvas();
            _canvasBitmap = Surface.Bitmap;
            _canvas = new SKCanvas(_canvasBitmap);
        }
        // The canvas is reused across frames; start each from a clean save stack.
        _canvas!.RestoreToCount(1);
        _canvas.ResetMatrix();
        _canvas.Scale(Window.Scale);
        try
        {
            View.Draw(_canvas);
            UpdateBackdrop();
        }
        catch (Exception ex)
        {
            // Keep the app alive; a broken frame is better than no island.
            Log.Write($"Draw failed: {ex}");
            _canvas.RestoreToCount(1);
        }
        _canvas.Flush();
        return Surface.Present();
    }

    /// <summary>Keeps the blurred backdrop of a see-through surface under the island's outline.</summary>
    private void UpdateBackdrop()
    {
        var outline = View.BackdropOutline;
        if (outline == null && _backdrop == null) return;
        if (_backdrop == null && !_backdropFailed)
        {
            _backdrop = AcrylicBackdrop.Create(Window.Handle);
            _backdropFailed = _backdrop == null;
        }
        if (outline is { } o)
            _backdrop?.Update(true, o.Rect.Left * Window.Scale, o.Rect.Top * Window.Scale, o.Rect.Width * Window.Scale, o.Rect.Height * Window.Scale, o.Radius * Window.Scale);
        else _backdrop?.Update(false, 0, 0, 0, 0, 0);
    }

    private void DropCanvas()
    {
        _canvas?.Dispose();
        _canvas = null;
        _canvasBitmap = null;
    }

    public void Dispose()
    {
        RevokeDragDrop(Window.Handle);
        _backdrop?.Dispose();
        DropCanvas();
        Surface.Dispose();
        Window.Dispose();
    }
}

/// <summary>Owns the windows, the services and the frame loop. Everything here runs on the main (STA) thread.</summary>
internal sealed class IslandApp : IDisposable
{
    private const uint WakeMessage = WM_APP + 2;
    private static readonly TimeSpan TrimEvery = TimeSpan.FromMinutes(5);
    private static readonly SKColor Green = new(0x34, 0xC7, 0x59), Orange = new(0xFF, 0x9F, 0x0A), Red = new(0xFF, 0x45, 0x3A);

    private enum HotkeyAction { ToggleCard = 1, PlayPause, Timer, Hide, MicMute, NewReminder, Output, ColorPick, Screenshot, ObsRecord }

    /// <summary>Default shortcuts; one taken by another app is skipped and logged.</summary>
    private static readonly (HotkeyAction Action, uint Modifiers, uint Key, string Name)[] Hotkeys =
    [
        (HotkeyAction.ToggleCard, MOD_CONTROL | MOD_ALT, 'I', "Ctrl+Alt+I"),
        (HotkeyAction.PlayPause, MOD_CONTROL | MOD_ALT, 'P', "Ctrl+Alt+P"),
        (HotkeyAction.Timer, MOD_CONTROL | MOD_ALT, 'T', "Ctrl+Alt+T"),
        (HotkeyAction.Hide, MOD_CONTROL | MOD_ALT, 'H', "Ctrl+Alt+H"),
        (HotkeyAction.MicMute, MOD_CONTROL | MOD_ALT, 'M', "Ctrl+Alt+M"),
        (HotkeyAction.NewReminder, MOD_CONTROL | MOD_ALT, 'E', "Ctrl+Alt+E"),
        (HotkeyAction.Output, MOD_CONTROL | MOD_ALT, 'O', "Ctrl+Alt+O"),
        (HotkeyAction.ColorPick, MOD_CONTROL | MOD_ALT, 'C', "Ctrl+Alt+C"),
        (HotkeyAction.Screenshot, MOD_CONTROL | MOD_ALT, 'F', "Ctrl+Alt+F"),
        (HotkeyAction.ObsRecord, MOD_CONTROL | MOD_ALT, 'R', "Ctrl+Alt+R"),
    ];

    private readonly AppSettings _settings;
    private readonly HostWindow _host = new();
    private GraphicsDevice _gfx;
    private readonly Theme _theme;
    private readonly Hub _hub;
    private readonly ControlsService _controls = new();
    private readonly ObsService _obs;
    private readonly AgentService _agents;
    private readonly LauncherService _launcher;
    private string _frontApp = "";
    private readonly NotifyServer _server;
    private readonly VolumeFlyoutSuppressor _flyout = new();
    private readonly TrayIcon _tray;
    private readonly ConcurrentQueue<Action> _ui = new();
    private readonly HashSet<HotkeyAction> _registeredHotkeys = [];
    private List<IslandScreen> _screens = [];
    private volatile bool _dirty = true;
    private bool _layoutDirty, _userHidden;
    private double _trimAt = 8;
    private (bool Caps, bool Num, bool Scroll) _locks;
    private (string File, int Frames)? _snapshot;
    private DateTime _snipRequestedAt, _lastChatPopup;
    private readonly Dictionary<string, DateTime> _callStarted = new(StringComparer.OrdinalIgnoreCase);

    public IslandApp(AppSettings settings)
    {
        _settings = settings;
        // The CPU rasteriser only needs its caches for glyphs and a few small images.
        SKGraphics.SetResourceCacheTotalByteLimit(4 * 1024 * 1024);
        SKGraphics.SetFontCacheLimit(2 * 1024 * 1024);

        _gfx = new GraphicsDevice();
        Log.Write($"GPU {_gfx.AdapterName}");
        _theme = new Theme(settings.Surface, settings.Accent, settings.AlbumCard);
        _theme.Changed += () =>
        {
            _settings.Surface = _theme.Surface.Id;
            _settings.Accent = _theme.AccentChoice.Id;
            _settings.AlbumCard = _theme.AlbumCard;
            _settings.Save();
            _dirty = true;
        };
        _server = new NotifyServer(settings.NotifyPort);
        _obs = new ObsService(settings);
        _agents = new AgentService(settings.NotifyPort);
        _launcher = new LauncherService(settings);

        var shelf = new ShelfService(settings);
        var clipboard = new ClipboardService(_host.Handle, settings.ClipboardHistorySize, Post);
        clipboard.UseSnippets(() => settings.Snippets, settings.Save);
        _hub = new Hub
        {
            Settings = settings,
            Theme = _theme,
            Media = new MediaService(),
            Volume = new VolumeService(),
            Microphone = new MicrophoneService(),
            Brightness = new BrightnessService(),
            Power = new PowerService(),
            Privacy = new PrivacyService(),
            Bluetooth = new BluetoothService(),
            Clipboard = clipboard,
            Notices = new NoticeHistory(),
            Notifications = new NotificationService(),
            Weather = new WeatherService(settings),
            Stats = new StatsService(),
            Timer = new TimerService(settings),
            Reminders = new ReminderService(settings),
            Calendar = new CalendarService(settings),
            Lyrics = new LyricsService(),
            Shelf = shelf,
            Controls = _controls,
            Agents = _agents,
            Launcher = _launcher,
            Obs = _obs,
            Phone = new PhoneShareService(shelf, () => clipboard.Entries.FirstOrDefault(e => e.Kind == ClipKind.Text)?.Content.Text),
            Chat = new ChatService(settings),
            Camera = new CameraService(),
            Downloads = new DownloadService(),
            Ticker = new TickerService(settings),
            ApiPort = settings.NotifyPort,
            ShowHud = hud => Post(() => ShowHud(hud)),
            Open = Open,
            Reveal = path => Open("explorer.exe", $"/select,\"{path}\""),
        };

        // OLE drag and drop needs OLE initialised on this (STA) thread before any window registers for it.
        OleInitialize(IntPtr.Zero);
        _host.MessageHook = OnHostMessage;
        _host.DisplayChanged += () =>
        {
            _layoutDirty = true;
            _theme.RefreshWindowsAccent(); // Personalization changes arrive as WM_SETTINGCHANGE too
        };
        RebuildScreens();

        _tray = new TrayIcon(_host.Handle, "HaloBar")
        {
            LeftClick = () => ScreenUnderCursor()?.View.ToggleExpanded(),
            BuildMenu = TrayMenu,
        };
        foreach (var (action, modifiers, key, name) in Hotkeys)
        {
            if (RegisterHotKey(_host.Handle, (int)action, modifiers | MOD_NOREPEAT, key)) _registeredHotkeys.Add(action);
            else Log.Write($"Hotkey {name} is taken by another app");
        }
        if (settings.ReplaceVolumePopup) _flyout.Ensure();
        _locks = ReadLocks();

        StartServices();
    }

    // ───────────────────────────── screens ─────────────────────────────

    /// <summary>Matches islands to the connected monitors: keeps, moves, adds or removes them.</summary>
    private void RebuildScreens()
    {
        var monitors = Monitors.All();
        if (!_settings.AllMonitors) monitors = [.. monitors.Take(1)];

        var remaining = new List<IslandScreen>(_screens);
        var next = new List<IslandScreen>();
        foreach (var monitor in monitors)
        {
            var existing = remaining.FirstOrDefault(s => s.Monitor.Handle == monitor.Handle)
                           ?? remaining.FirstOrDefault(s => s.Monitor.Key == monitor.Key);
            if (existing != null) remaining.Remove(existing);
            var screen = existing ?? CreateScreen(monitor);
            screen.Window.SetLayout(_settings.Size / 100f, _settings.Position, _settings.EdgeMargin);
            screen.Place(monitor);
            screen.View.ApplySettings();
            next.Add(screen);
        }
        foreach (var gone in remaining) gone.Dispose();
        _screens = next;
        foreach (var s in _screens)
        {
            s.Window.SetCaptureExcluded(_settings.HideFromCapture);
        }
        Log.Write($"Screens: {string.Join(", ", _screens.Select(s => $"{s.Monitor.Key}{(s.Monitor.IsPrimary ? " (primary)" : "")}"))}");
    }

    private IslandScreen CreateScreen(MonitorInfo monitor)
    {
        var window = new OverlayWindow((int)IslandView.CanvasWidth, (int)IslandView.CanvasHeight, monitor);
        var view = new IslandView(_hub, ApplyLayout, CopyFiles)
        {
            NoticeTime = TimeSpan.FromSeconds(Math.Clamp(_settings.NotificationSeconds, 2, 30)),
            ActivitySource = CurrentActivity,
        };
        var screen = new IslandScreen(window, _gfx, view);
        window.MouseUp += (x, y) =>
        {
            view.Click(new SKPoint(x / window.Scale, y / window.Scale));
            _dirty = true;
        };
        window.MouseWheel += delta =>
        {
            view.Wheel(delta);
            _dirty = true;
        };
        window.CharTyped += c => { view.Char(c); _dirty = true; };
        window.KeyPressed += vk => { view.Key(vk); _dirty = true; };
        window.FocusLost += () => { view.EndTyping(); _dirty = true; };
        view.TypingChanged += typing =>
        {
            if (typing) window.BeginTyping();
            else window.EndTyping();
        };
        window.DpiChanged += () => _layoutDirty = true;
        if (DropTarget.Register(window.Handle) is { } drop)
        {
            drop.Hovering += hovering => Post(() => view.DragHover(hovering));
            drop.Dropped += files => Post(() =>
            {
                _hub.Shelf.Add(files);
                ShowHud(new Hud(HudIcon.Download, files.Length == 1 ? Path.GetFileName(files[0]) : $"{files.Length} files", Detail: "On the shelf"));
            });
        }
        return screen;
    }

    /// <summary>Settings that change windows (monitors, capture, the volume pop-up) apply on the next frame.</summary>
    private void ApplyLayout()
    {
        _layoutDirty = true;
        if (_settings.ReplaceVolumePopup) _flyout.Ensure();
        else _flyout.Release();
    }

    private IslandScreen? ScreenUnderCursor()
    {
        GetCursorPos(out var p);
        var monitor = MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST);
        return _screens.FirstOrDefault(s => s.Monitor.Handle == monitor) ?? _screens.FirstOrDefault();
    }

    private void RecreateGraphics()
    {
        Log.Write("Graphics: device lost; recreating");
        foreach (var s in _screens) s.Surface.Dispose();
        _gfx.Dispose();
        _gfx = new GraphicsDevice();
        foreach (var s in _screens) s.ReplaceSurface(_gfx);
        _dirty = true;
    }

    // ───────────────────────────── services ─────────────────────────────

    private void StartServices()
    {
        var h = _hub;
        h.Media.Changed += Invalidate;
        _ = Task.Run(h.Media.StartAsync);
        h.Lyrics.Changed += Invalidate;

        h.Volume.Changed += () => Post(() =>
        {
            if (_settings.ShowVolumeHud)
                ShowHud(new Hud(HudIcon.Volume, h.Volume.Muted ? "Muted" : $"{Math.Round(h.Volume.Volume * 100)}%", h.Volume.Volume));
            // Explorer creates the pop-up's window the first time it is needed, so look again now.
            if (_settings.ReplaceVolumePopup) _flyout.Ensure();
        });
        h.Volume.Start();

        h.Microphone.Changed += () => Post(() =>
        {
            if (_settings.ShowMicHud)
                ShowHud(h.Microphone.Muted
                    ? new Hud(HudIcon.MicOff, "Microphone muted", Tint: Red)
                    : new Hud(HudIcon.Mic, "Microphone on", Detail: h.Microphone.DeviceName));
        });
        h.Microphone.Start();

        h.Brightness.Changed += () => Post(() =>
        {
            if (_settings.ShowBrightnessHud)
                ShowHud(new Hud(HudIcon.Brightness, $"{Math.Round(h.Brightness.Level * 100)}%", h.Brightness.Level));
        });
        _ = Task.Run(h.Brightness.Start);

        h.Power.Event += e => Post(() =>
        {
            if (!_settings.ShowBatteryHud) return;
            var p = h.Power;
            var level = p.Percent / 100f;
            ShowHud(e switch
            {
                PowerEvent.PluggedIn => new Hud(HudIcon.Charging, "Charging", Detail: $"{p.Percent}%", Tint: Green),
                PowerEvent.Unplugged => new Hud(HudIcon.Battery, "On battery", Detail: p.TimeLeft is { } t && _settings.ShowBatteryEstimate
                    ? $"{p.Percent}% · {(int)t.TotalHours}h {t.Minutes}m" : $"{p.Percent}%"),
                PowerEvent.Low => new Hud(HudIcon.Battery, "Low battery", Detail: $"{p.Percent}%", Tint: Orange),
                PowerEvent.Critical => new Hud(HudIcon.Battery, "Battery very low", Detail: $"{p.Percent}% · plug in soon", Tint: Red),
                _ => new Hud(HudIcon.Charging, "Fully charged", Detail: "100%", Tint: Green),
            }, TimeSpan.FromSeconds(e is PowerEvent.Low or PowerEvent.Critical ? 5 : 2.5));
            _ = level;
        });
        h.Power.Changed += Invalidate;
        h.Power.Start();

        h.Bluetooth.ConnectionChanged += (name, connected) => Post(() =>
        {
            if (_settings.ShowBluetoothHud)
                ShowHud(new Hud(HudIcon.Bluetooth, name, Detail: connected ? "Connected" : "Disconnected", Tint: connected ? null : _theme.Dim),
                    TimeSpan.FromSeconds(2.5));
        });
        h.Bluetooth.Start();

        h.Privacy.Started += (what, app) => Post(() =>
        {
            if (_settings.ShowPrivacyIndicator)
                ShowHud(what == "camera"
                    ? new Hud(HudIcon.Camera, "Camera in use", Detail: app, Tint: Green)
                    : new Hud(HudIcon.Mic, "Microphone in use", Detail: app, Tint: Orange), TimeSpan.FromSeconds(2.5));
        });
        h.Privacy.Changed += Invalidate;
        h.Privacy.Start();

        if (_settings.ClipboardEnabled)
        {
            h.Clipboard.Changed += Invalidate;
            h.Clipboard.Start();
        }

        h.Shelf.Changed += Invalidate;
        _obs.Changed += Invalidate;
        h.Phone.Changed += Invalidate;
        h.Phone.Uploaded += files => Post(() =>
        {
            h.Shelf.Add(files);
            ShowHud(new Hud(HudIcon.Download, files.Length == 1 ? Path.GetFileName(files[0]) : $"{files.Length} files", Detail: "From your phone"), TimeSpan.FromSeconds(3));
        });
        h.Camera.Changed += Invalidate;
        h.Chat.Changed += Invalidate;
        h.Chat.Message += m => Post(() =>
        {
            // At most one pop-up every few seconds, so a busy chat doesn't take over the pill.
            if (!_settings.ChatPopups || !_settings.ChatPanel || DateTime.Now - _lastChatPopup < TimeSpan.FromSeconds(4)) return;
            _lastChatPopup = DateTime.Now;
            ShowHud(new Hud(HudIcon.Chat, m.Name, Detail: m.Text, Tint: m.Color), TimeSpan.FromSeconds(3));
        });
        h.Chat.Start();
        _obs.Start();
        _launcher.Changed += Invalidate;
        _agents.Changed += Invalidate;
        _agents.StateChanged += (session, previous) => Post(() => OnAgentState(session, previous));
        _server.ClaudeEvent += json => _agents.OnClaudeEvent(json);
        _server.AgentEvent += (name, state, text, project) => _agents.OnCustom(name, state, text, project);
        h.Ticker.Changed += Invalidate;
        h.Ticker.Start();
        h.Downloads.Changed += Invalidate;
        h.Downloads.Finished += path => Post(() =>
        {
            if (_settings.ShowDownloadActivity) ShowHud(new Hud(HudIcon.Download, Path.GetFileName(path), Detail: "Downloaded"), TimeSpan.FromSeconds(2.5));
        });
        h.Downloads.Start();
        h.Clipboard.PictureAdded += OnPictureCopied;
        h.Weather.Changed += Invalidate;
        h.Weather.Start();
        h.Stats.Changed += Invalidate;
        h.Stats.Start();
        h.Calendar.Changed += Invalidate;
        h.Calendar.Start();
        _controls.Changed += Invalidate;
        _ = Task.Run(_controls.StartAsync);

        h.Timer.Finished += phase => Post(() =>
        {
            Chime();
            var notice = phase == TimerPhase.Focus
                ? new Notice("Focus timer", "Focus done", $"Take a {_settings.BreakMinutes}-minute break.", null, DateTime.Now)
                : new Notice("Focus timer", "Break over", "Ready for the next focus?", null, DateTime.Now);
            ShowNotice(notice);
        });
        h.Reminders.Due += r => Post(() =>
        {
            Chime();
            ShowNotice(new Notice("Reminder", r.Text, r.Repeat == ReminderRepeat.None ? "" : $"Repeats {r.Repeat.ToString().ToLowerInvariant()}", null, DateTime.Now));
        });

        _server.Received += n => Post(() => ShowNotice(n));
        _server.StyleRequested += (surface, accent, album) => Post(() =>
            _theme.Choose(surface is null ? null : Looks.Surface(surface), accent is null ? null : Looks.Accent(accent), album));
        _server.PanelRequested += name => Post(() => ScreenUnderCursor()?.View.OpenPanel(name));
        _server.SnapshotRequested += (file, panel) => Post(() =>
        {
            if (_screens.FirstOrDefault()?.View is not { } view) return;
            _snapshot = (file, 45); // let the opening animation settle first
            view.Hidden = false;    // a hidden island would close the panel again
            if (panel != null) view.OpenPanel(panel);
        });
        _server.Start();

        if (_settings.MirrorNotifications)
        {
            h.Notifications.Arrived += n => Post(() => ShowNotice(n));
            _ = Task.Run(async () =>
            {
                await h.Notifications.StartAsync();
                Invalidate();
            });
        }
    }

    private void ShowHud(Hud hud, TimeSpan? time = null)
    {
        foreach (var s in _screens) s.View.ShowHud(hud, time);
        _dirty = true;
    }

    private void ShowNotice(Notice notice)
    {
        _hub.Notices.Add(notice);
        foreach (var s in _screens) s.View.PushNotice(notice);
        _dirty = true;
    }

    private static void Chime() => PlaySound("SystemNotification", IntPtr.Zero, SND_ALIAS | SND_ASYNC | SND_NODEFAULT);

    /// <summary>
    /// What deserves the resting pill right now, most important first: a call, a meeting about to
    /// start, a running timer, a download.
    /// </summary>
    private Activity? CurrentActivity()
    {
        if (_obs.Recording)
            return new Activity(80, HudIcon.Record, _obs.Streaming ? "Recording · Live" : "Recording", Red, Right: Ui.Clock(DateTime.Now - _obs.RecordingSince));
        if (_obs.Streaming)
            return new Activity(80, HudIcon.Record, "Live", Red, Right: Ui.Clock(DateTime.Now - _obs.StreamingSince));
        if (_settings.AgentsEnabled && _agents.Sessions.FirstOrDefault(s => s.State == AgentState.Waiting) is { } waiting)
            return new Activity(75, HudIcon.Agent, $"{waiting.Agent} needs you", Orange, Right: waiting.Project);
        if (_settings.ShowCallActivity && CallApp() is { } call)
        {
            var since = _callStarted.TryGetValue(call, out var t0) ? t0 : DateTime.Now;
            _callStarted.TryAdd(call, since);
            var muted = _hub.Microphone.Muted;
            return new Activity(70, muted ? HudIcon.MicOff : HudIcon.Mic, $"Call · {call}", muted ? Red : Green,
                Right: Ui.Clock(DateTime.Now - since));
        }
        _callStarted.Clear();
        if (_settings.MeetingCountdown && _hub.Calendar.Next is { } e)
        {
            var minutes = (e.Start - DateTime.Now).TotalMinutes;
            if (minutes is <= 10 and > -5)
                return new Activity(60, HudIcon.Alarm, e.Title, _theme.Accent,
                    Right: minutes > 0 ? $"in {Math.Ceiling(minutes)} min" : "now");
        }
        var t = _hub.Timer;
        if (t.Phase != TimerPhase.Idle)
        {
            var label = t.Paused ? "Paused" : t.Phase == TimerPhase.Break ? "Break" : "Focus";
            return new Activity(50, HudIcon.Timer, label, t.Phase == TimerPhase.Break ? Green : _theme.Accent, t.Progress, Ui.Clock(t.Remaining));
        }
        if (_settings.AgentsEnabled && _agents.Sessions.FirstOrDefault(s => s.State == AgentState.Working) is { } working)
            return new Activity(45, HudIcon.Agent, $"{working.Agent} · {working.Doing}", _theme.Accent, Right: Ui.Clock(DateTime.Now - working.Since));
        if (_settings.ShowDownloadActivity && _hub.Downloads.Current is { } d)
            return new Activity(40, HudIcon.Download, d.Name, _theme.Accent,
                Right: d.BytesPerSec > 0 ? $"{DownloadService.Size(d.Bytes)} · {DownloadService.Size((long)d.BytesPerSec)}/s" : DownloadService.Size(d.Bytes));
        return null;
    }

    private static readonly string[] CallApps = ["Teams", "Zoom", "WhatsApp", "Discord", "Skype", "Slack", "Webex", "Telegram", "Signal", "FaceTime", "Meet"];

    /// <summary>A calling app that has the microphone open, if any.</summary>
    private string? CallApp() =>
        _hub.Privacy.MicrophoneApps.FirstOrDefault(app => CallApps.Any(c => app.Contains(c, StringComparison.OrdinalIgnoreCase)));

    private void OnAgentState(AgentSession s, AgentState? previous)
    {
        if (!_settings.AgentsEnabled) return;
        var where = s.Project.Length > 0 ? s.Project : null;
        if (s.State == AgentState.Done && previous == AgentState.Working)
        {
            ShowHud(new Hud(HudIcon.Agent, $"{s.Agent} finished", Detail: where, Tint: Green), TimeSpan.FromSeconds(3));
            if (_settings.AgentsNotify) _hub.Notices.Add(new Notice(s.Agent, "Finished", where ?? "", null, DateTime.Now));
        }
        else if (s.State == AgentState.Waiting)
        {
            Chime();
            ShowHud(new Hud(HudIcon.Agent, $"{s.Agent} needs you", Detail: s.Doing, Tint: Orange), TimeSpan.FromSeconds(4));
            if (_settings.AgentsNotify) _hub.Notices.Add(new Notice(s.Agent, "Needs you", s.Doing, null, DateTime.Now));
        }
        else if (s.State == AgentState.Error)
            ShowHud(new Hud(HudIcon.Agent, $"{s.Agent} hit an error", Detail: where, Tint: Red), TimeSpan.FromSeconds(3));
    }

    private void CopyFiles(string[] files) => ClipboardIO.Write(_host.Handle, new ClipboardContent(null, files, null, null));

    /// <summary>The colour under the pointer, copied as #RRGGBB.</summary>
    private void PickColor()
    {
        GetCursorPos(out var p);
        var dc = GetDC(IntPtr.Zero);
        var bgr = GetPixel(dc, p.X, p.Y);
        ReleaseDC(IntPtr.Zero, dc);
        var color = new SKColor((byte)bgr, (byte)(bgr >> 8), (byte)(bgr >> 16));
        var hex = $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";
        ClipboardIO.Write(_host.Handle, new ClipboardContent(hex, null, null, null));
        ShowHud(new Hud(HudIcon.Color, hex, Detail: $"rgb({color.Red}, {color.Green}, {color.Blue}) · copied", Tint: color), TimeSpan.FromSeconds(2.5));
    }

    /// <summary>Opens Windows' region snipping; the picture comes back through the clipboard.</summary>
    private void TakeScreenshot()
    {
        _snipRequestedAt = DateTime.Now;
        Open("ms-screenclip:");
    }

    private void OnPictureCopied(ClipEntry entry)
    {
        if (DateTime.Now - _snipRequestedAt > TimeSpan.FromMinutes(2) || entry.Content.Png is not { } png) return;
        _snipRequestedAt = default;
        try
        {
            var folder = Path.Combine(KnownFolder(FolderPictures) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "HaloBar");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, $"Screenshot {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png");
            File.WriteAllBytes(file, png);
            if (_settings.CapturesToShelf) _hub.Shelf.Add([file]);
            ShowHud(new Hud(HudIcon.Copied, "Screenshot saved", Detail: "Copied · on the shelf"), TimeSpan.FromSeconds(2.5));
        }
        catch (IOException ex)
        {
            Log.Write($"Screenshot not saved: {ex.Message}");
        }
    }

    /// <summary>Caps, Num and Scroll Lock are toggles kept system-wide, so reading them from here works.</summary>
    private static (bool, bool, bool) ReadLocks() =>
        ((GetKeyState(0x14) & 1) != 0, (GetKeyState(0x90) & 1) != 0, (GetKeyState(0x91) & 1) != 0);

    private void CheckLockKeys()
    {
        var now = ReadLocks();
        if (now == _locks) return;
        var (caps, num, scroll) = now;
        if (_settings.ShowLockKeysHud)
        {
            if (caps != _locks.Caps) ShowHud(new Hud(HudIcon.LockKey, caps ? "Caps Lock on" : "Caps Lock off", Tint: caps ? _theme.Accent : null));
            else if (num != _locks.Num) ShowHud(new Hud(HudIcon.LockKey, num ? "Num Lock on" : "Num Lock off"));
            else if (scroll != _locks.Scroll) ShowHud(new Hud(HudIcon.LockKey, scroll ? "Scroll Lock on" : "Scroll Lock off"));
        }
        _locks = now;
    }

    private void OnHotkey(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.ToggleCard: ScreenUnderCursor()?.View.ToggleExpanded(); break;
            case HotkeyAction.PlayPause: _hub.Media.TogglePlayPause(); break;
            case HotkeyAction.Timer:
                _hub.Timer.Toggle();
                ShowHud(new Hud(HudIcon.Timer, _hub.Timer.Paused ? "Timer paused" : "Focus timer", Detail: Ui.Clock(_hub.Timer.Remaining)));
                break;
            case HotkeyAction.Hide:
                _userHidden = !_userHidden;
                Log.Write(_userHidden ? "Island hidden by hotkey" : "Island shown by hotkey");
                break;
            case HotkeyAction.MicMute: _hub.Microphone.ToggleMute(); break;
            case HotkeyAction.NewReminder: ScreenUnderCursor()?.View.NewReminder(); break;
            case HotkeyAction.ColorPick: PickColor(); break;
            case HotkeyAction.ObsRecord:
                if (_obs.Connected) _obs.ToggleRecord();
                else ShowHud(new Hud(HudIcon.Record, "OBS isn't connected", Detail: _obs.Status));
                break;
            case HotkeyAction.Screenshot: TakeScreenshot(); break;
            case HotkeyAction.Output:
                if (ControlsService.CycleDefault(NAudio.CoreAudioApi.DataFlow.Render) is { } name)
                    ShowHud(new Hud(HudIcon.Volume, name, Detail: "Output"));
                break;
        }
        _dirty = true;
    }

    private IReadOnlyList<TrayMenuItem> TrayMenu() =>
    [
        new("Open island\tCtrl+Alt+I", () => ScreenUnderCursor()?.View.ToggleExpanded()),
        new("New reminder\tCtrl+Alt+E", () => ScreenUnderCursor()?.View.NewReminder()),
        new("Take a screenshot\tCtrl+Alt+F", TakeScreenshot),
        new("Pick a colour\tCtrl+Alt+C", PickColor),
        new(_hub.Timer.Running ? "Pause focus timer\tCtrl+Alt+T" : "Start focus timer\tCtrl+Alt+T", _hub.Timer.Toggle),
        TrayMenuItem.Separator,
        TrayMenuItem.Submenu("Surface", [.. Looks.Surfaces.Select(s =>
            new TrayMenuItem(s.Name, () => _theme.Choose(surface: s), s == _theme.Surface))]),
        TrayMenuItem.Submenu("Accent", [.. Looks.Accents.Select(a =>
            new TrayMenuItem(a.Name, () => _theme.Choose(accent: a), a.Id == _theme.AccentChoice.Id))]),
        TrayMenuItem.Submenu("Music card",
        [
            new("Plain", () => _theme.Choose(albumCard: false), !_theme.AlbumCard),
            new("Album colours", () => _theme.Choose(albumCard: true), _theme.AlbumCard),
        ]),
        TrayMenuItem.Separator,
        new("Start with Windows", () => StartupRegistration.Set(!StartupRegistration.IsEnabled), StartupRegistration.IsEnabled),
        new("Replace the Windows volume pop-up", () =>
        {
            _settings.ReplaceVolumePopup = !_settings.ReplaceVolumePopup;
            ApplyLayout();
            _settings.Save();
        }, _settings.ReplaceVolumePopup),
        new("Show on every monitor", () =>
        {
            _settings.AllMonitors = !_settings.AllMonitors;
            _layoutDirty = true;
            _settings.Save();
        }, _settings.AllMonitors),
        new("Hide from screenshots and sharing", () =>
        {
            _settings.HideFromCapture = !_settings.HideFromCapture;
            _layoutDirty = true;
            _settings.Save();
        }, _settings.HideFromCapture),
        new("Hide the island\tCtrl+Alt+H", () => _userHidden = !_userHidden, _userHidden),
        TrayMenuItem.Separator,
        new($"Windows notifications: {_hub.Notifications.Status}", () => { }, Enabled: false),
        new($"Local API: 127.0.0.1:{_server.Port}", () => { }, Enabled: false),
        new($"Memory: {MemoryText()}", () => { }, Enabled: false),
        TrayMenuItem.Separator,
        new("Open settings file", () => { _settings.Save(); Open(Path.Combine(AppPaths.Folder, "settings.json")); }),
        new("Open log", () => Open(Log.FilePath)),
        TrayMenuItem.Separator,
        new("Exit", () => DestroyWindow(_host.Handle)),
    ];

    private static void Open(string target) => Open(target, null);

    private static void Open(string target, string? arguments)
    {
        try
        {
            var info = new ProcessStartInfo(target) { UseShellExecute = true };
            if (arguments != null) info.Arguments = arguments;
            Process.Start(info);
        }
        catch (Exception ex) { Log.Write($"Open {target}: {ex.Message}"); }
    }

    private bool OnHostMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == TrayIcon.TaskbarCreated)
        {
            // Explorer restarted: the pop-up's window is a new one too.
            _flyout.Reset();
            if (_settings.ReplaceVolumePopup) _flyout.Ensure();
        }
        if (_tray.HandleMessage(msg, lParam))
        {
            _dirty = true;
            return true;
        }
        switch (msg)
        {
            case WM_HOTKEY:
                OnHotkey((HotkeyAction)(int)wParam);
                return true;
            case WM_CLIPBOARDUPDATE:
                if (_settings.ClipboardEnabled) _hub.Clipboard.OnClipboardUpdate();
                return true;
            case WakeMessage:
                return true;
        }
        return false;
    }

    /// <summary>Runs <paramref name="action"/> on the window thread and wakes the loop.</summary>
    private void Post(Action action)
    {
        _ui.Enqueue(action);
        PostMessageW(_host.Handle, WakeMessage, IntPtr.Zero, IntPtr.Zero);
    }

    private void Invalidate()
    {
        _dirty = true;
        PostMessageW(_host.Handle, WakeMessage, IntPtr.Zero, IntPtr.Zero);
    }

    // ───────────────────────────── frame loop ─────────────────────────────

    public void Run()
    {
        var clock = Stopwatch.StartNew();
        double last = 0, nextFullscreenCheck = 0, nextTopCheck = 0, nextSecond = 0;
        var ownWindows = new HashSet<IntPtr>();

        while (true)
        {
            while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                if (msg.message == WM_QUIT) return;
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
            while (_ui.TryDequeue(out var action))
            {
                action();
                _dirty = true;
            }
            if (_layoutDirty)
            {
                _layoutDirty = false;
                RebuildScreens();
                _dirty = true;
            }

            var now = clock.Elapsed.TotalSeconds;
            var dt = now - last;
            last = now;
            _theme.Tick(dt, _hub.Media.Snapshot?.Palette);
            CheckLockKeys();
            if (now >= nextSecond)
            {
                _hub.Timer.Tick();
                _hub.Reminders.Tick();
                nextSecond = now + 1;
            }

            if (now >= nextFullscreenCheck)
            {
                ownWindows.Clear();
                foreach (var s in _screens) ownWindows.Add(s.Window.Handle);
                var front = FullscreenWatcher.Inspect(ownWindows);
                _frontApp = front.Process ?? "";
                _hub.FrontApp = _frontApp;
                var rule = _settings.AppRules.FirstOrDefault(r => r.Process.Equals(_frontApp, StringComparison.OrdinalIgnoreCase))?.Behaviour;
                foreach (var s in _screens)
                {
                    var here = s.Monitor.Handle == front.Monitor;
                    var game = here && front.Game && _settings.GameStrip;
                    var fullscreenHere = here && front.Fullscreen && _settings.HideInFullscreen && !game;
                    s.View.GameStrip = game && _snapshot == null;
                    s.View.Slim = here && (rule == "slim" || (_settings.SlimWhenCovered && front.Maximized && rule != "normal"));
                    var hidden = (_userHidden || fullscreenHere || (here && rule == "hide")) && _snapshot == null;
                    if (hidden != s.View.Hidden && !_userHidden) Log.Write($"Full screen on {s.Monitor.Key}: {(hidden ? "hiding" : "showing")}");
                    s.View.Hidden = hidden;
                }
                nextFullscreenCheck = now + 0.5;
            }
            if (now >= nextTopCheck)
            {
                foreach (var s in _screens) s.Window.KeepOnTop();
                nextTopCheck = now + 2;
            }

            // The windows are click-through, so they never see the pointer arrive; poll it instead.
            foreach (var s in _screens)
            {
                var (cx, cy) = s.Window.CursorPosition();
                var inside = cx >= 0 && cy >= 0 && cx < s.Window.Width && cy < s.Window.Height;
                var cursor = new SKPoint(cx / s.Window.Scale, cy / s.Window.Scale);
                s.Interval = s.View.Update(dt, inside ? cursor : null, DateTime.Now);
                s.Window.ClickThrough = !(inside && s.View.Contains(cursor)) && !s.View.IsTyping;
            }

            bool rendered = false, animating = false, lost = false;
            foreach (var s in _screens)
            {
                if (!_dirty && now - s.LastFrame < s.Interval) continue;
                lost |= !s.Render();
                s.LastFrame = now;
                rendered = true;
                animating |= s.Interval == 0;
            }
            _dirty = false;
            if (_snapshot is { } snap && _screens.Count > 0)
            {
                _screens[0].View.Hidden = false;
                if (snap.Frames > 0)
                {
                    _snapshot = snap with { Frames = snap.Frames - 1 };
                    _dirty = true; // keep frames coming until the snapshot is taken
                }
                else
                {
                    _screens[0].Render();
                    using (var data = _screens[0].Surface.Bitmap.Encode(SKEncodedImageFormat.Png, 100))
                        File.WriteAllBytes(snap.File, data.ToArray());
                    _snapshot = null;
                }
            }
            if (lost || _gfx.IsLost)
            {
                RecreateGraphics();
                continue;
            }

            TrimMemoryWhenIdle(now);

            // One wait for the next composition paces every monitor's animation together.
            if (rendered && animating)
            {
                DwmFlush();
                continue;
            }
            var elapsed = clock.Elapsed.TotalSeconds;
            var wait = _screens.Count == 0 ? 0.25 : _screens.Min(s => s.Interval - (elapsed - s.LastFrame));
            wait = Math.Clamp(wait, 0.001, 0.033);
            MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, (uint)(wait * 1000), QS_ALLINPUT, MWMO_INPUTAVAILABLE);
        }
    }

    // ───────────────────────────── memory ─────────────────────────────

    /// <summary>
    /// The island spends most of its life idle. Once nothing has been open for a few seconds,
    /// return freed heap to Windows and let it page out what isn't being touched; pages that are
    /// needed again fault back in. Repeats every few minutes while idle.
    /// </summary>
    private void TrimMemoryWhenIdle(double now)
    {
        if (_screens.Any(s => s.View.Mode != IslandMode.Collapsed))
        {
            _trimAt = now + 10;
            return;
        }
        if (now < _trimAt) return;
        _trimAt = now + TrimEvery.TotalSeconds;

        var before = MemoryText();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        EmptyWorkingSet(GetCurrentProcess());
        Log.Write($"Memory trimmed: {before} -> {MemoryText()}");
    }

    private static string MemoryText()
    {
        using var p = Process.GetCurrentProcess();
        return $"{p.WorkingSet64 / (1024 * 1024)} MB in use, {p.PrivateMemorySize64 / (1024 * 1024)} MB private, " +
               $"{GC.GetTotalMemory(false) / (1024 * 1024)} MB managed";
    }

    public void Dispose()
    {
        _flyout.Dispose();
        foreach (var action in _registeredHotkeys) UnregisterHotKey(_host.Handle, (int)action);
        RemoveClipboardFormatListener(_host.Handle);
        _tray.Dispose();
        _server.Dispose();
        _hub.Notifications.Dispose();
        _hub.Volume.Dispose();
        _hub.Microphone.Dispose();
        _hub.Brightness.Dispose();
        _hub.Power.Dispose();
        _hub.Privacy.Dispose();
        _hub.Bluetooth.Dispose();
        _hub.Weather.Dispose();
        _hub.Stats.Dispose();
        _hub.Calendar.Dispose();
        _hub.Lyrics.Dispose();
        _hub.Downloads.Dispose();
        _hub.Ticker.Dispose();
        _obs.Dispose();
        _hub.Phone.Dispose();
        _hub.Chat.Dispose();
        _hub.Camera.Dispose();
        _hub.Media.Dispose();
        foreach (var s in _screens) s.Dispose();
        _gfx.Dispose();
        _theme.Dispose();
        _host.Dispose();
        Log.Write("HaloBar stopped");
    }
}
