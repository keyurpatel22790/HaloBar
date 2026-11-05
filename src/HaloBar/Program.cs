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

        // For the build: HaloBar.exe --write-icon <path.ico> draws the app icon.
        if (args is ["--write-icon", var iconPath])
        {
            IconArt.WriteIco(iconPath);
            return 0;
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
    public OverlayWindow Window { get; }
    public CompositionSurface Surface { get; private set; }
    public IslandView View { get; }
    public MonitorInfo Monitor => Window.Monitor;
    public double Interval { get; set; }
    public double LastFrame { get; set; } = double.NegativeInfinity;
    /// <summary>The button went down on the island itself (a click or a drag out, not something carried in).</summary>
    public bool PressedHere { get; set; }
    /// <summary>Something held with the button is over the island: the shelf shows as a drop zone.</summary>
    public bool Carrying { get; set; }
    private AcrylicBackdrop? _backdrop;
    private bool _backdropFailed;
    private GraphicsDevice _gfx;
    private IGlassSource? _glass;
    private bool _excluded;
    /// <summary>After the screen reader stops (PC locked, display mode change), when to try a fresh one.</summary>
    private DateTime _glassRetryAt;
    private bool _glassGdiOnly;
    private double _glassRetrySeconds = 5;

    public IslandScreen(OverlayWindow window, GraphicsDevice gfx, IslandView view)
    {
        Window = window;
        View = view;
        _gfx = gfx;
        Surface = new CompositionSurface(gfx, window.Handle, window.Width, window.Height);
    }

    public void ReplaceSurface(GraphicsDevice gfx)
    {
        _gfx = gfx;
        _glass?.Dispose();
        _glass = null;
        _glassRetryAt = default;
        DropUploadedGlass();
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
        // On the GPU each frame has a fresh canvas; on the CPU it's reused, so start from a clean save stack.
        var canvas = Surface.BeginFrame();
        canvas.RestoreToCount(1);
        canvas.ResetMatrix();
        canvas.Scale(Window.Scale);
        var timer = Stopwatch.StartNew();
        FeedGlass();
        try
        {
            View.Draw(canvas);
            UpdateBackdrop();
        }
        catch (Exception ex)
        {
            // Keep the app alive; a broken frame is better than no island.
            Log.Write($"Draw failed: {ex}");
            canvas.RestoreToCount(1);
        }
        canvas.Flush();
        DrawMs += timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        var ok = Surface.Present();
        PresentMs += timer.Elapsed.TotalMilliseconds;
        return ok;
    }

    /// <summary>Time spent drawing and handing frames to the GPU, for the performance line in the log.</summary>
    public static double DrawMs, PresentMs;

    /// <summary>
    /// Gives the view the screen behind the island while the Glass lens is the surface. The island
    /// is kept out of captures meanwhile (Windows would otherwise hand it a picture of itself), so
    /// it doesn't show in screenshots then, the same as NotchPixel's glass.
    /// </summary>
    private void FeedGlass()
    {
        // While HaloBar records the screen, or the island is to show in screen sharing (Settings),
        // it can't be kept out of captures: the copy then has the island itself in it, and that
        // spot is filled from the screen around it (a wider area is read, for room around it).
        var seen = Recording || Sharing;
        var want = View.WantsGlass && DateTime.Now >= _glassRetryAt;
        var exclude = !seen;
        if (exclude != _excluded)
        {
            Window.SetCaptureExcluded(exclude);
            _excluded = exclude;
        }
        if (!want)
        {
            View.Glass = null;
            return;
        }
        // Desktop duplication is tried first (smoother behind video); but some outputs (an HDR or
        // mirrored monitor) duplicate only blank frames, so once one proves itself a dud we remember
        // to use the plain screen copy for this screen instead.
        _glass ??= (_glassGdiOnly ? null : (IGlassSource?)GlassBackdrop.Create(_gfx, Monitor.Handle)) ?? new ScreenCopyBackdrop();
        if (_glass == null || !_glass.Working)
        {
            if (_glass is GlassBackdrop { NeverDelivered: true }) _glassGdiOnly = true;
            _glass?.Dispose();
            _glass = null;
            View.Glass = null;
            // Switch straight to the screen copy; otherwise back off and retry (locked PC, mode change).
            if (!_glassGdiOnly)
            {
                _glassRetryAt = DateTime.Now.AddSeconds(_glassRetrySeconds);
                _glassRetrySeconds = Math.Min(60, _glassRetrySeconds * 2);
            }
            return;
        }
        // Open (or opening): the whole window, so resizing never waits for a copy. Closed: just a
        // band around the pill, far less to copy 40 times a second. The new area is copied at once.
        Native.GetWindowRect(Window.Handle, out var w);
        var s = Window.Scale;
        var b = View.Bounds;
        var open = View.Mode == IslandMode.Expanded || b.Height > 44;
        var screen = open
            ? new SKRectI(w.Left, w.Top, w.Right, w.Bottom)
            : new SKRectI(w.Left + (int)((b.Left - 24) * s), w.Top, w.Left + (int)Math.Ceiling((b.Right + 24) * s), w.Top + (int)Math.Ceiling((b.Bottom + 20) * s));
        SKRectI? hole = null;
        if (seen)
        {
            // The island and everything it draws outside itself (its shadow), and a band around it to fill from.
            var (sides, below) = Rendering.LiquidGlass.Reach(Math.Clamp((b.Height - 34) / 120f, 0, 1));
            var island = new SKRectI(w.Left + (int)((b.Left - sides) * s), w.Top + (int)((b.Top - sides) * s),
                w.Left + (int)Math.Ceiling((b.Right + sides) * s), w.Top + (int)Math.Ceiling((b.Bottom + below) * s));
            // The copy is taken a moment later, on its own thread, and the island may still be drawn
            // where it was then (a resize, say the recording timer ticking wider): everywhere it was in
            // the last third of a second is cut out too, or a sliver of its own glass leaks into the
            // picture and the whole pill blinks darker for a frame.
            var now = DateTime.Now;
            _recentHoles.Enqueue((now, island));
            while (_recentHoles.Count > 1 && now - _recentHoles.Peek().At > TimeSpan.FromSeconds(0.35)) _recentHoles.Dequeue();
            foreach (var (_, was) in _recentHoles) island = SKRectI.Union(island, was);
            var m = Monitor.Bounds;
            var room = (int)(48 * s);
            screen = SKRectI.Intersect(SKRectI.Inflate(island, room, room), new SKRectI(m.Left, m.Top, m.Right, m.Bottom));
            hole = island;
        }
        else _recentHoles.Clear();
        var picture = _glass.Behind(screen, View.GlassBlur, (open ? 16 : 25) * (View.SaveGlass ? 3 : 1), hole);
        _shownVersion = _glass.Version;
        RawGlass = picture;
        View.Glass = OnGraphicsCard(picture);
        // Where that picture lies on the canvas: it may be the last area while the new one is copied.
        var area = _glass.Area;
        View.GlassArea = new SKRect((area.Left - w.Left) / s, (area.Top - w.Top) / s, (area.Right - w.Left) / s, (area.Bottom - w.Top) / s);
        // How bright it is behind, for auto text colour (measured on the copier's thread).
        if (picture != null)
        {
            View.Theme.BehindTone = _glass.Tone;
            _glassRetrySeconds = 5;
        }
    }

    private int _shownVersion = -1;
    private readonly Queue<(DateTime At, SKRectI Hole)> _recentHoles = new();

    /// <summary>The glass picture as the reader made it (in memory), for the glass snapshot.</summary>
    public SKImage? RawGlass { get; private set; }
    private SKImage? _uploadedFrom, _uploaded;

    /// <summary>
    /// Drawing on the GPU, the glass picture goes up to the graphics card once, when a new one
    /// arrives; drawn as a memory image, Skia would upload it again on every frame.
    /// </summary>
    private SKImage? OnGraphicsCard(SKImage? picture)
    {
        if (picture == null || _gfx.Gpu is not { } gpu) return picture;
        if (ReferenceEquals(picture, _uploadedFrom) && _uploaded != null) return _uploaded;
        _uploaded?.Dispose();
        _uploaded = picture.ToTextureImage(gpu.Context);
        _uploadedFrom = picture;
        return _uploaded ?? picture;
    }

    private void DropUploadedGlass()
    {
        _uploaded?.Dispose();
        _uploaded = null;
        _uploadedFrom = null;
    }
    /// <summary>Set by the app while its screen recorder runs.</summary>
    public static bool Recording { get; set; }
    /// <summary>Settings › Show in screenshots and sharing: the island stays visible to captures (its glass fills its own spot).</summary>
    public static bool Sharing { get; set; }

    /// <summary>What the glass is reading, for the log.</summary>
    public string GlassInfo => $"{_glass?.GetType().Name ?? "none"} area {_glass?.Area} picture {View.Glass?.Width}x{View.Glass?.Height} on {Monitor.Key}";

    /// <summary>A newer glass picture is ready than the one on screen: draw a frame now.</summary>
    public bool GlassStale
    {
        get
        {
            if (_glass == null || !View.WantsGlass) return false;
            _glass.Poll();
            return _glass.Version != _shownVersion;
        }
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


    public void Dispose()
    {
        _glass?.Dispose();
        _backdrop?.Dispose();
        DropUploadedGlass();
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
    private readonly Platform.DimOverlay _dim = new();
    private int _dimFor = -1, _warmthFor = -1;
    private readonly TrayIcon _tray;
    private readonly ConcurrentQueue<Action> _ui = new();
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
        Lang.Use(settings.Language);
        Prefs.Apply(settings);
        // The CPU rasteriser only needs its caches for glyphs and a few small images.
        SKGraphics.SetResourceCacheTotalByteLimit(4 * 1024 * 1024);
        SKGraphics.SetFontCacheLimit(2 * 1024 * 1024);

        GraphicsDevice.UseGpu = settings.GpuDrawing;
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
        var stats = new StatsService();
        _hub = new Hub
        {
            Settings = settings,
            Theme = _theme,
            Media = new MediaService(settings),
            Volume = new VolumeService(),
            Mixer = new AudioMixerService(),
            Microphone = new MicrophoneService(),
            Brightness = new BrightnessService(),
            Power = new PowerService(),
            Privacy = new PrivacyService(() => settings.PrivacyIgnoredApps),
            Bluetooth = new BluetoothService(),
            Clipboard = clipboard,
            Notices = new NoticeHistory(),
            Notifications = new NotificationService(),
            Weather = new WeatherService(settings),
            Stats = stats,
            Timer = new TimerService(settings),
            Reminders = new ReminderService(settings),
            Calendar = new CalendarService(settings),
            Lyrics = new LyricsService(),
            Shelf = shelf,
            Controls = _controls,
            Wifi = new WifiService(),
            Profiles = new ProfileService(settings, _controls, settings.Save, ApplyLayout),
            Network = new NetworkHealth(() => settings.NetworkWatch),
            Health = new HealthWatch(settings, stats),
            Layouts = new LayoutService(settings, settings.Save),
            Agents = _agents,
            Launcher = _launcher,
            Obs = _obs,
            Phone = new PhoneShareService(shelf, () => settings.PhoneShareCopiedText ? clipboard.Entries.FirstOrDefault(e => e.Kind == ClipKind.Text)?.Content.Text : null),
            Chat = new ChatService(settings),
            Camera = new CameraService(),
            Downloads = new DownloadService(),
            Ticker = new TickerService(settings),
            ScreenTime = new ScreenTimeService(settings),
            Hotkeys = new HotkeyService(settings, _host.Handle),
            Web = new WebDrawer(() => ScreenUnderCursor()?.Monitor, Post, url => Open(url), () => settings.WebExtensions,
                () => (settings.WebAdBlock, settings.WebAdFastForward), settings),
            Recorder = new ScreenRecorder(),
            KeepAwake = new KeepAwakeService(),
            Speech = new SpeechService(),
            ToggleRecording = () => Post(ToggleRecording),
            Quiet = () => Quiet,
            ToggleDoNotDisturb = () => Post(ToggleDoNotDisturb),
            ToggleSharing = () => Post(ToggleSharing),
            ToggleKeepAwake = () => Post(ToggleKeepAwake),
            Banners = new BannerService(settings),
            ApiPort = settings.NotifyPort,
            ShowHud = hud => Post(() => ShowHud(hud)),
            RunOnUi = Post,
            OpenSettings = page => Post(() => OpenSettingsWindow(page)),
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
        _hub.Hotkeys.Pressed += OnHotkey;
        _hub.Hotkeys.Register();
        _hub.KeepAwake.Set(settings.KeepAwake);
        ApplyDim();
        if (settings.ReplaceVolumePopup) _flyout.Ensure();
        _locks = ReadLocks();

        StartServices();
        SyncWidgets();
        WatchForeground();
    }

    // ───────────────────────────── screens ─────────────────────────────

    /// <summary>Matches islands to the connected monitors: keeps, moves, adds or removes them.</summary>
    private void RebuildScreens()
    {
        var monitors = Monitors.All();
        if (!_settings.AllMonitors) monitors = [.. monitors.Take(1)];

        // A new Open size may need bigger windows: build them again.
        var grow = Math.Max(1, Math.Clamp(_settings.OpenSize, 90, 120) / 100f);
        if (grow != IslandView.Grow)
        {
            IslandView.Grow = grow;
            foreach (var old in _screens) old.Dispose();
            _screens = [];
        }
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
        // Each screen keeps itself out of captures as needed (Hide from screenshots, or the glass lens) as it draws.
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
        window.MouseDown += (x, y) => screen.PressedHere = true;
        window.MouseDown += (x, y) => view.PointerDown(new SKPoint(x / window.Scale, y / window.Scale));
        // Not mid-frame: the shell's drag holds the thread until the drop.
        view.FileDragStarted += files => Post(() => DragOut(window.Handle, files));
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
        window.FilesDropped += files =>
        {
            if (!_settings.ShelfEnabled || DateTime.Now - _draggedOutAt < TimeSpan.FromSeconds(1)) return; // off, or a shelf file let go over the island
            _hub.Shelf.Add(files);
            ShowHud(new Hud(HudIcon.Download, files.Length == 1 ? Path.GetFileName(files[0]) : $"{files.Length} files", Detail: "On the shelf"));
            if (_settings.MediaInfoOnDrop && files.Length == 1 && MediaInfo.IsMedia(files[0]))
            {
                var file = files[0];
                _ = MediaInfo.DescribeAsync(file).ContinueWith(t =>
                {
                    if (t.Result is { } info)
                        Post(() => ShowHud(new Hud(HudIcon.Download, Path.GetFileName(file), Detail: info), TimeSpan.FromSeconds(4)));
                });
            }
            _dirty = true;
        };
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
        foreach (var w in _widgets) w.ReplaceSurface(_gfx);
        _chatOverlay?.ReplaceSurface(_gfx);
        _settingsWindow?.ReplaceSurface(_gfx);
        _dirty = true;
    }

    // ───────────────────────────── desktop widgets ─────────────────────────────

    private readonly List<DesktopWidget> _widgets = [];
    private ChatOverlay? _chatOverlay;
    private SettingsWindow? _settingsWindow;

    /// <summary>The Settings window, made the first time it's asked for and kept (closing hides it).</summary>
    private void OpenSettingsWindow(string? page)
    {
        _settingsWindow ??= new SettingsWindow(_hub, _gfx, () => _layoutDirty = true);
        _settingsWindow.Open(page);
    }
    private WinEventProc? _foregroundHook; // kept alive: Windows calls it
    private IntPtr _foregroundHookHandle;

    /// <summary>Watches which window comes to the front, so widgets can rise over "Show desktop".</summary>
    private void WatchForeground()
    {
        _foregroundHook = (_, _, hwnd, _, _, _, _) =>
        {
            // A widget or the island taking the keyboard isn't an app coming forward: the desktop
            // is still what's showing, so the widgets stay where they are.
            GetWindowThreadProcessId(hwnd, out var process);
            if (process == Environment.ProcessId) return;
            _desktopInFront = IsDesktop(hwnd);
            foreach (var w in _widgets) w.Window.OverDesktop(_desktopInFront);
        };
        _foregroundHookHandle = SetWinEventHook(3 /* EVENT_SYSTEM_FOREGROUND */, 3, IntPtr.Zero, _foregroundHook, 0, 0, 0 /* WINEVENT_OUTOFCONTEXT */);
    }

    /// <summary>The last window to come forward that wasn't HaloBar's own was the desktop.</summary>
    private bool _desktopInFront;

    /// <summary>
    /// Re-checks which window is in front and puts the widgets right, in case a foreground event
    /// was missed (they'd otherwise stay lifted over an app, or hidden behind the desktop).
    /// </summary>
    private void SettleWidgets()
    {
        var front = GetForegroundWindow();
        GetWindowThreadProcessId(front, out var process);
        if (process != Environment.ProcessId) _desktopInFront = IsDesktop(front);
        foreach (var w in _widgets) w.Window.OverDesktop(_desktopInFront);
    }

    private static bool IsDesktop(IntPtr hwnd)
    {
        var name = new char[32];
        var length = GetClassNameW(hwnd, name, name.Length);
        return new string(name, 0, Math.Max(0, length)) is "WorkerW" or "Progman";
    }

    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint process, uint thread, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    /// <summary>
    /// Opens a window for each tile pinned to the desktop and closes the ones unpinned. A newly
    /// pinned tile, or one left off every screen, goes into the first free spot on the primary
    /// screen: down a column from the top right, then the next column to the left.
    /// </summary>
    private void SyncWidgets()
    {
        // The chat overlay comes and goes with its switch in Settings.
        if (_settings.ChatOverlay && _chatOverlay == null) _chatOverlay = new ChatOverlay(_hub, _gfx);
        else if (!_settings.ChatOverlay && _chatOverlay != null)
        {
            _chatOverlay.Dispose();
            _chatOverlay = null;
        }
        var places = _settings.DesktopWidgets;
        foreach (var gone in _widgets.Where(w => !places.Contains(w.Place)).ToList())
        {
            _widgets.Remove(gone);
            gone.Dispose();
        }
        var monitors = Monitors.All();
        foreach (var place in places.Where(p => p.X != 0 || p.Y != 0).ToList())
        {
            // Off screen (pinned below the last one, or a monitor was unplugged): place it again.
            if (monitors.Any(m => place.X + 40 > m.Bounds.Left && place.X < m.Bounds.Right - 40
                                  && place.Y >= m.Bounds.Top && place.Y < m.Bounds.Bottom - 60)) continue;
            Log.Write($"Widget {place.Tile} was off screen at {place.X},{place.Y}; placing it again");
            (place.X, place.Y) = (0, 0);
            if (_widgets.FirstOrDefault(w => w.Place == place) is { } stale)
            {
                _widgets.Remove(stale);
                stale.Dispose();
            }
        }
        foreach (var place in places.Where(p => _widgets.All(w => w.Place != p)).ToList())
        {
            var tile = UI.Tiles.TileCatalogue.Create(_hub).FirstOrDefault(t => t.Id == place.Tile);
            if (tile == null)
            {
                places.Remove(place);
                continue;
            }
            if (place.X == 0 && place.Y == 0)
            {
                (place.X, place.Y) = FreeWidgetSpot(monitors, place.Wide);
                _settings.Save();
            }
            var widget = new DesktopWidget(_hub, _gfx, tile, place);
            // Pinned while the desktop is in front: at the bottom it would start out behind it.
            widget.Window.OverDesktop(_desktopInFront);
            _widgets.Add(widget);
        }
    }

    /// <summary>The top-left of the first spot on the primary screen that no widget covers.</summary>
    private (int X, int Y) FreeWidgetSpot(IReadOnlyList<MonitorInfo> monitors, bool wide)
    {
        var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        var work = GetMonitorInfoW(primary.Handle, ref info) ? info.rcWork : primary.Bounds;
        var s = primary.Scale;
        int width = (int)((wide ? DesktopWidget.WideWidth : DesktopWidget.NarrowWidth) * s), height = (int)(DesktopWidget.TileHeight * s);
        int gap = (int)(12 * s), margin = (int)(24 * s), top = work.Top + (int)(60 * s);
        var taken = _widgets.Select(w => w.Window.Rect).Select(r => new SKRectI(r.Left, r.Top, r.Right, r.Bottom)).ToList();
        var column = (int)(DesktopWidget.NarrowWidth * s) + gap;
        for (var x = work.Right - margin - width; x >= work.Left + margin; x -= column)
            for (var y = top; y + height <= work.Bottom - margin; y += height + gap)
            {
                var spot = new SKRectI(x, y, x + width, y + height);
                var padded = new SKRectI(spot.Left - gap / 2, spot.Top - gap / 2, spot.Right + gap / 2, spot.Bottom + gap / 2);
                if (!taken.Any(t => t.IntersectsWith(padded))) return (x, y);
            }
        return (work.Right - margin - width, top); // the screen is full: overlap the first spot
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
            if (!_settings.ChatPopups || !_settings.ChatPanel || Quiet || DateTime.Now - _lastChatPopup < TimeSpan.FromSeconds(4)) return;
            _lastChatPopup = DateTime.Now;
            ShowHud(new Hud(HudIcon.Chat, m.Name, Detail: m.Text, Tint: m.Color), TimeSpan.FromSeconds(3));
        });
        h.Chat.Start();
        _obs.Start();
        _launcher.Changed += Invalidate;
        _agents.Changed += Invalidate;
        _agents.StateChanged += (session, previous) => Post(() => OnAgentState(session, previous));
        _server.HookEvent += (agent, json) => _agents.OnHookEvent(agent, json);
        _server.WebRequested += site => Post(() => ToggleWeb(site));
        _server.RecordRequested += action => Post(() =>
        {
            if (action == "toggle" || (action == "start") != _hub.Recorder.Recording) ToggleRecording();
        });
        _hub.Web.Changed += Invalidate;
        _hub.Recorder.Changed += Invalidate;
        _server.AgentEvent += (name, state, text, project) => _agents.OnCustom(name, state, text, project);
        h.Ticker.Changed += Invalidate;
        h.Ticker.AlertTriggered += (alert, quote) => Post(() =>
        {
            var text = $"{alert.Symbol} is {(alert.Above ? "above" : "below")} {alert.Level:#,0.##}";
            var detail = $"Now {quote.Price:#,0.####} {quote.Currency}";
            ShowNotice(new Notice("Price alert", text, detail, null, DateTime.Now));
        });
        h.Ticker.Start();
        h.Downloads.Changed += Invalidate;
        h.Downloads.Finished += path => Post(() =>
        {
            if (_settings.ShowDownloadActivity) ShowHud(new Hud(HudIcon.Download, Path.GetFileName(path), Detail: "Downloaded"), TimeSpan.FromSeconds(2.5));
        });
        h.Downloads.Start();
        h.Clipboard.PictureAdded += OnPictureCopied;
        h.Clipboard.TextAdded += OnTextCopied;
        h.Weather.Changed += Invalidate;
        h.Weather.Start();
        h.Stats.Changed += Invalidate;
        h.Stats.Start();
        h.Calendar.Changed += Invalidate;
        h.Calendar.Start();
        h.ScreenTime.BreakDue += () => ShowHud(new Hud(HudIcon.Timer, "Time for a break",
            Detail: $"{(int)h.ScreenTime.Session.TotalMinutes} min on screen"), TimeSpan.FromSeconds(4));
        _controls.Changed += Invalidate;
        h.Wifi.Changed += Invalidate;
        h.Network.Changed += Invalidate;
        h.Network.OnlineChanged += online => Post(() => ShowHud(online
            ? new Hud(HudIcon.Info, "Back online", Detail: h.Network.PingMs is { } ms ? $"{ms} ms" : null)
            : new Hud(HudIcon.Info, "Internet is down", Detail: "HaloBar tells you when it's back", Tint: Red), TimeSpan.FromSeconds(3)));
        h.Network.Start();
        h.Health.Alert += (title, detail) => Post(() => ShowNotice(new Notice("PC health", title, detail, null, DateTime.Now)));
        h.Health.Start();
        h.Power.Changed += () => Post(CheckChargeLimit);
        h.Web.Focusing = () => h.Timer.Phase == TimerPhase.Focus && !h.Timer.Paused || AutoFocus != null;
        h.ScreenTime.WeeklyReport += (title, body) => Post(() => ShowNotice(new Notice("Screen time", title, body, null, DateTime.Now)));
        h.Bluetooth.Changed += Invalidate;
        _ = Task.Run(_controls.StartAsync);

        h.Timer.Finished += phase => Post(() =>
        {
            if (phase == TimerPhase.Focus) CountFocus(_settings.FocusMinutes);
            Chime();
            var notice = phase == TimerPhase.Focus
                ? new Notice("Focus timer", "Focus done", $"Take a {_settings.BreakMinutes}-minute break.", null, DateTime.Now)
                : new Notice("Focus timer", "Break over", "Ready for the next focus?", null, DateTime.Now);
            ShowNotice(notice);
        });
        _exports = new ExportWatcher(() => _settings.ExportFolders);
        _exports.Finished += path => Post(() =>
        {
            _hub.Shelf.Add([path]);
            ShowNotice(new Notice("Export done", Path.GetFileName(path), $"{DownloadService.Size(new FileInfo(path).Length)} · on the shelf; click to open",
                null, DateTime.Now, Click: () => Open(path)));
        });
        h.Reminders.Due += r => Post(() =>
        {
            if (_settings.ReminderSound != "off") Chime();
            _reminderChimes = _settings.ReminderSound == "repeat" ? 4 : 0;
            _nextReminderChime = DateTime.Now.AddSeconds(15);
            ShowNotice(new Notice("Reminder", r.Text, r.Repeat == ReminderRepeat.None ? "" : $"Repeats {r.Repeat.ToString().ToLowerInvariant()}", null, DateTime.Now));
        });

        // Scripts' notices get the same one-time-code button as Windows' (an SMS forwarder, say).
        _server.Received += n => Post(() => ShowNotice(OneTimeCode.Find(n.Title, n.Body) is { } code ? n with { Code = code } : n));
        _server.StyleRequested += (surface, accent, album) => Post(() =>
            _theme.Choose(surface is null ? null : Looks.Surface(surface), accent is null ? null : Looks.Accent(accent), album));
        _server.PanelRequested += name => Post(() => ScreenUnderCursor()?.View.OpenPanel(name));
        _server.SettingsRequested += page => Post(() => OpenSettingsWindow(page));
        _server.SnapshotRequested += (file, panel) => Post(() =>
        {
            // "glass:1" saves what the second screen's glass lens currently sees behind its island.
            if (panel?.StartsWith("glass:", StringComparison.OrdinalIgnoreCase) == true && int.TryParse(panel[6..], out var index))
            {
                if (_screens.ElementAtOrDefault(index)?.RawGlass is { } glass)
                {
                    using var data = glass.Encode(SKEncodedImageFormat.Png, 100);
                    File.WriteAllBytes(file, data.ToArray());
                }
                Log.Write($"Glass {index}: {_screens.ElementAtOrDefault(index)?.GlassInfo ?? "no such screen"}");
                return;
            }
            if (panel?.Equals("settings-window", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (_settingsWindow is { } window)
                {
                    window.Dirty = true;
                    window.CaptureNextFrame();
                    window.Render(window.LastFrame + 0.016, 0.016);
                    if (window.Picture is { } picture)
                    {
                        using var data = picture.Encode(SKEncodedImageFormat.Png, 100);
                        File.WriteAllBytes(file, data.ToArray());
                    }
                }
                return;
            }
            // "widget:date" saves that desktop widget's picture, since it usually sits behind windows.
            if (panel?.StartsWith("widget:", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (_widgets.FirstOrDefault(w => w.TileId.Equals(panel[7..], StringComparison.OrdinalIgnoreCase)) is { } widget)
                {
                    widget.CaptureNextFrame();
                    widget.Render(0);
                    if (widget.Picture is { } picture)
                    {
                        using var data = picture.Encode(SKEncodedImageFormat.Png, 100);
                        File.WriteAllBytes(file, data.ToArray());
                    }
                }
                return;
            }
            if (_screens.FirstOrDefault()?.View is not { } view) return;
            _snapshot = (file, 45); // let the opening animation settle first
            view.Hidden = false;    // a hidden island would close the panel again
            if (panel != null) view.OpenPanel(panel);
        });
        if (_settings.NotifyEndpoint) _server.Start();
        else Log.Write("Local API: off in Settings");

        if (_settings.MirrorNotifications)
        {
            h.Notifications.Arrived += n => Post(() =>
            {
                if (OneTimeCode.Find(n.Title, n.Body) is { } code)
                {
                    n = n with { Code = code };
                    if (_settings.AutoCopyCodes) _hub.Clipboard.CopyText(code);
                }
                ShowNotice(n);
                _hub.Banners.Quiet(n.AppId);
            });
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
        // While quiet, it waits in the Notifications list without popping up.
        if (!Quiet) foreach (var s in _screens) s.View.PushNotice(notice);
        _dirty = true;
    }

    /// <summary>Do not disturb is on, or a focus-timer session is running and that counts as quiet.</summary>
    private bool Quiet => _settings.DoNotDisturb || AutoFocus != null
                          || (_settings.QuietDuringFocus && _hub.Timer.Phase == TimerPhase.Focus && !_hub.Timer.Paused);

    /// <summary>
    /// Why it's quiet without anyone asking, or null: a calendar meeting is on, or an app set to
    /// Quiet (Settings › Per-app rules) is in front. Notifications wait and focus sites stay closed.
    /// </summary>
    private string? AutoFocus
    {
        get
        {
            if (_settings.AppRules.Any(r => r.Behaviour == "quiet" && r.Process.Equals(_frontApp, StringComparison.OrdinalIgnoreCase)))
                return $"while {ScreenTimeService.Pretty(_frontApp)} is in front";
            if (_settings.QuietInMeetings && _hub.Calendar.Events.FirstOrDefault(e => !e.AllDay && e.Start <= DateTime.Now && e.End > DateTime.Now) is { } meeting)
                return $"during {meeting.Title}";
            return null;
        }
    }

    private string? _autoFocusShown;
    private DateTime _quietSince;

    /// <summary>Says on the pill when automatic quiet starts and ends.</summary>
    private void AnnounceAutoFocus()
    {
        var now = AutoFocus;
        if (now == _autoFocusShown) return;
        if (now != null && _autoFocusShown == null) _quietSince = DateTime.Now;
        if (now != null) ShowHud(new Hud(HudIcon.Info, "Quiet", Detail: now), TimeSpan.FromSeconds(2.5));
        else if (_autoFocusShown != null)
        {
            var waiting = _hub.Notices.Items.Count(n => n.Time >= _quietSince);
            ShowHud(new Hud(HudIcon.Info, "Notifications are back", Detail: waiting > 0 ? $"{waiting} arrived meanwhile" : null), TimeSpan.FromSeconds(2.5));
        }
        _autoFocusShown = now;
    }

    private bool _chargeWarned;

    /// <summary>Says once per charge when the battery reaches the chosen limit.</summary>
    private void CheckChargeLimit()
    {
        var p = _hub.Power;
        if (!p.HasBattery || !p.PluggedIn) { _chargeWarned = false; return; }
        if (_settings.ChargeLimit <= 0 || _chargeWarned || p.Percent < _settings.ChargeLimit) return;
        _chargeWarned = true;
        ShowNotice(new Notice("Battery", $"Charged to {p.Percent}%", "Unplug to keep the battery healthy", null, DateTime.Now));
    }

    /// <summary>Adds finished focus minutes to today's total (a new day starts from zero).</summary>
    private void CountFocus(int minutes)
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (_settings.FocusDay != today) (_settings.FocusDay, _settings.FocusMinutesToday) = (today, 0);
        _settings.FocusMinutesToday += minutes;
        _settings.Save();
    }

    private void ToggleSharing()
    {
        _settings.ShowInSharing = !_settings.ShowInSharing;
        _settings.Save();
        ShowHud(new Hud(HudIcon.Info, _settings.ShowInSharing ? "Visible in screenshots and shares" : "Hidden from screenshots and shares",
            Detail: _settings.ShowInSharing ? "Others see the island too" : "Only you see the island"), TimeSpan.FromSeconds(2.5));
    }

    /// <summary>Applies the software dim and warmth from settings to the screen overlay.</summary>
    private void ApplyDim()
    {
        _dimFor = _settings.ScreenDim;
        _warmthFor = _settings.ScreenWarmth;
        _dim.Set(Math.Clamp(_dimFor, 0, 80) / 100f, Math.Clamp(_warmthFor, 0, 100) / 100f);
    }

    private void ToggleKeepAwake()
    {
        _settings.KeepAwake = !_settings.KeepAwake;
        _settings.Save();
        _hub.KeepAwake.Set(_settings.KeepAwake);
        ShowHud(new Hud(HudIcon.Info, _settings.KeepAwake ? "Staying awake" : "Sleep as usual",
            Detail: _settings.KeepAwake ? "The screen won't dim or sleep" : null), TimeSpan.FromSeconds(2));
    }

    /// <summary>Reads the latest clipboard text aloud, or stops if it's already reading.</summary>
    private void ReadAloud()
    {
        if (_hub.Speech.Speaking) { _hub.Speech.Stop(); ShowHud(new Hud(HudIcon.Info, "Stopped reading"), TimeSpan.FromSeconds(1.5)); return; }
        var text = _hub.Clipboard.Entries.FirstOrDefault(e => e.Kind == ClipKind.Text)?.Content.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowHud(new Hud(HudIcon.Info, "Nothing to read", Detail: "Copy some text first"), TimeSpan.FromSeconds(2));
            return;
        }
        _hub.Speech.Speak(text);
        ShowHud(new Hud(HudIcon.Info, "Reading aloud", Detail: text.Length > 40 ? text[..40].Trim() + "…" : text.Trim()), TimeSpan.FromSeconds(2));
    }

    private void ToggleDoNotDisturb()
    {
        _settings.DoNotDisturb = !_settings.DoNotDisturb;
        _settings.Save();
        ShowHud(new Hud(HudIcon.Info, _settings.DoNotDisturb ? "Do not disturb" : "Do not disturb off",
            Detail: _settings.DoNotDisturb ? "Notifications wait in the list" : null), TimeSpan.FromSeconds(2));
    }

    private static void Chime() => PlaySound("SystemNotification", IntPtr.Zero, SND_ALIAS | SND_ASYNC | SND_NODEFAULT);

    /// <summary>
    /// What deserves the resting pill right now, most important first: a call, a meeting about to
    /// start, a running timer, a download.
    /// </summary>
    private Activity? CurrentActivity()
    {
        if (_hub.Recorder.Recording)
            return new Activity(85, HudIcon.Record, "Recording", Red, Right: Ui.Clock(DateTime.Now - _hub.Recorder.Since));
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
            // From 10 minutes before until it ends (Join stays useful for a late arrival).
            if (minutes <= 10 && (e.JoinUrl != null ? e.End > DateTime.Now : minutes > -5))
                return new Activity(60, HudIcon.Alarm, e.Title, _theme.Accent,
                    Right: minutes > 0 ? $"in {Math.Ceiling(minutes)} min" : "now",
                    ButtonLabel: e.JoinUrl != null ? "Join" : null, Button: e.JoinUrl is { } url ? () => Open(url) : null);
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
            if (!Quiet) ShowHud(new Hud(HudIcon.Agent, $"{s.Agent} finished", Detail: where, Tint: Green), TimeSpan.FromSeconds(3));
            if (_settings.AgentsNotify) _hub.Notices.Add(new Notice(s.Agent, "Finished", where ?? "", null, DateTime.Now));
        }
        else if (s.State == AgentState.Waiting)
        {
            if (!Quiet) Chime();
            if (!Quiet) ShowHud(new Hud(HudIcon.Agent, $"{s.Agent} needs you", Detail: s.Doing, Tint: Orange), TimeSpan.FromSeconds(4));
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

    /// <summary>What the snip in progress is for: a screenshot to keep, its text, or a QR code in it.</summary>
    private enum SnipPurpose { Screenshot, Text, Code }
    private SnipPurpose _snipPurpose;

    /// <summary>Opens Windows' region snipping; the picture comes back through the clipboard.</summary>
    private void TakeScreenshot(SnipPurpose purpose = SnipPurpose.Screenshot)
    {
        _snipRequestedAt = DateTime.Now;
        _snipPurpose = purpose;
        Open("ms-screenclip:");
    }

    /// <summary>The last result a clipboard action copied: copying it isn't news.</summary>
    private string? _insightCopied;

    /// <summary>Copied maths, units, money, colours or links: the result on the pill, a click copies or opens it.</summary>
    private void OnTextCopied(string text)
    {
        if (!_settings.ClipboardActions || text == _insightCopied) return;
        _ = Task.Run(async () =>
        {
            if (await ClipSense.LookAsync(text, _settings) is not { } insight) return;
            Post(() => ShowNotice(new Notice("Clipboard", insight.Title, insight.Detail, null, DateTime.Now, Click: () =>
            {
                if (insight.Open is { } url) Open(url);
                else if (insight.Copy is { } result)
                {
                    _insightCopied = result;
                    _hub.Clipboard.CopyText(result);
                    ShowHud(new Hud(HudIcon.Copied, result, Detail: "Copied"));
                }
            })));
        });
    }

    private void OnPictureCopied(ClipEntry entry)
    {
        if (DateTime.Now - _snipRequestedAt > TimeSpan.FromMinutes(2) || entry.Content.Png is not { } png) return;
        _snipRequestedAt = default;
        if (_snipPurpose != SnipPurpose.Screenshot)
        {
            Read(png, _snipPurpose);
            return;
        }
        try
        {
            var folder = Path.Combine(KnownFolder(FolderPictures) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "HaloBar");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, $"Screenshot {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png");
            File.WriteAllBytes(file, png);
            if (_settings.CapturesToShelf) _hub.Shelf.Add([file]);
            // Clicking it opens the picture in Paint to mark it up (arrows, boxes, text, highlight).
            ShowNotice(new Notice("Screenshot saved", "Copied · on the shelf", "Click to mark it up", null, DateTime.Now,
                Click: () => Open("mspaint.exe", $"\"{file}\"")));
        }
        catch (IOException ex)
        {
            Log.Write($"Screenshot not saved: {ex.Message}");
        }
    }

    /// <summary>Reads the snip's text or QR code (off the window thread), copies what it found and says so.</summary>
    private void Read(byte[] png, SnipPurpose purpose) => _ = Task.Run(async () =>
    {
        string? found = null;
        try
        {
            found = purpose == SnipPurpose.Text ? await ScreenReader.TextAsync(png) : ScreenReader.Code(png);
        }
        catch (Exception ex)
        {
            Log.Write($"Reading the snip failed: {ex.GetType().Name} {ex.Message}");
        }
        Post(() =>
        {
            if (found == null)
            {
                ShowHud(new Hud(HudIcon.Info, purpose == SnipPurpose.Text ? "No text found" : "No QR code found",
                    Detail: "Try a larger area"), TimeSpan.FromSeconds(2.5));
                return;
            }
            _hub.Clipboard.CopyText(found);
            var preview = found.ReplaceLineEndings(" ");
            if (preview.Length > 140) preview = preview[..139] + "…";
            if (purpose == SnipPurpose.Code && Uri.TryCreate(found, UriKind.Absolute, out var link) && (link.Scheme == "https" || link.Scheme == "http"))
                ShowNotice(new Notice("QR code", link.Host, $"{preview} · copied; click to open", null, DateTime.Now, Click: () => Open(link.AbsoluteUri)));
            else
                ShowNotice(new Notice(purpose == SnipPurpose.Text ? "Text copied" : "QR code copied",
                    purpose == SnipPurpose.Text ? $"{found.Split('\n').Length} lines" : "", preview, null, DateTime.Now));
        });
    });

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

    private void OnHotkey(string action)
    {
        switch (action)
        {
            case "open": ScreenUnderCursor()?.View.ToggleExpanded(); break;
            case "playpause": _hub.Media.TogglePlayPause(); break;
            case "next": _hub.Media.Next(); break;
            case "web": ToggleWeb(null); break;
            case "controls": ScreenUnderCursor()?.View.OpenPanel("Controls"); break;
            case "sound-output": ScreenUnderCursor()?.View.OpenPanel("Controls:output"); break;
            case "mixer": ScreenUnderCursor()?.View.OpenPanel("Controls:mixer"); break;
            case "keep-awake": ToggleKeepAwake(); break;
            case "read-aloud": ReadAloud(); break;
            case "clipboard": ScreenUnderCursor()?.View.SearchClipboard(); break;
            case "profile":
                var next = _hub.Profiles.Next();
                ShowHud(new Hud(HudIcon.Info, $"{next} profile", Detail: _hub.Profiles.Use(next) == "Saved" ? "Saved as it is now" : null), TimeSpan.FromSeconds(2));
                break;
            case "dnd": ToggleDoNotDisturb(); break;
            case "sharing": ToggleSharing(); break;
            case "record": ToggleRecording(); break;
            case "previous": _hub.Media.Previous(); break;
            case "timer":
                _hub.Timer.Toggle();
                ShowHud(new Hud(HudIcon.Timer, _hub.Timer.Paused ? "Timer paused" : "Focus timer", Detail: Ui.Clock(_hub.Timer.Remaining)));
                break;
            case "hide":
                _userHidden = !_userHidden;
                Log.Write(_userHidden ? "Island hidden by hotkey" : "Island shown by hotkey");
                break;
            case "mic": _hub.Microphone.ToggleMute(); break;
            case "reminder": ScreenUnderCursor()?.View.NewReminder(); break;
            case "color": PickColor(); break;
            case "obs":
                if (_obs.Connected) _obs.ToggleRecord();
                else ShowHud(new Hud(HudIcon.Record, "OBS isn't connected", Detail: _obs.Status));
                break;
            case "screenshot": TakeScreenshot(); break;
            case "screentext": TakeScreenshot(SnipPurpose.Text); break;
            case "qr": TakeScreenshot(SnipPurpose.Code); break;
            case "output":
                if (ControlsService.CycleDefault(NAudio.CoreAudioApi.DataFlow.Render) is { } name)
                    ShowHud(new Hud(HudIcon.Volume, name, Detail: "Output"));
                break;
            case ['l', 'a', 'u', 'n', 'c', 'h', var digit] when char.IsAsciiDigit(digit):
                if (_hub.Launcher.Slotted.ElementAtOrDefault(digit - '1') is { } app) Open(app.ShellPath);
                else ShowHud(new Hud(HudIcon.Info, $"Slot {digit} is empty", Detail: "Pick an app on the Apps panel"));
                break;
        }
        _dirty = true;
    }

    private bool _recorderBusy;

    /// <summary>
    /// Starts recording the screen the pointer is on, or stops and saves the file to the shelf.
    /// Finishing a file can take a moment, so it happens off the window thread.
    /// </summary>
    private void ToggleRecording()
    {
        if (_recorderBusy) return;
        var recorder = _hub.Recorder;
        if (!recorder.Recording)
        {
            if (ScreenUnderCursor()?.Monitor is not { } monitor) return;
            if (recorder.Start(monitor.Handle, _settings.RecordSound, _settings.RecordMicrophone) is { } error)
                ShowHud(new Hud(HudIcon.Record, "Couldn't record", Detail: error), TimeSpan.FromSeconds(3));
            return;
        }
        _recorderBusy = true;
        Task.Run(() =>
        {
            var saved = recorder.Stop();
            Post(() =>
            {
                _recorderBusy = false;
                if (saved == null)
                {
                    ShowHud(new Hud(HudIcon.Record, "Nothing recorded"), TimeSpan.FromSeconds(2));
                    return;
                }
                _hub.Shelf.Add([saved]);
                // On the clipboard too, ready to paste into a chat, a mail or a folder.
                CopyFiles([saved]);
                ShowHud(new Hud(HudIcon.Record, "Recording saved and copied", Detail: Path.GetFileName(saved)), TimeSpan.FromSeconds(3));
            });
        });
    }

    /// <summary>
    /// Shows or hides the web drawer. <paramref name="which"/> is a site's name or address, an
    /// address that isn't pinned, "none" to hide, or null for the site shown last.
    /// </summary>
    private void ToggleWeb(string? which)
    {
        var sites = _settings.WebSites.Select(WebSite.Parse).OfType<WebSite>().ToList();
        if (which?.Equals("none", StringComparison.OrdinalIgnoreCase) == true)
        {
            _hub.Web.Hide();
            return;
        }
        var site = which == null
            ? sites.FirstOrDefault(s => s.Url == _hub.Web.Last) ?? sites.FirstOrDefault()
            : sites.FirstOrDefault(s => s.Name.Equals(which, StringComparison.OrdinalIgnoreCase))
              ?? sites.FirstOrDefault(s => s.Host.Contains(which, StringComparison.OrdinalIgnoreCase))
              ?? WebSite.FromTyped(which);
        if (site == null) return;
        if (which == null) _hub.Web.Toggle(site);
        else _hub.Web.Show(site);
    }

    /// <summary>A menu label with its current shortcut after a tab, as Windows menus show them.</summary>
    private string Menu(string label, string action) =>
        _hub.Hotkeys.Text(action) is { Length: > 0 } keys ? $"{label}\t{keys}" : label;

    private IReadOnlyList<TrayMenuItem> TrayMenu() =>
    [
        new(Menu("Open island", "open"), () => ScreenUnderCursor()?.View.ToggleExpanded()),
        new(Menu("New reminder", "reminder"), () => ScreenUnderCursor()?.View.NewReminder()),
        new(Menu("Take a screenshot", "screenshot"), () => TakeScreenshot()),
        new(Menu("Copy text from the screen", "screentext"), () => TakeScreenshot(SnipPurpose.Text)),
        new(Menu("Scan a QR code", "qr"), () => TakeScreenshot(SnipPurpose.Code)),
        new(Menu("Pick a colour", "color"), PickColor),
        new(Menu("App volume mixer", "mixer"), () => ScreenUnderCursor()?.View.OpenPanel("Controls:mixer")),
        new(Menu(_hub.Recorder.Recording ? "Stop recording" : "Record the screen", "record"), ToggleRecording),
        new(Menu("Do not disturb", "dnd"), ToggleDoNotDisturb, _settings.DoNotDisturb),
        new(Menu(_hub.Timer.Running ? "Pause focus timer" : "Start focus timer", "timer"), _hub.Timer.Toggle),
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
        new("Show in screenshots and sharing", ToggleSharing, _settings.ShowInSharing),
        new(Menu("Hide the island", "hide"), () => _userHidden = !_userHidden, _userHidden),
        TrayMenuItem.Separator,
        new($"Windows notifications: {_hub.Notifications.Status}", () => { }, Enabled: false),
        new($"Local API: 127.0.0.1:{_server.Port}", () => { }, Enabled: false),
        new($"Memory: {MemoryText()}", () => { }, Enabled: false),
        TrayMenuItem.Separator,
        new("Settings…", () => OpenSettingsWindow(null)),
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
                _hub.Hotkeys.OnHotkey((int)wParam);
                return true;
            case WM_CLIPBOARDUPDATE:
                if (_settings.ClipboardEnabled) _hub.Clipboard.OnClipboardUpdate();
                return true;
            case WakeMessage:
                return true;
            case WM_TIMER when (nuint)wParam == DragTimer:
                DragFrame();
                return true;
        }
        return false;
    }

    /// <summary>Motion: "saver" always, "balanced" on battery, keeps animation under 60 frames a second.</summary>
    private bool FrameCapped => _settings.Motion == "saver"
                                || (_settings.Motion == "balanced" && _hub.Power.HasBattery && !_hub.Power.PluggedIn);

    private static readonly string[] ColourApps =
        ["photoshop", "lightroom", "lightroomclassic", "adobe premiere pro", "premiere", "resolve", "captureone", "capture one",
         "affinity", "photo", "designer", "gimp", "krita", "blender"];

    /// <summary>
    /// A colour-work app came to the front with Night light on: the picture looks warmer than the
    /// file is. Said once per app until another app comes forward; a click turns Night light off.
    /// </summary>
    private void WarnAboutNightLight()
    {
        var app = _frontApp.ToLowerInvariant();
        var colourWork = app.Length > 0 && (ColourApps.Any(a => app == a) || app.StartsWith("gimp") || app.StartsWith("affinity") || app.StartsWith("photoshop") || app.StartsWith("lightroom"));
        if (!colourWork) { _warnedColourApp = ""; return; }
        if (!_settings.ColorWorkWarning || _warnedColourApp == app || _controls.NightLightOn != true) return;
        _warnedColourApp = app;
        ShowNotice(new Notice("Night light is on", $"{_frontApp} sees warmer colours than the file", "Click to turn Night light off", null, DateTime.Now,
            Click: () => _controls.ToggleNightLight()));
    }

    private int _reminderChimes;
    private DateTime _nextBannerSweep;
    private DateTime _nextReminderChime;
    private ExportWatcher? _exports;
    private string _warnedColourApp = "";

    private int _nextWidget;
    private int _frames, _slowFrames;
    private double _worstFrame, _nextPerfLog;

    /// <summary>
    /// Counts frames that took longer than a 60 Hz frame to draw, and logs a line a minute when
    /// there were any, so a stutter shows up in the log with how bad it was.
    /// </summary>
    private double _islandMs, _widgetMs;
    private int _widgetFrames;

    private void NoteFrame(double ms, double islandMs, int widgetsDrawn, bool rendered, double now)
    {
        if (rendered)
        {
            _frames++;
            if (ms > 16) _slowFrames++;
            _worstFrame = Math.Max(_worstFrame, ms);
            _islandMs += islandMs;
            if (widgetsDrawn > 0)
            {
                _widgetMs += ms - islandMs;
                _widgetFrames += widgetsDrawn;
            }
        }
        if (now < _nextPerfLog) return;
        if (_slowFrames > 0)
            Log.Write($"Frames: {_slowFrames} of {_frames} over 16 ms in the last minute, worst {_worstFrame:0} ms; " +
                      $"island {_islandMs / Math.Max(1, _frames):0.0} ms a frame (drawing {IslandScreen.DrawMs / Math.Max(1, _frames):0.0}, to the GPU {IslandScreen.PresentMs / Math.Max(1, _frames):0.0}), " +
                      $"widget {_widgetMs / Math.Max(1, _widgetFrames):0.0} ms each ({_widgets.Count} widgets); {DesktopWidget.Timings.Take()}");
        IslandScreen.DrawMs = IslandScreen.PresentMs = 0;
        (_frames, _slowFrames, _worstFrame, _nextPerfLog, _islandMs, _widgetMs, _widgetFrames) = (0, 0, 0, now + 60, 0, 0, 0);
    }

    private const nuint DragTimer = 7;
    private DateTime _draggedOutAt;
    private readonly Stopwatch _dragClock = new();

    /// <summary>
    /// Drags shelf files out. The shell's drag loop keeps this thread until the drop, so the frame
    /// loop can't run; a timer (which that loop still delivers) keeps the islands drawing meanwhile.
    /// </summary>
    private void DragOut(IntPtr hwnd, string[] files)
    {
        _dragClock.Restart();
        SetTimer(_host.Handle, DragTimer, 16, IntPtr.Zero);
        try { ShellDrag.Start(hwnd, files); }
        finally
        {
            KillTimer(_host.Handle, DragTimer);
            _draggedOutAt = DateTime.Now;
            _dirty = true;
        }
    }

    /// <summary>One frame while a drag out is running: queued work, animation and drawing.</summary>
    private void DragFrame()
    {
        var dt = _dragClock.Elapsed.TotalSeconds;
        _dragClock.Restart();
        while (_ui.TryDequeue(out var action)) action();
        _theme.Tick(dt, _hub.Media.Snapshot?.Palette);
        foreach (var s in _screens)
        {
            var (cx, cy) = s.Window.CursorPosition();
            var inside = cx >= 0 && cy >= 0 && cx < s.Window.Width && cy < s.Window.Height;
            s.Interval = s.View.Update(dt, inside ? new SKPoint(cx / s.Window.Scale, cy / s.Window.Scale) : null, DateTime.Now);
            s.Render();
        }
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
            IslandScreen.Recording = _hub.Recorder.Recording;
            IslandScreen.Sharing = _settings.ShowInSharing;
            CheckLockKeys();
            if (now >= nextSecond)
            {
                _hub.Timer.Tick();
                _hub.Reminders.Tick();
                _hub.ScreenTime.Tick(_frontApp);
                AnnounceAutoFocus();
                SyncWidgets();
                if (_settings.ClipboardKeepHours > 0) _hub.Clipboard.Expire(TimeSpan.FromHours(_settings.ClipboardKeepHours));
                // Apps installed since: their Windows banners go too (cheap: a registry walk every 10 minutes).
                if (DateTime.Now >= _nextBannerSweep)
                {
                    _nextBannerSweep = DateTime.Now.AddMinutes(10);
                    if (_settings.MirrorNotifications) _ = Task.Run(() => _hub.Banners.QuietEveryApp());
                }
                _exports?.Sync();
                if (_settings.ScreenDim != _dimFor || _settings.ScreenWarmth != _warmthFor) ApplyDim();
                if (_reminderChimes > 0 && DateTime.Now >= _nextReminderChime)
                {
                    // Opening the island answers it; otherwise it chimes again.
                    if (_screens.Any(s => s.View.Mode == IslandMode.Expanded)) _reminderChimes = 0;
                    else
                    {
                        Chime();
                        _reminderChimes--;
                        _nextReminderChime = DateTime.Now.AddSeconds(15);
                    }
                }
                nextSecond = now + 1;
            }

            if (now >= nextFullscreenCheck)
            {
                ownWindows.Clear();
                foreach (var s in _screens) ownWindows.Add(s.Window.Handle);
                var front = FullscreenWatcher.Inspect(ownWindows);
                // What covers each monitor, so every screen slims over its own maximised window.
                var cover = FullscreenWatcher.CoverByMonitor(ownWindows);
                _frontApp = front.Process ?? "";
                _hub.FrontApp = _frontApp;
                WarnAboutNightLight();
                SettleWidgets();
                var rule = _settings.AppRules.FirstOrDefault(r => r.Process.Equals(_frontApp, StringComparison.OrdinalIgnoreCase))?.Behaviour;
                foreach (var s in _screens)
                {
                    var here = s.Monitor.Handle == front.Monitor;
                    var game = here && front.Game && _settings.GameStrip;
                    // This screen's own top window (maximised or full screen), so each display slims over
                    // whatever covers it, not only the one the active window happens to be on.
                    var mon = cover.GetValueOrDefault(s.Monitor.Handle);
                    var fullscreenHere = mon.Fullscreen && !game;
                    var covered = (mon.Maximized || fullscreenHere) && !(here && rule == "normal");
                    var everywhere = _settings.GameStripEverywhere && !game;
                    s.View.GameStrip = (game || everywhere) && _snapshot == null;
                    s.View.StripOpens = everywhere;
                    // Slim to a thin line over a maximised or full-screen window (so it never covers tabs);
                    // only truly hide in full screen when the user asked for hide and not slim.
                    s.View.Slim = (here && rule == "slim") || (_settings.SlimWhenCovered && covered);
                    var hidden = (_userHidden || (fullscreenHere && _settings.HideInFullscreen && !_settings.SlimWhenCovered) || (here && rule == "hide")) && _snapshot == null;
                    if (hidden != s.View.Hidden && !_userHidden) Log.Write($"Full screen on {s.Monitor.Key}: {(hidden ? "hiding" : "showing")}");
                    s.View.Hidden = hidden;
                }
                nextFullscreenCheck = now + 0.5;
            }
            if (now >= nextTopCheck)
            {
                foreach (var s in _screens) s.Window.KeepOnTop();
                _chatOverlay?.Window.KeepOnTop();
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
                // A drop arrives as one message, with no notice while it hovers. A button held over
                // the island that wasn't pressed on it, while Windows shows a drag picture, is files
                // on their way in. (Without the picture it was a window or a text selection being
                // dragged past, which used to open the Files tab.)
                var held = PrimaryButtonDown();
                if (!held) s.PressedHere = false;
                var carrying = held && inside && s.View.Contains(cursor) && !s.PressedHere && !ShellDrag.Active && _settings.ShelfEnabled
                               && ShellDragImageShown();
                if (carrying != s.Carrying)
                {
                    s.Carrying = carrying;
                    s.View.DragHover(carrying);
                }
            }

            bool rendered = false, animating = false, lost = false;
            var work = Stopwatch.StartNew();
            foreach (var s in _screens)
            {
                if (!_dirty && _snapshot == null && now - s.LastFrame < s.Interval && !s.GlassStale) continue;
                lost |= !s.Render();
                s.LastFrame = now;
                rendered = true;
                animating |= s.Interval == 0;
            }
            // Widgets go at their own pace (a second, or at once when touched), never because
            // something else on the island changed; a few per frame, so drawing 29 of them never
            // holds up the island, and none while it's animating unless one was just touched.
            var islandMs = work.Elapsed.TotalMilliseconds;
            var widgetsDrawn = 0;
            var budget = animating ? 0 : 4;
            for (var i = 0; i < _widgets.Count; i++)
            {
                var w = _widgets[(_nextWidget + i) % _widgets.Count];
                if (!w.Due(now)) continue;
                if (!w.Dirty && (budget <= 0 || w.Window.Covered)) continue; // behind windows: drawn once it shows again
                lost |= !w.Render(now);
                rendered = true;
                budget--;
                widgetsDrawn++;
            }
            if (_widgets.Count > 0) _nextWidget = (_nextWidget + 1) % _widgets.Count; // nobody waits behind the same ones
            if (_settingsWindow is { } settingsWindow && settingsWindow.Due(now))
            {
                lost |= !settingsWindow.Render(now, Math.Min(0.05, now - settingsWindow.LastFrame));
                rendered = true;
            }
            if (_chatOverlay is { } chat && chat.Due(now) && !animating)
            {
                lost |= !chat.Render(now);
                rendered = true;
            }
            NoteFrame(work.Elapsed.TotalMilliseconds, islandMs, widgetsDrawn, rendered, now);
            _dirty = false;
            if (_snapshot is { } snap && _screens.Count > 0)
            {
                _screens[0].View.Hidden = false;
                if (snap.Frames > 0)
                {
                    _snapshot = snap with { Frames = snap.Frames - 1 };
                    // Frames keep coming for the island (above) until the snapshot is taken; the widgets keep their own pace.
                }
                else
                {
                    _screens[0].Surface.CaptureNext = true;
                    _screens[0].Render();
                    if (_screens[0].Surface.Captured is { } picture)
                        using (var data = picture.Encode(SKEncodedImageFormat.Png, 100))
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
                if (FrameCapped)
                {
                    var spare = 1 / 60.0 - (clock.Elapsed.TotalSeconds - now);
                    if (spare > 0.001) MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, (uint)(spare * 1000), QS_ALLINPUT, MWMO_INPUTAVAILABLE);
                }
                else DwmFlush();
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
        // Textures Skia keeps for reuse on the graphics card (glyphs and images come back on demand).
        _gfx.Gpu?.Context.PurgeUnlockedResources(false);
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
        _dim.Dispose();
        _hub.Hotkeys.Unregister();
        _hub.Web.Dispose();
        _hub.Recorder.Dispose();
        foreach (var w in _widgets) w.Dispose();
        _chatOverlay?.Dispose();
        _settingsWindow?.Dispose();
        if (_foregroundHookHandle != IntPtr.Zero) UnhookWinEvent(_foregroundHookHandle);
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
        _hub.Network.Dispose();
        _hub.Health.Dispose();
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
