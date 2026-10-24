namespace HaloBar;

/// <summary>A plain text log in %AppData%\HaloBar, kept small by trimming on startup.</summary>
internal static class Log
{
    private static readonly object Gate = new();
    public static string FilePath { get; } = Path.Combine(AppPaths.Folder, "halobar.log");

    public static void Start()
    {
        try
        {
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 512 * 1024)
                File.Move(FilePath, FilePath + ".old", overwrite: true);
        }
        catch (IOException) { }
        Write($"HaloBar {typeof(Log).Assembly.GetName().Version} starting");
    }

    public static void Write(string line)
    {
        var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}";
        lock (Gate)
        {
            try { File.AppendAllText(FilePath, text); }
            catch (IOException) { }
        }
    }
}

internal static class AppPaths
{
    public static string Folder { get; } = CreateFolder();

    private static string CreateFolder()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HaloBar");
        Directory.CreateDirectory(path);
        return path;
    }
}
