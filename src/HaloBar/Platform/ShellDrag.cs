using System.Runtime.InteropServices;

namespace HaloBar.Platform;

/// <summary>
/// Drags files out of HaloBar into Explorer, a mail, a chat or anything else that takes dropped
/// files. The shell builds the data object (SHCreateShellItemArrayFromIDLists → BHID_DataObject)
/// and runs the drag with its own drag image (SHDoDragDrop), so it behaves like dragging from
/// Explorer. Files are offered as copies, so they stay where they are. Window thread only, with
/// the left button held.
/// </summary>
internal static class ShellDrag
{
    private const int DROPEFFECT_COPY = 1, DROPEFFECT_LINK = 4;
    private static readonly Guid BHID_DataObject = new("B8C0BD9F-ED24-455C-83E6-D5390C4FE8C4");
    private static readonly Guid IID_IDataObject = new("0000010E-0000-0000-C000-000000000046");

    /// <summary>A drag out of HaloBar is running (the island then turns its own drop away).</summary>
    public static bool Active { get; private set; }

    /// <summary>
    /// Starts the drag and returns when the files are dropped or the drag is cancelled. The drag
    /// runs its own message loop until then, so call it from a posted action, never mid-frame.
    /// </summary>
    public static bool Start(IntPtr hwnd, IReadOnlyList<string> paths)
    {
        if (Active || !Native.PrimaryButtonDown()) // a drag without the button held would wait for a click
        {
            Log.Write($"Drag out: not started ({(Active ? "another drag is running" : "button already let go")})");
            return false;
        }
        var data = DataObject(paths);
        if (data == IntPtr.Zero) return false;
        Active = true;
        try
        {
            Log.Write($"Drag out: starting with {string.Join(", ", paths.Select(Path.GetFileName))}");
            var hr = SHDoDragDrop(hwnd, data, IntPtr.Zero, DROPEFFECT_COPY | DROPEFFECT_LINK, out var effect);
            Log.Write($"Drag out: {paths.Count} file(s), result 0x{hr:X8}, effect {effect}");
            return hr == 0x00040100; // DRAGDROP_S_DROP
        }
        catch (Exception ex)
        {
            Log.Write($"Drag out failed: {ex.Message}");
            return false;
        }
        finally
        {
            Active = false;
            Marshal.Release(data);
        }
    }

    /// <summary>The shell's IDataObject for these files (release it when done), or zero.</summary>
    internal static unsafe IntPtr DataObject(IReadOnlyList<string> paths)
    {
        var pidls = new List<IntPtr>();
        var array = IntPtr.Zero;
        try
        {
            foreach (var path in paths.Where(p => File.Exists(p) || Directory.Exists(p)))
            {
                var hr = SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _);
                if (hr == 0) pidls.Add(pidl);
                else Log.Write($"Drag out: can't find {path} (0x{hr:X8})");
            }
            if (pidls.Count == 0)
            {
                Log.Write($"Drag out: none of {paths.Count} file(s) exist");
                return IntPtr.Zero;
            }
            var made = SHCreateShellItemArrayFromIDLists((uint)pidls.Count, [.. pidls], out array);
            if (made != 0)
            {
                Log.Write($"Drag out: no item array (0x{made:X8})");
                return IntPtr.Zero;
            }

            // IShellItemArray::BindToHandler is the first method after IUnknown.
            var bhid = BHID_DataObject;
            var iid = IID_IDataObject;
            var bindToHandler = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, Guid*, IntPtr*, int>)(*(IntPtr**)array)[3];
            IntPtr result;
            var bound = bindToHandler(array, IntPtr.Zero, &bhid, &iid, &result);
            if (bound != 0)
            {
                Log.Write($"Drag out: no data object (0x{bound:X8})");
                return IntPtr.Zero;
            }
            return result;
        }
        catch (Exception ex)
        {
            Log.Write($"Drag out failed: {ex.Message}");
            return IntPtr.Zero;
        }
        finally
        {
            if (array != IntPtr.Zero) Marshal.Release(array);
            foreach (var pidl in pidls) Marshal.FreeCoTaskMem(pidl);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHCreateShellItemArrayFromIDLists(uint count, IntPtr[] pidls, out IntPtr array);

    [DllImport("shell32.dll")]
    private static extern int SHDoDragDrop(IntPtr hwnd, IntPtr dataObject, IntPtr dropSource, int okEffects, out int effect);
}
