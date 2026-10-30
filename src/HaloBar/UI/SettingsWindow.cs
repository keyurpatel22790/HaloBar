using System.Runtime.InteropServices;
using HaloBar.Platform;
using HaloBar.Rendering;
using HaloBar.Services;
using HaloBar.UI.Panels;
using SkiaSharp;
using static HaloBar.Platform.Native;

namespace HaloBar.UI;

/// <summary>
/// Settings in a window of their own, like NotchPixel's: a sidebar of pages with a search box,
/// each option with its explanation under it. The options are the island Settings panel's own
/// rows (<see cref="SettingsPanel.Sections"/>), so both always agree. Drawn with the island's
/// colours and fonts through the same Skia and composition path. Window thread only.
/// </summary>
internal sealed class SettingsWindow : NativeWindow
{
    private const float Sidebar = 232, RowHeight = 58, MinWidth = 760, MinHeight = 520;

    /// <summary>Pages in the sidebar, each made of sections from the settings list.</summary>
    private static readonly (string Page, string[] Sections)[] Pages =
    [
        ("General", ["Island", "Panels", "Integrations", "App rules"]),
        ("Appearance", ["Surface and colour", "Look and place"]),
        ("Accessibility", ["Accessibility"]),
        ("Read-outs", ["HUDs"]),
        ("Music", ["Music", "Music sources"]),
        ("Stats and gaming", ["Stats and game strip"]),
        ("Clipboard and files", ["Clipboard", "Files and exports"]),
        ("Chat", ["Chat"]),
        ("Web", ["Web drawer", "Web extensions"]),
        ("Privacy", ["Privacy dots"]),
        ("Shortcuts", ["Keys"]),
        ("About", []),
    ];

    private readonly Hub _hub;
    private readonly SettingsPanel _options;
    private readonly Ui _ui;
    private readonly TextField _search;
    private GraphicsDevice _gfx;
    private CompositionSurface _surface;
    private SKCanvas? _canvas;
    private SKBitmap? _canvasBitmap;
    private TextField? _focused;
    private readonly ScreenCopyBackdrop _glass;
    private SKImage? _behind;
    private SKPoint? _cursor;
    private int _page;
    private Spring _scroll = new(0, 320, 1);
    private float _contentHeight, _viewHeight;

    public bool Dirty { get; set; } = true;
    public double LastFrame { get; private set; } = double.NegativeInfinity;
    public bool Visible => IsWindowVisible(Handle);
    /// <summary>The last frame drawn (for snapshots).</summary>
    public SKBitmap Picture => _surface.Bitmap;

    public SettingsWindow(Hub hub, GraphicsDevice gfx, Action applyLayout)
        : base("HaloBar.Settings", WS_EX_NOREDIRECTIONBITMAP, WS_OVERLAPPEDWINDOW)
    {
        _hub = hub;
        _gfx = gfx;
        _options = new SettingsPanel(hub, applyLayout);
        _ui = new Ui(hub.Theme, Focus, () => _focused);
        _search = new TextField("Search settings", _ => { });
        SetWindowTextW(Handle, "HaloBar Settings");
        var dark = 1;
        DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
        // Glass: the screen behind the window, copied, blurred and drawn under the pages (Windows'
        // own Acrylic is faint over dark windows and turns off when the window isn't focused).
        // The window stays out of screen captures so the copy never contains itself.
        SetWindowDisplayAffinity(Handle, 0x11 /* WDA_EXCLUDEFROMCAPTURE */);
        _glass = new ScreenCopyBackdrop(openMs: 100, smallMs: 100);

        // Centred on the main screen, 920×660 DIPs.
        var primary = Monitors.All().FirstOrDefault(m => m.IsPrimary) ?? Monitors.All()[0];
        var s = primary.Scale;
        int w = (int)(920 * s), h = (int)(660 * s);
        var b = primary.Bounds;
        SetWindowPos(Handle, IntPtr.Zero, b.Left + (b.Width - w) / 2, b.Top + (b.Height - h) / 2, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
        GetClientRect(Handle, out var client);
        _surface = new CompositionSurface(gfx, Handle, Math.Max(1, client.Width), Math.Max(1, client.Height));
    }

    private float Scale => GetDpiForWindow(Handle) / 96f;

    /// <summary>Shows the window on <paramref name="page"/> (a page or section name), or where it was.</summary>
    public void Open(string? page = null)
    {
        if (page != null)
        {
            var i = Array.FindIndex(Pages, p => p.Page.Equals(page, StringComparison.OrdinalIgnoreCase)
                                                || p.Sections.Any(sec => sec.Equals(page, StringComparison.OrdinalIgnoreCase)));
            if (i >= 0) ShowPage(i);
        }
        if (IsIconic(Handle)) ShowWindow(Handle, 9 /* SW_RESTORE */);
        ShowWindow(Handle, 5 /* SW_SHOW */);
        SetForegroundWindow(Handle);
        Dirty = true;
    }

    private void ShowPage(int i)
    {
        _page = i;
        _search.Clear();
        _scroll.Snap(0);
        Dirty = true;
    }

    /// <summary>A frame is due: something changed, the scroll is moving, or a caret blinks.</summary>
    public bool Due(double now)
    {
        if (!Visible || IsIconic(Handle)) return false;
        GetClientRect(Handle, out var client);
        var origin = new POINT();
        ClientToScreen(Handle, ref origin);
        var picture = _glass.Behind(new SKRectI(origin.X, origin.Y, origin.X + client.Width, origin.Y + client.Height), 100, paceMs: 100);
        if (!ReferenceEquals(picture, _behind))
        {
            _behind = picture;
            Dirty = true;
        }
        // The pointer left without a move message inside: one more frame clears the hover.
        if (_cursor != null && PointerInside(Scale) == null) Dirty = true;
        return Dirty || !_scroll.Settled || (_focused != null && now - LastFrame > 0.25) || now - LastFrame > 1;
    }

    public void ReplaceSurface(GraphicsDevice gfx)
    {
        _gfx = gfx;
        DropCanvas();
        _surface.Dispose();
        GetClientRect(Handle, out var client);
        _surface = new CompositionSurface(gfx, Handle, Math.Max(1, client.Width), Math.Max(1, client.Height));
        Dirty = true;
    }

    // ───────────────────────────── drawing ─────────────────────────────

    public bool Render(double now, double dt)
    {
        if (!ReferenceEquals(_canvasBitmap, _surface.Bitmap))
        {
            DropCanvas();
            _canvasBitmap = _surface.Bitmap;
            _canvas = new SKCanvas(_canvasBitmap);
        }
        _scroll.Step(dt);
        if (_options.TakeFocus() is { } capture) Focus(capture);
        var canvas = _canvas!;
        canvas.RestoreToCount(1);
        canvas.ResetMatrix();
        var scale = Scale;
        canvas.Scale(scale);
        var size = new SKSize(_surface.Width / scale, _surface.Height / scale);
        // Where the pointer really is, read each frame: with the glass frame stretched over the
        // window, "mouse left" notices came while it was still inside and hover flickered off and on.
        _cursor = PointerInside(scale);
        _ui.Begin(canvas, _cursor);
        var theme = _hub.Theme;
        try
        {
            // Solid colours: the island's surface may be see-through, a window shouldn't be.
            // Glass: the blurred screen behind the window, then a tint in the island's colours thick
            // enough to read on (plain colour until the first copy arrives).
            var dark = Luminance(theme.Text) > 0.5;
            canvas.Clear(dark ? new SKColor(0x16, 0x16, 0x1A) : new SKColor(0xF2, 0xF2, 0xF5));
            if (_behind is { } behind)
            {
                using var glass = new SKPaint { IsAntialias = false };
                canvas.DrawImage(behind, new SKRect(0, 0, size.Width, size.Height), new SKSamplingOptions(SKFilterMode.Linear), glass);
            }
            using (var tint = new SKPaint { Color = dark ? new SKColor(0x14, 0x14, 0x18, 0xA8) : new SKColor(0xF6, 0xF6, 0xF8, 0xB0) })
                canvas.DrawRect(0, 0, size.Width, size.Height, tint);
            using (var colour = new SKPaint { Color = theme.Background.WithAlpha((byte)Math.Min(40, (int)theme.Background.Alpha)) })
                canvas.DrawRect(0, 0, size.Width, size.Height, colour);
            DrawSidebar(new SKRect(0, 0, Sidebar, size.Height));
            DrawContent(new SKRect(Sidebar, 0, size.Width, size.Height));
        }
        catch (Exception ex)
        {
            Log.Write($"Settings window failed to draw: {ex}");
        }
        canvas.Flush();
        LastFrame = now;
        Dirty = false;
        return _surface.Present();
    }

    private void DrawSidebar(SKRect r)
    {
        var theme = _hub.Theme;
        _ui.Fill(r, 0, theme.Overlay(10));
        _ui.Text("HaloBar", r.Left + 22, 40, r.Width - 40, 18, Theme.Weight.Bold);
        _ui.Text("Settings", r.Left + 22, 58, r.Width - 40, 11.5f, Theme.Weight.Regular, theme.Dim);
        _ui.Field(_search, new SKRect(r.Left + 14, 74, r.Right - 14, 104), 12.5f);
        var y = 122f;
        for (var i = 0; i < Pages.Length; i++)
        {
            var item = new SKRect(r.Left + 10, y, r.Right - 10, y + 34);
            var selected = i == _page && _search.Text.Length == 0;
            if (selected) _ui.Fill(item, 9, theme.Accent.WithAlpha(60));
            else if (_ui.Hovering(item)) _ui.Fill(item, 9, theme.Overlay(16));
            if (selected) _ui.Fill(new SKRect(item.Left, item.Top + 9, item.Left + 3, item.Bottom - 9), 1.5f, theme.Accent);
            _ui.Text(Pages[i].Page, item.Left + 14, item.MidY + 4.5f, item.Width - 24, 13, selected ? Theme.Weight.SemiBold : Theme.Weight.Regular,
                selected ? theme.Text : theme.Dim);
            var chosen = i;
            _ui.Hit(item, () => ShowPage(chosen));
            y += 36;
        }
    }

    private void DrawContent(SKRect r)
    {
        var theme = _hub.Theme;
        var query = _search.Text.Trim();
        var title = query.Length > 0 ? $"Results for “{query}”" : Pages[_page].Page;
        _ui.Text(title, r.Left + 32, 48, r.Width - 64, 22, Theme.Weight.Bold);

        var view = new SKRect(r.Left, 70, r.Right, r.Bottom);
        _viewHeight = view.Height;
        _ui.Canvas.Save();
        _ui.Canvas.ClipRect(view);
        _ui.HitClip = view;
        var top = view.Top + 12 - (float)_scroll.Value;
        var y = top;
        var width = Math.Min(r.Width - 64, 760);
        var left = r.Left + 32;

        if (query.Length == 0 && Pages[_page].Page == "About") y = DrawAbout(left, y, width);
        else
        {
            var all = _options.Sections().ToList();
            IEnumerable<(string Section, SettingsPanel.Row[] Rows)> shown = query.Length > 0
                ? all.Select(s => (s.Section, s.Rows.Where(row => Matches(row, s.Section, query)).ToArray())).Where(s => s.Item2.Length > 0)
                : Pages[_page].Sections.Select(name => all.FirstOrDefault(s => s.Section == name)).Where(s => s.Rows != null);
            var any = false;
            foreach (var (section, rows) in shown)
            {
                any = true;
                y = DrawSection(section, rows, left, y, width);
            }
            if (!any)
                _ui.Text(query.Length > 0 ? "Nothing matches. Try another word." : "Nothing here yet.", left, y + 20, width, 13, Theme.Weight.Regular, theme.Dim);
        }
        _contentHeight = y - top + 24;
        _ui.HitClip = null;
        _ui.Canvas.Restore();
        // Keep the scroll inside what there is.
        var max = Math.Max(0, _contentHeight - _viewHeight);
        if (_scroll.Target > max) _scroll.Target = max;
    }

    private static bool Matches(SettingsPanel.Row row, string section, string query) =>
        Lang.T(row.Label).Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || row.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
        || (row.Hint != null && (Lang.T(row.Hint).Contains(query, StringComparison.CurrentCultureIgnoreCase) || row.Hint.Contains(query, StringComparison.OrdinalIgnoreCase)))
        || section.Contains(query, StringComparison.OrdinalIgnoreCase);

    /// <summary>A section: its name, then its options on one card, each with its explanation.</summary>
    private float DrawSection(string section, SettingsPanel.Row[] rows, float left, float y, float width)
    {
        var theme = _hub.Theme;
        _ui.Text(Lang.T(section).ToUpper(System.Globalization.CultureInfo.CurrentCulture), left + 4, y + 14, width, 11, Theme.Weight.SemiBold, theme.Accent);
        y += 24;
        var card = new SKRect(left, y, left + width, y + rows.Length * RowHeight);
        _ui.Fill(card, 12, theme.Overlay(12));
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            var r = new SKRect(left, y, left + width, y + RowHeight);
            if (_ui.Hovering(r)) _ui.Fill(Ui.Inflate(r, -2), 10, theme.Overlay(14));
            if (i > 0) _ui.Fill(new SKRect(left + 16, y, left + width - 16, y + 1), 0, theme.Overlay(14));
            var controlWidth = row.On != null ? 60 : row.Slider != null ? 260 : row.Value != null ? _ui.Measure(row.Value() + row.Suffix, 12) + 40 : 0;
            var textWidth = width - 32 - controlWidth;
            if (row.Hint is { Length: > 0 } hint)
            {
                _ui.Text(row.Label, r.Left + 16, r.Top + 24, textWidth, 13.5f, Theme.Weight.SemiBold);
                _ui.Text(hint, r.Left + 16, r.Top + 43, textWidth, 11.5f, Theme.Weight.Regular, theme.Dim);
            }
            else _ui.Text(row.Label, r.Left + 16, r.MidY + 5, textWidth, 13.5f, Theme.Weight.SemiBold);
            if (row.Slider == null) _ui.Hit(r, () => { row.Click(); Dirty = true; });
            if (row.On != null) _ui.Switch(r.Right - 52, r.MidY, row.On(), () => { row.Click(); Dirty = true; });
            else if (row.Slider is { } slide)
            {
                _ui.Text(row.Value!() + row.Suffix, r.Right - 196, r.MidY + 4.5f, 60, 12.5f, Theme.Weight.SemiBold, theme.Text, SKTextAlign.Right);
                _ui.Slider(new SKRect(r.Right - 180, r.MidY - 10, r.Right - 20, r.MidY + 10), slide.Fraction, slide.SetFraction);
            }
            else if (row.Value != null)
            {
                var label = row.Value() + row.Suffix;
                _ui.Chip(r.Right - 16 - _ui.Measure(label, 12) - 22, r.MidY, label, () => { row.Click(); Dirty = true; }, true, 12);
            }
            y += RowHeight;
        }
        return y + 22;
    }

    private float DrawAbout(float left, float y, float width)
    {
        var theme = _hub.Theme;
        var version = typeof(SettingsWindow).Assembly.GetName().Version;
        _ui.Text($"HaloBar {version?.ToString(3)}", left + 4, y + 22, width, 16, Theme.Weight.SemiBold);
        _ui.Text("A Dynamic Island for Windows.", left + 4, y + 44, width, 12.5f, Theme.Weight.Regular, theme.Dim);
        y += 70;
        SettingsPanel.Row[] rows =
        [
            new("Settings file", Path.Combine(AppPaths.Folder, "settings.json"), () => "Open", () => { _hub.Settings.Save(); _hub.Open(Path.Combine(AppPaths.Folder, "settings.json")); }),
            new("Log", Log.FilePath, () => "Open", () => _hub.Open(Log.FilePath)),
            new("HaloBar's folder", AppPaths.Folder, () => "Show", () => _hub.Open(AppPaths.Folder)),
        ];
        return DrawSection("About", rows, left, y, width);
    }

    private static double Luminance(SKColor c) => (0.2126 * c.Red + 0.7152 * c.Green + 0.0722 * c.Blue) / 255;

    // ───────────────────────────── input ─────────────────────────────

    private bool _focusedByClick;
    private Action<float, bool>? _slide;
    private SKRect _slideTrack;

    private void Slide(SKPoint p, bool done)
    {
        _slide?.Invoke((p.X - _slideTrack.Left) / Math.Max(1, _slideTrack.Width), done);
        Dirty = true;
    }

    private void Focus(TextField field)
    {
        _focusedByClick = true;
        _focused = field;
        field.Caret = field.Text.Length;
        Dirty = true;
    }

    private void EndTyping()
    {
        if (_focused == null) return;
        var field = _focused;
        _focused = null;
        field.Ended?.Invoke();
        Dirty = true;
    }

    private void Key(int vk)
    {
        if (_focused == null)
        {
            // Typing anywhere goes to the search box; Ctrl+F does too.
            if (vk == 0x46 && GetKeyState(0x11) < 0) Focus(_search);
            return;
        }
        if (_focused.CaptureKey is { } capture)
        {
            if (capture(vk)) EndTyping();
        }
        else if (vk == 0x1B)
        {
            if (_focused == _search) _search.Clear();
            EndTyping();
        }
        else if (vk != 0x0D) _focused.Key(vk);
        if (_focused == _search) _scroll.Snap(0);
        Dirty = true;
    }

    private void Char(char c)
    {
        if (char.IsControl(c)) return;
        if (_focused == null)
        {
            Focus(_search);
            _scroll.Snap(0);
        }
        if (_focused!.CaptureKey != null) return;
        _focused.Insert(c.ToString());
        Dirty = true;
    }

    private SKPoint? PointerInside(float scale)
    {
        GetCursorPos(out var p);
        var client = new POINT { X = 0, Y = 0 };
        ClientToScreen(Handle, ref client);
        GetClientRect(Handle, out var r);
        int x = p.X - client.X, y = p.Y - client.Y;
        if (x < 0 || y < 0 || x >= r.Width || y >= r.Height || WindowFromPoint(p) != Handle) return null;
        return new SKPoint(x / scale, y / scale);
    }

    private SKPoint Dip(IntPtr lParam) => new((short)((long)lParam & 0xFFFF) / Scale, (short)(((long)lParam >> 16) & 0xFFFF) / Scale);

    protected override IntPtr? OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_CLOSE:
                // Closing only hides it: it opens again where you were.
                EndTyping();
                ShowWindow(Handle, 0 /* SW_HIDE */);
                return IntPtr.Zero;
            case WM_SIZE:
                if (_surface != null && ((long)wParam) != 1 /* SIZE_MINIMIZED */)
                {
                    _surface.Resize(Math.Max(1, (int)((long)lParam & 0xFFFF)), Math.Max(1, (int)(((long)lParam >> 16) & 0xFFFF)));
                    Dirty = true;
                }
                return IntPtr.Zero;
            case WM_GETMINMAXINFO:
                var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                info.MinTrackSize = new POINT { X = (int)(MinWidth * Scale), Y = (int)(MinHeight * Scale) };
                Marshal.StructureToPtr(info, lParam, false);
                return IntPtr.Zero;
            case WM_DPICHANGED:
                var suggested = Marshal.PtrToStructure<RECT>(lParam);
                SetWindowPos(Handle, IntPtr.Zero, suggested.Left, suggested.Top, suggested.Width, suggested.Height, SWP_NOZORDER | SWP_NOACTIVATE);
                Dirty = true;
                return IntPtr.Zero;
            case WM_LBUTTONDOWN:
                // Pressing a slider grabs it; it follows the pointer until the button is let go.
                _slide = _ui.SliderAt(Dip(lParam), out _slideTrack);
                if (_slide != null)
                {
                    SetCapture(Handle);
                    Slide(Dip(lParam), false);
                }
                return IntPtr.Zero;
            case WM_MOUSEMOVE:
                if (_slide != null) Slide(Dip(lParam), false);
                Dirty = true;
                return IntPtr.Zero;
            case WM_LBUTTONUP when _slide != null:
                Slide(Dip(lParam), true);
                _slide = null;
                ReleaseCapture();
                return IntPtr.Zero;
            case WM_LBUTTONUP:
                // A click anywhere but the box being typed into ends typing (a shortcut being recorded waits for keys).
                var typing = _focused;
                _focusedByClick = false;
                _ui.Click(Dip(lParam));
                if (typing != null && _focused == typing && !_focusedByClick && typing.CaptureKey == null) EndTyping();
                Dirty = true;
                return IntPtr.Zero;
            case WM_MOUSEWHEEL:
                var notches = (short)(((long)wParam >> 16) & 0xFFFF) / 120f;
                _scroll.Target = Math.Clamp(_scroll.Target - notches * 90, 0, Math.Max(0, _contentHeight - _viewHeight));
                Dirty = true;
                return IntPtr.Zero;
            case WM_CHAR:
                Char((char)(long)wParam);
                return IntPtr.Zero;
            case WM_KEYDOWN:
            case WM_SYSKEYDOWN when _focused?.CaptureKey != null:
                Key((int)(long)wParam);
                return IntPtr.Zero;
            case WM_KILLFOCUS:
                if (_focused?.CaptureKey != null) EndTyping();
                return null;
            case WM_ERASEBKGND:
                return new IntPtr(1);
        }
        return null;
    }

    private void DropCanvas()
    {
        _canvas?.Dispose();
        _canvas = null;
        _canvasBitmap = null;
    }

    public override void Dispose()
    {
        _glass.Dispose();
        DropCanvas();
        _surface.Dispose();
        base.Dispose();
    }

    private const uint WM_SIZE = 0x5, WM_GETMINMAXINFO = 0x24, WM_MOUSELEAVE = 0x2A3, WM_ERASEBKGND = 0x14;
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint SWP_NOZORDER = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO { public POINT Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT { public int cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }

    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT track);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowTextW(IntPtr hwnd, string text);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
