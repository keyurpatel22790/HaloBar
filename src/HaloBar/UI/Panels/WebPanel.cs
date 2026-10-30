using System.Net.Http;
using HaloBar.Platform;
using HaloBar.Services;
using SkiaSharp;

namespace HaloBar.UI.Panels;

/// <summary>
/// Pinned websites. Clicking one opens it in a drawer under the island (and again hides it);
/// pages stay open while hidden, so music keeps playing. "+" adds a site; Edit removes sites and
/// closes open pages.
/// </summary>
internal sealed class WebPanel : Panel
{
    private const int Columns = 6, Rows = 2;
    private static readonly HttpClient Http = CreateClient();
    private static readonly Dictionary<string, SKImage?> Icons = [];
    private static readonly HashSet<string> Fetching = [];
    private readonly TextField _add;
    private bool _editing;

    public WebPanel(Hub hub) : base(hub) => _add = new TextField("Name and address, e.g. Notion notion.so", text =>
    {
        if (WebSite.FromTyped(text) is not { } site) return;
        Hub.Settings.WebSites.Add(site.ToString());
        Hub.Settings.Save();
    });

    public override string Title => "Web";
    public override void Icon(SKCanvas c, float cx, float cy, float size, SKColor color) => UI.Icons.Globe(c, cx, cy, size, color);
    public override bool Available => Hub.Settings.WebPanel;
    public override IEnumerable<(string Label, Action Click)> Actions => [(_editing ? "Done" : "Edit", () => _editing = !_editing)];

    private List<WebSite> Sites => [.. Hub.Settings.WebSites.Select(WebSite.Parse).OfType<WebSite>()];

    public override void Draw(Ui ui, SKRect body)
    {
        if (ui.Focused == _add)
        {
            ui.Text("Add a site", body.Left + 22, body.Top + 26, 300, 12, Theme.Weight.SemiBold, Theme.Dim);
            ui.Field(_add, new SKRect(body.Left + 18, body.MidY - 16, body.Right - 18, body.MidY + 16), 12.5f);
            ui.Text("Enter to add · Esc to cancel", body.MidX, body.Bottom - 14, body.Width, 10.5f, Theme.Weight.Regular, Theme.Faint, SKTextAlign.Center);
            return;
        }
        const float gap = 10;
        var cellW = (body.Width - 36 - gap * (Columns - 1)) / Columns;
        var cellH = (body.Height - 22 - gap * (Rows - 1)) / Rows;
        var sites = Sites;
        var web = Hub.Web;
        for (var i = 0; i <= Math.Min(sites.Count, Columns * Rows - 1); i++)
        {
            var x = body.Left + 18 + i % Columns * (cellW + gap);
            var y = body.Top + 4 + i / Columns * (cellH + gap);
            var cell = new SKRect(x, y, x + cellW, y + cellH);
            var hovered = ui.Hovering(cell);
            if (i == sites.Count)
            {
                // The last cell adds a site.
                if (hovered) ui.Fill(cell, 14, Theme.Overlay(14));
                UI.Icons.Plus(ui.Canvas, cell.MidX, cell.Top + 22, 18, Theme.Dim);
                ui.Text("Add", cell.MidX, cell.Bottom - 8, cellW, 10.5f, Theme.Weight.Regular, Theme.Dim, SKTextAlign.Center);
                ui.Hit(cell, () => { _add.Clear(); ui.Focus(_add); });
                break;
            }
            var site = sites[i];
            var showing = web.Showing == site.Url;
            var open = web.IsOpen(site);
            if (showing) ui.Fill(cell, 14, Theme.Overlay(30));
            else if (hovered) ui.Fill(cell, 14, Theme.Overlay(14));
            DrawIcon(ui, site, cell.MidX, cell.Top + 22, 15);
            ui.Text(site.Name, cell.MidX, cell.Bottom - 8, cellW - 6, 10.5f, Theme.Weight.Regular, showing ? Theme.Text : Theme.Dim, SKTextAlign.Center);
            // A green dot: the page is open (signed in, playing…) even while hidden.
            if (open) ui.Circle(cell.MidX + 17, cell.Top + 9, 3.5f, new SKColor(0x34, 0xC7, 0x59));
            var chosen = site;
            if (_editing)
            {
                ui.IconButton(cell.Right - 10, cell.Top + 10, 8, UI.Icons.Close, () => Remove(chosen));
                if (open) ui.Hit(new SKRect(cell.Left, cell.Top + 20, cell.Right, cell.Bottom), () => web.Close(chosen));
            }
            else ui.Hit(cell, () => web.Toggle(chosen));
        }
        var note = _editing ? "× removes a site · clicking an open one (green dot) closes its page"
            : web.Status.Length > 0 ? web.Status
            : sites.Count == 0 ? "Add a site with +" : "";
        if (note.Length > 0) ui.Text(note, body.MidX, body.Bottom - 5, body.Width - 40, 9.5f, Theme.Weight.Regular, Theme.Faint, SKTextAlign.Center);
    }

    private void Remove(WebSite site)
    {
        Hub.Web.Close(site);
        Hub.Settings.WebSites.RemoveAll(s => WebSite.Parse(s)?.Url == site.Url);
        Hub.Settings.Save();
    }

    /// <summary>The site's own icon once fetched; a coloured letter until then (or if it has none).</summary>
    private void DrawIcon(Ui ui, WebSite site, float cx, float cy, float radius)
    {
        var host = site.Host;
        lock (Icons)
        {
            if (Icons.TryGetValue(host, out var image))
            {
                if (image != null)
                {
                    ui.Image(image, Ui.Square(cx, cy, radius), radius * 0.45f);
                    return;
                }
            }
            else if (Fetching.Add(host)) _ = FetchIconAsync(host);
        }
        var hue = (uint)site.Name.GetHashCode() % 360;
        ui.Fill(Ui.Square(cx, cy, radius), radius * 0.45f, SKColor.FromHsv(hue, 55, 70));
        ui.Text(site.Name.Length > 0 ? site.Name[..1].ToUpperInvariant() : "?", cx, cy + 6, radius * 2, 16, Theme.Weight.Bold, SKColors.White, SKTextAlign.Center);
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 HaloBar/0.5");
        return http;
    }

    /// <summary>Asks the site itself for its icon (no third-party icon service), and keeps it on disk.</summary>
    private static async Task FetchIconAsync(string host)
    {
        var folder = Path.Combine(AppPaths.Folder, "icons");
        var file = Path.Combine(folder, host + ".img");
        SKImage? image = null;
        try
        {
            if (File.Exists(file)) image = Rendering.ImageTools.DecodeSmall(await File.ReadAllBytesAsync(file), 64);
            var candidates = new List<string> { $"https://{host}/apple-touch-icon.png", $"https://{host}/favicon.ico" };
            if (image == null) candidates.AddRange(await LinkedIconsAsync(host));
            foreach (var url in candidates)
            {
                if (image != null) break;
                using var response = await Http.GetAsync(url);
                if (!response.IsSuccessStatusCode) continue;
                var bytes = await response.Content.ReadAsByteArrayAsync();
                image = Rendering.ImageTools.DecodeSmall(bytes, 64);
                if (image == null) continue;
                Directory.CreateDirectory(folder);
                await File.WriteAllBytesAsync(file, bytes);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            Log.Write($"Web: no icon for {host}: {ex.Message}");
        }
        lock (Icons) Icons[host] = image;
    }

    private static readonly System.Text.RegularExpressions.Regex LinkTag = new(@"<link\b[^>]*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Icons the home page lists in its &lt;link rel="…icon…"&gt; tags (ChatGPT and Gemini keep
    /// theirs there), touch icons first, then bigger ones. SVG is skipped: it can't be drawn here.
    /// </summary>
    private static async Task<IEnumerable<string>> LinkedIconsAsync(string host)
    {
        var page = new Uri($"https://{host}/");
        string html;
        try { html = await Http.GetStringAsync(page); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return []; }
        string? Attr(string tag, string name) =>
            System.Text.RegularExpressions.Regex.Match(tag, $@"\b{name}\s*=\s*[""']([^""']*)[""']", System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : null;
        return LinkTag.Matches(html).Select(m => m.Value)
            .Where(tag => Attr(tag, "rel")?.Contains("icon", StringComparison.OrdinalIgnoreCase) == true)
            .Select(tag => (Href: Attr(tag, "href"), Touch: Attr(tag, "rel")!.Contains("apple", StringComparison.OrdinalIgnoreCase),
                Size: int.TryParse(Attr(tag, "sizes")?.Split('x')[0], out var n) ? n : 0))
            .Where(i => !string.IsNullOrEmpty(i.Href) && !i.Href.Contains(".svg", StringComparison.OrdinalIgnoreCase) && !i.Href.StartsWith("data:", StringComparison.Ordinal))
            .OrderByDescending(i => i.Touch).ThenByDescending(i => i.Size)
            .Select(i => new Uri(page, i.Href!).ToString())
            .Distinct()
            .Take(3)
            .ToList();
    }
}
