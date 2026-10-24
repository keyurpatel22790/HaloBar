using HaloBar.Platform;

namespace HaloBar.Services;

internal sealed record DownloadProgress(string Name, long Bytes, double BytesPerSec);

/// <summary>
/// Notices downloads in the Downloads folder while they happen. Browsers write to a temporary
/// file (.crdownload for Chrome and Edge, .part for Firefox) and rename it when done, so a growing
/// temp file is a download in progress and its rename is the finish.
/// </summary>
internal sealed class DownloadService : IDisposable
{
    private static readonly string[] TempSuffixes = [".crdownload", ".part", ".partial", ".download", ".opdownload"];
    private readonly Dictionary<string, (long Bytes, DateTime At, double Rate)> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private FileSystemWatcher? _watcher;

    public string? Folder { get; } = Native.KnownFolder(Native.FolderDownloads);

    public DownloadProgress? Current
    {
        get
        {
            lock (_gate)
            {
                var top = _active.OrderByDescending(a => a.Value.At).FirstOrDefault();
                return top.Key == null ? null : new DownloadProgress(FinalName(Path.GetFileName(top.Key)), top.Value.Bytes, top.Value.Rate);
            }
        }
    }

    /// <summary>Full path of a finished download.</summary>
    public event Action<string>? Finished;
    public event Action? Changed;

    public void Start()
    {
        if (Folder == null || !Directory.Exists(Folder)) return;
        _watcher = new FileSystemWatcher(Folder) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size };
        _watcher.Created += (_, e) => Touch(e.FullPath);
        _watcher.Changed += (_, e) => Touch(e.FullPath);
        _watcher.Renamed += (_, e) =>
        {
            bool was;
            lock (_gate) was = _active.Remove(e.OldFullPath);
            if (was && !IsTemp(e.FullPath)) Finished?.Invoke(e.FullPath);
            Changed?.Invoke();
        };
        _watcher.Deleted += (_, e) =>
        {
            lock (_gate) _active.Remove(e.FullPath);
            Changed?.Invoke();
        };
        _watcher.EnableRaisingEvents = true;

        // Sizes don't always raise events while a browser writes; sample them once a second.
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                string[] paths;
                lock (_gate) paths = [.. _active.Keys];
                foreach (var p in paths) Touch(p);
                // Abandoned temp files (cancelled downloads) drop out after a minute of no growth.
                lock (_gate)
                    foreach (var stale in _active.Where(a => DateTime.Now - a.Value.At > TimeSpan.FromMinutes(1)).Select(a => a.Key).ToList())
                        _active.Remove(stale);
                if (paths.Length > 0) Changed?.Invoke();
            }
        });
    }

    private void Touch(string path)
    {
        if (!IsTemp(path)) return;
        long size;
        try { size = new FileInfo(path).Length; }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        lock (_gate)
        {
            var now = DateTime.Now;
            if (_active.TryGetValue(path, out var old))
            {
                if (size == old.Bytes) return;
                var secs = Math.Max(0.25, (now - old.At).TotalSeconds);
                _active[path] = (size, now, (size - old.Bytes) / secs);
            }
            else _active[path] = (size, now, 0);
        }
    }

    private static bool IsTemp(string path) => TempSuffixes.Any(s => path.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    /// <summary>"Unconfirmed 123.crdownload" or "setup.exe.crdownload" → a readable name.</summary>
    private static string FinalName(string temp)
    {
        var name = TempSuffixes.Aggregate(temp, (n, s) => n.EndsWith(s, StringComparison.OrdinalIgnoreCase) ? n[..^s.Length] : n);
        return name.StartsWith("Unconfirmed ", StringComparison.Ordinal) ? "Download" : name;
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };

    public void Dispose()
    {
        _cts.Cancel();
        _watcher?.Dispose();
    }
}
