using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using static HaloBar.Platform.Native;

namespace HaloBar.Platform;

/// <summary>A site on the web panel.</summary>
internal sealed record WebSite(string Name, string Url)
{
    public string Host => Uri.TryCreate(Url, UriKind.Absolute, out var u) ? u.Host : Url;

    /// <summary>"Name|https://…" as stored in settings.</summary>
    public static WebSite? Parse(string stored)
    {
        var bar = stored.IndexOf('|');
        return bar > 0 ? new WebSite(stored[..bar], stored[(bar + 1)..]) : null;
    }

    /// <summary>
    /// What someone typed to add a site: "Notion notion.so", "https://x.com" or just "notion.so".
    /// The last word is the address; the rest, if any, is the name.
    /// </summary>
    public static WebSite? FromTyped(string text)
    {
        var words = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return null;
        var address = words[^1];
        if (!address.Contains('.') && !address.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return null;
        if (!address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !address.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            address = "https://" + address;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) return null;
        var name = words.Length > 1 ? string.Join(' ', words[..^1]) : Pretty(uri.Host);
        return new WebSite(name, uri.ToString());
    }

    /// <summary>"music.youtube.com" → "Youtube", "web.whatsapp.com" → "Whatsapp".</summary>
    private static string Pretty(string host)
    {
        var parts = host.Split('.');
        var main = parts.Length >= 2 ? parts[^2] : host;
        return main.Length > 0 ? char.ToUpperInvariant(main[0]) + main[1..] : host;
    }

    public override string ToString() => $"{Name}|{Url}";
}

/// <summary>
/// The web panel's sites (YouTube Music, ChatGPT, WhatsApp…), each in a window of its own, so
/// several can be open side by side; each keeps its page (music keeps playing while hidden), and
/// logins live in one profile under %AppData%\HaloBar\WebView2. With "Stays open" (the default)
/// each is an ordinary window: in Alt+Tab and the taskbar, moved and sized on its own, reopening
/// where it was left. With "Hides on click" a site drops down under the island, one at a time,
/// and hides when you click elsewhere. Window thread only.
/// </summary>
internal sealed class WebDrawer : IDisposable
{
    private readonly Dictionary<string, SiteWindow> _windows = [];
    private readonly Func<MonitorInfo?> _monitor;
    private readonly Action<Action> _post;
    private readonly Action<string> _openOutside;
    private readonly Func<IReadOnlyList<string>> _extensions;
    private readonly Func<(bool Block, bool FastForward)> _ads;
    private readonly AppSettings _settings;
    private Task<CoreWebView2Environment>? _creating;
    private bool _extensionsAdded;
    private string? _last;

    public WebDrawer(Func<MonitorInfo?> monitor, Action<Action> post, Action<string> openOutside, Func<IReadOnlyList<string>> extensions,
        Func<(bool Block, bool FastForward)> ads, AppSettings settings)
    {
        (_monitor, _post, _openOutside, _extensions, _ads, _settings) = (monitor, post, openOutside, extensions, ads, settings);
    }

    /// <summary>The site shown last, if its window is on show; null otherwise.</summary>
    public string? Showing => _last != null && _windows.TryGetValue(_last, out var w) && w.Visible ? _last : null;
    /// <summary>The site shown last, shown or not.</summary>
    public string? Last => _last;
    public string Status { get; private set; } = "";
    /// <summary>Requests the ad blocker refused since HaloBar started.</summary>
    public int AdsBlocked { get; private set; }
    public event Action? Changed;

    internal bool KeepOpen => _settings.WebDrawerKeepOpen;
    internal AppSettings Settings => _settings;
    internal MonitorInfo? Monitor => _monitor();
    internal void OpenOutside(string url) => _openOutside(url);
    internal void Post(Action action) => _post(action);
    internal void Notify() => Changed?.Invoke();

    /// <summary>The site has a page open (playing, signed in…), whether or not it is on show.</summary>
    public bool IsOpen(WebSite site) => _windows.TryGetValue(site.Url, out var w) && w.HasPage;

    public void Show(WebSite site)
    {
        // Dropping down from the island, sites take turns; as windows they sit side by side.
        if (!KeepOpen)
            foreach (var other in _windows.Values.Where(w => w.Site.Url != site.Url)) other.Hide();
        if (!_windows.TryGetValue(site.Url, out var window))
            _windows[site.Url] = window = new SiteWindow(this, site, _windows.Count);
        _last = site.Url;
        window.Show();
        Changed?.Invoke();
    }

    /// <summary>Clicking a site on the island: hides it when it's the window in front, shows it otherwise.</summary>
    public void Toggle(WebSite site)
    {
        if (_windows.TryGetValue(site.Url, out var window) && window.Visible && window.InFront) window.Hide();
        else Show(site);
    }

    /// <summary>Hides every site's window (they keep their pages).</summary>
    public void Hide()
    {
        foreach (var window in _windows.Values) window.Hide();
        Changed?.Invoke();
    }

    /// <summary>Closes the site's page and window entirely (stops its music, frees its memory).</summary>
    public void Close(WebSite site)
    {
        if (!_windows.Remove(site.Url, out var window)) return;
        window.Dispose();
        Changed?.Invoke();
    }

    /// <summary>"Stays open" was switched in one window's bar: every window follows.</summary>
    internal void KeepOpenChanged()
    {
        foreach (var window in _windows.Values) window.ApplyKeepOpen();
        if (!KeepOpen && _last != null)
            foreach (var other in _windows.Values.Where(w => w.Site.Url != _last)) other.Hide();
    }

    /// <summary>The shared WebView2 environment (one profile, so one login for every site).</summary>
    internal void WithEnvironment(WebSite site, Action<CoreWebView2Environment> ready)
    {
        Status = $"Opening {site.Name}…";
        Changed?.Invoke();
        _creating ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.Folder, "WebView2"),
            new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = true });
        // WebView2 must be driven from this thread: every step comes back through _post.
        _creating.ContinueWith(env => _post(() =>
        {
            if (env.IsFaulted) Failed(site, env.Exception?.GetBaseException());
            else ready(env.Result);
        }));
    }

    internal void Opened(WebSite site, CoreWebView2 web, CoreWebView2Environment environment)
    {
        AddExtensions(web.Profile);
        // Ad blocking is set up before the first page loads, so its script runs ahead of the site's own.
        var (block, fastForward) = _ads();
        if (block)
            _ = AdBlocker.AttachAsync(web, environment, fastForward, () =>
            {
                AdsBlocked++;
                if (AdsBlocked % 50 == 1) Log.Write($"Web: {AdsBlocked} ad and tracker requests blocked so far");
            });
        Status = "";
        Log.Write($"Web: opened {site.Host}");
        Changed?.Invoke();
    }

    internal void Failed(WebSite site, Exception? ex)
    {
        Status = ex is WebView2RuntimeNotFoundException ? "Needs the WebView2 runtime from Microsoft" : "Couldn't open the page";
        Log.Write($"Web: {site.Host} failed: {ex?.Message}");
        if (_windows.Remove(site.Url, out var window)) window.Dispose();
        Changed?.Invoke();
    }

    /// <summary>
    /// Loads the unpacked extensions chosen in Settings into the profile (once per run; the
    /// profile keeps them), and removes ones taken off the list.
    /// </summary>
    private void AddExtensions(CoreWebView2Profile profile)
    {
        if (_extensionsAdded) return;
        _extensionsAdded = true;
        _ = SyncExtensionsAsync(profile, _extensions().Where(f => File.Exists(Path.Combine(f, "manifest.json"))).ToList());
    }

    /// <summary>Adds each chosen folder (an extension already there is simply returned), then removes any other.</summary>
    private static async Task SyncExtensionsAsync(CoreWebView2Profile profile, List<string> folders)
    {
        var keep = new HashSet<string>();
        foreach (var folder in folders)
        {
            try
            {
                var added = await profile.AddBrowserExtensionAsync(folder);
                keep.Add(added.Id);
                Log.Write($"Extension loaded: {added.Name}");
            }
            catch (Exception ex) { Log.Write($"Extension {folder}: {ex.Message}"); }
        }
        try
        {
            foreach (var existing in await profile.GetBrowserExtensionsAsync())
                if (!keep.Contains(existing.Id))
                {
                    await existing.RemoveAsync();
                    Log.Write($"Extension removed: {existing.Name}");
                }
        }
        catch (Exception ex) { Log.Write($"Extensions: {ex.Message}"); }
    }

    public void Dispose()
    {
        foreach (var window in _windows.Values) window.Dispose();
        _windows.Clear();
    }
}

/// <summary>
/// One site's window: a bar across the top (Back, Forward, Reload, Home, the site's name, Stays
/// open, Open in browser, Close page, ✕) and the site's page below it.
/// </summary>
internal sealed class SiteWindow : NativeWindow
{
    private const int Width = 920, Height = 640; // DIPs
    private readonly WebDrawer _drawer;
    private readonly int _order;
    private CoreWebView2Controller? _view;
    private bool _starting, _visible;

    public WebSite Site { get; }
    public bool Visible => _visible;
    public bool HasPage => _view != null;
    /// <summary>This window is the one in front (or holds the keyboard).</summary>
    public bool InFront => GetForegroundWindow() is var fg && (fg == Handle || IsChild(Handle, fg));

    public SiteWindow(WebDrawer drawer, WebSite site, int order)
        : base("HaloBar.Web", WS_EX_TOOLWINDOW | WS_EX_TOPMOST, WS_POPUP | WS_CLIPCHILDREN | WS_THICKFRAME)
    {
        _drawer = drawer;
        _order = order;
        Site = site;
        // Rounded corners like other Windows 11 windows.
        var round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
    }

    private bool KeepOpen => _drawer.KeepOpen;
    private CoreWebView2? Page => _view?.CoreWebView2;

    public void Show()
    {
        Place();
        ComeToThisScreen();
        ApplyKind();
        InvalidateRect(Handle, IntPtr.Zero, true);
        if (!_visible)
        {
            ShowWindow(Handle, SW_SHOW);
            _visible = true;
        }
        SetForegroundWindow(Handle);
        if (_view != null)
        {
            _view.IsVisible = true;
            _view.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
        }
        else Start();
    }

    public void Hide()
    {
        if (!_visible) return;
        ShowWindow(Handle, SW_HIDE);
        _visible = false;
        _drawer.Notify();
    }

    private void Start()
    {
        if (_starting) return;
        _starting = true;
        _drawer.WithEnvironment(Site, environment =>
            environment.CreateCoreWebView2ControllerAsync(Handle).ContinueWith(made => _drawer.Post(() => Ready(made, environment))));
    }

    private void Ready(Task<CoreWebView2Controller> made, CoreWebView2Environment environment)
    {
        _starting = false;
        if (made.IsFaulted)
        {
            _drawer.Failed(Site, made.Exception?.GetBaseException());
            return;
        }
        var view = _view = made.Result;
        view.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 24, 24, 24);
        view.Bounds = PageBounds();
        view.IsVisible = _visible;
        var web = view.CoreWebView2;
        _drawer.Opened(Site, web, environment);
        web.Settings.IsStatusBarEnabled = false;
        web.Settings.AreDefaultContextMenusEnabled = true;
        // Back and Forward light up as the page's history changes.
        web.HistoryChanged += (_, _) => InvalidateRect(Handle, IntPtr.Zero, false);
        // Links meant for a new window: sign-in pages stay here, anything else opens in the browser.
        web.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            var target = e.Uri;
            if (target.Contains("accounts.", StringComparison.OrdinalIgnoreCase) || target.Contains("login", StringComparison.OrdinalIgnoreCase)
                || target.Contains("auth", StringComparison.OrdinalIgnoreCase) || new Uri(target).Host == Site.Host)
                web.Navigate(target);
            else _drawer.OpenOutside(target);
        };
        view.AcceleratorKeyPressed += (_, e) =>
        {
            // Esc hides the window, unless the page is in full screen (then it leaves that).
            if (e.KeyEventKind == CoreWebView2KeyEventKind.KeyDown && e.VirtualKey == 0x1B && _visible && !web.ContainsFullScreenElement)
            {
                e.Handled = true;
                Hide();
            }
        };
        web.Navigate(Site.Url);
        if (_visible) view.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
    }

    // ── Where the window sits ──

    private void Place()
    {
        if (KeepOpen && SavedBounds() is { } saved)
        {
            SetWindowPos(Handle, HWND_NOTOPMOST, saved.Left, saved.Top, saved.Width, saved.Height, SWP_NOACTIVATE);
            return;
        }
        if (_drawer.Monitor is not { } m) return;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        var work = GetMonitorInfoW(m.Handle, ref info) ? info.rcWork : m.Bounds;
        var w = Math.Min((int)(Width * m.Scale), work.Width - 24);
        var top = m.Bounds.Top + (int)(60 * m.Scale);
        var h = Math.Min((int)(Height * m.Scale), work.Bottom - top - 12);
        var x = work.Left + (work.Width - w) / 2;
        // A second and third site window open a little down and right of the first, not on top of it.
        var step = KeepOpen ? (int)(36 * m.Scale) * (_order % 5) : 0;
        SetWindowPos(Handle, KeepOpen ? HWND_NOTOPMOST : HWND_TOPMOST, x + step, top + step, w, Math.Min(h, work.Bottom - top - step - 12), SWP_NOACTIVATE);
    }

    /// <summary>
    /// Opened from the island while the window sits on another screen: it comes to the screen
    /// being worked on (the island's under the pointer), keeping its size, so it's in view.
    /// </summary>
    private void ComeToThisScreen()
    {
        if (!KeepOpen || _drawer.Monitor is not { } here) return;
        GetWindowRect(Handle, out var r);
        var b = here.Bounds;
        var middle = new POINT { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 };
        if (middle.X >= b.Left && middle.X < b.Right && middle.Y >= b.Top && middle.Y < b.Bottom) return;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        var work = GetMonitorInfoW(here.Handle, ref info) ? info.rcWork : b;
        int w = Math.Min(r.Right - r.Left, work.Width - 24), h = Math.Min(r.Bottom - r.Top, work.Height - (int)(72 * here.Scale));
        var x = work.Left + (work.Width - w) / 2;
        var y = work.Top + (int)(60 * here.Scale);
        SetWindowPos(Handle, HWND_NOTOPMOST, x, y, w, h, SWP_NOACTIVATE);
        SaveBounds();
    }

    /// <summary>Where this site's window was left, if that's still on a screen (the first site also takes the older single place).</summary>
    private RECT? SavedBounds()
    {
        var settings = _drawer.Settings;
        var text = settings.WebWindowBounds.GetValueOrDefault(Site.Url) ?? (_order == 0 ? settings.WebDrawerBounds : "");
        var parts = text.Split(',');
        if (parts.Length != 4 || !parts.All(p => int.TryParse(p, out _))) return null;
        var v = parts.Select(int.Parse).ToArray();
        var r = new RECT { Left = v[0], Top = v[1], Right = v[0] + v[2], Bottom = v[1] + v[3] };
        return Monitors.All().Any(m => r.Left + 80 < m.Bounds.Right && r.Right - 80 > m.Bounds.Left && r.Top >= m.Bounds.Top - 10 && r.Top < m.Bounds.Bottom - 40) ? r : null;
    }

    private void SaveBounds()
    {
        GetWindowRect(Handle, out var r);
        _drawer.Settings.WebWindowBounds[Site.Url] = $"{r.Left},{r.Top},{r.Right - r.Left},{r.Bottom - r.Top}";
        _drawer.Settings.Save();
    }

    /// <summary>
    /// Staying open, the window is an ordinary app window: in Alt+Tab and on the taskbar, titled
    /// with the site's name and carrying HaloBar's icon. Otherwise it's a tool window that drops
    /// down from the island and keeps out of both.
    /// </summary>
    private void ApplyKind()
    {
        var ex = (long)GetWindowLongPtrW(Handle, GWL_EXSTYLE);
        var wanted = KeepOpen ? (ex & ~WS_EX_TOOLWINDOW) | WS_EX_APPWINDOW : (ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
        SetWindowTextW(Handle, $"{Site.Name} · HaloBar");
        if (_icon == IntPtr.Zero && Environment.ProcessPath is { } exe && ExtractIconExW(exe, 0, out var big, out var small, 1) > 0)
        {
            _icon = big;
            SendMessageW(Handle, 0x0080 /* WM_SETICON */, new IntPtr(1), big);
            SendMessageW(Handle, 0x0080, IntPtr.Zero, small);
        }
        if (wanted == ex) return;
        SetWindowLongPtrW(Handle, GWL_EXSTYLE, new IntPtr(wanted));
        // The taskbar only notices the change when the window is shown again.
        if (_visible)
        {
            ShowWindow(Handle, SW_HIDE);
            ShowWindow(Handle, SW_SHOW);
        }
    }

    private IntPtr _icon;

    /// <summary>The drawer's Stays open setting changed: this window follows.</summary>
    public void ApplyKeepOpen()
    {
        if (KeepOpen)
        {
            SetWindowPos(Handle, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            SaveBounds();
        }
        else Place();
        ApplyKind();
        if (_view != null) _view.Bounds = PageBounds();
        InvalidateRect(Handle, IntPtr.Zero, false);
    }

    // ── The bar ──

    private const int BarDip = 38;
    private int Bar => (int)(BarDip * GetDpiForWindow(Handle) / 96f);

    /// <summary>The page fills the window below the bar, leaving a thin edge to grab when it's a window.</summary>
    private System.Drawing.Rectangle PageBounds()
    {
        GetClientRect(Handle, out var r);
        var edge = KeepOpen ? (int)(5 * GetDpiForWindow(Handle) / 96f) : 0;
        return new System.Drawing.Rectangle(edge, Bar, Math.Max(1, r.Right - r.Left - 2 * edge), Math.Max(1, r.Bottom - r.Top - Bar - edge));
    }

    private enum BarButton { None, Back, Forward, Reload, Home, Pin, Browser, ClosePage, Hide }

    /// <summary>Back, Forward, Reload and Home draw as icons (Segoe MDL2 Assets glyphs).</summary>
    private static bool IsIcon(BarButton b) => b is BarButton.Back or BarButton.Forward or BarButton.Reload or BarButton.Home;

    /// <summary>Back and Forward are dim (and do nothing) when there's no page to go to.</summary>
    private bool Enabled(BarButton b) => b switch
    {
        BarButton.Back => Page?.CanGoBack == true,
        BarButton.Forward => Page?.CanGoForward == true,
        BarButton.Reload or BarButton.Home => Page != null,
        _ => true,
    };

    /// <summary>The buttons' places in the bar, in window pixels: navigation on the left, the rest on the right.</summary>
    private (BarButton Button, RECT Rect, string Label)[] Buttons()
    {
        GetClientRect(Handle, out var r);
        var s = GetDpiForWindow(Handle) / 96f;
        int pad = (int)(6 * s), h = Bar - 2 * pad, right = r.Right - pad;
        RECT Box(int width)
        {
            var box = new RECT { Left = right - width, Top = pad, Right = right, Bottom = pad + h };
            right -= width + pad;
            return box;
        }
        var hide = Box((int)(34 * s));
        var close = Box((int)(92 * s));
        var browser = Box((int)(124 * s));
        var pin = Box((int)(112 * s));
        int left = pad, square = h;
        RECT Nav()
        {
            var box = new RECT { Left = left, Top = pad, Right = left + square, Bottom = pad + h };
            left += square + pad / 2;
            return box;
        }
        var back = Nav();
        var forward = Nav();
        var reload = Nav();
        var home = Nav();
        return [(BarButton.Hide, hide, "✕"), (BarButton.ClosePage, close, "Close page"), (BarButton.Browser, browser, "Open in browser"),
                (BarButton.Pin, pin, KeepOpen ? "Stays open ✓" : "Hides on click"),
                (BarButton.Back, back, ""), (BarButton.Forward, forward, ""), (BarButton.Reload, reload, ""), (BarButton.Home, home, "")];
    }

    /// <summary>As a window: the bar drags it, its edges resize it; the buttons stay buttons.</summary>
    private int HitTest(IntPtr lParam)
    {
        var p = new POINT { X = (short)((long)lParam & 0xFFFF), Y = (short)(((long)lParam >> 16) & 0xFFFF) };
        ScreenToClient(Handle, ref p);
        GetClientRect(Handle, out var r);
        var edge = (int)(6 * GetDpiForWindow(Handle) / 96f);
        bool left = p.X < edge, right = p.X >= r.Right - edge, top = p.Y < edge / 2, bottom = p.Y >= r.Bottom - edge;
        if (top && left) return 13;
        if (top && right) return 14;
        if (bottom && left) return 16;
        if (bottom && right) return 17;
        if (left) return 10;
        if (right) return 11;
        if (top) return 12;
        if (bottom) return 15;
        if (p.Y < Bar && ButtonAt(p.X, p.Y) == BarButton.None) return 2; // HTCAPTION
        return 1; // HTCLIENT
    }

    private BarButton _hover;

    private BarButton ButtonAt(int x, int y)
    {
        foreach (var (button, rect, _) in Buttons())
            if (x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom) return button;
        return BarButton.None;
    }

    private void Press(BarButton button)
    {
        switch (button)
        {
            case BarButton.Hide:
                Hide();
                break;
            case BarButton.Pin:
                _drawer.Settings.WebDrawerKeepOpen = !KeepOpen;
                _drawer.Settings.Save();
                _drawer.KeepOpenChanged();
                break;
            case BarButton.Back when Page?.CanGoBack == true:
                Page.GoBack();
                break;
            case BarButton.Forward when Page?.CanGoForward == true:
                Page.GoForward();
                break;
            case BarButton.Reload:
                Page?.Reload();
                break;
            case BarButton.Home:
                Page?.Navigate(Site.Url); // the site's own start page, as pinned
                break;
            case BarButton.ClosePage:
                _drawer.Close(Site);
                break;
            case BarButton.Browser:
                _drawer.OpenOutside(Page?.Source ?? Site.Url);
                Hide();
                break;
        }
    }

    /// <summary>The bar, painted with GDI: dark, navigation and the site's name on the left, the rest on the right.</summary>
    private void PaintBar()
    {
        var dc = BeginPaint(Handle, out var ps);
        try
        {
            GetClientRect(Handle, out var r);
            var s = GetDpiForWindow(Handle) / 96f;
            var bar = new RECT { Left = 0, Top = 0, Right = r.Right, Bottom = Bar };
            var background = CreateSolidBrush(0x00201C1C);  // BGR: #1C1C20
            FillRect(dc, ref bar, background);
            DeleteObject(background);
            var font = CreateFontW(-(int)(13 * s), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5 /* CLEARTYPE */, 0, "Segoe UI");
            var old = SelectObject(dc, font);
            SetBkMode(dc, 1 /* TRANSPARENT */);
            SetTextColor(dc, 0x00F2EEEE);
            var navEnd = Buttons().Where(b => IsIcon(b.Button)).Max(b => b.Rect.Right);
            var title = new RECT { Left = navEnd + (int)(12 * s), Top = 0, Right = r.Right / 2, Bottom = Bar };
            DrawTextW(dc, Site.Name, -1, ref title, 0x24 | 0x8000 /* VCENTER | SINGLELINE | END_ELLIPSIS */);
            SelectObject(dc, old);
            DeleteObject(font);
            font = CreateFontW(-(int)(12 * s), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            var icons = CreateFontW(-(int)(13 * s), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe MDL2 Assets");
            old = SelectObject(dc, font);
            foreach (var (button, rect, label) in Buttons())
            {
                var box = rect;
                var hover = button == _hover;
                var on = button == BarButton.Pin && KeepOpen;
                var fill = CreateSolidBrush(on ? 0x00B07A2Eu : hover ? (button == BarButton.Hide ? 0x002311C4u : 0x00403A3Au) : 0x002E2A2Au);
                var round = CreateRoundRectRgn(box.Left, box.Top, box.Right + 1, box.Bottom + 1, (int)(10 * s), (int)(10 * s));
                FillRgn(dc, round, fill);
                DeleteObject(round);
                DeleteObject(fill);
                SetTextColor(dc, Enabled(button) ? 0x00F2EEEEu : 0x00706A6Au);
                SelectObject(dc, IsIcon(button) ? icons : font);
                DrawTextW(dc, label, -1, ref box, 0x25 /* CENTER | VCENTER | SINGLELINE */);
            }
            SelectObject(dc, old);
            DeleteObject(font);
            DeleteObject(icons);
        }
        finally
        {
            EndPaint(Handle, ref ps);
        }
    }

    protected override IntPtr? OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_SIZE:
                if (_view != null) _view.Bounds = PageBounds();
                return IntPtr.Zero;
            case WM_ACTIVATE when (wParam.ToInt64() & 0xFFFF) == 0 && _visible && !KeepOpen:
                // Dropped down and clicked somewhere else: hide. The island never takes activation,
                // so choosing another site there doesn't count; WebView2's own child windows are inside this one.
                var other = lParam;
                if (other != IntPtr.Zero && IsChild(Handle, other)) return null;
                _drawer.Post(() =>
                {
                    if (!InFront) Hide();
                });
                return null;
            case WM_CLOSE:
                Hide();
                return IntPtr.Zero;
            case WM_PAINT:
                PaintBar();
                return IntPtr.Zero;
            case WM_NCCALCSIZE when wParam != IntPtr.Zero:
                return IntPtr.Zero; // the whole window is client area: no frame drawn
            case WM_NCHITTEST when KeepOpen:
                return new IntPtr(HitTest(lParam));
            case WM_EXITSIZEMOVE when KeepOpen:
                SaveBounds();
                return IntPtr.Zero;
            case WM_ERASEBKGND:
                return new IntPtr(1);
            case WM_MOUSEMOVE:
                var over = ButtonAt((short)((long)lParam & 0xFFFF), (short)(((long)lParam >> 16) & 0xFFFF));
                if (over != _hover)
                {
                    _hover = over;
                    InvalidateRect(Handle, IntPtr.Zero, false);
                    var track = new TRACKMOUSEEVENT { cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = 2 /* TME_LEAVE */, hwndTrack = Handle };
                    TrackMouseEvent(ref track);
                }
                return IntPtr.Zero;
            case WM_MOUSELEAVE:
                _hover = BarButton.None;
                InvalidateRect(Handle, IntPtr.Zero, false);
                return IntPtr.Zero;
            case WM_LBUTTONUP:
                Press(ButtonAt((short)((long)lParam & 0xFFFF), (short)(((long)lParam >> 16) & 0xFFFF)));
                return IntPtr.Zero;
            case WM_SETCURSOR when (((long)lParam) & 0xFFFF) == 1 /* HTCLIENT */:
                SetCursor(LoadCursorW(IntPtr.Zero, _hover != BarButton.None ? IDC_HAND : IDC_ARROW));
                return new IntPtr(1);
        }
        return null;
    }

    public override void Dispose()
    {
        _view?.Close();
        _view = null;
        base.Dispose();
    }

    private const int SW_HIDE = 0, SW_SHOW = 5;
    private const uint WM_SIZE = 0x0005, WM_ACTIVATE = 0x0006, WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014, WM_MOUSELEAVE = 0x02A3,
        WM_NCCALCSIZE = 0x0083, WM_NCHITTEST = 0x0084, WM_EXITSIZEMOVE = 0x0232;
    private const uint WS_THICKFRAME = 0x00040000, WS_CLIPCHILDREN = 0x02000000;
    private const long WS_EX_APPWINDOW = 0x40000;
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT { public IntPtr hdc; public int fErase; public RECT rcPaint; public int fRestore, fIncUpdate; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT { public int cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }

    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hwnd, ref POINT p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowTextW(IntPtr hwnd, string text);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconExW(string file, int index, out IntPtr large, out IntPtr small, uint count);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref RECT rect, IntPtr brush);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawTextW(IntPtr dc, string text, int count, ref RECT rect, uint format);
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT track);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [DllImport("gdi32.dll")] private static extern bool FillRgn(IntPtr dc, IntPtr rgn, IntPtr brush);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr dc, uint color);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrecision, uint clipPrecision, uint quality, uint pitch, string face);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
}
