using HaloBar.Rendering;
using HaloBar.Services;
using SkiaSharp;

namespace HaloBar.UI;

internal enum IslandMode { Collapsed, Hud, Notice, Expanded }
internal enum IslandTab { Media, Clipboard, Notices, Style }

/// <summary>
/// The island: decides what it should show, animates its shape between states, draws it,
/// and maps clicks back to actions. All coordinates are DIPs relative to the overlay window;
/// the caller scales the canvas for the monitor's DPI. Runs only on the window thread.
/// </summary>
internal sealed class IslandView
{
    public const float CanvasWidth = 640, CanvasHeight = 260;
    private const float TopGap = 6;
    private const float ExpandedWidth = 560, ExpandedHeight = 200;
    private const float CardWidth = 128, CardGap = 10;
    private const float NoticeRowHeight = 46;
    private static readonly TimeSpan HudTime = TimeSpan.FromSeconds(1.6);
    private static readonly TimeSpan LeaveDelay = TimeSpan.FromMilliseconds(400);

    private readonly Theme _theme;
    private readonly MediaService _media;
    private readonly VolumeService _volume;
    private readonly ClipboardService _clipboard;
    private readonly List<Hit> _hits = [];
    private readonly Queue<Notice> _pending = new();
    private readonly NoticeHistory _history;
    private readonly float[] _bars = new float[4];

    private Spring _width = new(160), _height = new(34), _radius = new(17);
    private Spring _content = new(1, 520, 1);
    private Spring _visible = new(1, 260, 1);
    private Spring _clipScroll = new(0, 300, 1);
    private Spring _noticeScroll = new(0, 300, 1);

    private IslandMode _mode = IslandMode.Collapsed;
    private IslandTab _tab = IslandTab.Media;
    private bool _expanded, _pinned;
    private DateTime? _hoverSince, _leftSince;
    private DateTime _hudUntil;
    private Notice? _notice;
    private DateTime _noticeUntil;
    private int _unseenNotices;
    private ClipEntry? _flashEntry;
    private DateTime _flashUntil;
    private SKPoint? _cursor;
    private double _clock;

    public bool Hidden { get; set; }
    public TimeSpan HoverDelay { get; set; } = TimeSpan.FromMilliseconds(150);
    public TimeSpan NoticeTime { get; set; } = TimeSpan.FromSeconds(5);
    public string NotificationStatus { get; set; } = "";
    public int ApiPort { get; set; }

    private sealed record Hit(SKRect Rect, Action Action);

    public IslandView(Theme theme, MediaService media, VolumeService volume, ClipboardService clipboard, NoticeHistory history)
    {
        _theme = theme;
        _media = media;
        _volume = volume;
        _clipboard = clipboard;
        _history = history;
    }

    /// <summary>The island's current outline.</summary>
    public SKRect Bounds
    {
        get
        {
            var w = (float)_width.Value;
            var x = (CanvasWidth - w) / 2;
            return new SKRect(x, TopGap, x + w, TopGap + (float)_height.Value);
        }
    }

    public IslandMode Mode => _mode;

    public void ShowVolume() => _hudUntil = DateTime.Now + HudTime;

    public void PushNotice(Notice notice)
    {
        if (_expanded) _unseenNotices++;
        else _pending.Enqueue(notice);
    }

    public void ToggleExpanded()
    {
        if (_expanded) Collapse();
        else Expand(pinned: true);
    }

    private void Expand(bool pinned)
    {
        _expanded = true;
        _pinned = pinned;
        _leftSince = null;
        if (_notice != null || _unseenNotices > 0) _tab = IslandTab.Notices;
        else if (_tab == IslandTab.Notices && _history.Items.Count == 0) _tab = IslandTab.Media;
        _notice = null;
        _pending.Clear();
        if (_tab == IslandTab.Notices) _unseenNotices = 0;
    }

    private void Collapse()
    {
        _expanded = false;
        _pinned = false;
        _hoverSince = null;
    }

    /// <summary>
    /// Advances animations and timers. <paramref name="cursor"/> is the pointer in DIPs, or null
    /// when it is nowhere near the window. Returns how soon the next frame is wanted, in seconds.
    /// </summary>
    public double Update(double dt, SKPoint? cursor, DateTime now)
    {
        _clock += dt;
        _cursor = cursor;
        var over = cursor is { } c && Inflate(Bounds, 6).Contains(c);

        if (Hidden && _expanded && !over) Collapse();

        // Hover opens the card; leaving closes it, unless it was opened from the keyboard or tray.
        if (!_expanded && !Hidden)
        {
            if (over)
            {
                _hoverSince ??= now;
                if (now - _hoverSince >= HoverDelay) Expand(pinned: false);
            }
            else _hoverSince = null;
        }
        else if (_expanded)
        {
            if (over)
            {
                _pinned = false;
                _leftSince = null;
            }
            else if (!_pinned)
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
            _noticeUntil = now + NoticeTime;
        }

        var mode = _expanded ? IslandMode.Expanded
            : _notice != null ? IslandMode.Notice
            : now < _hudUntil ? IslandMode.Hud
            : IslandMode.Collapsed;
        if (mode != _mode)
        {
            _mode = mode;
            _content.Snap(0);
            _content.Target = 1;
        }

        var media = CurrentMedia;
        var (w, h, r) = _mode switch
        {
            IslandMode.Expanded => (ExpandedWidth, ExpandedHeight, 30f),
            IslandMode.Notice => (440f, 76f, 28f),
            IslandMode.Hud => (300f, 40f, 20f),
            _ => media != null ? (300f, 34f, 17f) : (168f, 34f, 17f),
        };
        _width.Target = w;
        _height.Target = h;
        _radius.Target = r;
        _visible.Target = Hidden ? 0 : 1;

        _width.Step(dt);
        _height.Step(dt);
        _radius.Step(dt);
        _content.Step(dt);
        _visible.Step(dt);
        _clipScroll.Step(dt);
        _noticeScroll.Step(dt);
        UpdateBars(dt, media?.IsPlaying == true);

        var animating = !(_width.Settled && _height.Settled && _radius.Settled && _content.Settled && _visible.Settled
                          && _clipScroll.Settled && _noticeScroll.Settled);
        if (animating || (_theme.Transitioning && !Hidden)) return 0;
        if (Hidden) return 1;
        var interval = media?.IsPlaying == true && _mode is IslandMode.Collapsed or IslandMode.Expanded ? 1 / 30.0
            : _mode != IslandMode.Collapsed ? 0.25
            : 1;
        // Looks that move by themselves, and the album streaks drifting behind the music card.
        var drifting = _mode == IslandMode.Expanded && _tab == IslandTab.Media && _theme.AlbumCard && media?.Palette != null;
        if (_theme.Animated || drifting) interval = Math.Min(interval, 1 / 30.0);
        return interval;
    }

    private MediaSnapshot? CurrentMedia =>
        _media.Snapshot is { } m && !string.IsNullOrWhiteSpace(m.Title) ? m : null;

    /// <summary>Bars follow the real output level, each with its own wobble so they don't move in lockstep.</summary>
    private void UpdateBars(double dt, bool playing)
    {
        var peak = playing ? Math.Clamp(_volume.Peak * 1.6f, 0f, 1f) : 0f;
        for (var i = 0; i < _bars.Length; i++)
        {
            var wobble = 0.55f + 0.45f * (float)Math.Abs(Math.Sin(_clock * (5.1 + i * 1.7) + i * 1.3));
            var target = playing ? Math.Max(0.18f, peak * wobble) : 0.12f;
            var k = target > _bars[i] ? 0.55f : 0.18f;
            _bars[i] += (target - _bars[i]) * Math.Min(1f, k * (float)(dt * 60));
        }
    }

    public bool Contains(SKPoint p) => !Hidden && Inflate(Bounds, 2).Contains(p);

    public void Click(SKPoint p)
    {
        for (var i = _hits.Count - 1; i >= 0; i--)
        {
            if (!_hits[i].Rect.Contains(p)) continue;
            _hits[i].Action();
            return;
        }
        if (!_expanded && Contains(p)) Expand(pinned: true);
    }

    public void Wheel(int delta)
    {
        var notches = delta / 120f;
        if (_mode == IslandMode.Expanded && _tab == IslandTab.Clipboard)
        {
            var max = Math.Max(0, _clipboard.Entries.Count * (CardWidth + CardGap) - CardGap - (ExpandedWidth - 36));
            _clipScroll.Target = Math.Clamp(_clipScroll.Target - notches * 90, 0, max);
            return;
        }
        if (_mode == IslandMode.Expanded && _tab == IslandTab.Notices)
        {
            var max = Math.Max(0, _history.Items.Count * NoticeRowHeight - 130);
            _noticeScroll.Target = Math.Clamp(_noticeScroll.Target - notches * 46, 0, max);
            return;
        }
        _volume.Nudge(notches * 0.02f);
    }

    // ───────────────────────────── drawing ─────────────────────────────

    public void Draw(SKCanvas canvas)
    {
        _hits.Clear();
        canvas.Clear(SKColors.Transparent);
        var visible = (float)Math.Clamp(_visible.Value, 0, 1);
        if (visible <= 0.01f) return;

        var rect = Bounds;
        var rrect = new SKRoundRect(rect, Math.Min((float)_radius.Value, rect.Height / 2));
        using var layer = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * visible)) };
        canvas.SaveLayer(layer);

        var lift = Math.Clamp((rect.Height - 34) / 120f, 0, 1);
        using (var fill = new SKPaint { Color = _theme.Background, IsAntialias = true })
        {
            if (lift > 0)
                fill.ImageFilter = SKImageFilter.CreateDropShadow(0, 6 * lift, 14 * lift, 14 * lift, SKColors.Black.WithAlpha((byte)(120 * lift)));
            using var gradient = _theme.BackgroundShader(rect);
            fill.Shader = gradient;
            canvas.DrawRoundRect(rrect, fill);
        }
        using (var edge = new SKPaint { Color = _theme.Edge, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 })
        {
            using var ring = _theme.EdgeShader(rect);
            if (ring != null)
            {
                edge.Shader = ring;
                edge.StrokeWidth = 1.6f;
            }
            if (edge.Color.Alpha > 0 || ring != null) canvas.DrawRoundRect(rrect, edge);
        }

        canvas.Save();
        canvas.ClipRoundRect(rrect, SKClipOperation.Intersect, true);
        using var fade = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * Math.Clamp(_content.Value, 0, 1))) };
        canvas.SaveLayer(fade);
        switch (_mode)
        {
            case IslandMode.Collapsed: DrawCollapsed(canvas, rect); break;
            case IslandMode.Hud: DrawHud(canvas, rect); break;
            case IslandMode.Notice: DrawNotice(canvas, rect, _notice!); break;
            case IslandMode.Expanded: DrawExpanded(canvas, rect); break;
        }
        canvas.Restore();
        canvas.Restore();
        canvas.Restore();
    }

    private void DrawCollapsed(SKCanvas c, SKRect r)
    {
        var media = CurrentMedia;
        if (media == null)
        {
            var text = DateTime.Now.ToShortTimeString() + "  ·  " + DateTime.Now.ToString("ddd d");
            using var font = _theme.Font(12.5f, Theme.Weight.SemiBold);
            _theme.DrawLine(c, text, r.MidX, r.MidY + 4.5f, r.Width - 24, font, _theme.Text, SKTextAlign.Center);
            return;
        }
        DrawArtwork(c, media, new SKRect(r.Left + 7, r.MidY - 11, r.Left + 29, r.MidY + 11), 6);
        using var title = _theme.Font(12.5f, Theme.Weight.SemiBold, media.Title);
        _theme.DrawLine(c, media.Title, r.Left + 38, r.MidY + 4.5f, r.Width - 38 - 46, title, _theme.Text);
        DrawBars(c, r.Right - 34, r.MidY, 16, media.IsPlaying ? _theme.Accent : _theme.Faint);
    }

    private void DrawBars(SKCanvas c, float left, float midY, float maxHeight, SKColor color)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        for (var i = 0; i < _bars.Length; i++)
        {
            var h = Math.Max(3, _bars[i] * maxHeight);
            var x = left + i * 5.5f;
            c.DrawRoundRect(new SKRect(x, midY - h / 2, x + 3, midY + h / 2), 1.5f, 1.5f, paint);
        }
    }

    private void DrawHud(SKCanvas c, SKRect r)
    {
        var muted = _volume.Muted;
        var level = _volume.Volume;
        Icons.Speaker(c, r.Left + 24, r.MidY, 20, _theme.Text, level, muted);
        var track = new SKRect(r.Left + 46, r.MidY - 2.5f, r.Right - 58, r.MidY + 2.5f);
        using var paint = new SKPaint { Color = _theme.Faint, IsAntialias = true };
        c.DrawRoundRect(track, 2.5f, 2.5f, paint);
        paint.Color = muted ? _theme.Dim : _theme.Text;
        c.DrawRoundRect(new SKRect(track.Left, track.Top, track.Left + track.Width * level, track.Bottom), 2.5f, 2.5f, paint);
        using var font = _theme.Font(12.5f, Theme.Weight.SemiBold);
        _theme.DrawLine(c, muted ? "Muted" : $"{Math.Round(level * 100)}%", r.Right - 18, r.MidY + 4.5f, 44, font, _theme.Text, SKTextAlign.Right);
        _hits.Add(new Hit(new SKRect(r.Left, r.Top, r.Left + 46, r.Bottom), _volume.ToggleMute));
    }

    private void DrawNotice(SKCanvas c, SKRect r, Notice n)
    {
        DrawNoticeIcon(c, n, r.Left + 16 + 22, r.MidY, 22);
        var x = r.Left + 74;
        var width = r.Right - 18 - x;
        using var small = _theme.Font(11, Theme.Weight.Regular, n.App);
        using var title = _theme.Font(14, Theme.Weight.SemiBold, n.Title);
        using var body = _theme.Font(12.5f, Theme.Weight.Regular, n.Body);
        _theme.DrawLine(c, n.App, x, r.Top + 23, width - 40, small, _theme.Dim);
        _theme.DrawLine(c, "now", r.Right - 18, r.Top + 23, 40, small, _theme.Faint, SKTextAlign.Right);
        _theme.DrawLine(c, n.Title, x, r.Top + 43, width, title, _theme.Text);
        _theme.DrawLine(c, n.Body, x, r.Top + 61, width, body, _theme.Dim);
        _hits.Add(new Hit(r, () => Expand(pinned: true)));
    }

    private void DrawNoticeIcon(SKCanvas c, Notice n, float cx, float cy, float radius)
    {
        var box = new SKRect(cx - radius, cy - radius, cx + radius, cy + radius);
        if (n.Icon != null)
        {
            c.Save();
            c.ClipRoundRect(new SKRoundRect(box, radius * 0.45f), SKClipOperation.Intersect, true);
            DrawImageFill(c, n.Icon, box);
            c.Restore();
            return;
        }
        using var paint = new SKPaint { Color = _theme.Accent, IsAntialias = true };
        c.DrawRoundRect(new SKRoundRect(box, radius * 0.45f), paint);
        var initial = string.IsNullOrEmpty(n.App) ? "•" : n.App[..1].ToUpperInvariant();
        using var font = _theme.Font(radius * 0.9f, Theme.Weight.Bold, initial);
        _theme.DrawLine(c, initial, cx, cy + radius * 0.32f, radius * 2, font, _theme.OnAccent, SKTextAlign.Center);
    }

    private void DrawExpanded(SKCanvas c, SKRect r)
    {
        if (_tab == IslandTab.Media) DrawAlbumStreaks(c, r);

        // Tabs.
        var x = r.Left + 16;
        using var tabFont = _theme.Font(12.5f, Theme.Weight.SemiBold);
        foreach (var tab in Enum.GetValues<IslandTab>())
        {
            var label = tab switch
            {
                IslandTab.Notices when _unseenNotices > 0 => $"Notices ({_unseenNotices})",
                _ => tab.ToString(),
            };
            var w = tabFont.MeasureText(label) + 24;
            var pill = new SKRect(x, r.Top + 14, x + w, r.Top + 40);
            if (tab == _tab)
            {
                using var bg = new SKPaint { Color = _theme.Overlay(26), IsAntialias = true };
                c.DrawRoundRect(pill, 13, 13, bg);
            }
            _theme.DrawLine(c, label, pill.MidX, pill.MidY + 4.5f, w, tabFont, tab == _tab ? _theme.Text : _theme.Dim, SKTextAlign.Center);
            var chosen = tab;
            _hits.Add(new Hit(pill, () =>
            {
                _tab = chosen;
                if (chosen == IslandTab.Notices) _unseenNotices = 0;
                _content.Snap(0.4);
                _content.Target = 1;
            }));
            x += w + 4;
        }

        using var dim = _theme.Font(12, Theme.Weight.Regular);
        _theme.DrawLine(c, DateTime.Now.ToShortTimeString(), r.Right - 20, r.Top + 32, 80, dim, _theme.Dim, SKTextAlign.Right);

        var clearable = _tab switch
        {
            IslandTab.Clipboard => _clipboard.Entries.Count > 0,
            IslandTab.Notices => _history.Items.Count > 0,
            _ => false,
        };
        if (clearable)
        {
            var clockWidth = dim.MeasureText(DateTime.Now.ToShortTimeString());
            var right = r.Right - 20 - clockWidth - 18;
            _theme.DrawLine(c, "Clear", right, r.Top + 32, 60, dim, _theme.Accent, SKTextAlign.Right);
            var w = dim.MeasureText("Clear");
            _hits.Add(new Hit(new SKRect(right - w - 6, r.Top + 14, right + 6, r.Top + 40), () =>
            {
                if (_tab == IslandTab.Clipboard)
                {
                    _clipboard.Clear();
                    _clipScroll.Snap(0);
                }
                else
                {
                    _history.Clear();
                    _noticeScroll.Snap(0);
                }
            }));
        }

        var body = new SKRect(r.Left, r.Top + 50, r.Right, r.Bottom);
        switch (_tab)
        {
            case IslandTab.Media: DrawMedia(c, body); break;
            case IslandTab.Clipboard: DrawClipboard(c, body); break;
            case IslandTab.Notices: DrawNotices(c, body); break;
            case IslandTab.Style: DrawStyle(c, body); break;
        }
    }

    private void DrawEmpty(SKCanvas c, SKRect body, string headline, string hint)
    {
        using var big = _theme.Font(15, Theme.Weight.SemiBold);
        using var small = _theme.Font(12, Theme.Weight.Regular);
        _theme.DrawLine(c, headline, body.MidX, body.MidY - 4, body.Width - 40, big, _theme.Dim, SKTextAlign.Center);
        _theme.DrawLine(c, hint, body.MidX, body.MidY + 18, body.Width - 40, small, _theme.Faint, SKTextAlign.Center);
    }

    private void DrawMedia(SKCanvas c, SKRect body)
    {
        var m = CurrentMedia;
        if (m == null)
        {
            DrawEmpty(c, body, "Nothing playing", "Play something in Spotify, a browser or Media Player");
            return;
        }

        var art = new SKRect(body.Left + 18, body.Top + 6, body.Left + 138, body.Top + 126);
        DrawArtwork(c, m, art, 16);

        var x0 = art.Right + 18;
        var x1 = body.Right - 22;
        var width = x1 - x0;
        using var small = _theme.Font(11.5f, Theme.Weight.Regular, m.SourceApp);
        using var title = _theme.Font(17, Theme.Weight.Bold, m.Title);
        using var artist = _theme.Font(13, Theme.Weight.Regular, m.Artist);
        _theme.DrawLine(c, m.SourceApp, x0, body.Top + 18, width - 30, small, _theme.Dim);
        DrawBars(c, x1 - 18, body.Top + 13, 12, m.IsPlaying ? _theme.Accent : _theme.Faint);
        _theme.DrawLine(c, m.Title, x0, body.Top + 42, width, title, _theme.Text);
        _theme.DrawLine(c, m.Artist, x0, body.Top + 61, width, artist, _theme.Dim);

        // Progress, clickable to seek when the player allows it.
        var bar = new SKRect(x0, body.Top + 76, x1, body.Top + 80);
        using var paint = new SKPaint { Color = _theme.Faint, IsAntialias = true };
        c.DrawRoundRect(bar, 2, 2, paint);
        var duration = m.Duration;
        var position = m.LivePosition;
        if (duration > TimeSpan.Zero)
        {
            var f = (float)Math.Clamp(position / duration, 0, 1);
            paint.Color = _theme.Text;
            c.DrawRoundRect(new SKRect(bar.Left, bar.Top, bar.Left + bar.Width * f, bar.Bottom), 2, 2, paint);
            using var time = _theme.Font(11, Theme.Weight.Regular);
            _theme.DrawLine(c, Format(position), x0, body.Top + 95, 60, time, _theme.Dim);
            _theme.DrawLine(c, "-" + Format(duration - position), x1, body.Top + 95, 60, time, _theme.Dim, SKTextAlign.Right);
            if (m.CanSeek)
            {
                var hit = new SKRect(bar.Left, bar.Top - 8, bar.Right, bar.Bottom + 8);
                _hits.Add(new Hit(hit, () =>
                {
                    if (_cursor is { } p) _media.Seek(duration * Math.Clamp((p.X - bar.Left) / bar.Width, 0, 1));
                }));
            }
        }

        // Controls.
        var mid = (x0 + x1) / 2;
        var cy = body.Top + 118;
        Icons.Previous(c, mid - 48, cy, 18, m.CanPrevious ? _theme.Text : _theme.Faint);
        Icons.Next(c, mid + 48, cy, 18, m.CanNext ? _theme.Text : _theme.Faint);
        paint.Color = _theme.Text;
        c.DrawCircle(mid, cy, 19, paint);
        if (m.IsPlaying) Icons.Pause(c, mid, cy, 15, _theme.Background);
        else Icons.Play(c, mid + 1.5f, cy, 16, _theme.Background);
        _hits.Add(new Hit(Square(mid - 48, cy, 20), _media.Previous));
        _hits.Add(new Hit(Square(mid, cy, 21), _media.TogglePlayPause));
        _hits.Add(new Hit(Square(mid + 48, cy, 20), _media.Next));
    }

    private void DrawArtwork(SKCanvas c, MediaSnapshot m, SKRect box, float radius)
    {
        c.Save();
        c.ClipRoundRect(new SKRoundRect(box, radius), SKClipOperation.Intersect, true);
        if (m.Artwork != null) DrawImageFill(c, m.Artwork, box);
        else
        {
            using var shader = SKShader.CreateLinearGradient(new SKPoint(box.Left, box.Top), new SKPoint(box.Right, box.Bottom),
                [_theme.Accent, _theme.Accent.WithAlpha(90)], SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = shader };
            c.DrawRect(box, paint);
            var initial = m.Title[..1].ToUpperInvariant();
            using var font = _theme.Font(box.Height * 0.45f, Theme.Weight.Bold, initial);
            _theme.DrawLine(c, initial, box.MidX, box.MidY + box.Height * 0.16f, box.Width, font, SKColors.White, SKTextAlign.Center);
        }
        c.Restore();
    }

    private void DrawClipboard(SKCanvas c, SKRect body)
    {
        var entries = _clipboard.Entries;
        if (entries.Count == 0)
        {
            DrawEmpty(c, body, "Clipboard is empty", "Copy text, files or pictures and they'll wait here");
            return;
        }

        var area = new SKRect(body.Left + 12, body.Top, body.Right - 12, body.Bottom - 8);
        c.Save();
        c.ClipRect(area);
        var x = body.Left + 18 - (float)_clipScroll.Value;
        using var cardPaint = new SKPaint { IsAntialias = true };
        using var textFont = _theme.Font(11.5f, Theme.Weight.Regular);
        using var labelFont = _theme.Font(10.5f, Theme.Weight.Regular);
        foreach (var entry in entries)
        {
            var card = new SKRect(x, body.Top + 6, x + CardWidth, body.Top + 130);
            x += CardWidth + CardGap;
            if (card.Right < area.Left || card.Left > area.Right) continue;

            var hovered = _cursor is { } p && card.Contains(p) && area.Contains(p);
            cardPaint.Color = _theme.Overlay(hovered ? (byte)34 : (byte)18);
            var rr = new SKRoundRect(card, 14);
            c.DrawRoundRect(rr, cardPaint);

            var inner = Inflate(card, -10);
            switch (entry.Kind)
            {
                case ClipKind.Image when entry.Content.Image != null:
                    c.Save();
                    c.ClipRoundRect(new SKRoundRect(new SKRect(card.Left + 6, card.Top + 6, card.Right - 6, card.Bottom - 24), 10), SKClipOperation.Intersect, true);
                    DrawImageFill(c, entry.Content.Image, new SKRect(card.Left + 6, card.Top + 6, card.Right - 6, card.Bottom - 24));
                    c.Restore();
                    break;
                case ClipKind.Files:
                    Icons.File(c, inner.Left + 9, inner.Top + 11, 20, _theme.Dim);
                    using (var f = _theme.Font(11.5f, Theme.Weight.Regular, entry.Preview))
                        _theme.DrawWrapped(c, entry.Preview, inner.Left, inner.Top + 24, inner.Width, 4, f, _theme.Text);
                    break;
                default:
                    using (var f = _theme.Font(11.5f, Theme.Weight.Regular, entry.Preview))
                        _theme.DrawWrapped(c, entry.Preview, inner.Left, inner.Top - 2, inner.Width, 6, f, _theme.Text);
                    break;
            }
            var label = entry.Kind switch { ClipKind.Image => "Picture", ClipKind.Files => "Files", _ => "Text" } + " · " + Ago(entry.Time);
            _theme.DrawLine(c, label, inner.Left, card.Bottom - 9, inner.Width, labelFont, _theme.Faint);

            if (_flashEntry == entry && DateTime.Now < _flashUntil)
            {
                cardPaint.Color = _theme.Accent.WithAlpha(230);
                c.DrawRoundRect(rr, cardPaint);
                using var bold = _theme.Font(13, Theme.Weight.Bold);
                _theme.DrawLine(c, "Copied", card.MidX, card.MidY + 5, card.Width, bold, _theme.OnAccent, SKTextAlign.Center);
            }

            var chosen = entry;
            var visibleCard = SKRect.Intersect(card, area);
            _hits.Add(new Hit(visibleCard, () =>
            {
                if (_clipboard.Restore(chosen))
                {
                    _flashEntry = _clipboard.Entries[0];
                    _flashUntil = DateTime.Now.AddSeconds(1.2);
                    _clipScroll.Target = 0;
                }
            }));
        }
        c.Restore();
    }

    private void DrawNotices(SKCanvas c, SKRect body)
    {
        if (_history.Items.Count == 0)
        {
            DrawEmpty(c, body, "No notifications yet",
                $"Windows: {NotificationStatus}. Scripts: curl \"http://127.0.0.1:{ApiPort}/notify?title=Hi\"");
            return;
        }
        var area = new SKRect(body.Left, body.Top, body.Right, body.Bottom - 8);
        c.Save();
        c.ClipRect(area);
        var y = body.Top + 2 - (float)_noticeScroll.Value;
        using var small = _theme.Font(10.5f, Theme.Weight.Regular);
        foreach (var n in _history.Items)
        {
            var row = new SKRect(body.Left + 16, y, body.Right - 16, y + NoticeRowHeight - 4);
            y += NoticeRowHeight;
            if (row.Bottom < area.Top || row.Top > area.Bottom) continue;
            DrawNoticeIcon(c, n, row.Left + 18, row.MidY, 16);
            var x = row.Left + 44;
            using var title = _theme.Font(13, Theme.Weight.SemiBold, n.Title);
            using var bodyFont = _theme.Font(12, Theme.Weight.Regular, n.Body);
            _theme.DrawLine(c, $"{n.App} · {Ago(n.Time)}", x, row.Top + 15, row.Right - x, small, _theme.Dim);
            var titleText = Theme.Fit(n.Title, (row.Right - x) * 0.55f, title);
            _theme.DrawLine(c, titleText, x, row.Top + 33, row.Right - x, title, _theme.Text);
            var tw = title.MeasureText(titleText) + 8;
            _theme.DrawLine(c, n.Body, x + tw, row.Top + 33, row.Right - x - tw, bodyFont, _theme.Dim);
        }
        c.Restore();
    }

    /// <summary>The cover's colours as soft, slowly drifting light behind the music card.</summary>
    private void DrawAlbumStreaks(SKCanvas c, SKRect r)
    {
        if (!_theme.AlbumCard || CurrentMedia?.Palette is not { } palette) return;
        var t = (float)_theme.Time;
        var alpha = (byte)(_theme.Surface.IsLight ? 120 : 150);
        using var paint = new SKPaint { IsAntialias = true };
        for (var i = 0; i < palette.Swatches.Length; i++)
        {
            var color = palette.Swatches[i];
            var center = new SKPoint(
                r.Left + r.Width * (0.18f + 0.32f * i) + MathF.Sin(t * 0.35f + i * 2.1f) * 46,
                r.Top + r.Height * (0.35f + 0.3f * (i % 2)) + MathF.Cos(t * 0.27f + i * 1.3f) * 24);
            using var glow = SKShader.CreateRadialGradient(center, r.Height * 0.95f,
                [color.WithAlpha(alpha), color.WithAlpha(0)], SKShaderTileMode.Clamp);
            paint.Shader = glow;
            c.DrawRect(r, paint);
        }
        // A veil of the surface colour keeps text readable over bright covers.
        paint.Shader = null;
        paint.Color = _theme.Background.WithAlpha(_theme.Surface.IsLight ? (byte)70 : (byte)95);
        c.DrawRect(r, paint);
    }

    private void DrawStyle(SKCanvas c, SKRect body)
    {
        var x0 = body.Left + 20;
        using var heading = _theme.Font(12, Theme.Weight.SemiBold);
        using var detail = _theme.Font(12, Theme.Weight.Regular);

        // Surfaces.
        string? hovered = null;
        var y = body.Top + 34;
        for (var i = 0; i < Looks.Surfaces.Length; i++)
        {
            var look = Looks.Surfaces[i];
            var cx = x0 + 12 + i * 34;
            if (Hovering(Square(cx, y, 15))) hovered = look.Name;
            DrawSurfaceSwatch(c, look, cx, y, 12);
            if (look == _theme.Surface) Ring(c, cx, y, 15.5f, _theme.Text);
            _hits.Add(new Hit(Square(cx, y, 16), () => _theme.Choose(surface: look)));
        }
        DrawHeading(c, "Surface", hovered ?? _theme.Surface.Name, x0, body.Top + 12, heading, detail);

        // Accents.
        hovered = null;
        y = body.Top + 86;
        for (var i = 0; i < Looks.Accents.Length; i++)
        {
            var look = Looks.Accents[i];
            var cx = x0 + 11 + i * 30;
            if (Hovering(Square(cx, y, 13))) hovered = look.Name;
            DrawAccentSwatch(c, look, cx, y, 10);
            if (look.Id == _theme.AccentChoice.Id) Ring(c, cx, y, 13.5f, _theme.Text);
            _hits.Add(new Hit(Square(cx, y, 14), () => _theme.Choose(accent: look)));
        }
        DrawHeading(c, "Accent", hovered ?? _theme.AccentChoice.Name, x0, body.Top + 64, heading, detail);

        // Music card background.
        _theme.DrawLine(c, "Music card", x0, body.Top + 125, 90, heading, _theme.Text);
        var x = x0 + 92;
        foreach (var (label, album) in new[] { ("Plain", false), ("Album colours", true) })
        {
            var w = detail.MeasureText(label) + 22;
            var chip = new SKRect(x, body.Top + 109, x + w, body.Top + 131);
            var on = _theme.AlbumCard == album;
            using (var bg = new SKPaint { Color = _theme.Overlay(on ? (byte)34 : Hovering(chip) ? (byte)18 : (byte)8), IsAntialias = true })
                c.DrawRoundRect(chip, 11, 11, bg);
            _theme.DrawLine(c, label, chip.MidX, chip.MidY + 4.3f, w, detail, on ? _theme.Text : _theme.Dim, SKTextAlign.Center);
            _hits.Add(new Hit(chip, () => _theme.Choose(albumCard: album)));
            x += w + 6;
        }
    }

    private void DrawHeading(SKCanvas c, string title, string value, float x, float baseline, SKFont heading, SKFont detail)
    {
        _theme.DrawLine(c, title, x, baseline, 90, heading, _theme.Text);
        var w = heading.MeasureText(title);
        _theme.DrawLine(c, "·  " + value, x + w + 8, baseline, 260, detail, _theme.Dim);
    }

    private void DrawSurfaceSwatch(SKCanvas c, SurfaceLook look, float cx, float cy, float radius)
    {
        var box = Square(cx, cy, radius);
        using var paint = new SKPaint { IsAntialias = true };
        switch (look.Effect)
        {
            case SurfaceEffect.Aurora:
                using (var ring = Hues(cx, cy, 60, 70)) { paint.Shader = ring; c.DrawOval(box, paint); }
                break;
            case SurfaceEffect.Album:
                DrawPaletteDisc(c, box);
                break;
            default:
                using (var fill = SKShader.CreateLinearGradient(new SKPoint(cx, box.Top), new SKPoint(cx, box.Bottom), [look.Top, look.Bottom], SKShaderTileMode.Clamp))
                {
                    paint.Shader = fill;
                    c.DrawOval(box, paint);
                }
                paint.Shader = null;
                paint.Color = look.Accent;
                c.DrawCircle(cx + radius * 0.48f, cy + radius * 0.48f, radius * 0.3f, paint);
                break;
        }
        Ring(c, cx, cy, radius, _theme.Overlay(70), 1);
    }

    private void DrawAccentSwatch(SKCanvas c, AccentLook look, float cx, float cy, float radius)
    {
        var box = Square(cx, cy, radius);
        using var paint = new SKPaint { IsAntialias = true };
        switch (look.Source)
        {
            case AccentSource.Rainbow:
                using (var hues = Hues(cx, cy, 70, 100)) { paint.Shader = hues; c.DrawOval(box, paint); }
                break;
            case AccentSource.Album:
                DrawPaletteDisc(c, box);
                break;
            case AccentSource.Surface:
                paint.Color = _theme.Surface.Accent;
                c.DrawOval(box, paint);
                using (var f = _theme.Font(radius * 1.05f, Theme.Weight.Bold))
                    _theme.DrawLine(c, "A", cx, cy + radius * 0.38f, radius * 2, f, SKColors.White, SKTextAlign.Center);
                break;
            case AccentSource.Windows:
                paint.Color = _theme.WindowsAccentColor;
                c.DrawOval(box, paint);
                paint.Color = SKColors.White.WithAlpha(230);
                var q = radius * 0.28f;
                foreach (var (dx, dy) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
                    c.DrawRect(SKRect.Create(cx + (dx < 0 ? -q - 0.6f : 0.6f), cy + (dy < 0 ? -q - 0.6f : 0.6f), q, q), paint);
                break;
            default:
                paint.Color = look.Color;
                c.DrawOval(box, paint);
                break;
        }
        Ring(c, cx, cy, radius, _theme.Overlay(70), 1);
    }

    /// <summary>The playing cover's colours as slices of a disc, or a colour wheel when nothing plays.</summary>
    private void DrawPaletteDisc(SKCanvas c, SKRect box)
    {
        using var paint = new SKPaint { IsAntialias = true };
        if (CurrentMedia?.Palette is { } p)
        {
            for (var i = 0; i < p.Swatches.Length; i++)
            {
                paint.Color = p.Swatches[i];
                c.DrawArc(box, -90 + i * 120, 120, true, paint);
            }
            return;
        }
        using var hues = Hues(box.MidX, box.MidY, 45, 90);
        paint.Shader = hues;
        c.DrawOval(box, paint);
    }

    private static SKShader Hues(float cx, float cy, float saturation, float value)
    {
        var colors = Enumerable.Range(0, 7).Select(i => SKColor.FromHsv(i * 60 % 360, saturation, value)).ToArray();
        return SKShader.CreateSweepGradient(new SKPoint(cx, cy), colors);
    }

    private static void Ring(SKCanvas c, float cx, float cy, float radius, SKColor color, float width = 2)
    {
        using var stroke = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width };
        c.DrawCircle(cx, cy, radius, stroke);
    }

    private bool Hovering(SKRect r) => _cursor is { } p && r.Contains(p);

    // ───────────────────────────── helpers ─────────────────────────────

    private static void DrawImageFill(SKCanvas c, SKImage image, SKRect dest)
    {
        // Crop to the destination's aspect ratio rather than stretching.
        var scale = Math.Max(dest.Width / image.Width, dest.Height / image.Height);
        var sw = dest.Width / scale;
        var sh = dest.Height / scale;
        var src = SKRect.Create((image.Width - sw) / 2, (image.Height - sh) / 2, sw, sh);
        using var paint = new SKPaint { IsAntialias = true };
        c.DrawImage(image, src, dest, new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
    }

    private static SKRect Inflate(SKRect r, float by) => new(r.Left - by, r.Top - by, r.Right + by, r.Bottom + by);
    private static SKRect Square(float cx, float cy, float half) => new(cx - half, cy - half, cx + half, cy + half);

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private static string Ago(DateTime time)
    {
        var d = DateTime.Now - time;
        if (d.TotalSeconds < 45) return "now";
        if (d.TotalMinutes < 60) return $"{Math.Max(1, (int)Math.Round(d.TotalMinutes))}m";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h";
        return $"{(int)d.TotalDays}d";
    }
}
