using System.Collections.Concurrent;
using System.Diagnostics;
using HaloBar.Platform;
using HaloBar.Rendering;
using HaloBar.Services;
using HaloBar.UI;
using SkiaSharp;
using static HaloBar.Platform.Native;

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
        _canvas!.ResetMatrix();
        _canvas.Scale(Window.Scale);
        View.Draw(_canvas);
        _canvas.Flush();
        return Surface.Present();
    }

    private void DropCanvas()
    {
        _canvas?.Dispose();
        _canvas = null;
        _canvasBitmap = null;
    }

    public void Dispose()
    {
        DropCanvas();
        Surface.Dispose();
        Window.Dispose();
    }
}

/// <summary>Owns the windows, the services and the frame loop. Everything here runs on the main (STA) thread.</summary>
internal sealed class IslandApp : IDisposable
{
    private const int HotkeyId = 1;
    private const uint WakeMessage = WM_APP + 2;
    private static readonly TimeSpan TrimEvery = TimeSpan.FromMinutes(5);

    private readonly AppSettings _settings;
    private readonly HostWindow _host = new();
    private GraphicsDevice _gfx;
    private readonly Theme _theme;
    private readonly MediaService _media = new();
    private readonly VolumeService _volume = new();
    private readonly NotificationService _notifications = new();
    private readonly NoticeHistory _history = new();
    private readonly NotifyServer _server;
    private readonly ClipboardService _clipboard;
    private readonly VolumeFlyoutSuppressor _flyout = new();
    private readonly TrayIcon _tray;
    private readonly ConcurrentQueue<Action> _ui = new();
    private List<IslandScreen> _screens = [];
    private volatile bool _dirty = true;
    private bool _layoutDirty;
    private double _trimAt = 8;

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
        _clipboard = new ClipboardService(_host.Handle, settings.ClipboardHistorySize, Post);

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
        if (!RegisterHotKey(_host.Handle, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 'I'))
            Log.Write("Hotkey Ctrl+Alt+I is taken by another app");
        if (settings.ReplaceVolumePopup) _flyout.Ensure();

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
            if (existing != null)
            {
                remaining.Remove(existing);
                existing.Place(monitor);
                next.Add(existing);
            }
            else next.Add(CreateScreen(monitor));
        }
        foreach (var gone in remaining) gone.Dispose();
        _screens = next;
        Log.Write($"Screens: {string.Join(", ", _screens.Select(s => $"{s.Monitor.Key}{(s.Monitor.IsPrimary ? " (primary)" : "")}"))}");
    }

    private IslandScreen CreateScreen(MonitorInfo monitor)
    {
        var window = new OverlayWindow((int)IslandView.CanvasWidth, (int)IslandView.CanvasHeight, monitor);
        window.SetCaptureExcluded(_settings.HideFromCapture);
        var view = new IslandView(_theme, _media, _volume, _clipboard, _history)
        {
            HoverDelay = TimeSpan.FromMilliseconds(_settings.HoverDelayMs),
            NoticeTime = TimeSpan.FromSeconds(Math.Clamp(_settings.NotificationSeconds, 2, 30)),
            ApiPort = _settings.NotifyPort,
            NotificationStatus = _settings.MirrorNotifications ? _notifications.Status : "off",
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
        window.DpiChanged += () => _layoutDirty = true;
        return screen;
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
        _media.Changed += Invalidate;
        _ = Task.Run(_media.StartAsync);

        _volume.Changed += () => Post(() =>
        {
            foreach (var s in _screens) s.View.ShowVolume();
            // Explorer creates the pop-up's window the first time it is needed, so look again now.
            if (_settings.ReplaceVolumePopup) _flyout.Ensure();
        });
        _volume.Start();

        if (_settings.ClipboardEnabled)
        {
            _clipboard.Changed += Invalidate;
            _clipboard.Start();
        }

        _server.Received += n => Post(() => ShowNotice(n));
        _server.StyleRequested += (surface, accent, album) => Post(() =>
            _theme.Choose(surface is null ? null : Looks.Surface(surface), accent is null ? null : Looks.Accent(accent), album));
        _server.Start();

        if (_settings.MirrorNotifications)
        {
            _notifications.Arrived += n => Post(() => ShowNotice(n));
            _ = Task.Run(async () =>
            {
                await _notifications.StartAsync();
                Post(() =>
                {
                    foreach (var s in _screens) s.View.NotificationStatus = _notifications.Status;
                });
            });
        }
    }

    private void ShowNotice(Notice notice)
    {
        _history.Add(notice);
        foreach (var s in _screens) s.View.PushNotice(notice);
    }

    private IReadOnlyList<TrayMenuItem> TrayMenu() =>
    [
        new("Open island\tCtrl+Alt+I", () => ScreenUnderCursor()?.View.ToggleExpanded()),
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
            if (_settings.ReplaceVolumePopup) _flyout.Ensure();
            else _flyout.Release();
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
            foreach (var s in _screens) s.Window.SetCaptureExcluded(_settings.HideFromCapture);
            _settings.Save();
        }, _settings.HideFromCapture),
        new("Hide when something is full screen", () =>
        {
            _settings.HideInFullscreen = !_settings.HideInFullscreen;
            _settings.Save();
        }, _settings.HideInFullscreen),
        TrayMenuItem.Separator,
        new($"Windows notifications: {_notifications.Status}", () => { }, Enabled: false),
        new($"Local API: 127.0.0.1:{_server.Port}/notify", () => { }, Enabled: false),
        new($"Memory: {MemoryText()}", () => { }, Enabled: false),
        TrayMenuItem.Separator,
        new("Open settings file", () => { _settings.Save(); Open(Path.Combine(AppPaths.Folder, "settings.json")); }),
        new("Open log", () => Open(Log.FilePath)),
        TrayMenuItem.Separator,
        new("Exit", () => DestroyWindow(_host.Handle)),
    ];

    private static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write($"Open {path}: {ex.Message}"); }
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
            case WM_HOTKEY when (int)wParam == HotkeyId:
                ScreenUnderCursor()?.View.ToggleExpanded();
                _dirty = true;
                return true;
            case WM_CLIPBOARDUPDATE:
                if (_settings.ClipboardEnabled) _clipboard.OnClipboardUpdate();
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
        double last = 0, nextFullscreenCheck = 0, nextTopCheck = 0;
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
            _theme.Tick(dt, _media.Snapshot?.Palette);

            if (now >= nextFullscreenCheck)
            {
                ownWindows.Clear();
                foreach (var s in _screens) ownWindows.Add(s.Window.Handle);
                var fullscreen = _settings.HideInFullscreen ? FullscreenWatcher.FullscreenMonitor(ownWindows) : IntPtr.Zero;
                foreach (var s in _screens)
                {
                    var hidden = s.Monitor.Handle == fullscreen;
                    if (hidden != s.View.Hidden) Log.Write($"Full screen on {s.Monitor.Key}: {(hidden ? "hiding" : "showing")}");
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
                s.Window.ClickThrough = !(inside && s.View.Contains(cursor));
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
        UnregisterHotKey(_host.Handle, HotkeyId);
        RemoveClipboardFormatListener(_host.Handle);
        _tray.Dispose();
        _server.Dispose();
        _notifications.Dispose();
        _volume.Dispose();
        _media.Dispose();
        foreach (var s in _screens) s.Dispose();
        _gfx.Dispose();
        _theme.Dispose();
        _host.Dispose();
        Log.Write("HaloBar stopped");
    }
}
