using System.Runtime.InteropServices;
using SkiaSharp;
using static HaloBar.Platform.Native;

namespace HaloBar.Platform;

internal sealed record TrayMenuItem(string Text, Action OnClick, bool Checked = false, bool Enabled = true,
    IReadOnlyList<TrayMenuItem>? Children = null)
{
    public static readonly TrayMenuItem Separator = new("-", () => { });

    public static TrayMenuItem Submenu(string text, IReadOnlyList<TrayMenuItem> children) => new(text, () => { }, Children: children);
}

/// <summary>The notification-area icon: the island has no taskbar button, so this is how to quit.</summary>
internal sealed class TrayIcon : IDisposable
{
    public const uint CallbackMessage = WM_APP + 1;
    /// <summary>Broadcast when Explorer (re)starts; every tray icon must be added again.</summary>
    public static readonly uint TaskbarCreated = RegisterWindowMessageW("TaskbarCreated");

    private readonly IntPtr _hwnd;
    private readonly IntPtr _icon;
    private readonly string _tip;

    public Func<IReadOnlyList<TrayMenuItem>>? BuildMenu;
    public Action? LeftClick;

    public TrayIcon(IntPtr hwnd, string tip)
    {
        _hwnd = hwnd;
        _tip = tip;
        _icon = DrawIcon();
        Add();
    }

    private void Add()
    {
        var data = Data();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        Shell_NotifyIconW(NIM_ADD, ref data);
    }

    private NOTIFYICONDATAW Data() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = 1,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tip,
        szInfo = "",
        szInfoTitle = "",
    };

    /// <summary>Handles the tray's own messages. Returns true when the message was the tray's.</summary>
    public bool HandleMessage(uint msg, IntPtr lParam)
    {
        if (msg == TaskbarCreated)
        {
            Add(); // Explorer restarted and forgot every icon
            return true;
        }
        if (msg != CallbackMessage) return false;
        switch ((uint)lParam)
        {
            case WM_LBUTTONUP:
                LeftClick?.Invoke();
                break;
            case WM_RBUTTONUP:
                ShowMenu();
                break;
        }
        return true;
    }

    private void ShowMenu()
    {
        var commands = new List<TrayMenuItem>();
        var menu = Build(BuildMenu?.Invoke() ?? [], commands);
        GetCursorPos(out var p);
        // Without this the menu won't close when clicking elsewhere.
        SetForegroundWindow(_hwnd);
        var chosen = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, p.X, p.Y, 0, _hwnd, IntPtr.Zero);
        DestroyMenu(menu); // also destroys the submenus attached to it
        if (chosen > 0) commands[chosen - 1].OnClick();
    }

    /// <summary>Builds a menu, numbering every clickable item across submenus.</summary>
    private static IntPtr Build(IReadOnlyList<TrayMenuItem> items, List<TrayMenuItem> commands)
    {
        var menu = CreatePopupMenu();
        foreach (var item in items)
        {
            if (ReferenceEquals(item, TrayMenuItem.Separator))
                AppendMenuW(menu, MF_SEPARATOR, 0, null);
            else if (item.Children is { } children)
                AppendMenuW(menu, MF_POPUP, (nuint)Build(children, commands), item.Text);
            else
            {
                commands.Add(item);
                AppendMenuW(menu, MF_STRING | (item.Checked ? MF_CHECKED : 0) | (item.Enabled ? 0 : MF_GRAYED),
                    (nuint)commands.Count, item.Text);
            }
        }
        return menu;
    }

    /// <summary>A small rounded pill, drawn so the app ships no .ico file.</summary>
    private static IntPtr DrawIcon()
    {
        const int size = 32;
        using var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(0x10, 0x10, 0x12) };
            canvas.DrawRoundRect(new SKRect(1, 9, 31, 23), 7, 7, paint);
            paint.Color = new SKColor(0x7C, 0x9C, 0xFF);
            canvas.DrawCircle(24, 16, 3.2f, paint);
            paint.Color = SKColors.White.WithAlpha(220);
            canvas.DrawRoundRect(new SKRect(6, 14.5f, 18, 17.5f), 1.5f, 1.5f, paint);
        }
        var color = CreateBitmap(size, size, 1, 32, bitmap.GetPixels());
        // All-zero AND mask: the colour bitmap's alpha decides what shows.
        var maskBits = new byte[size * size / 8];
        IntPtr mask;
        unsafe
        {
            fixed (byte* bits = maskBits) mask = CreateBitmap(size, size, 1, 1, (IntPtr)bits);
        }
        var info = new ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
        var icon = CreateIconIndirect(ref info);
        DeleteObject(color);
        DeleteObject(mask);
        return icon;
    }

    public void Dispose()
    {
        var data = Data();
        Shell_NotifyIconW(NIM_DELETE, ref data);
        DestroyIcon(_icon);
    }
}
