using HaloBar.Services;
using HaloBar.UI.Panels;
using SkiaSharp;

namespace HaloBar.UI;

internal enum IslandMode { Collapsed, Hud, Notice, Expanded }

/// <summary>
/// One monitor's island: decides what it should show, animates its shape between states, draws
/// it, and maps clicks and typing back to actions. All coordinates are DIPs relative to the
/// overlay window; the caller scales the canvas for the monitor's DPI. Window thread only.
/// </summary>
internal sealed class IslandView
{
    /// <summary>Open size above 100% needs a bigger window; at 100% it stays small, so each frame has fewer pixels.</summary>
    public static float Grow { get; set; } = 1;
    public static float CanvasWidth => 640 * Grow;
    public static float CanvasHeight => 260 * Grow;
    private const float BaseWidth = 560, BaseHeight = 200;
    private float OpenScale => Math.Min(Math.Clamp(Settings.OpenSize, 90, 120) / 100f, Grow);
    private float ExpandedWidth => BaseWidth * OpenScale;
    private float ExpandedHeight => BaseHeight * OpenScale;
    private static readonly TimeSpan LeaveDelay = TimeSpan.FromMilliseconds(400);
    private static readonly SKColor CameraGreen = new(0x34, 0xC7, 0x59), MicOrange = new(0xFF, 0x9F, 0x0A);

    private readonly Hub _hub;
    private readonly Theme _theme;
    private readonly Ui _ui;
    private readonly Visualizer _visualizer = new();
    private readonly List<Panel> _panels;
    private readonly HomePanel _home;
    private readonly MediaPanel _music;
    private readonly NoticesPanel _notices;
    private readonly RemindersPanel _reminders;
    private readonly ShelfPanel _shelf;
    private readonly FilesPanel _files;
    private readonly Queue<Notice> _pending = new();

    private Spring _width = new(160), _height = new(34), _radius = new(17);
    private Spring _content = new(1, 520, 1);
    private Spring _visible = new(1, 260, 1);
    /// <summary>Where the selected tab's highlight is, so it slides between tabs (-1 until first drawn).</summary>
    private Spring _tabPill = new(-1, 420, 0.82);

    private IslandMode _mode = IslandMode.Collapsed;
    private Panel _panel;
    private bool _expanded, _pinned;
    private DateTime? _hoverSince, _leftSince;
    private Hud? _hud;
    private DateTime _hudUntil;
    private Notice? _notice;
    private DateTime _noticeUntil;
    private TextField? _focused;
    private SKPoint? _cursor;

    public bool Hidden { get; set; }
    /// <summary>A thin line instead of the pill: a maximised window or an app rule asked for it.</summary>
    public bool Slim { get; set; }
    /// <summary>A game is full screen: show the stats strip instead of the island.</summary>
    public bool GameStrip { get; set; }

    /// <summary>The strip is up because of "Show it everywhere", not a game: hovering opens the card.</summary>
    public bool StripOpens { get; set; }
    public TimeSpan HoverDelay { get; set; } = TimeSpan.FromMilliseconds(150);
    public TimeSpan NoticeTime { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>The live activity that should take over the resting pill, if any.</summary>
    public Func<Activity?> ActivitySource { get; set; } = () => null;

    /// <summary>Raised with true when a text field wants the keyboard, false when it is done.</summary>
    public event Action<bool>? TypingChanged;

    public IslandView(Hub hub, Action applyLayout, Action<string[]> copyFiles)
    {
        _hub = hub;
        _theme = hub.Theme;
        _ui = new Ui(_theme, Focus, () => _focused);
        _home = new HomePanel(hub);
        _music = new MediaPanel(hub, _visualizer);
        _notices = new NoticesPanel(hub);
        _reminders = new RemindersPanel(hub);
        _shelf = new ShelfPanel(hub);
        _files = new FilesPanel(hub, copyFiles);
        _panels =
        [
            _home, _music, new WebPanel(hub), _files, _shelf, _notices, _reminders, new AgentsPanel(hub, hub.Agents),
            new LauncherPanel(hub, hub.Launcher), new PhonePanel(hub, hub.Phone), new CallPanel(hub, hub.Camera), new ChatPanel(hub, hub.Chat),
            new ControlsPanel(hub, hub.Controls), new StylePanel(hub), new SettingsPanel(hub, applyLayout),
        ];
        _panel = _home;
        ApplySettings();
    }

    private AppSettings Settings => _hub.Settings;
    private float TopGap => Settings.Shape == "notch" ? 0 : Settings.TopGap;
    private bool Notch => Settings.Shape == "notch";

    /// <summary>Picks up animation speed and other settings that live in the springs.</summary>
    public void ApplySettings()
    {
        var k = Settings.AnimationSpeed switch { "relaxed" => 0.55, "quick" => 1.7, _ => 1.0 };
        _width = Retune(_width, 340 * k);
        _height = Retune(_height, 340 * k);
        _radius = Retune(_radius, 340 * k);
        HoverDelay = TimeSpan.FromMilliseconds(Settings.HoverDelayMs);
    }

    private static Spring Retune(Spring s, double stiffness) => new(s.Value, stiffness) { Target = s.Target, Velocity = s.Velocity };

    /// <summary>The island's current outline.</summary>
    public SKRect Bounds
    {
        get
        {
            var w = (float)_width.Value;
            var inset = (CanvasWidth - BaseWidth) / 2;
            var x = Settings.Position switch
            {
                "left" => inset,
                "right" => CanvasWidth - inset - w,
                _ => (CanvasWidth - w) / 2,
            };
            return new SKRect(x, TopGap, x + w, TopGap + (float)_height.Value);
        }
    }

    public IslandMode Mode => _mode;

    /// <summary>The Glass lens is the surface and the island is showing: it wants the screen behind it.</summary>
    public bool WantsGlass => _theme.Surface.Effect == SurfaceEffect.Lens && !SlimNow && !Hidden && _visible.Value > 0.05;
    /// <summary>The blurred screen behind <see cref="Bounds"/> for the lens (shrunk), set before each frame; null without.</summary>
    public SKImage? Glass { get; set; }
    /// <summary>Where <see cref="Glass"/> lies on the canvas (the whole window), in DIPs.</summary>
    public SKRect GlassArea { get; set; }
    public int GlassBlur => Settings.GlassBlur;
    /// <summary>Motion asks to save power (Battery saver, or Balanced on battery): the glass refreshes less often.</summary>
    public bool SaveGlass => Settings.Motion == "saver" || (Settings.Motion == "balanced" && _hub.Power.HasBattery && !_hub.Power.PluggedIn);
    public Theme Theme => _theme;

    /// <summary>Where the blurred backdrop of a see-through surface should show, in DIPs; null when it shouldn't.</summary>
    public (SKRect Rect, float Radius)? BackdropOutline =>
        _theme.Surface.SeeThrough && Glass == null && _visible.Value > 0.5 && !SlimNow && !Hidden
            ? (Bounds, Math.Min((float)_radius.Value, Bounds.Height / 2))
            : null;
    public bool IsTyping => _focused != null;

    // ───────────────────────────── commands ─────────────────────────────

    public void ShowHud(Hud hud, TimeSpan? time = null)
    {
        _hud = hud;
        _hudUntil = DateTime.Now + (time ?? Hud.DefaultTime) * (Settings.LongerNotices ? 2 : 1);
    }

    public void PushNotice(Notice notice)
    {
        if (_expanded)
        {
            if (_panel != _notices) _notices.Unseen++;
        }
        else _pending.Enqueue(notice);
    }

    public void ToggleExpanded()
    {
        if (_expanded) Collapse();
        else Expand(pinned: true);
    }

    /// <summary>Opens the island on the panel called <paramref name="title"/>, or closes it for "none".</summary>
    public bool OpenPanel(string title)
    {
        if (title.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            Collapse();
            return true;
        }
        // "home:2" opens Home at its second page; what follows the colon is the panel's to read.
        var colon = title.IndexOf(':');
        var place = colon >= 0 ? title[(colon + 1)..] : null;
        if (colon >= 0) title = title[..colon];
        var panel = _panels.FirstOrDefault(p => p.Available && p.Title.Equals(title, StringComparison.OrdinalIgnoreCase));
        if (panel == null) return false;
        if (!_expanded) Expand(pinned: true);
        Show(panel);
        if (place != null) panel.GoTo(place);
        return true;
    }

    /// <summary>Files are being dragged over the island: open the shelf as a drop zone.</summary>
    public void DragHover(bool hovering)
    {
        _files.DropHover = hovering;
        if (!hovering) return;
        if (!_expanded) Expand(pinned: false);
        Show(_files);
        _leftSince = null;
    }

    /// <summary>Opens the island on the Reminders panel with the reminder box ready for typing.</summary>
    /// <summary>Opens the clipboard history with its search box ready for typing (the clipboard hotkey).</summary>
    public void SearchClipboard()
    {
        if (!_shelf.Available) return;
        Expand(pinned: true);
        Show(_shelf);
        Focus(_shelf.SearchField);
    }

    public void NewReminder()
    {
        Expand(pinned: true);
        Show(_reminders);
        Focus(_reminders.ReminderField);
    }

    private void Expand(bool pinned)
    {
        _expanded = true;
        _pinned = pinned;
        _leftSince = null;
        if (_notice != null || _notices.Unseen > 0) Show(_notices);
        else if (Settings.OpenOn == "last" && _panels.FirstOrDefault(p => p.Available && p.Title == Settings.LastPanel) is { } last && last != _panel) Show(last);
        else if (Settings.OpenOn != "last" && (CurrentMedia != null ? (Panel)_music : _home) is var main && main.Available && main != _panel) Show(main);
        else _panel.Opened();
        _notice = null;
        _pending.Clear();
    }

    private void Collapse()
    {
        EndTyping();
        _expanded = false;
        _panel.Closed();
        if (Settings.LastPanel != _panel.Title)
        {
            Settings.LastPanel = _panel.Title; // "Last tab" opens here next time, even after a restart
            Settings.Save();
        }
        _tabPill.Snap(-1); // next time it opens, the highlight starts on the open tab rather than sliding in
        _pinned = false;
        _hoverSince = null;
    }

    private void Show(Panel panel)
    {
        if (_panel == panel) return;
        EndTyping();
        _panel.Closed();
        _panel = panel;
        panel.Opened();
        _content.Snap(0.4);
        _content.Target = 1;
    }

    // ───────────────────────────── typing ─────────────────────────────

    private void Focus(TextField field)
    {
        _focusedByClick = true;
        _focused = field;
        field.Caret = field.Text.Length;
        _pinned = true;
        TypingChanged?.Invoke(true);
    }

    public void EndTyping()
    {
        if (_focused == null) return;
        var field = _focused;
        _focused = null;
        TypingChanged?.Invoke(false);
        field.Ended?.Invoke();
    }

    public void Char(char c)
    {
        if (_focused == null || char.IsControl(c) || _focused.CaptureKey != null) return;
        _focused.Insert(c.ToString());
    }

    public void Key(int vk)
    {
        if (_focused == null) return;
        if (_focused.CaptureKey is { } capture)
        {
            if (capture(vk)) EndTyping();
            return;
        }
        switch (vk)
        {
            case 0x0D: // Enter
                var field = _focused;
                EndTyping();
                field.Submit(field.Text);
                break;
            case 0x1B: // Escape
                EndTyping();
                break;
            default:
                _focused.Key(vk);
                break;
        }
    }

    // ───────────────────────────── update ─────────────────────────────

    /// <summary>
    /// Advances animations and timers. <paramref name="cursor"/> is the pointer in DIPs, or null
    /// when it is nowhere near the window. Returns how soon the next frame is wanted, in seconds.
    /// </summary>
    public double Update(double dt, SKPoint? cursor, DateTime now)
    {
        _cursor = cursor;
        CheckDrag(cursor);
        // A slim line is easy to miss, so it listens over a wider strip along the top edge.
        var zone = SlimNow ? new SKRect(Bounds.Left - 30, 0, Bounds.Right + 30, Bounds.Bottom + 8) : Ui.Inflate(Bounds, 6);
        var over = cursor is { } c && zone.Contains(c);
        if ((Hidden || (GameStrip && !StripOpens)) && _expanded && !over) Collapse();

        // Hover opens the card; leaving closes it, unless it was opened from the keyboard or tray
        // or a field is being typed into.
        if (!_expanded && !Hidden && (!GameStrip || StripOpens))
        {
            if (over)
            {
                _hoverSince ??= now;
                // Over browser tabs (a slim island) wait a little longer, so it never opens on the way past.
                var delay = SlimNow ? HoverDelay + TimeSpan.FromMilliseconds(100) : HoverDelay;
                if (now - _hoverSince >= delay) Expand(pinned: false);
            }
            else _hoverSince = null;
        }
        else if (_expanded)
        {
            if (over && _focused == null)
            {
                _pinned = false;
                _leftSince = null;
            }
            else if (!_pinned && !over)
            {
                _leftSince ??= now;
                if (now - _leftSince >= LeaveDelay) Collapse();
            }
        }

        // A notice stays while the pointer rests on it.
        if (_notice != null && over && _mode == IslandMode.Notice) _noticeUntil = now + TimeSpan.FromSeconds(1.5);
        if (_notice != null && now >= _noticeUntil) _notice = null;
        if (_notice == null && !_expanded && _pending.Count > 0)
        {
            _notice = _pending.Dequeue();
            _noticeUntil = now + TimeSpan.FromSeconds(Math.Clamp(Settings.NotificationSeconds, 2, 30)) * (Settings.LongerNotices ? 2 : 1);
        }

        var mode = _expanded ? IslandMode.Expanded
            : _notice != null ? IslandMode.Notice
            : _hud != null && now < _hudUntil ? IslandMode.Hud
            : IslandMode.Collapsed;
        if (mode != _mode)
        {
            _mode = mode;
            _content.Snap(0);
            _content.Target = 1;
        }

        var media = CurrentMedia;
        var activity = ActivitySource();
        var (w, h, r) = _mode switch
        {
            IslandMode.Collapsed when GameStrip => (400f, 26f, 13f),
            IslandMode.Collapsed when SlimNow => (150f, 5f, 2.5f),
            IslandMode.Expanded => (ExpandedWidth, ExpandedHeight, 30f),
            IslandMode.Notice => (NoticeWidth, 54f, 27f),
            IslandMode.Hud => (_hud!.Width(_ui), 40f, 20f),
            _ when activity != null => (activity.Width(_ui), 34f, 17f),
            _ => media != null ? (Settings.TitleInPill ? 300f : 120f, 34f, 17f) : (168f, 34f, 17f),
        };
        if (_mode == IslandMode.Collapsed && PrivacyDots > 0 && !SlimNow && !GameStrip) w += 6 + PrivacyDots * 9;
        _width.Target = w;
        _height.Target = h;
        _radius.Target = SlimNow ? r : r * Math.Clamp(Settings.CornerRoundness / 100f, 0, 1);
        _visible.Target = Hidden ? 0 : 1;

        _width.Step(dt);
        _height.Step(dt);
        _radius.Step(dt);
        _content.Step(dt);
        _visible.Step(dt);
        _tabPill.Step(dt);
        _visualizer.Update(dt, media?.IsPlaying == true, _hub.Volume.Peak);
        if (_expanded) _panel.Update(dt);

        var animating = !(_width.Settled && _height.Settled && _radius.Settled && _content.Settled && _visible.Settled)
                        || (_expanded && !_tabPill.Settled);
        if (animating || (_theme.Transitioning && !Hidden)) return 0;
        if (Hidden) return 1;
        var interval = _mode switch
        {
            IslandMode.Expanded => _panel.FrameInterval,
            IslandMode.Collapsed when GameStrip => 1,
            IslandMode.Collapsed when SlimNow => 1,
            IslandMode.Collapsed when media?.IsPlaying == true && activity == null => 1 / 30.0,
            IslandMode.Collapsed => activity != null ? 0.5 : 1,
            _ => 0.25,
        };
        if (_focused != null) interval = Math.Min(interval, 0.25); // caret blink
        if (_theme.Animated) interval = Math.Min(interval, 1 / 30.0);
        return interval;
    }

    /// <summary>The notice pill is as wide as its text needs, between a compact and a roomy width.</summary>
    private float NoticeWidth
    {
        get
        {
            if (_notice is not { } n) return 360;
            var need = 54 + 20 + Math.Max(_ui.Measure(n.Title.Length > 0 ? n.Title : n.App, 13, Theme.Weight.SemiBold) + 70, _ui.Measure(n.Body, 11.5f));
            return Math.Clamp(need, 300, 420);
        }
    }

    /// <summary>The resting island is a thin line right now (covered, an app rule, or hidden until hover).</summary>
    private bool SlimNow => _mode == IslandMode.Collapsed && !_expanded && !GameStrip && (Slim || Settings.HiddenUntilHover);

    private MediaSnapshot? CurrentMedia =>
        _hub.Media.Snapshot is { } m && !string.IsNullOrWhiteSpace(m.Title) ? m : null;

    private int PrivacyDots => !_hub.Settings.ShowPrivacyIndicator ? 0
        : (_hub.Privacy.CameraInUse ? 1 : 0) + (_hub.Privacy.MicrophoneInUse ? 1 : 0);

    // ───────────────────────────── input ─────────────────────────────

    /// <summary>Whether a click at <paramref name="p"/> is the island's. A slim line and the game strip let clicks through.</summary>
    public bool Contains(SKPoint p) => !Hidden && !(GameStrip && !_expanded && !StripOpens) && !SlimNow && Ui.Inflate(Bounds, 2).Contains(p);

    private (SKPoint At, Func<string[]> Files)? _press;
    private bool _dragged;

    /// <summary>Files to drag out with the left button held (the app runs the shell's drag).</summary>
    public event Action<string[]>? FileDragStarted;

    /// <summary>The left button went down at <paramref name="p"/>: a drag may follow.</summary>
    public void PointerDown(SKPoint p)
    {
        _dragged = false;
        _slide = _ui.SliderAt(p, out _slideTrack);
        if (_slide != null)
        {
            _slideAt = (p.X - _slideTrack.Left) / Math.Max(1, _slideTrack.Width);
            _slide(_slideAt, false);
            _dragged = true; // the release is the end of the drag, not a click
            return;
        }
        _press = _ui.DraggableAt(p) is { } files ? (p, files) : null;
    }

    /// <summary>Starts a drag once the pointer has moved a few pixels with the button still held.</summary>
    private Action<float, bool>? _slide;
    private SKRect _slideTrack;
    private float _slideAt;

    private void CheckDrag(SKPoint? cursor)
    {
        if (_slide != null)
        {
            var held = Platform.Native.PrimaryButtonDown();
            if (cursor is { } at) _slideAt = (at.X - _slideTrack.Left) / Math.Max(1, _slideTrack.Width);
            _slide(_slideAt, !held); // let go away from the island: it keeps the last value
            if (!held) _slide = null;
            return;
        }
        if (_press is not { } press) return;
        if (!Platform.Native.PrimaryButtonDown())
        {
            _press = null;
            return;
        }
        if (cursor is not { } c || SKPoint.Distance(c, press.At) < 6) return;
        _press = null;
        _dragged = true;
        FileDragStarted?.Invoke(press.Files());
    }

    public void Click(SKPoint p)
    {
        if (_dragged)
        {
            _dragged = false;
            return;
        }
        // A notice that does something when clicked does it, instead of opening the card.
        if (_mode == IslandMode.Notice && !_expanded && _notice?.Click is { } act && Contains(p))
        {
            act();
            _notice = null;
            return;
        }
        var typing = _focused;
        _focusedByClick = false;
        var handled = _ui.Click(p);
        // Clicking anything other than a text field ends typing.
        if (typing != null && !_focusedByClick && _focused == typing) EndTyping();
        if (!handled && !_expanded && Contains(p)) Expand(pinned: true);
    }

    private bool _focusedByClick;
    private string? _lastPanelError;

    public void Wheel(int delta)
    {
        var notches = delta / 120f;
        if (_mode == IslandMode.Expanded && _panel.Wheel(notches)) return;
        _hub.Volume.Nudge(notches * 0.02f);
    }

    // ───────────────────────────── drawing ─────────────────────────────

    public void Draw(SKCanvas canvas)
    {
        _ui.Begin(canvas, _cursor);
        canvas.Clear(SKColors.Transparent);
        var visible = (float)Math.Clamp(_visible.Value, 0, 1);
        // Hidden until hover: nothing at all at rest, only the listening strip.
        if (SlimNow && Settings.HiddenUntilHover && !Slim && _width.Settled) visible = 0;
        if (visible <= 0.01f) return;

        var rect = Bounds;
        using var shape = Shape(rect, Math.Min((float)_radius.Value, rect.Height / 2));
        using var layer = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * visible)) };
        canvas.SaveLayer(layer);

        var lift = Math.Clamp((rect.Height - 34) / 120f, 0, 1);
        if (Glass is { } glass && _theme.Surface.Effect == SurfaceEffect.Lens)
        {
            using (var shadow = new SKPaint { Color = SKColors.Black.WithAlpha((byte)(40 + 50 * lift)), IsAntialias = true,
                       MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 6 + 8 * lift) })
            {
                canvas.Save();
                canvas.Translate(0, 3 + 5 * lift);
                canvas.DrawPath(shape, shadow);
                canvas.Restore();
            }
            DrawGlass(canvas, shape, rect, glass);
        }
        else using (var fill = new SKPaint { Color = _theme.Background, IsAntialias = true })
        {
            // A shadow would show through a see-through surface as a dark smudge.
            if (lift > 0 && !_theme.Surface.SeeThrough)
                fill.ImageFilter = SKImageFilter.CreateDropShadow(0, 6 * lift, 14 * lift, 14 * lift, SKColors.Black.WithAlpha((byte)(120 * lift)));
            using var gradient = _theme.BackgroundShader(rect);
            fill.Shader = gradient;
            canvas.DrawPath(shape, fill);
        }
        using (var edge = new SKPaint { Color = _theme.Edge, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 })
        {
            using var ring = _theme.EdgeShader(rect);
            if (ring != null)
            {
                edge.Shader = ring;
                edge.StrokeWidth = 1.6f;
            }
            if (edge.Color.Alpha > 0 || ring != null) canvas.DrawPath(shape, edge);
        }
        _theme.Sheen(canvas, shape, rect);

        canvas.Save();
        canvas.ClipPath(shape, SKClipOperation.Intersect, true);
        using var fade = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * Math.Clamp(_content.Value, 0, 1))) };
        canvas.SaveLayer(fade);
        switch (_mode)
        {
            case IslandMode.Collapsed: DrawCollapsed(rect); break;
            case IslandMode.Hud: _hud!.Draw(_ui, rect, _hud.Icon == HudIcon.Volume ? _hub.Volume.ToggleMute : null); break;
            case IslandMode.Notice: DrawNotice(rect, _notice!); break;
            case IslandMode.Expanded: DrawExpanded(rect); break;
        }
        canvas.Restore();
        canvas.Restore();
        canvas.Restore();
    }

    /// <summary>
    /// The island's outline. The notch shape hangs from the screen's top edge: square at the top,
    /// rounded at the bottom, and joined to the edge by small inward curves like a MacBook's notch.
    /// </summary>
    private SKPath Shape(SKRect r, float radius)
    {
        using var b = new SKPathBuilder();
        if (!Notch || SlimNow)
        {
            b.AddRoundRect(new SKRoundRect(r, radius));
            return b.Detach();
        }
        var ear = Math.Min(10, r.Height / 3);
        var bottom = Math.Min(radius, r.Height - ear);
        b.MoveTo(r.Left - ear, r.Top);
        b.QuadTo(r.Left, r.Top, r.Left, r.Top + ear);
        b.LineTo(r.Left, r.Bottom - bottom);
        b.QuadTo(r.Left, r.Bottom, r.Left + bottom, r.Bottom);
        b.LineTo(r.Right - bottom, r.Bottom);
        b.QuadTo(r.Right, r.Bottom, r.Right, r.Bottom - bottom);
        b.LineTo(r.Right, r.Top + ear);
        b.QuadTo(r.Right, r.Top, r.Right + ear, r.Top);
        b.Close();
        return b.Detach();
    }

    // ───────────────────────────── glass ─────────────────────────────

    // The lens, in SkSL (Skia's shading language), after the look of Apple's Liquid Glass. Near the
    // outline the glass is thick: what's behind is pulled in from further inside, more steeply
    // toward the edge, so the rim magnifies; red and blue bend a little differently for a fringe.
    // Light from the top left catches the rim where it faces that way, with a fainter glint on the
    // far side, and the bevel darkens slightly where it faces away. Written for HaloBar.
    private const string LensSksl = @"
        uniform shader behind;
        uniform float2 centre;
        uniform float2 extent;   // half the island's width and height
        uniform float radius;
        uniform float ring;      // how far in from the outline the glass bends
        uniform float bend;      // how far the picture is pulled in, right at the outline
        half4 main(float2 p) {
            float2 q = abs(p - centre) - (extent - radius);
            float edge = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
            float k = clamp(1.0 + edge / ring, 0.0, 1.0);
            float curve = k * k * k;
            float2 outward = sign(p - centre) * max(q, float2(0.0));
            float2 n = outward / max(length(outward), 0.0001);
            float2 o = -n * bend * curve;
            half3 c;
            c.r = behind.eval(p + o * 1.25).r;
            c.g = behind.eval(p + o).g;
            c.b = behind.eval(p + o * 0.75).b;
            float2 toLight = normalize(float2(-0.45, -0.9));
            float facing = max(dot(n, toLight), 0.0);
            float away = max(dot(n, -toLight), 0.0);
            float rim = smoothstep(0.7, 1.0, k);
            c += half3(rim * (0.42 * facing + 0.06) + smoothstep(0.85, 1.0, k) * away * 0.16);
            c *= half(1.0 - 0.08 * curve * (1.0 - facing));
            return half4(c, 1.0);
        }";

    private static readonly SKRuntimeEffect? Lens = BuildLens();

    private static SKRuntimeEffect? BuildLens()
    {
        var effect = SKRuntimeEffect.CreateShader(LensSksl, out var errors);
        if (effect == null) Log.Write($"Glass: lens shader didn't build: {errors}");
        return effect;
    }

    /// <summary>
    /// The island as a piece of glass: the blurred screen behind it, a bent and fringed rim, then a
    /// thin tint so text stays readable. Only the rim runs the shader; the middle is a plain image.
    /// </summary>
    private void DrawGlass(SKCanvas canvas, SKPath shape, SKRect rect, SKImage glass)
    {
        canvas.Save();
        canvas.ClipPath(shape, SKClipOperation.Intersect, true);
        var area = GlassArea.IsEmpty ? rect : GlassArea;
        var toIsland = SKMatrix.CreateScaleTranslation(area.Width / glass.Width, area.Height / glass.Height, area.Left, area.Top);
        var sampling = new SKSamplingOptions(SKFilterMode.Linear);
        using (var behind = glass.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, sampling, toIsland))
        using (var plain = new SKPaint { Shader = behind })
        {
            canvas.DrawRect(rect, plain);
            var radius = Math.Min((float)_radius.Value, rect.Height / 2);
            var ring = Math.Min(22, rect.Height / 2.2f);
            if (Lens != null)
            {
                var uniforms = new SKRuntimeEffectUniforms(Lens)
                {
                    ["centre"] = new[] { rect.MidX, rect.MidY },
                    ["extent"] = new[] { rect.Width / 2, rect.Height / 2 },
                    ["radius"] = radius,
                    ["ring"] = ring,
                    ["bend"] = ring * 1.15f,
                };
                var children = new SKRuntimeEffectChildren(Lens) { ["behind"] = behind };
                using var lens = Lens.ToShader(uniforms, children);
                using var rim = new SKPaint { Shader = lens, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = ring * 2 };
                canvas.DrawPath(shape, rim);
            }
        }
        // A thin wash: the glass reads as glass, and text on it stays legible (light under dark text).
        var tint = _theme.DarkText ? SKColors.White.WithAlpha(70) : _theme.Background.WithAlpha((byte)(_theme.Background.Alpha * 0.32f));
        using (var wash = new SKPaint { Color = tint })
            canvas.DrawRect(rect, wash);
        canvas.Restore();
    }

    /// <summary>While a game is full screen: CPU, GPU, memory, network and the time, in one line.</summary>
    private void DrawGameStrip(SKRect r)
    {
        _hub.Stats.Touch();
        var s = _hub.Stats.Snapshot;
        Icons.Gamepad(_ui.Canvas, r.Left + 18, r.MidY, 15, _theme.Accent);
        // Each reading the strip is set to show, left to right; CPU and GPU can carry a small bar.
        var parts = new List<(string Text, float? Bar)>();
        if (Settings.StripCpu) parts.Add((s.CpuTemp is { } ct ? $"CPU {s.Cpu:P0} {Prefs.Temp(ct)}" : $"CPU {s.Cpu:P0}", (float)s.Cpu));
        if (Settings.StripGpu) parts.Add((s.GpuTemp is { } gt ? $"GPU {s.Gpu:P0} {Prefs.Temp(gt)}" : $"GPU {s.Gpu:P0}", (float)s.Gpu));
        if (Settings.StripRam) parts.Add(($"RAM {s.RamUsedGb:0.0} GB", null));
        if (Settings.StripNetwork) parts.Add(($"↓ {Services.StatsService.Rate(s.DownBytesPerSec)}", null));
        if (Settings.StripClock) parts.Add((Prefs.Time(DateTime.Now), null));
        var x = r.Left + 34;
        foreach (var (text, bar) in parts)
        {
            if (x > r.Right - 30) break;
            if (bar is { } used && Settings.StripBars)
            {
                var track = new SKRect(x, r.MidY - 2, x + 18, r.MidY + 2);
                _ui.Fill(track, 2, _theme.Overlay(40));
                _ui.Fill(new SKRect(track.Left, track.Top, track.Left + track.Width * Math.Clamp(used, 0, 1), track.Bottom), 2, _theme.Accent);
                x += 24;
            }
            _ui.Text(text, x, r.MidY + 4, r.Right - 12 - x, 11.5f, Theme.Weight.SemiBold);
            x += _ui.Measure(text, 11.5f, Theme.Weight.SemiBold) + 16;
        }
    }

    private void DrawCollapsed(SKRect r)
    {
        if (GameStrip)
        {
            DrawGameStrip(r);
            return;
        }
        if (SlimNow) return;

        // Camera and microphone dots sit at the right end, like a phone's privacy indicator.
        var dots = PrivacyDots;
        if (dots > 0)
        {
            var x = r.Right - 12;
            if (_hub.Privacy.MicrophoneInUse) { _ui.Circle(x, r.MidY, 3.5f, MicOrange); x -= 9; }
            if (_hub.Privacy.CameraInUse) _ui.Circle(x, r.MidY, 3.5f, CameraGreen);
            r = new SKRect(r.Left, r.Top, r.Right - 6 - dots * 9, r.Bottom);
        }

        if (ActivitySource() is { } activity)
        {
            activity.Draw(_ui, r);
            return;
        }
        var media = CurrentMedia;
        if (media == null)
        {
            var text = Prefs.Time(DateTime.Now) + "  ·  " + DateTime.Now.ToString("ddd d");
            _ui.Text(text, r.MidX, r.MidY + 4.5f, r.Width - 24, 12.5f, Theme.Weight.SemiBold, _theme.Text, SKTextAlign.Center);
            return;
        }
        _music.DrawArtwork(_ui, media, new SKRect(r.Left + 7, r.MidY - 11, r.Left + 29, r.MidY + 11), 6);
        if (Settings.TitleInPill) _ui.Text(media.Title, r.Left + 38, r.MidY + 4.5f, r.Width - 38 - 46, 12.5f, Theme.Weight.SemiBold);
        _visualizer.Draw(_ui.Canvas, r.Right - 34, r.MidY, 16, media.IsPlaying ? _theme.Accent : _theme.Faint, _hub.Settings.Visualizer == "pixels");
    }

    /// <summary>
    /// A notice as a compact two-line pill: the icon, the title in bold with the app small beside
    /// it, and the message on one line underneath (cut short; the full text is in Notifications).
    /// </summary>
    private void DrawNotice(SKRect r, Notice n)
    {
        NoticesPanel.DrawIcon(_ui, n, r.Left + 14 + 15, r.MidY, 15);
        var x = r.Left + 54;
        var width = r.Right - 20 - x;
        var title = n.Title.Length > 0 ? n.Title : n.App;
        var app = n.Title.Length > 0 && n.App.Length > 0 && n.App != n.Title ? n.App : "";
        var appWidth = app.Length > 0 ? Math.Min(_ui.Measure(app, 10.5f) + 10, width * 0.4f) : 0;
        _ui.Text(title, x, r.Top + 22, width - appWidth, 13, Theme.Weight.SemiBold);
        if (app.Length > 0) _ui.Text(app, r.Right - 20, r.Top + 22, appWidth, 10.5f, Theme.Weight.Regular, _theme.Faint, SKTextAlign.Right);
        if (n.Body.Length > 0) _ui.Text(n.Body, x, r.Top + 40, width, 11.5f, Theme.Weight.Regular, _theme.Dim);
        _ui.Hit(r, () =>
        {
            // A notice from an app opens that app; others open the history.
            if (n.AppId != null)
            {
                _hub.Open($"shell:AppsFolder\\{n.AppId}");
                _notice = null;
            }
            else Expand(pinned: true);
        });
    }

    private void DrawExpanded(SKRect r)
    {
        _panel.Background(_ui, r);

        // Tab bar: icons only. A soft rounded square sits behind the open panel's icon and slides
        // to the next one; hovering any tab shows its name in a small tooltip underneath.
        var x = r.Left + 14;
        var y = r.Top + 27;
        var tabs = _panels.Where(p => p.Available).ToList();
        if (!tabs.Contains(_panel)) _panel = tabs[0];
        // Spacing depends only on how many tabs there are, never on the open panel, so the icons
        // stay put when switching. Room is kept for the clock and one short action.
        var room = r.Width - 28 - 140 - tabs.Count * 31;
        var step = room < 0 ? Math.Max(24, 31 + room / tabs.Count) : 31;
        var selectedX = x + step * tabs.IndexOf(_panel) + step / 2;
        if (_tabPill.Value < 0) _tabPill.Snap(selectedX);
        _tabPill.Target = selectedX;
        var pill = Ui.Square((float)_tabPill.Value, y, 12.5f);
        _ui.Fill(pill, 8, _theme.Overlay(34));
        (string Title, float X)? tooltip = null;
        foreach (var panel in tabs)
        {
            var selected = panel == _panel;
            var cx = x + step / 2;
            var hit = new SKRect(x, y - 14, x + step, y + 14);
            if (!selected && _ui.Hovering(hit)) _ui.Fill(Ui.Square(cx, y, 12.5f), 8, _theme.Overlay(14));
            panel.Icon(_ui.Canvas, cx, y, 15, selected ? _theme.Text : _theme.Dim);
            if (_ui.Hovering(hit)) tooltip = (panel.Title, cx);
            if (panel.Badge > 0)
            {
                _ui.Circle(cx + 8, y - 8, 6, _theme.Accent);
                _ui.Text(panel.Badge > 9 ? "9+" : panel.Badge.ToString(), cx + 8, y - 5, 12, 8, Theme.Weight.Bold, _theme.OnAccent, SKTextAlign.Center);
            }
            var chosen = panel;
            _ui.Hit(hit, () => Show(chosen));
            x += step;
        }

        // Clock and the panel's own actions at the right.
        var clock = Prefs.Time(DateTime.Now);
        var right = r.Right - 20;
        _ui.Text(clock, right, y + 4.5f, 80, 12, Theme.Weight.Regular, _theme.Dim, SKTextAlign.Right);
        right -= _ui.Measure(clock, 12) + 16;
        foreach (var (label, click) in _panel.Actions.Reverse())
        {
            // A label longer than the room left (some languages' words are long) shrinks a little,
            // then is cut short, rather than disappearing.
            var space = right - (x + 8);
            if (space < 28) break;
            var size = _ui.Measure(label, 12) <= space ? 12f : 10.5f;
            var w = Math.Min(_ui.Measure(label, size), space);
            _ui.Text(label, right, y + 4.5f, w + 4, size, Theme.Weight.Regular, _theme.Accent, SKTextAlign.Right);
            _ui.Hit(new SKRect(right - w - 6, y - 13, right + 6, y + 13), click);
            right -= w + 16;
        }

        var body = new SKRect(r.Left, r.Top + 50, r.Right, r.Bottom);
        try
        {
            _ui.HitClip = body;
            _panel.Draw(_ui, body);
        }
        catch (Exception ex)
        {
            if (_lastPanelError != ex.Message) Log.Write($"Panel {_panel.Title} failed to draw: {ex}");
            _lastPanelError = ex.Message;
            _ui.Empty(body, $"{_panel.Title} hit a problem", "It's in the log; the rest of HaloBar keeps working.");
        }
        finally { _ui.HitClip = null; }

        // Drawn last so it sits over the panel's content.
        if (tooltip is { } tip)
        {
            var w = _ui.Measure(tip.Title, 11, Theme.Weight.SemiBold) + 18;
            var left = Math.Clamp(tip.X - w / 2, r.Left + 8, r.Right - 8 - w);
            var box = new SKRect(left, y + 17, left + w, y + 37);
            _ui.Fill(box, 10, _theme.Text);
            _ui.Text(tip.Title, box.MidX, box.MidY + 4, w, 11, Theme.Weight.SemiBold, _theme.Background, SKTextAlign.Center);
        }
    }
}
