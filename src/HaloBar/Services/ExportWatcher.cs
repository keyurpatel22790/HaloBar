namespace HaloBar.Services;

/// <summary>
/// Watches the folders apps export and render into (Premiere, Resolve, Lightroom, OBS…). A new
/// file counts as finished once its size has stopped changing for a few seconds and it can be
/// opened, so a long render reports once, at the end.
/// </summary>
internal sealed class ExportWatcher(Func<IReadOnlyList<string>> folders) : IDisposable
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(4);
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Size, DateTime Since)> _growing = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>A file finished writing (any thread).</summary>
    public event Action<string>? Finished;

    /// <summary>Watches exactly the folders in the settings; call again after they change. Once a second is fine.</summary>
    public void Sync()
    {
        var wanted = folders().Where(Directory.Exists).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _watchers.Keys.Where(k => !wanted.Contains(k)).ToList())
        {
            _watchers[gone].Dispose();
            _watchers.Remove(gone);
        }
        foreach (var folder in wanted.Where(f => !_watchers.ContainsKey(f)))
        {
            try
            {
                var watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size };
                watcher.Created += (_, e) => Track(e.FullPath);
                watcher.Changed += (_, e) => Track(e.FullPath);
                watcher.Renamed += (_, e) => Track(e.FullPath);
                watcher.EnableRaisingEvents = true;
                _watchers[folder] = watcher;
                Log.Write($"Export folder watched: {folder}");
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                Log.Write($"Export folder not watched: {folder}: {ex.Message}");
            }
        }
        Check();
    }

    private void Track(string path)
    {
        // Temporary and partial files that renderers write before the real one.
        var name = Path.GetFileName(path);
        if (name.StartsWith('~') || name.StartsWith('.') || path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".part", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase)) return;
        lock (_gate) _growing[path] = (-1, DateTime.Now);
    }

    /// <summary>Reports files whose size has held still for a few seconds.</summary>
    private void Check()
    {
        List<string> done = [];
        lock (_gate)
        {
            foreach (var (path, (size, since)) in _growing.ToList())
            {
                long now;
                try { now = new FileInfo(path).Length; }
                catch (IOException) { _growing.Remove(path); continue; }
                if (!File.Exists(path)) { _growing.Remove(path); continue; }
                if (now != size) { _growing[path] = (now, DateTime.Now); continue; }
                if (DateTime.Now - since < Settle || now == 0) continue;
                try { using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)) { } }
                catch (IOException) { continue; } // still being written
                _growing.Remove(path);
                done.Add(path);
            }
        }
        foreach (var path in done) Finished?.Invoke(path);
    }

    public void Dispose()
    {
        foreach (var w in _watchers.Values) w.Dispose();
        _watchers.Clear();
    }
}
