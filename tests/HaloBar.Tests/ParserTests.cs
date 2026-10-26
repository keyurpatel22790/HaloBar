using System.Text;
using HaloBar.Rendering;
using HaloBar.Services;
using HaloBar.UI;
using SkiaSharp;

namespace HaloBar.Tests;

public class ReminderParsingTests
{
    private static readonly DateTime Now = new(2026, 10, 24, 14, 0, 0);

    [Theory]
    [InlineData("stretch in 45m", "Stretch", 2026, 10, 24, 14, 45)]
    [InlineData("call mum in 2 hours", "Call mum", 2026, 10, 24, 16, 0)]
    [InlineData("call mum at 6:30pm", "Call mum", 2026, 10, 24, 18, 30)]
    [InlineData("water plants 18:00", "Water plants", 2026, 10, 24, 18, 0)]
    [InlineData("tomorrow 9am pay rent", "Pay rent", 2026, 10, 25, 9, 0)]
    [InlineData("stand-up at 9", "Stand-up", 2026, 10, 25, 9, 0)] // nine has passed today
    [InlineData("tomorrow dentist", "Dentist", 2026, 10, 25, 9, 0)]
    public void Understands_plain_words(string sentence, string text, int y, int mo, int d, int h, int mi)
    {
        var (parsedText, due) = ReminderService.Parse(sentence, Now);
        Assert.Equal(text, parsedText);
        Assert.Equal(new DateTime(y, mo, d, h, mi, 0), due);
    }

    [Fact]
    public void Without_a_time_it_is_due_in_an_hour()
    {
        var (text, due) = ReminderService.Parse("buy milk", Now);
        Assert.Equal("Buy milk", text);
        Assert.Equal(Now.AddHours(1), due);
    }
}

public class CalendarTests
{
    private const string Ics = """
        BEGIN:VCALENDAR
        BEGIN:VEVENT
        DTSTART:20261026T093000Z
        DTEND:20261026T100000Z
        SUMMARY:Stand-up\, team
        LOCATION:https://meet.google.com/abc-defg-hij
        END:VEVENT
        BEGIN:VEVENT
        DTSTART;VALUE=DATE:20261027
        DTEND;VALUE=DATE:20261028
        SUMMARY:Holiday
        END:VEVENT
        BEGIN:VEVENT
        DTSTART;TZID=India Standard Time:20261020T150000
        DTEND;TZID=India Standard Time:20261020T153000
        RRULE:FREQ=WEEKLY;BYDAY=MO,WE
        SUMMARY:Review
        DESCRIPTION:Join https://us02web.zoom.us/j/81234567890 now
        END:VEVENT
        BEGIN:VEVENT
        DTSTART:20261026T120000Z
        SUMMARY:Cancelled thing
        STATUS:CANCELLED
        END:VEVENT
        END:VCALENDAR
        """;

    [Fact]
    public void Reads_events_repeats_and_join_links()
    {
        var events = CalendarService.Parse(Ics.Replace("\n", "\r\n"), new DateTime(2026, 10, 25), new DateTime(2026, 11, 1)).ToList();
        var standup = Assert.Single(events, e => e.Title == "Stand-up, team");
        Assert.Equal("https://meet.google.com/abc-defg-hij", standup.JoinUrl);
        Assert.Equal(TimeSpan.FromMinutes(30), standup.End - standup.Start);

        var holiday = Assert.Single(events, e => e.Title == "Holiday");
        Assert.True(holiday.AllDay);

        // Weekly on Monday and Wednesday: 26th and 28th fall in the window.
        var reviews = events.Where(e => e.Title == "Review").ToList();
        Assert.Equal(2, reviews.Count);
        Assert.All(reviews, r => Assert.StartsWith("https://us02web.zoom.us/j/81234567890", r.JoinUrl));
        Assert.DoesNotContain(events, e => e.Title == "Cancelled thing");
    }

    [Fact]
    public void Finds_teams_links()
    {
        var url = CalendarService.FindJoinLink("Click https://teams.microsoft.com/l/meetup-join/19%3ameeting_abc/0?context=x to join");
        Assert.StartsWith("https://teams.microsoft.com/l/meetup-join/", url);
    }
}

public class LyricsTests
{
    [Fact]
    public void Parses_synced_lyrics_in_order()
    {
        var lines = LyricsService.ParseLrc("[00:12.50] Second\n[00:01.00]First\n[00:20.00][00:30.00] Chorus\n[00:40.00]");
        Assert.Equal(["First", "Second", "Chorus", "Chorus", "♪"], lines.Select(l => l.Text));
        Assert.Equal(TimeSpan.FromSeconds(12.5), lines[1].At);
    }
}

public class ChatTests
{
    [Fact]
    public void Parses_a_twitch_message_with_tags()
    {
        var m = ChatService.Parse("@badge-info=;color=#1E90FF;display-name=CoolUser;emotes= :cooluser!cooluser@cooluser.tmi.twitch.tv PRIVMSG #chan :hello there :)");
        Assert.NotNull(m);
        Assert.Equal("CoolUser", m.Name);
        Assert.Equal("hello there :)", m.Text);
        Assert.Equal(new SKColor(0x1E, 0x90, 0xFF), m.Color);
    }

    [Fact]
    public void Ignores_other_lines() => Assert.Null(ChatService.Parse(":tmi.twitch.tv 001 justinfan1 :Welcome"));
}

public class UploadTests
{
    [Fact]
    public void Saves_files_from_a_multipart_form()
    {
        var folder = Path.Combine(Path.GetTempPath(), "halobar-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var body = Encoding.UTF8.GetBytes(
                "--XyZ\r\nContent-Disposition: form-data; name=\"f\"; filename=\"photo.jpg\"\r\nContent-Type: image/jpeg\r\n\r\nJPEGDATA\r\n" +
                "--XyZ\r\nContent-Disposition: form-data; name=\"f\"; filename=\"../evil.txt\"\r\n\r\nhi\r\n--XyZ--\r\n");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = "multipart/form-data; boundary=XyZ" };
            var saved = MiniHttp.SaveMultipart(headers, body, folder);
            Assert.Equal(2, saved.Length);
            Assert.Equal("JPEGDATA", File.ReadAllText(Path.Combine(folder, "photo.jpg")));
            // A path in the name can't escape the folder.
            Assert.Equal("hi", File.ReadAllText(Path.Combine(folder, "evil.txt")));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
}

public class PaletteTests
{
    [Fact]
    public void Finds_the_dominant_colour_of_a_cover()
    {
        using var bitmap = new SKBitmap(64, 64);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(0xE0, 0x20, 0x30)); // mostly red
            using var paint = new SKPaint { Color = new SKColor(0x20, 0x40, 0xE0) };
            canvas.DrawRect(0, 0, 16, 16, paint);       // a little blue
        }
        using var image = SKImage.FromBitmap(bitmap);
        var palette = ArtPalette.From(image)!;
        palette.Vibrant.ToHsv(out var hue, out _, out var value);
        Assert.True(hue < 15 || hue > 345, $"expected red, got hue {hue}");
        Assert.True(value >= 85, $"expected a bright accent, got value {value}"); // byte rounding trims a little
        palette.Deep.ToHsv(out _, out _, out var deep);
        Assert.True(deep <= 20);
        Assert.Equal(3, palette.Swatches.Length);
    }
}

public class AgentTests
{
    [Fact]
    public void Follows_a_claude_session_through_its_hooks()
    {
        var agents = new AgentService(47900);
        var changes = new List<(AgentState State, AgentState? Before)>();
        agents.StateChanged += (s, before) => changes.Add((s.State, before));

        agents.OnClaudeEvent("""{"session_id":"s1","hook_event_name":"UserPromptSubmit","cwd":"C:\\code\\HaloBar"}""");
        agents.OnClaudeEvent("""{"session_id":"s1","hook_event_name":"PreToolUse","tool_name":"Edit","tool_input":{"file_path":"C:\\code\\HaloBar\\Program.cs"}}""");
        var session = Assert.Single(agents.Sessions);
        Assert.Equal("HaloBar", session.Project);
        Assert.Equal("Editing Program.cs", session.Doing);

        agents.OnClaudeEvent("""{"session_id":"s1","hook_event_name":"Notification","message":"Claude needs your permission to use Bash"}""");
        agents.OnClaudeEvent("""{"session_id":"s1","hook_event_name":"Stop"}""");
        Assert.Equal([(AgentState.Working, null), (AgentState.Waiting, AgentState.Working), (AgentState.Done, AgentState.Waiting)], changes);
    }
}

public class StatsTests
{
    [Theory]
    [InlineData(0, "0B/s")]
    [InlineData(512, "512B/s")]
    [InlineData(2_000, "2KB/s")]
    [InlineData(999_000, "999KB/s")]
    [InlineData(4_900_000, "4.9MB/s")]
    [InlineData(49_100_000, "49MB/s")]
    [InlineData(1_250_000_000, "1.3GB/s")]
    public void Network_speeds_stay_short(double bytesPerSec, string expected) =>
        Assert.Equal(expected, StatsService.Rate(bytesPerSec));
}

public class TextTests
{
    [Fact]
    public void Invisible_marks_are_dropped_but_emoji_joiners_stay()
    {
        // Phone Link wraps names in first-strong-isolate / pop-directional-isolate.
        Assert.Equal("WhatsApp", Theme.Visible("⁨WhatsApp⁩"));
        Assert.Equal("ab", Theme.Visible("a​b‎﻿"));
        Assert.Equal("👨‍👩", Theme.Visible("👨‍👩"));
        Assert.Same("plain", Theme.Visible("plain"));
    }

    [Fact]
    public void Splits_lines_into_direction_runs()
    {
        var runs = Theme.DirectionRuns("Hello مرحبا world 42");
        Assert.Equal([("Hello ", false), ("مرحبا ", true), ("world 42", false)], runs);
        Assert.Single(Theme.DirectionRuns("સ્ટેશન ક્યાં છે?"));
        Assert.True(Theme.NeedsShaping("नमस्ते"));
        Assert.False(Theme.NeedsShaping("Hello, Привет"));
    }
}

public class CalculatorTests
{
    [Theory]
    [InlineData("2+3*4", "14")]
    [InlineData("(2+3)*4", "20")]
    [InlineData("2^3^2", "512")]
    [InlineData("-2^2", "-4")]
    [InlineData("2^-1", "0.5")]
    [InlineData("18% of 2500", "450")]
    [InlineData("2500 + 18%", "2,950")]
    [InlineData("2500 - 10%", "2,250")]
    [InlineData("50%", "0.5")]
    [InlineData("1,000 × 3 ÷ 4", "750")]
    [InlineData("sqrt(16) + 2pi", "10.28318531")]
    [InlineData("sin 30", "0.5")]
    [InlineData("5!", "120")]
    [InlineData("0.1+0.2", "0.3")]
    [InlineData("10/3", "3.333333333")]
    public void Works_out_sums(string sum, string expected)
    {
        Assert.True(Calculator.TryEvaluate(sum, out var value));
        Assert.Equal(expected, Calculator.Format(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("2+")]
    [InlineData("(2")]
    [InlineData("hello")]
    [InlineData("1/0")]
    public void Rejects_what_isnt_a_sum(string text) => Assert.False(Calculator.TryEvaluate(text, out _));
}

public class ConverterTests
{
    [Theory]
    [InlineData("5 km to mi", "3.10686 mi")]
    [InlineData("100 f to c", "37.7778 °C")]
    [InlineData("0 c in f", "32 °F")]
    [InlineData("70 kg lb", "154.324 lb")]
    [InlineData("2 GB to MB", "2,048 MB")]
    [InlineData("6 ft to cm", "182.88 cm")]
    [InlineData("90 min to h", "1.5 h")]
    [InlineData("100 km/h to mph", "62.1371 mph")]
    public void Converts_units(string text, string expected) => Assert.Equal(expected, UnitConverter.Convert(text));

    [Theory]
    [InlineData("5 km to kg")]
    [InlineData("hello")]
    [InlineData("5 usd to inr")]
    public void Leaves_other_text_alone(string text) => Assert.Null(UnitConverter.Convert(text));
}

public class HomeTileTests
{
    private static readonly DateTime Today = new(2026, 10, 26);

    [Theory]
    [InlineData("Trip to Goa 20 Dec", "Trip to Goa", 2026, 12, 20)]
    [InlineData("Diwali 8 Nov 2026", "Diwali", 2026, 11, 8)]
    [InlineData("Birthday 14/02", "Birthday", 2027, 2, 14)] // already passed this year
    [InlineData("Launch 2027-03-01", "Launch", 2027, 3, 1)]
    [InlineData("1 Jan", "", 2027, 1, 1)]
    public void Countdown_reads_a_title_and_date(string text, string title, int y, int m, int d)
    {
        var parsed = UI.Tiles.CountdownTile.Parse(text, Today);
        Assert.NotNull(parsed);
        Assert.Equal(title, parsed.Value.Title);
        Assert.Equal(new DateTime(y, m, d), parsed.Value.Date);
    }

    [Fact]
    public void Moon_phase_matches_known_dates()
    {
        // Full moon 2026-10-26 04:12 UTC; new moon 2026-11-09 07:02 UTC.
        Assert.Equal("Full moon", UI.Tiles.Moon.Name(UI.Tiles.Moon.Phase(new DateTime(2026, 10, 26, 4, 12, 0, DateTimeKind.Utc))));
        Assert.Equal("New moon", UI.Tiles.Moon.Name(UI.Tiles.Moon.Phase(new DateTime(2026, 11, 9, 7, 2, 0, DateTimeKind.Utc))));
    }

    [Theory]
    [InlineData("સ્ટેશન ક્યાં છે?", "gu")]
    [InlineData("स्टेशन कहाँ है?", "hi")]
    [InlineData("Привет", "ru")]
    [InlineData("Hello", null)]
    public void Guesses_language_from_script(string text, string? code) => Assert.Equal(code, WebTools.DetectScript(text));
}

public class BannerTests
{
    [Fact]
    public void Turns_banners_off_and_puts_back_only_what_it_changed()
    {
        var root = $@"Software\HaloBarTests\{Guid.NewGuid():N}";
        try
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{root}\Mine")) k.SetValue("ShowBanner", 0); // the user's own choice
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{root}\WasOn")) k.SetValue("ShowBanner", 1);
            var settings = new HaloBar.AppSettings { QuietWindowsBanners = true };
            var banners = new BannerService(settings, root, save: () => { }); // never touch the real settings.json

            Assert.True(banners.Quiet("Fresh"));          // no key at all yet
            Assert.True(banners.Quiet("WasOn"));
            Assert.False(banners.Quiet("Mine"));          // already off: not ours to undo
            Assert.False(banners.Quiet("Windows.SystemToast.BatterySaver"));
            Assert.False(banners.Quiet("Fresh"));         // second time: nothing to do
            Assert.Equal(new Dictionary<string, int> { ["Fresh"] = -1, ["WasOn"] = 1 }, settings.QuietedBanners);
            Assert.Equal(0, Read(root, "Fresh"));

            Assert.Equal(2, banners.RestoreAll());
            Assert.Null(Read(root, "Fresh"));
            Assert.Equal(1, Read(root, "WasOn"));
            Assert.Equal(0, Read(root, "Mine"));
            Assert.Empty(settings.QuietedBanners);

            Assert.True(banners.Quiet(@"{6D809377-6AF0-444B-8957-A3773F02200E}\Tool\tool.exe"));
            Assert.Equal(0, Read(root, @"{6D809377-6AF0-444B-8957-A3773F02200E}\Tool\tool.exe")); // nested keys, as Windows keeps them
            settings.QuietWindowsBanners = false;
            Assert.False(banners.Quiet("Other"));
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        }
    }

    private static object? Read(string root, string app)
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"{root}\{app}");
        return k?.GetValue("ShowBanner");
    }
}

public class SpringTests
{
    [Fact]
    public void Settles_on_its_target()
    {
        var s = new Spring(0) { Target = 100 };
        for (var i = 0; i < 120; i++) s.Step(1 / 60.0);
        Assert.True(s.Settled);
        Assert.Equal(100, s.Value);
    }
}
