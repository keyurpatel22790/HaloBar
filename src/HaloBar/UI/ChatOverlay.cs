using HaloBar.Platform;
using HaloBar.Rendering;
using HaloBar.Services;
using SkiaSharp;

namespace HaloBar.UI;

/// <summary>
/// The stream's chat in a window of its own, above other windows, in the colours chosen in
/// Settings (background, opacity, text colour and size). Drag it anywhere; its place is saved in
/// <see cref="AppSettings.ChatBounds"/>. Right-click to make it smaller, bigger or close it.
/// Window thread only.
/// </summary>
internal sealed class ChatOverlay : IDisposable
{
    private static readonly (float W, float H)[] Sizes = [(300, 360), (360, 460), (440, 560)];

    private readonly Hub _hub;
    private readonly Ui _ui;
    private GraphicsDevice _gfx;
    private CompositionSurface _surface;
    private SKCanvas? _canvas;
    private SKBitmap? _canvasBitmap;
    private int _size = 1;
    private int _seen = -1;

    public WidgetWindow Window { get; } = new() { AlwaysOnTop = true };
    public double LastFrame { get; set; } = double.NegativeInfinity;

    public ChatOverlay(Hub hub, GraphicsDevice gfx)
    {
        _hub = hub;
        _gfx = gfx;
        _ui = new Ui(hub.Theme, _ => { }, () => null);
        var (x, y) = Restore();
        Window.Place(x, y, Pixels(Size.W), Pixels(Size.H));
        Window.KeepOnTop();
        _surface = new CompositionSurface(gfx, Window.Handle, Pixels(Size.W), Pixels(Size.H));
        Window.Moved += (_, _) => Remember();
        Window.RightClicked += () => TrayIcon.Popup(Window.Handle,
        [
            new("Smaller", () => Resize(_size - 1)),
            new("Bigger", () => Resize(_size + 1)),
            TrayMenuItem.Separator,
            new("Close the chat overlay", () =>
            {
                _hub.Settings.ChatOverlay = false;
                _hub.Settings.Save();
            }),
        ]);
        Window.DpiChanged += () => Resize(_size);
    }

    private (float W, float H) Size => Sizes[_size];
    private int Pixels(float dip) => (int)Math.Ceiling(dip * Window.Scale);

    /// <summary>New messages, or a change in Settings, since the last frame.</summary>
    public bool Due(double now) => _hub.Chat.Messages.Count != _seen || now - LastFrame >= 1;

    /// <summary>The saved place and size, or the left side of the primary screen.</summary>
    private (int X, int Y) Restore()
    {
        var parts = _hub.Settings.ChatBounds.Split(',');
        if (parts.Length == 3 && int.TryParse(parts[0], out var x) && int.TryParse(parts[1], out var y) && int.TryParse(parts[2], out var size))
        {
            _size = Math.Clamp(size, 0, Sizes.Length - 1);
            if (Monitors.All().Any(m => x + 40 > m.Bounds.Left && x < m.Bounds.Right - 40 && y >= m.Bounds.Top && y < m.Bounds.Bottom - 60))
                return (x, y);
        }
        var primary = Monitors.All().FirstOrDefault(m => m.IsPrimary) ?? Monitors.All()[0];
        return (primary.Bounds.Left + (int)(24 * primary.Scale), primary.Bounds.Top + (int)(120 * primary.Scale));
    }

    private void Remember()
    {
        var r = Window.Rect;
        _hub.Settings.ChatBounds = $"{r.Left},{r.Top},{_size}";
        _hub.Settings.Save();
    }

    private void Resize(int size)
    {
        _size = Math.Clamp(size, 0, Sizes.Length - 1);
        var r = Window.Rect;
        Window.Place(r.Left, r.Top, Pixels(Size.W), Pixels(Size.H));
        Window.KeepOnTop();
        _surface.Resize(Pixels(Size.W), Pixels(Size.H));
        _seen = -1;
        Remember();
    }

    public void ReplaceSurface(GraphicsDevice gfx)
    {
        _gfx = gfx;
        DropCanvas();
        _surface.Dispose();
        _surface = new CompositionSurface(_gfx, Window.Handle, Pixels(Size.W), Pixels(Size.H));
        _seen = -1;
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
        var s = _hub.Settings;
        var canvas = _canvas!;
        canvas.ResetMatrix();
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(Window.Scale);
        var card = new SKRect(0, 0, Size.W, Size.H);
        var background = SKColor.TryParse(s.ChatBackground, out var b) ? b : new SKColor(0x0B, 0x0B, 0x0C);
        var text = SKColor.TryParse(s.ChatTextColor, out var t) ? t : new SKColor(0xF5, 0xF5, 0xF7);
        var size = Math.Clamp(s.ChatTextSize, 10, 24);
        using (var fill = new SKPaint { Color = background.WithAlpha((byte)(Math.Clamp(s.ChatOpacity, 0, 100) * 255 / 100)), IsAntialias = true })
            canvas.DrawRoundRect(card, 16, 16, fill);

        _ui.Begin(canvas, null);
        var messages = _hub.Chat.Messages;
        if (messages.Count == 0)
        {
            _ui.Text(_hub.Chat.Channel.Length == 0 ? "Choose a channel in the Chat tab" : "Waiting for messages…",
                card.MidX, card.MidY, card.Width - 32, size - 1, Theme.Weight.Regular, text.WithAlpha(150), SKTextAlign.Center);
        }
        else
        {
            canvas.Save();
            canvas.ClipRoundRect(new SKRoundRect(Ui.Inflate(card, -6), 12), SKClipOperation.Intersect, true);
            // Newest at the bottom; a long message wraps, the name on its first line.
            var y = card.Bottom - 12;
            var lineHeight = size * 1.35f;
            var width = card.Width - 28;
            foreach (var m in messages.Reverse())
            {
                var nameWidth = _ui.Measure(m.Name, size, Theme.Weight.Bold) + size * 0.5f;
                var lines = Wrap(m.Text, width - nameWidth, width, size, 4);
                y -= lines.Count * lineHeight;
                if (y < card.Top + 4) break;
                for (var i = 0; i < lines.Count; i++)
                {
                    var baseline = y + (i + 1) * lineHeight - lineHeight * 0.3f;
                    if (i == 0) _ui.Text(m.Name, 14, baseline, nameWidth, size, Theme.Weight.Bold, m.Color);
                    var x = i == 0 ? 14 + nameWidth : 14;
                    _ui.Text(lines[i], x, baseline, card.Width - 14 - x, size, Theme.Weight.Regular, text);
                }
                y -= 6;
            }
            canvas.Restore();
        }
        canvas.Flush();
        _seen = messages.Count;
        LastFrame = now;
        return _surface.Present();
    }

    /// <summary>Words into lines: the first line is narrower (the name sits before it).</summary>
    private List<string> Wrap(string text, float first, float rest, float size, int maxLines)
    {
        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && _ui.Measure(candidate, size) > (lines.Count == 0 ? first : rest))
            {
                lines.Add(line);
                if (lines.Count == maxLines) return lines;
                line = word;
            }
            else line = candidate;
        }
        if (line.Length > 0 || lines.Count == 0) lines.Add(line);
        return lines;
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
        _surface.Dispose();
        Window.Dispose();
    }
}
