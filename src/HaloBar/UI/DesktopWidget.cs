using HaloBar.Platform;
using HaloBar.Rendering;
using HaloBar.Services;
using HaloBar.UI.Tiles;
using SkiaSharp;

namespace HaloBar.UI;

/// <summary>
/// One Home tile pinned to the desktop: its own window, drawn with the same tile code as on the
/// island, on a card in the island's colours. Drag it anywhere; right-click to make it wide or
/// narrow or take it off. Window thread only.
/// </summary>
internal sealed class DesktopWidget : IDisposable
{
    public const float NarrowWidth = 140, WideWidth = 290, TileHeight = 140; // DIPs

    private readonly Hub _hub;
    private GraphicsDevice _gfx;
    private readonly Tile _tile;
    private readonly Ui _ui;
    private TextField? _focused;
    private CompositionSurface _surface;
    /// <summary>Windows' own blur of what's behind the card (Windows does the work, so many widgets cost nothing).</summary>
    private AcrylicBackdrop? _backdrop;
    private SKCanvas? _canvas;
    private SKBitmap? _canvasBitmap;
    private (int X, int Y)? _lastCursor;
    private bool _failed;

    public DesktopWidgetPlace Place { get; }
    public WidgetWindow Window { get; } = new();
    public double LastFrame { get; set; } = double.NegativeInfinity;

    public DesktopWidget(Hub hub, GraphicsDevice gfx, Tile tile, DesktopWidgetPlace place)
    {
        _hub = hub;
        _gfx = gfx;
        _tile = tile;
        Place = place;
        _ui = new Ui(hub.Theme, Focus, () => _focused);
        Window.Place(place.X, place.Y, Pixels(Width), Pixels(TileHeight));
        _surface = new CompositionSurface(gfx, Window.Handle, Pixels(Width), Pixels(TileHeight));
        _backdrop = AcrylicBackdrop.Create(Window.Handle);

        Window.Clicked += (x, y) =>
        {
            var typing = _focused;
            var hit = _ui.Click(new SKPoint(x / Window.Scale, y / Window.Scale));
            // A click that didn't land on this text box ends typing into it.
            if (typing != null && _focused == typing && !hit) EndTyping();
            Dirty = true;
        };
        Window.Moved += (x, y) =>
        {
            (Place.X, Place.Y) = (x, y);
            _hub.Settings.Save();
        };
        Window.RightClicked += () => TrayIcon.Popup(Window.Handle,
        [
            new(Place.Wide ? "Make narrow" : "Make wide", () =>
            {
                Place.Wide = !Place.Wide;
                Resize();
                _hub.Settings.Save();
            }),
            TrayMenuItem.Separator,
            new("Remove from desktop", () =>
            {
                _hub.Settings.DesktopWidgets.Remove(Place);
                _hub.Settings.Save();
            }),
        ]);
        Window.CharTyped += c =>
        {
            if (_focused == null || char.IsControl(c) || _focused.CaptureKey != null) return;
            _focused.Insert(c.ToString());
            Dirty = true;
        };
        Window.KeyPressed += Key;
        Window.FocusLost += EndTyping;
        Window.DpiChanged += Resize;
    }

    public string TileId => _tile.Id;
    /// <summary>The last frame drawn.</summary>
    public SKBitmap Picture => _surface.Bitmap;
    private float Width => Place.Wide ? WideWidth : NarrowWidth;
    private int Pixels(float dip) => (int)Math.Ceiling(dip * Window.Scale);

    /// <summary>Something changed that the widget should show now rather than at its next tick.</summary>
    public bool Dirty { get; set; } = true;

    /// <summary>Whether a frame is due: the tile's own pace, typing, or the pointer moving over it.</summary>
    public bool Due(double now)
    {
        var cursor = Window.Cursor;
        if (cursor != _lastCursor)
        {
            _lastCursor = cursor;
            Dirty = true;
        }
        if (_tile.Fresh)
        {
            _tile.Fresh = false;
            Dirty = true;
        }
        var interval = _focused != null ? Math.Min(_tile.FrameInterval, 0.25) : _tile.FrameInterval;
        return Dirty || now - LastFrame >= interval;
    }

    public void Resize()
    {
        Window.Place(Place.X, Place.Y, Pixels(Width), Pixels(TileHeight));
        _surface.Resize(Pixels(Width), Pixels(TileHeight));
        Dirty = true;
    }

    public void ReplaceSurface(GraphicsDevice gfx)
    {
        _gfx = gfx;
        DropCanvas();
        _surface.Dispose();
        _surface = new CompositionSurface(_gfx, Window.Handle, Pixels(Width), Pixels(TileHeight));
        Dirty = true;
    }

    /// <summary>Draws and presents one frame. Returns false when the GPU device was lost.</summary>
    public bool Render(double now)
    {
        if (!ReferenceEquals(_canvasBitmap, _surface.Bitmap))
        {
            DropCanvas();
            _canvasBitmap = _surface.Bitmap;
            _canvas = new SKCanvas(_canvasBitmap);
        }
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var canvas = _canvas!;
        canvas.RestoreToCount(1);
        canvas.ResetMatrix();
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(Window.Scale);
        var theme = _hub.Theme;
        var card = new SKRect(0, 0, Width, TileHeight);
        var cursor = _lastCursor is { } c ? new SKPoint(c.X / Window.Scale, c.Y / Window.Scale) : (SKPoint?)null;
        _ui.Begin(canvas, cursor);
        try
        {
            // The island's own colour, a touch see-through, with a hairline edge so it reads on any wallpaper.
            // Glass: Windows' blur behind (the backdrop), a tint of the island's colour over it so
            // text reads, light catching the top edge, and a rim that fades down the sides.
            const float radius = 20;
            _backdrop?.Update(true, 0, 0, Pixels(Width), Pixels(TileHeight), radius * Window.Scale);
            var light = theme.DarkText && !theme.Surface.IsLight;
            var tint = light ? new SKColor(0xF4, 0xF4, 0xF7) : theme.Background;
            using (var fill = new SKPaint { IsAntialias = true })
            {
                fill.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, card.Height),
                    [tint.WithAlpha((byte)(_backdrop != null ? 150 : 236)), tint.WithAlpha((byte)(_backdrop != null ? 190 : 236))], SKShaderTileMode.Clamp);
                canvas.DrawRoundRect(card, radius, radius, fill);
                // A soft sheen across the top third.
                fill.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, card.Height * 0.4f),
                    [SKColors.White.WithAlpha(light ? (byte)50 : (byte)22), SKColors.White.WithAlpha(0)], SKShaderTileMode.Clamp);
                canvas.DrawRoundRect(card, radius, radius, fill);
            }
            using (var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f })
            {
                rim.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, card.Height),
                    [SKColors.White.WithAlpha(110), SKColors.White.WithAlpha(18), SKColors.White.WithAlpha(40)], [0, 0.55f, 1], SKShaderTileMode.Clamp);
                canvas.DrawRoundRect(Ui.Inflate(card, -0.6f), radius, radius, rim);
            }
            canvas.Save();
            canvas.ClipRoundRect(new SKRoundRect(card, 20), SKClipOperation.Intersect, true);
            _tile.Draw(_ui, card);
            _failed = false;
        }
        catch (Exception ex)
        {
            if (!_failed) Log.Write($"Desktop widget {_tile.Id} failed to draw: {ex}");
            _failed = true;
        }
        canvas.RestoreToCount(1);
        canvas.Flush();
        LastFrame = now;
        Dirty = false;
        var drawn = timer.Elapsed.TotalMilliseconds;
        var ok = _surface.Present();
        Timings.Note(_tile.Id, drawn, timer.Elapsed.TotalMilliseconds - drawn);
        return ok;
    }

    /// <summary>Where widget frames spend their time, for the performance line in the log.</summary>
    public static class Timings
    {
        private static readonly Dictionary<string, (double Draw, double Present, int Count)> Totals = [];
        public static void Note(string id, double draw, double present)
        {
            var t = Totals.GetValueOrDefault(id);
            Totals[id] = (t.Draw + draw, t.Present + present, t.Count + 1);
        }
        /// <summary>"drawing 3.1, to the GPU 0.4; slowest: stats 9.0, month 6.2" and starts over.</summary>
        public static string Take()
        {
            if (Totals.Count == 0) return "";
            var n = Totals.Values.Sum(t => t.Count);
            var text = $"widgets: drawing {Totals.Values.Sum(t => t.Draw) / n:0.0}, to the GPU {Totals.Values.Sum(t => t.Present) / n:0.0}; slowest: " +
                       string.Join(", ", Totals.OrderByDescending(t => t.Value.Draw / t.Value.Count).Take(4).Select(t => $"{t.Key} {t.Value.Draw / t.Value.Count:0.0}"));
            Totals.Clear();
            return text;
        }
    }

    // ───────────────────────────── typing ─────────────────────────────

    private void Focus(TextField field)
    {
        _focused = field;
        field.Caret = field.Text.Length;
        Window.BeginTyping();
        Dirty = true;
    }

    private void EndTyping()
    {
        if (_focused == null) return;
        var field = _focused;
        _focused = null;
        Window.EndTyping();
        field.Ended?.Invoke();
        Dirty = true;
    }

    private void Key(int vk)
    {
        if (_focused == null) return;
        if (_focused.CaptureKey is { } capture)
        {
            if (capture(vk)) EndTyping();
        }
        else if (vk == 0x0D) // Enter
        {
            var field = _focused;
            EndTyping();
            field.Submit(field.Text);
        }
        else if (vk == 0x1B) EndTyping(); // Esc
        else _focused.Key(vk);
        Dirty = true;
    }

    private void DropCanvas()
    {
        _canvas?.Dispose();
        _canvas = null;
        _canvasBitmap = null;
    }

    public void Dispose()
    {
        _backdrop?.Dispose();
        DropCanvas();
        _surface.Dispose();
        Window.Dispose();
    }
}
