using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HaloBar.Services;

internal sealed record LyricLine(TimeSpan At, string Text);

/// <summary>
/// Time-synced lyrics from LRCLIB (lrclib.net), a free community library. Only the title, artist,
/// album and length of the playing song are sent, once per song; results are cached.
/// </summary>
internal sealed partial class LyricsService : IDisposable
{
    private const int CacheSize = 50;
    private readonly HttpClient _http = new() { BaseAddress = new Uri("https://lrclib.net/api/"), Timeout = TimeSpan.FromSeconds(10) };
    private readonly Dictionary<string, IReadOnlyList<LyricLine>> _cache = [];
    private readonly Queue<string> _order = new();
    private readonly object _gate = new();
    private string _requested = "";

    public IReadOnlyList<LyricLine>? Current { get; private set; }
    public string Status { get; private set; } = "";
    public event Action? Changed;

    public LyricsService() => _http.DefaultRequestHeaders.UserAgent.ParseAdd("HaloBar/0.5 (https://github.com/)");

    /// <summary>Asks for the lyrics of a song; a no-op when it is the song already asked for.</summary>
    public void Request(string title, string artist, string album, TimeSpan duration)
    {
        var key = $"{artist}\u0001{title}";
        lock (_gate)
        {
            if (key == _requested) return;
            _requested = key;
            if (_cache.TryGetValue(key, out var cached))
            {
                Current = cached;
                Status = cached.Count == 0 ? "No lyrics for this one" : "";
                Changed?.Invoke();
                return;
            }
        }
        Current = null;
        Status = "Looking for lyrics…";
        Changed?.Invoke();
        _ = Task.Run(async () =>
        {
            var lines = await LookUpAsync(title, artist, album, duration);
            lock (_gate)
            {
                Remember(key, lines ?? []);
                if (_requested != key) return; // the song changed meanwhile
            }
            Current = lines;
            Status = lines == null ? "Lyrics unavailable right now" : lines.Count == 0 ? "No lyrics for this one" : "";
            Changed?.Invoke();
        });
    }

    /// <summary>The line being sung at <paramref name="position"/>.</summary>
    public string? LineAt(TimeSpan position)
    {
        var lines = Current;
        if (lines == null || lines.Count == 0) return null;
        string? line = null;
        foreach (var l in lines)
        {
            if (l.At > position) break;
            line = l.Text;
        }
        return line;
    }

    private async Task<IReadOnlyList<LyricLine>?> LookUpAsync(string title, string artist, string album, TimeSpan duration)
    {
        try
        {
            var query = $"get?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}" +
                        (album.Length > 0 ? $"&album_name={Uri.EscapeDataString(album)}" : "") +
                        (duration > TimeSpan.Zero ? $"&duration={(int)duration.TotalSeconds}" : "");
            using var response = await _http.GetAsync(query);
            string? synced = null;
            if (response.IsSuccessStatusCode)
                synced = Synced(await response.Content.ReadAsStringAsync());
            if (synced == null)
            {
                // Browsers often report "Artist - Title" as the title; a free-text search copes with that.
                var search = await _http.GetStringAsync($"search?q={Uri.EscapeDataString($"{artist} {title}".Trim())}");
                using var doc = JsonDocument.Parse(search);
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String)
                    {
                        synced = s.GetString();
                        break;
                    }
                }
            }
            return synced == null ? [] : ParseLrc(synced);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Log.Write($"Lyrics: {ex.Message}");
            return null;
        }
    }

    private static string? Synced(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
    }

    [GeneratedRegex(@"\[(\d+):(\d+(?:\.\d+)?)\]")]
    private static partial Regex Stamp();

    internal static IReadOnlyList<LyricLine> ParseLrc(string lrc)
    {
        var lines = new List<LyricLine>();
        foreach (var raw in lrc.Split('\n'))
        {
            var stamps = Stamp().Matches(raw);
            if (stamps.Count == 0) continue;
            var text = raw[(stamps[^1].Index + stamps[^1].Length)..].Trim();
            foreach (Match m in stamps)
            {
                var at = TimeSpan.FromMinutes(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                         + TimeSpan.FromSeconds(double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
                lines.Add(new LyricLine(at, text.Length == 0 ? "♪" : text));
            }
        }
        lines.Sort((a, b) => a.At.CompareTo(b.At));
        return lines;
    }

    private void Remember(string key, IReadOnlyList<LyricLine> lines)
    {
        if (_cache.TryAdd(key, lines)) _order.Enqueue(key);
        while (_order.Count > CacheSize) _cache.Remove(_order.Dequeue());
    }

    public void Dispose() => _http.Dispose();
}
