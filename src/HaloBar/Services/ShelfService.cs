using HaloBar.Platform;
using SkiaSharp;

namespace HaloBar.Services;

internal sealed record ShelfFile(string Path, DateTime Added)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public bool Exists => File.Exists(Path) || Directory.Exists(Path);
}

/// <summary>
/// Files parked on the island: dropped onto it, or screenshots and recordings it made. The list
/// survives restarts; thumbnails are made on a worker thread and cached.
/// </summary>
internal sealed class ShelfService(AppSettings settings)
{
    private const int MaxFiles = 30;
    private readonly Dictionary<string, SKImage?> _thumbnails = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loading = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public IReadOnlyList<ShelfFile> Files => settings.ShelfFiles.Select(p => new ShelfFile(p.Path, p.Added)).Where(f => f.Exists).ToList();
    public event Action? Changed;

    public void Add(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            settings.ShelfFiles.RemoveAll(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            settings.ShelfFiles.Insert(0, new SavedShelfFile { Path = path, Added = DateTime.Now });
        }
        while (settings.ShelfFiles.Count > MaxFiles) settings.ShelfFiles.RemoveAt(settings.ShelfFiles.Count - 1);
        settings.Save();
        Changed?.Invoke();
    }

    public void Remove(ShelfFile file)
    {
        settings.ShelfFiles.RemoveAll(f => f.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase));
        settings.Save();
        Changed?.Invoke();
    }

    public void Clear()
    {
        settings.ShelfFiles.Clear();
        settings.Save();
        Changed?.Invoke();
    }

    /// <summary>The file's thumbnail if ready; starts making it otherwise.</summary>
    public SKImage? Thumbnail(string path)
    {
        lock (_gate)
        {
            if (_thumbnails.TryGetValue(path, out var image)) return image;
            if (!_loading.Add(path)) return null;
        }
        Task.Run(() =>
        {
            // COM shell objects want an STA-friendly caller; a pool thread is MTA, which the
            // image factory also supports.
            var image = ShellThumbnail.For(path, 192);
            lock (_gate)
            {
                _thumbnails[path] = image;
                _loading.Remove(path);
            }
            Changed?.Invoke();
        });
        return null;
    }
}

internal sealed class SavedShelfFile
{
    public string Path { get; set; } = "";
    public DateTime Added { get; set; }
}
