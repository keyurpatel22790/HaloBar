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

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=0sfDth4lMLc&t=3", "0sfDth4lMLc")]
    [InlineData("https://youtu.be/0sfDth4lMLc", "0sfDth4lMLc")]
    [InlineData("https://www.youtube.com/live/0sfDth4lMLc?si=x", "0sfDth4lMLc")]
    [InlineData("https://www.youtube.com/@LofiGirl", null)]
    public void Finds_youtube_video_ids(string link, string? id) => Assert.Equal(id, ChatService.VideoId(link));

    [Fact]
    public void Tells_youtube_from_twitch()
    {
        Assert.True(ChatService.IsYouTube("@LofiGirl"));
        Assert.True(ChatService.IsYouTube("https://youtu.be/abc"));
        Assert.False(ChatService.IsYouTube("shroud"));
    }

    [Fact]
    public void Reads_youtube_chat_messages()
    {
        const string json = """
            {"continuationContents":{"liveChatContinuation":{
              "continuations":[{"timedContinuationData":{"continuation":"NEXT","timeoutMs":4000}}],
              "actions":[
                {"addChatItemAction":{"item":{"liveChatTextMessageRenderer":{"authorName":{"simpleText":"@Asha"},
                  "message":{"runs":[{"text":"hello "},{"emoji":{"emojiId":"👋","shortcuts":[":wave:"]}}]}}}}},
                {"addChatItemAction":{"item":{"liveChatPaidMessageRenderer":{"authorName":{"simpleText":"Ravi"},
                  "purchaseAmountText":{"simpleText":"₹100"},"message":{"runs":[{"text":"great stream"}]}}}}},
                {"addLiveChatTickerItemAction":{}}
              ]}}}
            """;
        var (messages, next, wait) = ChatService.ParseYouTube(json);
        Assert.Equal(["Asha: hello 👋", "Ravi: [₹100] great stream"], messages.Select(m => $"{m.Name}: {m.Text}"));
        Assert.Equal("NEXT", next);
        Assert.Equal(4000, wait);
    }
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

public class AgentHookTests
{
    [Fact]
    public void Follows_codex_and_gemini_sessions()
    {
        var agents = new AgentService(47900);
        agents.OnHookEvent("codex", """{"session_id":"c1","hook_event_name":"PreToolUse","cwd":"D:\\work\\Shop","tool_name":"Bash","tool_input":{"command":"npm test"}}""");
        agents.OnHookEvent("codex", """{"session_id":"c1","hook_event_name":"PermissionRequest","tool_name":"Bash","tool_input":{"description":"Delete the build folder"}}""");
        agents.OnHookEvent("gemini", """{"session_id":"g1","hook_event_name":"BeforeTool","cwd":"C:\\code\\Site","tool_name":"write_file","tool_input":{"file_path":"C:\\code\\Site\\index.html"}}""");
        agents.OnHookEvent("nobody", """{"session_id":"x","hook_event_name":"Stop"}""");

        var codex = Assert.Single(agents.Sessions, s => s.Agent == "Codex");
        Assert.Equal(AgentState.Waiting, codex.State);
        Assert.Equal("Wants to: Delete the build folder", codex.Doing);
        Assert.Equal("Shop", codex.Project);
        var gemini = Assert.Single(agents.Sessions, s => s.Agent == "Gemini CLI");
        Assert.Equal("Writing index.html", gemini.Doing);
        Assert.Equal(2, agents.Sessions.Count);

        agents.OnHookEvent("gemini", """{"session_id":"g1","hook_event_name":"AfterAgent"}""");
        Assert.Equal(AgentState.Done, gemini.State);
    }

    [Fact]
    public void Connecting_adds_only_halobars_hooks_and_disconnecting_takes_only_those()
    {
        var folder = Path.Combine(Path.GetTempPath(), "halobar-hooks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var agents = new AgentService(47900);
            var gemini = AgentService.Targets.Single(t => t.Id == "gemini");
            var settings = Path.Combine(folder, "settings.json");
            const string mine = """{"theme":"dark","hooks":{"BeforeTool":[{"matcher":"write_file","hooks":[{"type":"command","command":"my-scanner"}]}]}}""";
            File.WriteAllText(settings, mine);

            agents.Connect(gemini, settings);
            var connected = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(settings))!;
            Assert.Equal(2, connected["hooks"]!["BeforeTool"]!.AsArray().Count);       // theirs and ours
            Assert.Equal(5000, (int)connected["hooks"]!["AfterAgent"]![0]!["hooks"]![0]!["timeout"]!); // Gemini counts ms
            Assert.True(File.Exists(settings + ".halobar-backup"));

            agents.Connect(gemini, settings); // twice is the same as once
            Assert.Equal(2, System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(settings))!["hooks"]!["BeforeTool"]!.AsArray().Count);

            agents.Disconnect(gemini, settings);
            Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(mine), System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(settings))));

            // Codex: a hooks.json HaloBar made, with a Windows command, removed again on disconnect.
            var codex = AgentService.Targets.Single(t => t.Id == "codex");
            var hooksJson = Path.Combine(folder, "hooks.json");
            agents.Connect(codex, hooksJson);
            var hook = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(hooksJson))!["hooks"]!["Stop"]![0]!["hooks"]![0]!;
            Assert.StartsWith("curl.exe ", (string)hook["commandWindows"]!);
            Assert.Contains("/agent/codex?", (string)hook["command"]!);
            agents.Disconnect(codex, hooksJson);
            Assert.False(File.Exists(hooksJson));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}

public class WebSiteTests
{
    [Theory]
    [InlineData("Notion notion.so", "Notion", "https://notion.so/")]
    [InlineData("notion.so", "Notion", "https://notion.so/")]
    [InlineData("https://music.youtube.com", "Youtube", "https://music.youtube.com/")]
    [InlineData("My Mail https://mail.google.com/mail/u/0", "My Mail", "https://mail.google.com/mail/u/0")]
    public void Reads_what_was_typed(string text, string name, string url)
    {
        var site = HaloBar.Platform.WebSite.FromTyped(text);
        Assert.NotNull(site);
        Assert.Equal(name, site.Name);
        Assert.Equal(url, site.Url);
        Assert.Equal(site, HaloBar.Platform.WebSite.Parse(site.ToString()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("just words")]
    public void Ignores_text_without_an_address(string text) => Assert.Null(HaloBar.Platform.WebSite.FromTyped(text));
}

public class LanguageTests
{
    [Fact]
    public void Shows_text_in_the_chosen_language()
    {
        // The expected text comes from the language file itself, so improving a translation never breaks this.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!Directory.Exists(Path.Combine(root.FullName, "src", "HaloBar", "Languages"))) root = root.Parent!;
        var gu = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(root.FullName, "src", "HaloBar", "Languages", "gu.json")))!;
        try
        {
            Lang.Use("gu");
            Assert.Equal("gu", Lang.Current);
            Assert.Equal(gu["Settings"], Lang.T("Settings"));
            Assert.Equal(gu["+{0} more"].Replace("{0}", "3"), Lang.T("+3 more"));                 // a template with a number in it
            Assert.Equal(gu["Finding {0}…"].Replace("{0}", "Ahmedabad"), Lang.T("Finding Ahmedabad…"));
            Assert.Equal("Shape of You", Lang.T("Shape of You"));                                  // not ours: left alone
            Lang.Use("en");
            Assert.Equal("Settings", Lang.T("Settings"));
        }
        finally
        {
            Lang.Use("en");
        }
    }

    [Fact]
    public void Every_translation_keeps_its_placeholders()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!Directory.Exists(Path.Combine(root.FullName, "src", "HaloBar", "Languages"))) root = root.Parent!;
        var folder = Path.Combine(root.FullName, "src", "HaloBar", "Languages");
        foreach (var file in Directory.GetFiles(folder, "*.json"))
        {
            var table = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file))!;
            foreach (var (en, translated) in table)
            {
                var want = System.Text.RegularExpressions.Regex.Matches(en, @"\{\d+\}").Select(m => m.Value).Order();
                var got = System.Text.RegularExpressions.Regex.Matches(translated, @"\{\d+\}").Select(m => m.Value).Order();
                Assert.True(want.SequenceEqual(got), $"{Path.GetFileName(file)}: \"{en}\" → \"{translated}\"");
            }
        }
    }
}

public class PriceAlertTests
{
    [Fact]
    public void Reads_alerts_in_plain_words()
    {
        var alerts = PriceAlert.Parse("btc > 70,000; AAPL below 180.5\n^NSEI >= 25000; nonsense; ETH over $4000");
        Assert.Equal(["BTC > 70000", "AAPL < 180.5", "^NSEI > 25000", "ETH > 4000"], alerts.Select(a => a.ToString()));
        Assert.True(alerts[0].Met(70_000));
        Assert.False(alerts[0].Met(69_999));
        Assert.True(alerts[1].Met(180));
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

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+I", "Ctrl+Alt+I")]
    [InlineData("alt + ctrl + i", "Ctrl+Alt+I")]
    [InlineData("Shift+Win+F5", "Win+Shift+F5")]
    [InlineData("Ctrl+Alt+Num7", "Ctrl+Alt+Num7")]
    [InlineData("Ctrl+Shift+/", "Ctrl+Shift+/")]
    [InlineData("Ctrl+Space", "Ctrl+Space")]
    [InlineData("F9", "F9")]
    public void Reads_and_writes_shortcuts(string text, string expected) => Assert.Equal(expected, Gesture.Parse(text)?.ToString());

    [Theory]
    [InlineData("")]
    [InlineData("I")]          // no modifier: would eat normal typing
    [InlineData("Ctrl+Alt")]   // no key
    [InlineData("Ctrl+I+O")]   // two keys
    [InlineData("Ctrl+Banana")]
    public void Rejects_what_cant_be_a_shortcut(string text) => Assert.Null(Gesture.Parse(text));

    [Fact]
    public void Moving_a_shortcut_takes_it_from_the_action_that_had_it()
    {
        var settings = new HaloBar.AppSettings();
        var keys = new HotkeyService(settings, IntPtr.Zero, save: () => { }); // never touch the real settings.json
        Assert.Equal("Ctrl+Alt+I", keys.Text("open"));
        Assert.Equal("", keys.Text("next"));

        keys.Set("next", Gesture.Parse("Ctrl+Alt+I"));
        Assert.Equal("Ctrl+Alt+I", keys.Text("next"));
        Assert.Equal("", keys.Text("open"));

        keys.Set("open", Gesture.Parse("Ctrl+Alt+I"));   // back to its default: stored as no entry
        Assert.False(settings.Hotkeys.ContainsKey("open"));
        keys.Set("mic", null);
        Assert.Equal("", keys.Text("mic"));
        keys.Reset();
        Assert.Equal("Ctrl+Alt+M", keys.Text("mic"));
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

public class ShellDragTests
{
    [Fact]
    public void Builds_a_data_object_for_real_files()
    {
        var file = Path.Combine(Path.GetTempPath(), $"halobar-drag-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "drag me");
        try
        {
            var data = HaloBar.Platform.ShellDrag.DataObject([file]);
            Assert.NotEqual(IntPtr.Zero, data);
            System.Runtime.InteropServices.Marshal.Release(data);
            Assert.Equal(IntPtr.Zero, HaloBar.Platform.ShellDrag.DataObject([file + ".missing"]));
        }
        finally { File.Delete(file); }
    }
}

public class ShellThumbnailTests
{
    [Fact]
    public void Photos_come_out_the_right_way_up()
    {
        var file = Path.Combine(Path.GetTempPath(), $"halobar-thumb-{Guid.NewGuid():N}.png");
        using (var bitmap = new SKBitmap(200, 200))
        {
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Blue);
                canvas.DrawRect(new SKRect(0, 0, 200, 100), new SKPaint { Color = SKColors.Red });
            }
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(file, data.ToArray());
        }
        try
        {
            using var thumb = HaloBar.Platform.ShellThumbnail.For(file, 96);
            Assert.NotNull(thumb);
            using var pixels = SKBitmap.FromImage(thumb);
            var top = pixels.GetPixel(pixels.Width / 2, 2);
            var bottom = pixels.GetPixel(pixels.Width / 2, pixels.Height - 3);
            Assert.True(top.Red > 200 && top.Blue < 60, $"top is {top}");
            Assert.True(bottom.Blue > 200 && bottom.Red < 60, $"bottom is {bottom}");
        }
        finally { File.Delete(file); }
    }
}

public class PrefsTests
{
    [Fact]
    public void Speeds_clocks_and_temperatures_follow_the_settings()
    {
        Assert.Equal("1.0MB/s", StatsService.Rate(1e6, bits: false));
        Assert.Equal("8.0Mb/s", StatsService.Rate(1e6, bits: true));
        Assert.Equal("80Mb/s", StatsService.Rate(1e7, bits: true));

        var at = new DateTime(2026, 10, 30, 14, 5, 0);
        HaloBar.UI.Prefs.Apply(new HaloBar.AppSettings { TimeFormat = "24", StatsFahrenheit = true });
        Assert.Equal("14:05", HaloBar.UI.Prefs.Time(at));
        Assert.Equal("212°", HaloBar.UI.Prefs.Temp(100));
        HaloBar.UI.Prefs.Apply(new HaloBar.AppSettings { TimeFormat = "12" });
        Assert.Equal("2:05 pm", HaloBar.UI.Prefs.Time(at));
        Assert.Equal("54°", HaloBar.UI.Prefs.Temp(54));
        HaloBar.UI.Prefs.Apply(new HaloBar.AppSettings());
    }
}

public class ExportWatcherTests
{
    [Fact]
    public async Task Reports_a_file_once_it_stops_growing()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"halobar-exports-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            using var watcher = new ExportWatcher(() => [folder]);
            var done = new List<string>();
            watcher.Finished += done.Add;
            watcher.Sync();
            var file = Path.Combine(folder, "render.mp4");
            await File.WriteAllBytesAsync(file, new byte[4096]);
            await Task.Delay(500);
            watcher.Sync();
            Assert.Empty(done); // still settling
            for (var i = 0; i < 12 && done.Count == 0; i++)
            {
                await Task.Delay(500);
                watcher.Sync();
            }
            Assert.Equal([file], done);
        }
        finally { Directory.Delete(folder, true); }
    }
}

public class AdBlockerTests
{
    [Theory]
    [InlineData("https://googleads.g.doubleclick.net/pagead/id", true)]
    [InlineData("https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js", true)]
    [InlineData("https://www.youtube.com/pagead/adview?ai=x", true)]
    [InlineData("https://www.youtube.com/api/stats/ads?ver=2", true)]
    [InlineData("https://music.youtube.com/youtubei/v1/player/ad_break?key=x", true)]
    [InlineData("https://cdn.taboola.com/libtrc/loader.js", true)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", false)]
    [InlineData("https://music.youtube.com/youtubei/v1/player?key=x", false)]
    [InlineData("https://rr3---sn-x.googlevideo.com/videoplayback?id=1", false)]
    [InlineData("https://notdoubleclick.net/", false)]
    [InlineData("not a url", false)]
    public void Blocks_ad_hosts_and_youtube_ad_paths_only(string url, bool blocked) =>
        Assert.Equal(blocked, HaloBar.Platform.AdBlocker.Blocks(url));
}
