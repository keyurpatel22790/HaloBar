using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace HaloBar.Services;

internal sealed record CalendarEvent(string Title, DateTime Start, DateTime End, bool AllDay, string? Location, string? JoinUrl);

/// <summary>
/// Upcoming events from a calendar's secret iCal (ICS) link — Google Calendar, Outlook and iCloud
/// all offer one. Refreshes every 15 minutes. Handles time zones, all-day events and simple
/// daily/weekly repeats; finds Zoom, Meet, Teams and Webex links to join with one click.
/// </summary>
internal sealed partial class CalendarService : IDisposable
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(15);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly AppSettings _settings;
    private readonly CancellationTokenSource _cts = new();

    public IReadOnlyList<CalendarEvent> Events { get; private set; } = [];
    public string Status { get; private set; } = "";
    public event Action? Changed;

    public CalendarService(AppSettings settings) => _settings = settings;

    public bool Configured => !string.IsNullOrWhiteSpace(_settings.CalendarIcsUrl);

    public void Start() => _ = Task.Run(async () =>
    {
        using var timer = new PeriodicTimer(Every);
        do await RefreshAsync();
        while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false));
    });

    public async Task SetUrlAsync(string url)
    {
        _settings.CalendarIcsUrl = url.Trim();
        _settings.Save();
        await RefreshAsync();
    }

    /// <summary>The next event that hasn't ended, skipping all-day ones.</summary>
    public CalendarEvent? Next => Events.FirstOrDefault(e => !e.AllDay && e.End > DateTime.Now);

    public async Task RefreshAsync()
    {
        if (!Configured)
        {
            Events = [];
            Status = "";
            Changed?.Invoke();
            return;
        }
        try
        {
            var url = _settings.CalendarIcsUrl.Trim();
            if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url[9..];
            var ics = await _http.GetStringAsync(url);
            var now = DateTime.Now;
            Events = [.. Parse(ics, now.Date.AddDays(-40), now.Date.AddDays(70)).OrderBy(e => e.Start)];
            Status = Events.Count == 0 ? "Nothing in the next week" : "";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException)
        {
            Status = "Calendar link not reachable";
            Log.Write($"Calendar: {ex.Message}");
        }
        Changed?.Invoke();
    }

    internal static IEnumerable<CalendarEvent> Parse(string ics, DateTime from, DateTime to)
    {
        // Unfold: a line starting with a space or tab continues the previous one.
        var lines = Regex.Replace(ics.Replace("\r\n", "\n"), @"\n[ \t]", "").Split('\n');
        Dictionary<string, (string Params, string Value)>? ev = null;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line == "BEGIN:VEVENT") { ev = []; continue; }
            if (line == "END:VEVENT")
            {
                if (ev != null) foreach (var e in Expand(ev, from, to)) yield return e;
                ev = null;
                continue;
            }
            if (ev == null) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var head = line[..colon];
            var semi = head.IndexOf(';');
            var name = semi < 0 ? head : head[..semi];
            ev.TryAdd(name.ToUpperInvariant(), (semi < 0 ? "" : head[(semi + 1)..], line[(colon + 1)..]));
        }
    }

    private static IEnumerable<CalendarEvent> Expand(Dictionary<string, (string Params, string Value)> ev, DateTime from, DateTime to)
    {
        if (!ev.TryGetValue("DTSTART", out var startRaw)) yield break;
        if (ev.TryGetValue("STATUS", out var status) && status.Value.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase)) yield break;
        var (start, allDay) = ParseTime(startRaw.Params, startRaw.Value);
        var end = ev.TryGetValue("DTEND", out var endRaw) ? ParseTime(endRaw.Params, endRaw.Value).Time
            : allDay ? start.AddDays(1) : start.AddHours(1);
        var length = end - start;
        var title = Unescape(ev.GetValueOrDefault("SUMMARY").Value ?? "Event");
        var location = ev.TryGetValue("LOCATION", out var loc) ? Unescape(loc.Value) : null;
        var join = FindJoinLink(string.Join(" ", ev.GetValueOrDefault("LOCATION").Value, ev.GetValueOrDefault("DESCRIPTION").Value,
            ev.GetValueOrDefault("URL").Value, ev.GetValueOrDefault("X-GOOGLE-CONFERENCE").Value));

        foreach (var s in Occurrences(start, ev.GetValueOrDefault("RRULE").Value, from, to))
            if (s + length >= from && s <= to)
                yield return new CalendarEvent(title, s, s + length, allDay, location, join);
    }

    /// <summary>The start times of an event within the window, following DAILY and WEEKLY rules.</summary>
    private static IEnumerable<DateTime> Occurrences(DateTime start, string? rrule, DateTime from, DateTime to)
    {
        if (string.IsNullOrEmpty(rrule))
        {
            yield return start;
            yield break;
        }
        var parts = rrule.Split(';').Select(p => p.Split('=')).Where(p => p.Length == 2).ToDictionary(p => p[0].ToUpperInvariant(), p => p[1]);
        var freq = parts.GetValueOrDefault("FREQ");
        var interval = int.TryParse(parts.GetValueOrDefault("INTERVAL"), out var i) && i > 0 ? i : 1;
        var count = int.TryParse(parts.GetValueOrDefault("COUNT"), out var c) ? c : int.MaxValue;
        var until = parts.TryGetValue("UNTIL", out var u) ? ParseTime("", u).Time : DateTime.MaxValue;
        var days = parts.TryGetValue("BYDAY", out var by)
            ? by.Split(',').Select(d => d[^2..]).Select(Day).Where(d => d != null).Select(d => d!.Value).ToHashSet()
            : [start.DayOfWeek];

        if (freq is not ("DAILY" or "WEEKLY"))
        {
            yield return start; // other rules (monthly, yearly) show their first time only
            yield break;
        }
        var n = 0;
        for (var day = start.Date; day <= to && day <= until.Date && n < count; day = day.AddDays(1))
        {
            var weeks = (day - start.Date).Days / 7;
            var match = freq == "DAILY"
                ? (day - start.Date).Days % interval == 0
                : weeks % interval == 0 && days.Contains(day.DayOfWeek);
            if (!match) continue;
            n++;
            var s = day + start.TimeOfDay;
            if (s >= from.AddDays(-1)) yield return s;
        }
    }

    private static DayOfWeek? Day(string d) => d switch
    {
        "MO" => DayOfWeek.Monday, "TU" => DayOfWeek.Tuesday, "WE" => DayOfWeek.Wednesday, "TH" => DayOfWeek.Thursday,
        "FR" => DayOfWeek.Friday, "SA" => DayOfWeek.Saturday, "SU" => DayOfWeek.Sunday, _ => null,
    };

    /// <summary>An iCal time as local time: UTC ("Z"), in a named zone (TZID), floating, or a date.</summary>
    private static (DateTime Time, bool AllDay) ParseTime(string parameters, string value)
    {
        value = value.Trim();
        if (value.Length == 8 && DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return (date, true);
        var utc = value.EndsWith('Z');
        if (!DateTime.TryParseExact(value.TrimEnd('Z'), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            return (DateTime.MinValue, false);
        if (utc) return (DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime(), false);
        var tzid = Regex.Match(parameters, "TZID=\"?([^;\"]+)").Groups[1].Value;
        if (tzid.Length > 0 && TimeZoneInfo.TryFindSystemTimeZoneById(tzid, out var zone))
            return (TimeZoneInfo.ConvertTime(t, zone, TimeZoneInfo.Local), false);
        return (t, false);
    }

    [GeneratedRegex(@"https://(?:[\w-]+\.)?(?:zoom\.us/(?:j|my|w)/[^\s""<>\\]+|meet\.google\.com/[a-z\-]+|teams\.microsoft\.com/l/meetup-join/[^\s""<>\\]+|[\w-]+\.webex\.com/[^\s""<>\\]+)", RegexOptions.IgnoreCase)]
    private static partial Regex JoinLink();

    internal static string? FindJoinLink(string text) => JoinLink().Match(text.Replace("\\n", " ")) is { Success: true } m ? m.Value : null;

    private static string Unescape(string s) =>
        new StringBuilder(s).Replace("\\n", " ").Replace("\\N", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\").ToString().Trim();

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
    }
}
