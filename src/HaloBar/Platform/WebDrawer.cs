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
/// A window under the island with a website in it (YouTube Music, ChatGPT, WhatsApp…). Each site
/// keeps its own WebView2, so music keeps playing while another site is shown or the drawer is
/// hidden; logins live in one profile under %AppData%\HaloBar\WebView2. The drawer hides when you
/// click elsewhere or press Esc. Window thread only.
/// </summary>
internal sealed class WebDrawer : NativeWindow
{
    private const int Width = 920, Height = 640; // DIPs
    private readonly Func<MonitorInfo?> _monitor;
    private readonly Action<Action> _post;
    private readonly Action<string> _openOutside;
    private readonly Dictionary<string, CoreWebView2Controller> _views = [];
    private readonly HashSet<string> _starting = [];
    private CoreWebView2Environment? _environment;
    private Task<CoreWebView2Environment>? _creating;
    private string? _shown;
    private bool _visible;

    public WebDrawer(Func<MonitorInfo?> monitor, Action<Action> post, Action<string> openOutside)
        : base("HaloBar.Web", WS_EX_TOOLWINDOW | WS_EX_TOPMOST, WS_POPUP | WS_CLIPCHILDREN)
    {
        _monitor = monitor;
        _post = post;
        _openOutside = openOutside;
        // Rounded corners like other Windows 11 flyouts.
        var round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
    }

    /// <summary>The site on show, or null while hidden.</summary>
    public string? Showing => _visible ? _shown : null;
    /// <summary>The site shown last, shown or not.</summary>
    public string? Last => _shown;
    public string Status { get; private set; } = "";
    public event Action? Changed;

    /// <summary>The site has a page open (playing, signed in…), whether or not it is on show.</summary>
    public bool IsOpen(WebSite site) => _views.ContainsKey(site.Url);

    public void Show(WebSite site)
    {
        Place();
        foreach (var (url, view) in _views) view.IsVisible = url == site.Url;
        _shown = site.Url;
        if (!_visible)
        {
            ShowWindow(Handle, SW_SHOW);
            _visible = true;
        }
        SetForegroundWindow(Handle);
        if (_views.TryGetValue(site.Url, out var existing)) existing.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
        else Start(site);
        Changed?.Invoke();
    }

    public void Toggle(WebSite site)
    {
        if (Showing == site.Url) Hide();
        else Show(site);
    }

    public void Hide()
    {
        if (!_visible) return;
        ShowWindow(Handle, SW_HIDE);
        _visible = false;
        Changed?.Invoke();
    }

    /// <summary>Closes the site's page entirely (stops its music, frees its memory).</summary>
    public void Close(WebSite site)
    {
        if (!_views.Remove(site.Url, out var view)) return;
        view.Close();
        if (_shown == site.Url) Hide();
        Changed?.Invoke();
    }

    private void Start(WebSite site)
    {
        if (!_starting.Add(site.Url)) return;
        Status = $"Opening {site.Name}…";
        _creating ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.Folder, "WebView2"));
        // WebView2 must be driven from this thread: every step comes back through _post.
        _creating.ContinueWith(env => _post(() =>
        {
            if (env.IsFaulted)
            {
                Fail(site, env.Exception?.GetBaseException());
                return;
            }
            _environment = env.Result;
            _environment.CreateCoreWebView2ControllerAsync(Handle).ContinueWith(made => _post(() => Ready(site, made)));
        }));
    }

    private void Ready(WebSite site, Task<CoreWebView2Controller> made)
    {
        _starting.Remove(site.Url);
        if (made.IsFaulted)
        {
            Fail(site, made.Exception?.GetBaseException());
            return;
        }
        var view = made.Result;
        _views[site.Url] = view;
        view.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 24, 24, 24);
        view.Bounds = ClientBounds();
        view.IsVisible = _shown == site.Url;
        var web = view.CoreWebView2;
        web.Settings.IsStatusBarEnabled = false;
        web.Settings.AreDefaultContextMenusEnabled = true;
        // Links meant for a new window: sign-in pages stay here, anything else opens in the browser.
        web.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            var target = e.Uri;
            if (target.Contains("accounts.", StringComparison.OrdinalIgnoreCase) || target.Contains("login", StringComparison.OrdinalIgnoreCase)
                || target.Contains("auth", StringComparison.OrdinalIgnoreCase) || new Uri(target).Host == site.Host)
                web.Navigate(target);
            else _openOutside(target);
        };
        view.AcceleratorKeyPressed += (_, e) =>
        {
            if (e.KeyEventKind == CoreWebView2KeyEventKind.KeyDown && e.VirtualKey == 0x1B && _visible)
            {
                // Esc hides the drawer, unless the page is in full screen (then it leaves that).
                if (!web.ContainsFullScreenElement)
                {
                    e.Handled = true;
                    Hide();
                }
            }
        };
        web.Navigate(site.Url);
        if (view.IsVisible) view.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
        Status = "";
        Log.Write($"Web: opened {site.Host}");
        Changed?.Invoke();
    }

    private void Fail(WebSite site, Exception? ex)
    {
        _starting.Remove(site.Url);
        Status = ex is WebView2RuntimeNotFoundException ? "Needs the WebView2 runtime from Microsoft" : "Couldn't open the page";
        Log.Write($"Web: {site.Host} failed: {ex?.Message}");
        Changed?.Invoke();
    }

    /// <summary>Under the island, on the screen the pointer is on, inside the work area.</summary>
    private void Place()
    {
        if (_monitor() is not { } m) return;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        var work = GetMonitorInfoW(m.Handle, ref info) ? info.rcWork : m.Bounds;
        var w = Math.Min((int)(Width * m.Scale), work.Width - 24);
        var top = m.Bounds.Top + (int)(60 * m.Scale);
        var h = Math.Min((int)(Height * m.Scale), work.Bottom - top - 12);
        var x = work.Left + (work.Width - w) / 2;
        SetWindowPos(Handle, HWND_TOPMOST, x, top, w, h, SWP_NOACTIVATE);
        foreach (var view in _views.Values) view.Bounds = ClientBounds();
    }

    private System.Drawing.Rectangle ClientBounds()
    {
        GetClientRect(Handle, out var r);
        return new System.Drawing.Rectangle(0, 0, r.Right - r.Left, r.Bottom - r.Top);
    }

    protected override IntPtr? OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_SIZE:
                foreach (var view in _views.Values) view.Bounds = ClientBounds();
                return IntPtr.Zero;
            case WM_ACTIVATE when (wParam.ToInt64() & 0xFFFF) == 0 && _visible:
                // Clicked somewhere else. The island never takes activation, so choosing another
                // site there doesn't count; WebView2's own child windows are inside this one.
                var other = lParam;
                if (other != IntPtr.Zero && IsChild(Handle, other)) return null;
                _post(() =>
                {
                    var fg = GetForegroundWindow();
                    if (fg != Handle && !IsChild(Handle, fg)) Hide();
                });
                return null;
            case WM_CLOSE:
                Hide();
                return IntPtr.Zero;
        }
        return null;
    }

    public override void Dispose()
    {
        foreach (var view in _views.Values) view.Close();
        _views.Clear();
        base.Dispose();
    }

    private const int SW_HIDE = 0, SW_SHOW = 5;
    private const uint WM_SIZE = 0x0005, WM_ACTIVATE = 0x0006;
    private const uint WS_CLIPCHILDREN = 0x02000000;

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
}
