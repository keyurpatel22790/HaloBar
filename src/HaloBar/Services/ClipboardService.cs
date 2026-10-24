using System.Text.Json;
using System.Text.Json.Serialization;
using HaloBar.Platform;
using HaloBar.Rendering;

namespace HaloBar.Services;

internal enum ClipKind { Text, Files, Image }

internal sealed record ClipEntry(ClipKind Kind, ClipboardContent Content, DateTime Time)
{
    public string Preview => Kind switch
    {
        ClipKind.Text => Content.Text!,
        ClipKind.Files => string.Join("\n", Content.Files!.Select(Path.GetFileName)),
        _ => $"Picture {Content.Image?.Width}×{Content.Image?.Height}",
    };
}

/// <summary>
/// Keeps the last few things copied. Runs on the window thread: Windows posts
/// WM_CLIPBOARDUPDATE to the host window, which calls <see cref="OnClipboardUpdate"/>.
/// Text and file entries survive restarts; pictures are kept in memory only, as a small
/// thumbnail plus their PNG encoding (a 1080p screenshot is ~8 MB raw, well under 1 MB as PNG).
/// </summary>
internal sealed class ClipboardService
{
    private const int MaxTextLength = 20_000;
    private const int ThumbnailSize = 320;
    private static readonly string FilePath = Path.Combine(AppPaths.Folder, "clipboard.json");

    private readonly IntPtr _hwnd;
    private readonly int _capacity;
    private readonly Action<Action> _post;
    private readonly List<ClipEntry> _entries = [];
    private int _ignoreUpdates;

    public IReadOnlyList<ClipEntry> Entries => _entries;
    public event Action? Changed;
    /// <summary>A picture was copied (screenshots arrive this way).</summary>
    public event Action<ClipEntry>? PictureAdded;

    /// <param name="post">Runs an action on the window thread.</param>
    public ClipboardService(IntPtr hwnd, int capacity, Action<Action> post)
    {
        _hwnd = hwnd;
        _capacity = Math.Max(1, capacity);
        _post = post;
        Load();
    }

    public void Start()
    {
        if (!Native.AddClipboardFormatListener(_hwnd))
            Log.Write("Clipboard: could not listen for changes");
    }

    public void OnClipboardUpdate()
    {
        if (_ignoreUpdates > 0)
        {
            _ignoreUpdates--;
            return;
        }
        ClipboardContent? content;
        try { content = ClipboardIO.Read(_hwnd); }
        catch (Exception ex)
        {
            Log.Write($"Clipboard read: {ex.Message}");
            return;
        }
        if (content == null) return;
        var time = DateTime.Now;

        if (content.Image is { } full)
        {
            // Shrinking and PNG-encoding a big screenshot takes tens of milliseconds: keep it off the frame loop.
            var png = content.Png;
            Task.Run(() =>
            {
                try
                {
                    var thumbnail = ImageTools.Shrink(full, ThumbnailSize);
                    var encoded = png ?? ImageTools.EncodePng(full);
                    _post(() => Add(new ClipEntry(ClipKind.Image, new ClipboardContent(null, null, thumbnail, encoded), time)));
                }
                catch (Exception ex)
                {
                    Log.Write($"Clipboard picture: {ex.Message}");
                }
                finally
                {
                    full.Dispose();
                }
            });
            return;
        }

        if (content.Text is { Length: > MaxTextLength } longText) content = content with { Text = longText[..MaxTextLength] };
        Add(new ClipEntry(content.Text != null ? ClipKind.Text : ClipKind.Files, content, time));
    }

    private void Add(ClipEntry entry)
    {
        // Copying the same thing again moves it to the front instead of duplicating it.
        _entries.RemoveAll(e => SameContent(e, entry));
        _entries.Insert(0, entry);
        while (_entries.Count > _capacity)
        {
            _entries[^1].Content.Image?.Dispose();
            _entries.RemoveAt(_entries.Count - 1);
        }
        Save();
        Changed?.Invoke();
        if (entry.Kind == ClipKind.Image) PictureAdded?.Invoke(entry);
    }

    /// <summary>Puts an old entry back on the clipboard and moves it to the front.</summary>
    public bool Restore(ClipEntry entry)
    {
        _ignoreUpdates++;
        if (!ClipboardIO.Write(_hwnd, entry.Content))
        {
            _ignoreUpdates--;
            return false;
        }
        _entries.Remove(entry);
        _entries.Insert(0, entry with { Time = DateTime.Now });
        Save();
        Changed?.Invoke();
        return true;
    }

    public IReadOnlyList<string> Snippets => _snippets();
    private Func<List<string>> _snippets = () => [];

    /// <summary>Pinned texts kept in settings, shown under the Pinned filter.</summary>
    public void UseSnippets(Func<List<string>> list, Action save) { _snippets = list; _saveSnippets = save; }
    private Action _saveSnippets = () => { };

    public bool IsPinned(string text) => _snippets().Contains(text);

    public void TogglePin(string text)
    {
        var list = _snippets();
        if (!list.Remove(text)) list.Insert(0, text);
        while (list.Count > 30) list.RemoveAt(list.Count - 1);
        _saveSnippets();
        Changed?.Invoke();
    }

    /// <summary>Puts text on the clipboard (it then shows up first in the history).</summary>
    public bool CopyText(string text) => ClipboardIO.Write(_hwnd, new ClipboardContent(text, null, null, null));

    public void Clear()
    {
        foreach (var e in _entries) e.Content.Image?.Dispose();
        _entries.Clear();
        Save();
        Changed?.Invoke();
    }

    private static bool SameContent(ClipEntry a, ClipEntry b) => a.Kind == b.Kind && a.Kind switch
    {
        ClipKind.Text => a.Content.Text == b.Content.Text,
        ClipKind.Files => a.Content.Files!.SequenceEqual(b.Content.Files!),
        _ => a.Content.Png is { } x && b.Content.Png is { } y && x.AsSpan().SequenceEqual(y),
    };

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var saved = JsonSerializer.Deserialize(File.ReadAllText(FilePath), ClipJson.Default.ListSavedClip) ?? [];
            foreach (var s in saved.Take(_capacity))
            {
                var content = new ClipboardContent(s.Text, s.Files, null, null);
                _entries.Add(new ClipEntry(s.Files != null ? ClipKind.Files : ClipKind.Text, content, s.Time));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Write($"Clipboard history unreadable: {ex.Message}");
        }
    }

    private void Save()
    {
        var saved = _entries.Where(e => e.Kind != ClipKind.Image)
            .Select(e => new SavedClip(e.Content.Text, e.Content.Files, e.Time)).ToList();
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(saved, ClipJson.Default.ListSavedClip)); }
        catch (IOException ex) { Log.Write($"Clipboard history not saved: {ex.Message}"); }
    }
}

internal sealed record SavedClip(string? Text, string[]? Files, DateTime Time);

[JsonSerializable(typeof(List<SavedClip>))]
internal partial class ClipJson : JsonSerializerContext;
