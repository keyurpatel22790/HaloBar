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
