using HaloBar.Platform;
using HaloBar.Rendering;
using HaloBar.Services;
using SkiaSharp;
using static HaloBar.Platform.Native;

namespace HaloBar.UI;

/// <summary>
/// HaloBar's own screenshot mark-up window, in place of opening Paint. Shows the picture with a
/// toolbar of tools — arrow, box, circle, pen, highlighter, text and crop — then saves over the
/// file or copies the marked-up picture. Drawn through the same Skia + composition path as the
/// rest of the app. Window thread only.
/// </summary>
internal sealed class AnnotateWindow : NativeWindow
{
    private enum Tool { Arrow, Box, Circle, Pen, Highlight, Text, Crop }

    private sealed class Shape
    {
        public Tool Tool;
        public SKColor Color;
        public float Stroke = 3;
        public readonly List<SKPoint> Points = []; // image coordinates
        public string Text = "";
    }

    private static readonly SKColor[] Palette =
    [
        new(0xFF, 0x3B, 0x30), new(0xFF, 0x9F, 0x0A), new(0xFF, 0xD6, 0x0A), new(0x34, 0xC7, 0x59),
        new(0x0A, 0x84, 0xFF), new(0xAF, 0x52, 0xDE), new(0xFF, 0xFF, 0xFF), new(0x11, 0x11, 0x14),
    ];

    private readonly Hub _hub;
    private GraphicsDevice _gfx;
    private CompositionSurface _surface;
    private readonly Ui _ui;

    private SKBitmap? _base;
    private string _file = "";
    private readonly List<Shape> _shapes = [];
    private Shape? _drawing;
    private Shape? _typing;
    private Tool _tool = Tool.Arrow;
    private SKColor _color = Palette[0];
    private float _stroke = 3;
    private string _status = "";
    private DateTime _statusUntil;

    private SKRect _view; // where the image is drawn, in DIP
    private float _viewScale = 1;
    private bool _toolbarPress;

    public bool Dirty { get; set; } = true;
    public double LastFrame { get; private set; } = double.NegativeInfinity;
    public bool Visible => IsWindowVisible(Handle) && !IsIconic(Handle);
    /// <summary>The last frame drawn after <see cref="CaptureNextFrame"/> (for snapshots).</summary>
    public SKBitmap? Picture => _surface.Captured;
    public void CaptureNextFrame() => _surface.CaptureNext = true;

    public AnnotateWindow(Hub hub, GraphicsDevice gfx) : base("HaloBar.Annotate", WS_EX_NOREDIRECTIONBITMAP, WS_OVERLAPPEDWINDOW)
    {
        _hub = hub;
        _gfx = gfx;
        _ui = new Ui(hub.Theme, _ => { }, () => null);
        SetWindowTextW(Handle, "HaloBar — Mark up");
        var dark = 1;
        DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
        GetClientRect(Handle, out var client);
        _surface = new CompositionSurface(gfx, Handle, Math.Max(1, client.Width), Math.Max(1, client.Height));
    }

    private const float ToolbarH = 46;
    private float Scale => GetDpiForWindow(Handle) / 96f;

    /// <summary>Loads <paramref name="file"/> and shows the window sized to fit the picture on screen.</summary>
    public void Open(string file)
    {
        try
        {
            var bytes = File.ReadAllBytes(file);
            var bmp = SKBitmap.Decode(bytes);
            if (bmp == null) { _hub.Open(file); return; } // can't read it: fall back to the default app
            _base?.Dispose();
            _base = bmp;
            _file = file;
            _shapes.Clear();
            _drawing = _typing = null;
            _status = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Mark up: can't open {file}: {ex.Message}");
            _hub.Open(file);
            return;
        }

        var primary = Monitors.All().FirstOrDefault(m => m.IsPrimary) ?? Monitors.All()[0];
        var s = primary.Scale;
        var b = primary.Bounds;
        // Fit the picture into ~82% of the screen, in DIP, plus room for the toolbar.
        var maxW = b.Width * 0.82f / s;
        var maxH = b.Height * 0.82f / s - ToolbarH;
        var fit = Math.Min(Math.Min(maxW / _base.Width, maxH / _base.Height), 1f);
        int w = (int)((_base.Width * fit + 24) * s), h = (int)((_base.Height * fit + ToolbarH + 18) * s);
        SetWindowPos(Handle, IntPtr.Zero, b.Left + (b.Width - w) / 2, b.Top + (b.Height - h) / 2, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
        GetClientRect(Handle, out var client);
        _surface.Resize(Math.Max(1, client.Width), Math.Max(1, client.Height));
        ShowWindow(Handle, 5 /* SW_SHOW */);
        SetForegroundWindow(Handle);
        Dirty = true;
    }

    public bool Due(double now) => Visible && (Dirty || _drawing != null || (_typing != null && now - LastFrame > 0.4) || now - LastFrame > 1);

    public void ReplaceSurface(GraphicsDevice gfx)
    {
        _gfx = gfx;
        _surface.Dispose();
        GetClientRect(Handle, out var client);
        _surface = new CompositionSurface(gfx, Handle, Math.Max(1, client.Width), Math.Max(1, client.Height));
        Dirty = true;
    }

    // ───────────────────────────── drawing ─────────────────────────────

    /// <summary>Works out where the picture sits in the client area (in DIP), and the toolbar rect.</summary>
    private (SKRect Toolbar, SKRect Image) Layout()
    {
        GetClientRect(Handle, out var client);
        var scale = Scale;
        var size = new SKSize(client.Width / scale, client.Height / scale);
        var toolbar = new SKRect(0, 0, size.Width, ToolbarH);
        var area = new SKRect(12, ToolbarH + 6, size.Width - 12, size.Height - 10);
        if (_base == null) return (toolbar, area);
        var fit = Math.Min(Math.Min(area.Width / _base.Width, area.Height / _base.Height), 1f);
        _viewScale = fit;
        var iw = _base.Width * fit;
        var ih = _base.Height * fit;
        var image = new SKRect(area.MidX - iw / 2, area.Top, area.MidX + iw / 2, area.Top + ih);
        _view = image;
        return (toolbar, image);
    }

    public bool Render(double now, double dt)
    {
        var canvas = _surface.BeginFrame();
        canvas.RestoreToCount(1);
        canvas.ResetMatrix();
        var scale = Scale;
        canvas.Scale(scale);
        _ui.Begin(canvas, PointerInside(scale));
        var theme = _hub.Theme;
        canvas.Clear(new SKColor(0x14, 0x14, 0x17));
        try
        {
            var (toolbar, image) = Layout();
            DrawToolbar(toolbar);
            if (_base != null)
            {
                // A soft shadow under the picture, then the picture, then the annotations on top.
                using (var shadow = new SKPaint { Color = SKColors.Black.WithAlpha(90), IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 10) })
                    canvas.DrawRoundRect(SKRect.Inflate(image, 2, 2), 8, 8, shadow);
                using (var checker = new SKPaint { Color = SKColors.White })
                    canvas.DrawRect(image, checker);
                canvas.DrawBitmap(_base, new SKRect(0, 0, _base.Width, _base.Height), image);
                canvas.Save();
                canvas.ClipRect(image);
                foreach (var shape in _shapes) DrawShape(canvas, shape, image);
                if (_drawing != null) DrawShape(canvas, _drawing, image);
                canvas.Restore();
            }
            if (_status.Length > 0 && DateTime.Now < _statusUntil)
                _ui.Text(_status, image.MidX, toolbar.Bottom + 18, image.Width, 12, Theme.Weight.SemiBold, theme.Accent, SKTextAlign.Center);
        }
        catch (Exception ex)
        {
            Log.Write($"Mark up failed to draw: {ex}");
        }
        canvas.Flush();
        LastFrame = now;
        Dirty = false;
        return _surface.Present();
    }

    private void DrawToolbar(SKRect bar)
    {
        var theme = _hub.Theme;
        _ui.Fill(bar, 0, new SKColor(0x1D, 0x1D, 0x22));
        float x = 10;
        var y = bar.MidY;
        void ToolChip(string label, Tool tool)
        {
            var r = _ui.Chip(x, y - 11, label, () => { _tool = tool; Commit(); }, on: _tool == tool, size: 11.5f);
            x = r.Right + 5;
        }
        ToolChip("Arrow", Tool.Arrow);
        ToolChip("Box", Tool.Box);
        ToolChip("Circle", Tool.Circle);
        ToolChip("Pen", Tool.Pen);
        ToolChip("Mark", Tool.Highlight);
        ToolChip("Text", Tool.Text);
        ToolChip("Crop", Tool.Crop);

        x += 6;
        foreach (var c in Palette)
        {
            var sel = c == _color;
            _ui.Circle(x + 8, y, sel ? 9 : 7, c);
            if (sel) { using var ring = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f }; _ui.Canvas.DrawCircle(x + 8, y, 11, ring); }
            var chosen = c;
            _ui.Hit(new SKRect(x - 2, y - 11, x + 18, y + 11), () => _color = chosen);
            x += 22;
        }

        // Stroke width − n +
        x += 6;
        _ui.IconButton(x, y, 8, Minus, () => _stroke = Math.Max(1, _stroke - 1));
        _ui.Text(((int)_stroke).ToString(), x + 18, y + 4, 18, 11.5f, Theme.Weight.SemiBold, theme.Text, SKTextAlign.Center);
        _ui.IconButton(x + 34, y, 8, Icons.Plus, () => _stroke = Math.Min(24, _stroke + 1));
        x += 50;

        // Right side: Undo, Save, Copy, Done.
        var rx = bar.Right - 10;
        rx = Right(rx, "Done", () => { Commit(); ShowWindow(Handle, 0 /* SW_HIDE */); }, accent: true);
        rx = Right(rx, "Copy", CopyToClipboard);
        rx = Right(rx, "Save", Save);
        Right(rx, "Undo", Undo);
        return;

        float Right(float right, string label, Action act, bool accent = false)
        {
            var w = _ui.Measure(label, 11.5f, Theme.Weight.SemiBold) + 22;
            _ui.Chip(right - w, y - 11, label, act, on: accent, size: 11.5f);
            return right - w - 6;
        }
    }

    private void DrawShape(SKCanvas canvas, Shape shape, SKRect image)
    {
        if (shape.Points.Count == 0) return;
        var stroke = shape.Stroke * _viewScale;
        using var paint = new SKPaint { Color = shape.Color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1, stroke), StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        SKPoint P(SKPoint p) => new(image.Left + p.X * _viewScale, image.Top + p.Y * _viewScale);
        switch (shape.Tool)
        {
            case Tool.Pen:
                using (var path = new SKPath())
                {
                    path.MoveTo(P(shape.Points[0]));
                    for (var i = 1; i < shape.Points.Count; i++) path.LineTo(P(shape.Points[i]));
                    canvas.DrawPath(path, paint);
                }
                break;
            case Tool.Highlight:
                if (shape.Points.Count >= 2)
                {
                    paint.Color = shape.Color.WithAlpha(90);
                    paint.StrokeWidth = Math.Max(10, stroke * 4);
                    canvas.DrawLine(P(shape.Points[0]), P(shape.Points[^1]), paint);
                }
                break;
            case Tool.Box when shape.Points.Count >= 2:
                canvas.DrawRect(Rect(P(shape.Points[0]), P(shape.Points[1])), paint);
                break;
            case Tool.Circle when shape.Points.Count >= 2:
                canvas.DrawOval(Rect(P(shape.Points[0]), P(shape.Points[1])), paint);
                break;
            case Tool.Arrow when shape.Points.Count >= 2:
                Arrow(canvas, P(shape.Points[0]), P(shape.Points[1]), paint);
                break;
            case Tool.Text:
                var editing = shape == _typing;
                var text = shape.Text + (editing && (DateTime.Now.Millisecond < 500) ? "|" : "");
                using (var font = _hub.Theme.Font(Math.Max(12, shape.Stroke * 5) * _viewScale, Theme.Weight.SemiBold, text))
                using (var fill = new SKPaint { Color = shape.Color, IsAntialias = true })
                {
                    if (text.Length > 0) canvas.DrawText(text, P(shape.Points[0]).X, P(shape.Points[0]).Y, font, fill);
                    else if (editing) canvas.DrawText("|", P(shape.Points[0]).X, P(shape.Points[0]).Y, font, fill);
                }
                break;
        }
    }

    private static SKRect Rect(SKPoint a, SKPoint b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    private static void Arrow(SKCanvas canvas, SKPoint from, SKPoint to, SKPaint paint)
    {
        canvas.DrawLine(from, to, paint);
        var angle = (float)Math.Atan2(to.Y - from.Y, to.X - from.X);
        var head = Math.Max(10, paint.StrokeWidth * 3);
        for (var s = -1; s <= 1; s += 2)
        {
            var a = angle + (float)Math.PI + s * 0.5f;
            canvas.DrawLine(to, new SKPoint(to.X + (float)Math.Cos(a) * head, to.Y + (float)Math.Sin(a) * head), paint);
        }
    }

    private static void Minus(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        using var p = new SKPaint { Color = color, IsAntialias = true, StrokeWidth = 1.6f, StrokeCap = SKStrokeCap.Round };
        c.DrawLine(cx - size / 2, cy, cx + size / 2, cy, p);
    }

    // ───────────────────────────── actions ─────────────────────────────

    private void Commit()
    {
        // A text shape with no text is dropped; the drawing in progress is already committed on mouse-up.
        if (_typing is { Text.Length: 0 }) _shapes.Remove(_typing);
        _typing = null;
    }

    private void Undo()
    {
        Commit();
        if (_shapes.Count > 0) _shapes.RemoveAt(_shapes.Count - 1);
        Dirty = true;
    }

    /// <summary>Paints the picture and all the mark-up onto one bitmap.</summary>
    private SKBitmap? Flatten()
    {
        if (_base == null) return null;
        var bmp = new SKBitmap(_base.Width, _base.Height);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.DrawBitmap(_base, 0, 0);
            var full = new SKRect(0, 0, _base.Width, _base.Height);
            var savedScale = _viewScale;
            _viewScale = 1; // draw the shapes at full image resolution
            foreach (var shape in _shapes) DrawShape(canvas, shape, full);
            _viewScale = savedScale;
        }
        return bmp;
    }

    private void Save()
    {
        Commit();
        try
        {
            using var flat = Flatten();
            if (flat == null) return;
            using var image = SKImage.FromBitmap(flat);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(_file, data.ToArray());
            // Reload so the saved file is the new base and the shapes are now baked in.
            _base?.Dispose();
            _base = SKBitmap.Decode(_file);
            _shapes.Clear();
            Flash("Saved");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Flash(ex.Message);
        }
    }

    private void CopyToClipboard()
    {
        Commit();
        using var flat = Flatten();
        if (flat == null) return;
        using var image = SKImage.FromBitmap(flat);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        if (ClipboardIO.Write(Handle, new ClipboardContent(null, null, null, data.ToArray()))) Flash("Copied");
    }

    private void Flash(string text) { _status = text; _statusUntil = DateTime.Now.AddSeconds(2); Dirty = true; }

    // ───────────────────────────── input ─────────────────────────────

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

    /// <summary>A DIP point mapped into image pixels, clamped to the picture.</summary>
    private SKPoint ToImage(SKPoint dip) => _base == null ? dip : new SKPoint(
        Math.Clamp((dip.X - _view.Left) / _viewScale, 0, _base.Width),
        Math.Clamp((dip.Y - _view.Top) / _viewScale, 0, _base.Height));

    protected override IntPtr? OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_CLOSE:
                Commit();
                ShowWindow(Handle, 0 /* SW_HIDE */);
                return IntPtr.Zero;
            case WM_SIZE:
                if (((long)wParam) != 1 /* SIZE_MINIMIZED */)
                {
                    _surface.Resize(Math.Max(1, (int)((long)lParam & 0xFFFF)), Math.Max(1, (int)(((long)lParam >> 16) & 0xFFFF)));
                    Dirty = true;
                }
                return IntPtr.Zero;
            case WM_DPICHANGED:
                var suggested = System.Runtime.InteropServices.Marshal.PtrToStructure<RECT>(lParam);
                SetWindowPos(Handle, IntPtr.Zero, suggested.Left, suggested.Top, suggested.Width, suggested.Height, SWP_NOZORDER | SWP_NOACTIVATE);
                Dirty = true;
                return IntPtr.Zero;
            case WM_LBUTTONDOWN:
                var down = Dip(lParam);
                if (down.Y < ToolbarH) { _toolbarPress = true; return IntPtr.Zero; }
                if (_base == null) return IntPtr.Zero;
                Commit();
                var at = ToImage(down);
                if (_tool == Tool.Crop) { _drawing = new Shape { Tool = Tool.Box, Color = SKColors.White, Stroke = 1 }; _drawing.Points.Add(at); _drawing.Points.Add(at); SetCapture(Handle); }
                else if (_tool == Tool.Text)
                {
                    _typing = new Shape { Tool = Tool.Text, Color = _color, Stroke = _stroke };
                    _typing.Points.Add(at);
                    _shapes.Add(_typing);
                }
                else
                {
                    _drawing = new Shape { Tool = _tool, Color = _color, Stroke = _stroke };
                    _drawing.Points.Add(at);
                    _drawing.Points.Add(at);
                    SetCapture(Handle);
                }
                Dirty = true;
                return IntPtr.Zero;
            case WM_MOUSEMOVE:
                if (_drawing != null)
                {
                    var p = ToImage(Dip(lParam));
                    if (_drawing.Tool == Tool.Pen) _drawing.Points.Add(p);
                    else _drawing.Points[^1] = p;
                    Dirty = true;
                }
                return IntPtr.Zero;
            case WM_LBUTTONUP:
                if (_toolbarPress) { _toolbarPress = false; _ui.Click(Dip(lParam)); Dirty = true; return IntPtr.Zero; }
                if (_drawing != null)
                {
                    ReleaseCapture();
                    var d = _drawing;
                    _drawing = null;
                    if (_tool == Tool.Crop) ApplyCrop(d);
                    else if (Big(d)) _shapes.Add(d);
                    Dirty = true;
                }
                return IntPtr.Zero;
            case WM_CHAR when _typing != null:
                var ch = (char)(long)wParam;
                if (ch == '\b') { if (_typing.Text.Length > 0) _typing.Text = _typing.Text[..^1]; }
                else if (ch == '\r' || ch == '\n') Commit();
                else if (!char.IsControl(ch)) _typing.Text += ch;
                Dirty = true;
                return IntPtr.Zero;
            case WM_KEYDOWN when (int)(long)wParam == 0x1B: // Esc
                Commit();
                Dirty = true;
                return IntPtr.Zero;
            case WM_KEYDOWN when (int)(long)wParam == 0x5A && (GetKeyState(0x11) & 0x8000) != 0: // Ctrl+Z
                Undo();
                return IntPtr.Zero;
            case WM_ERASEBKGND:
                return new IntPtr(1);
        }
        return null;
    }

    private static bool Big(Shape s) => s.Tool == Tool.Pen ? s.Points.Count > 2
        : s.Points.Count >= 2 && (Math.Abs(s.Points[0].X - s.Points[1].X) > 3 || Math.Abs(s.Points[0].Y - s.Points[1].Y) > 3);

    private void ApplyCrop(Shape rect)
    {
        if (_base == null || rect.Points.Count < 2) return;
        var r = Rect(rect.Points[0], rect.Points[1]);
        var cropped = new SKRectI((int)r.Left, (int)r.Top, (int)r.Right, (int)r.Bottom);
        if (cropped.Width < 8 || cropped.Height < 8) return;
        // Bake the current mark-up, then crop to the chosen box.
        using var flat = Flatten();
        if (flat == null) return;
        var outBmp = new SKBitmap(cropped.Width, cropped.Height);
        using (var canvas = new SKCanvas(outBmp))
            canvas.DrawBitmap(flat, new SKRect(cropped.Left, cropped.Top, cropped.Right, cropped.Bottom), new SKRect(0, 0, cropped.Width, cropped.Height));
        _base.Dispose();
        _base = outBmp;
        _shapes.Clear();
        _tool = Tool.Arrow;
        Flash("Cropped");
    }

    public override void Dispose()
    {
        _base?.Dispose();
        _surface.Dispose();
        base.Dispose();
    }

    private const uint WM_SIZE = 0x5, WM_ERASEBKGND = 0x14;
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint SWP_NOZORDER = 0x4;

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern short GetKeyState(int vk);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern bool SetWindowTextW(IntPtr hwnd, string text);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
