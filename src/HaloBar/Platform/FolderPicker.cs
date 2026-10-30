using System.Runtime.InteropServices;

namespace HaloBar.Platform;

/// <summary>Windows' own "Select folder" dialog (IFileOpenDialog with FOS_PICKFOLDERS).</summary>
internal static class FolderPicker
{
    private const uint FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, SIGDN_FILESYSPATH = 0x80058000;
    private static readonly Guid FileOpenDialog = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");

    /// <summary>
    /// Opens the dialog on a thread of its own (it's modal, and the island keeps drawing meanwhile)
    /// and calls <paramref name="picked"/> with the folder, unless it's cancelled.
    /// </summary>
    public static void Pick(string title, string? start, Action<string> picked)
    {
        var thread = new Thread(() =>
        {
            try
            {
                if (Show(title, start) is { } folder) picked(folder);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                Log.Write($"Folder picker: {ex.Message}");
            }
        }) { IsBackground = true, Name = "Folder picker" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static string? Show(string title, string? start)
    {
        var dialog = (IFileDialog)Activator.CreateInstance(Type.GetTypeFromCLSID(FileOpenDialog)!)!;
        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM);
            dialog.SetTitle(title);
            var iid = typeof(IShellItem).GUID;
            if (!string.IsNullOrEmpty(start) && Directory.Exists(start) && SHCreateItemFromParsingName(start, IntPtr.Zero, ref iid, out var folder) == 0)
                dialog.SetFolder(folder);
            if (dialog.Show(IntPtr.Zero) != 0) return null; // cancelled
            dialog.GetResult(out var item);
            item.GetDisplayName(SIGDN_FILESYSPATH, out var name);
            try { return Marshal.PtrToStringUni(name); }
            finally { Marshal.FreeCoTaskMem(name); }
        }
        finally { Marshal.ReleaseComObject(dialog); }
    }

    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr owner);
        void SetFileTypes(uint count, IntPtr specs);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem item);
        void SetFolder(IShellItem item);
        void GetFolder(out IShellItem item);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName(out IntPtr name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint form, out IntPtr name);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, out IShellItem item);
}
