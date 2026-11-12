using System.Globalization;
using System.Text.RegularExpressions;
using HaloBar.Services;
using SkiaSharp;

namespace HaloBar.UI.Panels;

/// <summary>The clipboard shelf: recent copies as cards; click one to copy it again. A search box narrows them down.</summary>
internal sealed class ShelfPanel : Panel
{
    private const float CardWidth = 128, CardGap = 10;
    private readonly TextField _search;

    public ShelfPanel(Hub hub) : base(hub)
    {
        // Enter copies the first match.
        _search = new TextField("Search", _ =>
        {
            if (_filter == "pinned")
            {
                if (Pinned.FirstOrDefault() is { } text) Hub.Clipboard.CopyText(text);
            }
            else if (Shown.FirstOrDefault() is { } first && Hub.Clipboard.Restore(first))
            {
                _flash = Hub.Clipboard.Entries[0];
                _flashUntil = DateTime.Now.AddSeconds(1.2);
            }
        });
        // Type a custom extension (Enter applies it to the file open in the detail view).
        _extField = new TextField("ext", text => { if (_detail is { } d) ChangeFileExtension(d, text); });
    }

    /// <summary>The search box (the clipboard hotkey types into it).</summary>
    public TextField SearchField => _search;

    private bool Matches(string? text) => _search.Text.Length == 0 || text?.Contains(_search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase) == true;

    private bool Matches(ClipEntry e) => _search.Text.Length == 0 || e.Kind switch
    {
        ClipKind.Files => e.Content.Files?.Any(f => Matches(Path.GetFileName(f))) == true,
        ClipKind.Image => Matches("picture"),
        _ => Matches(e.Content.Text),
    };

    private IEnumerable<string> Pinned => Hub.Clipboard.Snippets.Where(Matches);
    private Spring _scroll = new(0, 300, 1);
    private ClipEntry? _flash;
    private DateTime _flashUntil;
    private string _filter = "all";
    private string? _flashSnippet;
    private ClipEntry? _detail;
    private bool _showQr;
    private string? _detailText;
    private DateTime _detailCopiedUntil;
    private readonly TextField _extField;
    private string _extStatus = "";
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex EmailRe = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    public override string Title => "Clipboard";
    public override void Icon(SKCanvas c, float cx, float cy, float size, SKColor color) => Icons.Clipboard(c, cx, cy, size, color);
    public override bool Available => Hub.Settings.ClipboardEnabled;
    public override double FrameInterval => _detail != null && DateTime.Now < _detailCopiedUntil ? 0.1 : _scroll.Settled ? 1 : 0;

    public override IEnumerable<(string Label, Action Click)> Actions =>
        _detail != null ? [("Back", () => { _detail = null; _showQr = false; })]
        : Hub.Clipboard.Entries.Count == 0 ? [] : [("Clear", () => { Hub.Clipboard.Clear(); _scroll.Snap(0); })];

    public override void Closed() { _detail = null; _showQr = false; }

    private IEnumerable<ClipEntry> Shown => (_filter switch
    {
        "text" => Hub.Clipboard.Entries.Where(e => e.Kind == ClipKind.Text),
        "files" => Hub.Clipboard.Entries.Where(e => e.Kind == ClipKind.Files),
        "pictures" => Hub.Clipboard.Entries.Where(e => e.Kind == ClipKind.Image),
        _ => Hub.Clipboard.Entries,
    }).Where(Matches);

    public override bool Wheel(float notches)
    {
        var max = Hub.Settings.ClipboardList && _filter != "pinned"
            ? Math.Max(0, Shown.Count() * ListRow - 150)
            : Math.Max(0, Shown.Count() * (CardWidth + CardGap) - CardGap - 524);
        _scroll.Target = Math.Clamp(_scroll.Target - notches * 90, 0, max);
        return true;
    }

    public override void Update(double dt) => _scroll.Step(dt);

    public override void Draw(Ui ui, SKRect body)
    {
        if (_detail is { } detail)
        {
            DrawDetail(ui, body, detail);
            return;
        }
        if (Hub.Clipboard.Entries.Count == 0 && Hub.Clipboard.Snippets.Count == 0 && _search.Text.Length == 0)
        {
            ui.Empty(body, "Clipboard is empty", "Copy text, files or pictures and they'll wait here");
            return;
        }

        // Filters.
        var x = body.Left + 18;
        foreach (var (id, label) in new[] { ("all", "All"), ("text", "Text"), ("files", "Files"), ("pictures", "Pictures"), ("pinned", "Pinned") })
        {
            var chosen = id;
            x = ui.Chip(x, body.Top + 4, label, () => { _filter = chosen; _scroll.Snap(0); _detail = null; }, _filter == id, 11).Right + 5;
        }
        var search = new SKRect(Math.Max(x + 10, body.Right - 230), body.Top - 7, body.Right - 18, body.Top + 15);
        ui.Field(_search, search, 11.5f);
        if (_search.Text.Length > 0) ui.IconButton(search.Right - 12, search.MidY, 8, Icons.Close, () => _search.Clear());

        var top = body.Top + 22;
        if (_filter == "pinned")
        {
            DrawSnippets(ui, body, top);
            return;
        }
        var entries = Shown.ToList();
        if (entries.Count == 0)
        {
            ui.Empty(new SKRect(body.Left, top, body.Right, body.Bottom), _search.Text.Length > 0 ? "No matches" : "Nothing of this kind yet",
                _search.Text.Length > 0 ? "Press the ✕ to see everything again" : "");
            return;
        }
        var area = new SKRect(body.Left + 12, top, body.Right - 12, body.Bottom - 6);
        if (Hub.Settings.ClipboardList)
        {
            DrawList(ui, area, entries);
            return;
        }
        var c = ui.Canvas;
        c.Save();
        c.ClipRect(area);
        x = body.Left + 18 - (float)_scroll.Value;
        foreach (var entry in entries)
        {
            var card = new SKRect(x, top + 4, x + CardWidth, body.Bottom - 8);
            x += CardWidth + CardGap;
            if (card.Right < area.Left || card.Left > area.Right) continue;

            var hovered = ui.Hovering(card) && ui.Hovering(area);
            ui.Fill(card, 14, Theme.Overlay(hovered ? (byte)34 : (byte)18));
            var inner = Ui.Inflate(card, -10);
            switch (entry.Kind)
            {
                case ClipKind.Image when entry.Content.Image != null:
                    ui.Image(entry.Content.Image, new SKRect(card.Left + 6, card.Top + 6, card.Right - 6, card.Bottom - 24), 10);
                    break;
                case ClipKind.Files:
                    Icons.File(c, inner.Left + 9, inner.Top + 9, 18, Theme.Dim);
                    ui.Wrapped(entry.Preview, inner.Left, inner.Top + 20, inner.Width, 3, 11.5f);
                    break;
                default:
                    ui.Wrapped(entry.Preview, inner.Left, inner.Top - 3, inner.Width, 5, 11.5f);
                    break;
            }
            var label = entry.Kind switch { ClipKind.Image => "Picture", ClipKind.Files => "Files", _ => "Text" } + " · " + Ui.Ago(entry.Time);
            ui.Text(label, inner.Left, card.Bottom - 8, inner.Width, 10.5f, Theme.Weight.Regular, Theme.Faint);

            if (_flash == entry && DateTime.Now < _flashUntil)
            {
                ui.Fill(card, 14, Theme.Accent.WithAlpha(230));
                ui.Text("Copied", card.MidX, card.MidY + 5, card.Width, 13, Theme.Weight.Bold, Theme.OnAccent, SKTextAlign.Center);
            }

            var chosen = entry;
            ui.Hit(SKRect.Intersect(card, area), () =>
            {
                if (!Hub.Clipboard.Restore(chosen)) return;
                _flash = Hub.Clipboard.Entries[0];
                _flashUntil = DateTime.Now.AddSeconds(1.2);
                _scroll.Target = 0;
            });
            // Text can be pinned as a snippet that stays when the history moves on.
            if (hovered && entry.Kind == ClipKind.Text)
            {
                var pinned = Hub.Clipboard.IsPinned(entry.Content.Text!);
                ui.IconButton(card.Right - 13, card.Bottom - 13, 9, (cv, px, py, s, col) => Icons.Pin(cv, px, py, s, pinned ? Theme.Accent : col),
                    () => Hub.Clipboard.TogglePin(chosen.Content.Text!));
                ui.IconButton(card.Right - 35, card.Bottom - 13, 9, Dots, () => { _detail = chosen; _showQr = false; _detailText = null; _extStatus = ""; });
            }
            else if (hovered && entry.Kind == ClipKind.Files)
                ui.IconButton(card.Right - 13, card.Bottom - 13, 9, Dots, () => { _detail = chosen; _showQr = false; _detailText = null; _extStatus = ""; });
        }
        c.Restore();
    }

    private const float ListRow = 26;

    /// <summary>The history as one line per entry, newest first; the wheel scrolls down the list.</summary>
    private void DrawList(Ui ui, SKRect area, List<ClipEntry> entries)
    {
        ui.Canvas.Save();
        ui.Canvas.ClipRect(area);
        var y = area.Top + 6 - (float)_scroll.Value;
        foreach (var entry in entries)
        {
            var row = new SKRect(area.Left, y, area.Right, y + ListRow - 2);
            y += ListRow;
            if (row.Bottom < area.Top || row.Top > area.Bottom) continue;
            var hovered = ui.Hovering(row) && ui.Hovering(area);
            var flashing = _flash == entry && DateTime.Now < _flashUntil;
            ui.Fill(row, 8, flashing ? Theme.Accent.WithAlpha(230) : Theme.Overlay(hovered ? (byte)30 : (byte)12));
            var kind = entry.Kind switch { ClipKind.Image => "Picture", ClipKind.Files => "Files", _ => "Text" };
            var line = flashing ? "Copied" : entry.Preview.ReplaceLineEndings(" ");
            ui.Text(line, row.Left + 10, row.MidY + 4, row.Width - 130, 11.5f, flashing ? Theme.Weight.Bold : Theme.Weight.Regular, flashing ? Theme.OnAccent : Theme.Text);
            // While hovered, the action buttons take the right end, so the kind·time label steps aside.
            if (!flashing && !hovered) ui.Text($"{kind} · {Ui.Ago(entry.Time)}", row.Right - 34, row.MidY + 4, 90, 10, Theme.Weight.Regular, Theme.Faint, SKTextAlign.Right);
            var chosen = entry;
            ui.Hit(SKRect.Intersect(row, area), () =>
            {
                if (!Hub.Clipboard.Restore(chosen)) return;
                _flash = Hub.Clipboard.Entries[0];
                _flashUntil = DateTime.Now.AddSeconds(1.2);
                _scroll.Target = 0;
            });
            if (hovered && entry.Kind == ClipKind.Text)
            {
                var pinned = Hub.Clipboard.IsPinned(entry.Content.Text!);
                ui.IconButton(row.Right - 14, row.MidY, 9, (cv, px, py, s, col) => Icons.Pin(cv, px, py, s, pinned ? Theme.Accent : col),
                    () => Hub.Clipboard.TogglePin(chosen.Content.Text!));
                ui.IconButton(row.Right - 36, row.MidY, 9, Dots, () => { _detail = chosen; _showQr = false; _detailText = null; _extStatus = ""; });
            }
            else if (hovered && entry.Kind == ClipKind.Files)
                ui.IconButton(row.Right - 14, row.MidY, 9, Dots, () => { _detail = chosen; _showQr = false; _detailText = null; _extStatus = ""; });
        }
        ui.Canvas.Restore();
    }

    private void DrawSnippets(Ui ui, SKRect body, float top)
    {
        var snippets = Pinned.ToList();
        if (snippets.Count == 0)
        {
            ui.Empty(new SKRect(body.Left, top, body.Right, body.Bottom), _search.Text.Length > 0 ? "No matches" : "No pinned snippets",
                _search.Text.Length > 0 ? "" : "Hover a text card and press the pin to keep it here");
            return;
        }
        var area = new SKRect(body.Left + 12, top, body.Right - 12, body.Bottom - 6);
        ui.Canvas.Save();
        ui.Canvas.ClipRect(area);
        var x = body.Left + 18 - (float)_scroll.Value;
        foreach (var text in snippets)
        {
            var card = new SKRect(x, top + 4, x + CardWidth, body.Bottom - 8);
            x += CardWidth + CardGap;
            if (card.Right < area.Left || card.Left > area.Right) continue;
            var hovered = ui.Hovering(card) && ui.Hovering(area);
            ui.Fill(card, 14, Theme.Overlay(hovered ? (byte)34 : (byte)18));
            ui.Wrapped(text, card.Left + 10, card.Top + 7, card.Width - 20, 5, 11.5f);
            ui.Text("Pinned", card.Left + 10, card.Bottom - 8, card.Width - 20, 10.5f, Theme.Weight.Regular, Theme.Accent);
            if (_flashSnippet == text && DateTime.Now < _flashUntil)
            {
                ui.Fill(card, 14, Theme.Accent.WithAlpha(230));
                ui.Text("Copied", card.MidX, card.MidY + 5, card.Width, 13, Theme.Weight.Bold, Theme.OnAccent, SKTextAlign.Center);
            }
            var chosen = text;
            ui.Hit(SKRect.Intersect(card, area), () =>
            {
                if (!Hub.Clipboard.CopyText(chosen)) return;
                _flashSnippet = chosen;
                _flashUntil = DateTime.Now.AddSeconds(1.2);
            });
            if (hovered) ui.IconButton(card.Right - 13, card.Bottom - 13, 9, Icons.Close, () => Hub.Clipboard.TogglePin(chosen));
        }
        ui.Canvas.Restore();
    }

    /// <summary>Search the web for the text, or translate it (into Settings' Translate-to language), in the browser.</summary>
    private static void Dots(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        using var p = new SKPaint { Color = color, IsAntialias = true };
        for (var i = -1; i <= 1; i++) c.DrawCircle(cx + i * size * 0.42f, cy, size * 0.14f, p);
    }

    /// <summary>One clipboard item opened up, with everything you can do with it.</summary>
    private void DrawDetail(Ui ui, SKRect body, ClipEntry entry)
    {
        var text = _detailText ?? entry.Content.Text ?? entry.Preview;
        var trimmed = text.Trim();
        var isUrl = !trimmed.Contains(' ') && Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
        var isEmail = EmailRe.IsMatch(trimmed);
        var q = Uri.EscapeDataString(trimmed.Length > 500 ? trimmed[..500] : trimmed);
        var qrOk = trimmed.Length is > 0 and <= 1500;
        var qr = _showQr && qrOk;
        var textRight = qr ? body.Right - 184 : body.Right - 18;
        var copied = DateTime.Now < _detailCopiedUntil;
        ui.Text(copied ? "Copied ✓" : entry.Kind == ClipKind.Text ? "Clipboard text" : "Clipboard item", body.Left + 18, body.Top + 12, 240, 11, Theme.Weight.SemiBold, copied ? Theme.Accent : Theme.Dim);
        ui.Wrapped(text.ReplaceLineEndings(" "), body.Left + 18, body.Top + 32, textRight - body.Left - 36, 3, 12.5f);
        if (qr) Platform.QrCode.Draw(ui.Canvas, trimmed, new SKRect(body.Right - 168, body.Top + 12, body.Right - 18, body.Top + 162));

        float cx = body.Left + 18, cy = body.Top + 96;
        void Chip(string label, Action act, bool on = false)
        {
            var r = ui.Chip(cx, cy, label, act, on, 11.5f);
            cx = r.Right + 6;
            if (cx > textRight - 86) { cx = body.Left + 18; cy += 28; }
        }
        void Copy(string t) { Hub.Clipboard.CopyText(t); _detailText = t; _detailCopiedUntil = DateTime.Now.AddSeconds(1.6); }

        if (entry.Kind == ClipKind.Text)
        {
            Chip("Copy", () => Copy(text));
            Chip(Hub.Speech.Speaking ? "Stop" : "Read aloud", () => Hub.Speech.Toggle(text));
            if (isUrl) Chip("Open", () => Hub.Open(trimmed));
            else if (isEmail) Chip("Email", () => Hub.Open("mailto:" + trimmed));
            if (qrOk) Chip(_showQr ? "Hide QR" : "QR code", () => _showQr = !_showQr);
            Chip("UPPER", () => Copy(text.ToUpperInvariant()));
            Chip("lower", () => Copy(text.ToLowerInvariant()));
            Chip("Title", () => Copy(CultureInfo.CurrentCulture.TextInfo.ToTitleCase(text.ToLowerInvariant())));
            Chip("One line", () => Copy(Whitespace.Replace(text, " ").Trim()));
            var pinned = Hub.Clipboard.IsPinned(text);
            Chip(pinned ? "Unpin" : "Pin", () => Hub.Clipboard.TogglePin(text), pinned);
            Chip("Search", () => Hub.Open($"https://www.google.com/search?q={q}"));
            Chip("Translate", () => Hub.Open($"https://translate.google.com/?sl=auto&tl={Hub.Settings.TranslateTo}&text={q}&op=translate"));
        }
        else if (entry.Kind == ClipKind.Files && entry.Content.Files is { Length: > 0 } files)
        {
            var path = files[0];
            var currentExt = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            Chip("Open", () => Hub.Open(path));
            Chip("Show in folder", () => Hub.Reveal(path));
            Chip("Copy again", () => { Hub.Clipboard.Restore(entry); });

            // Change extension: a label, common targets for this kind of file, then a box to type any.
            cx = body.Left + 18; cy += 32;
            ui.Text("Change extension", cx, cy, 200, 11, Theme.Weight.SemiBold, Theme.Accent);
            cy += 18;
            foreach (var ext in SuggestExtensions(currentExt))
                Chip("." + ext, () => ChangeFileExtension(entry, ext), on: ext == currentExt);

            // Any other extension, typed in.
            if (cx > textRight - 150) { cx = body.Left + 18; cy += 28; }
            var fieldRect = new SKRect(cx, cy - 13, cx + 86, cy + 13);
            ui.Field(_extField, fieldRect, 11.5f);
            ui.Chip(fieldRect.Right + 6, cy, "Apply", () => { ChangeFileExtension(entry, _extField.Text); }, size: 11.5f);

            if (_extStatus.Length > 0)
                ui.Text(_extStatus, body.Left + 18, body.Bottom - 12, textRight - body.Left - 30, 10.5f, Theme.Weight.Regular,
                    _extStatus.StartsWith("Saved", StringComparison.Ordinal) ? Theme.Accent : Theme.Dim);
        }
        else
        {
            Chip("Copy again", () => { Hub.Clipboard.Restore(entry); });
        }
    }

    /// <summary>Common extensions to offer for a file, by what it looks like now.</summary>
    private static IEnumerable<string> SuggestExtensions(string ext)
    {
        string[] image = ["png", "jpg", "jpeg", "webp", "gif", "bmp"];
        string[] video = ["mp4", "mkv", "mov", "avi", "webm"];
        string[] audio = ["mp3", "wav", "m4a", "flac", "aac", "ogg"];
        string[] text = ["txt", "md", "csv", "json", "log", "xml"];
        if (image.Contains(ext)) return image;
        if (video.Contains(ext)) return video;
        if (audio.Contains(ext)) return audio;
        if (text.Contains(ext)) return text;
        return [ext, "txt", "bin"];
    }

    /// <summary>Picture formats SkiaSharp can read and write, so an image can be truly converted, not just relabelled.</summary>
    private static SKEncodedImageFormat? ImageCodec(string ext) => ext switch
    {
        "png" => SKEncodedImageFormat.Png,
        "jpg" or "jpeg" or "jfif" => SKEncodedImageFormat.Jpeg,
        "webp" => SKEncodedImageFormat.Webp,
        "bmp" => SKEncodedImageFormat.Bmp,
        "gif" => SKEncodedImageFormat.Gif,
        _ => null,
    };

    /// <summary>
    /// Renames the first file in <paramref name="entry"/> to a new extension. Between two different
    /// picture formats it actually re-encodes the image (so the file is valid, not just relabelled);
    /// otherwise it's a plain rename. The shelf card then points at the new file.
    /// </summary>
    private void ChangeFileExtension(ClipEntry entry, string newExtRaw)
    {
        if (entry.Content.Files is not { Length: > 0 } files) return;
        var newExt = newExtRaw.Trim().TrimStart('.').ToLowerInvariant();
        if (newExt.Length == 0 || !newExt.All(char.IsLetterOrDigit)) { _extStatus = "Enter a valid extension"; return; }
        var old = files[0];
        try
        {
            if (!File.Exists(old)) { _extStatus = "File no longer exists"; return; }
            var oldExt = Path.GetExtension(old).TrimStart('.').ToLowerInvariant();
            if (oldExt == newExt) { _extStatus = "Already ." + newExt; return; }
            var dir = Path.GetDirectoryName(old)!;
            var name = Path.GetFileNameWithoutExtension(old);
            var target = Path.Combine(dir, $"{name}.{newExt}");
            for (var n = 2; File.Exists(target); n++) target = Path.Combine(dir, $"{name} ({n}).{newExt}");

            var reEncoded = false;
            if (ImageCodec(oldExt) is { } && ImageCodec(newExt) is { } to)
            {
                using var img = SKImage.FromEncodedData(File.ReadAllBytes(old));
                if (img != null)
                {
                    using var data = img.Encode(to, 95);
                    if (data == null) { _extStatus = "Can't convert to ." + newExt; return; }
                    File.WriteAllBytes(target, data.ToArray());
                    File.Delete(old);
                    reEncoded = true;
                }
            }
            if (!reEncoded) File.Move(old, target);

            files[0] = target;
            _extField.Text = "";
            _extStatus = "Saved as " + Path.GetFileName(target);
            Hub.ShowHud(new Hud(HudIcon.Copied, "Changed to ." + newExt, Detail: Path.GetFileName(target)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _extStatus = ex.Message;
            Log.Write($"Change extension: {ex.Message}");
        }
    }
}
