using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using static HaloBar.Platform.Native;

namespace HaloBar.Platform;

[ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleDropTarget
{
    [PreserveSig] int DragEnter(IDataObject data, int keys, POINT pt, ref int effect);
    [PreserveSig] int DragOver(int keys, POINT pt, ref int effect);
    [PreserveSig] int DragLeave();
    [PreserveSig] int Drop(IDataObject data, int keys, POINT pt, ref int effect);
}

/// <summary>
/// Lets files be dropped onto the island from Explorer, a browser or anywhere else that drags
/// files. The window has to be under the pointer and not click-through for Windows to offer it
/// the drop, which the frame loop arranges while the pointer is over the island.
/// </summary>
internal sealed class DropTarget : IOleDropTarget
{
    private const int DROPEFFECT_NONE = 0, DROPEFFECT_COPY = 1;
    private bool _hasFiles;

    /// <summary>Raised while files hover over the window (true) and when they leave (false).</summary>
    public event Action<bool>? Hovering;
    public event Action<string[]>? Dropped;

    public static DropTarget? Register(IntPtr hwnd)
    {
        var target = new DropTarget();
        var hr = RegisterDragDrop(hwnd, target);
        if (hr == 0) return target;
        Log.Write($"Drag and drop unavailable (0x{hr:X8})");
        return null;
    }

    public int DragEnter(IDataObject data, int keys, POINT pt, ref int effect)
    {
        _hasFiles = Files(data).Length > 0;
        effect = _hasFiles ? DROPEFFECT_COPY : DROPEFFECT_NONE;
        if (_hasFiles) Hovering?.Invoke(true);
        return 0;
    }

    public int DragOver(int keys, POINT pt, ref int effect)
    {
        effect = _hasFiles ? DROPEFFECT_COPY : DROPEFFECT_NONE;
        return 0;
    }

    public int DragLeave()
    {
        Hovering?.Invoke(false);
        return 0;
    }

    public int Drop(IDataObject data, int keys, POINT pt, ref int effect)
    {
        var files = Files(data);
        effect = files.Length > 0 ? DROPEFFECT_COPY : DROPEFFECT_NONE;
        Hovering?.Invoke(false);
        if (files.Length > 0) Dropped?.Invoke(files);
        return 0;
    }

    private static string[] Files(IDataObject data)
    {
        var format = new FORMATETC
        {
            cfFormat = (short)CF_HDROP,
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = TYMED.TYMED_HGLOBAL,
        };
        try
        {
            if (data.QueryGetData(ref format) != 0) return [];
            data.GetData(ref format, out var medium);
            try
            {
                var drop = medium.unionmember;
                var count = DragQueryFileW(drop, 0xFFFFFFFF, null, 0);
                var files = new string[count];
                for (uint i = 0; i < count; i++)
                {
                    var len = DragQueryFileW(drop, i, null, 0);
                    var buffer = new char[len + 1];
                    DragQueryFileW(drop, i, buffer, len + 1);
                    files[i] = new string(buffer, 0, (int)len);
                }
                return files;
            }
            finally
            {
                ReleaseStgMedium(ref medium);
            }
        }
        catch (COMException)
        {
            return [];
        }
    }
}
