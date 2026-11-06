using System.Globalization;
using System.Text.RegularExpressions;

namespace HaloBar.Services;

internal enum ReminderRepeat { None, Daily, Weekdays, Weekly }

internal sealed class Reminder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = "";
    public DateTime Due { get; set; }
    public ReminderRepeat Repeat { get; set; }
}

internal sealed class TodoItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = "";
    public bool Done { get; set; }
}

/// <summary>
/// Reminders and the to-do list, kept in the settings file. Reminders are typed in plain words:
/// "call mum at 6:30pm", "stand up in 45m", "tomorrow 9am pay rent", "water plants 18:00".
/// </summary>
internal sealed partial class ReminderService(AppSettings settings)
{
    public IReadOnlyList<Reminder> Reminders => settings.Reminders;
    public IReadOnlyList<TodoItem> Todos => settings.Todos;

    public event Action<Reminder>? Due;

    /// <summary>Adds a reminder from a sentence. Without a time it is due in an hour.</summary>
    public Reminder Add(string sentence, ReminderRepeat repeat)
    {
        var (text, due) = Parse(sentence, DateTime.Now);
        var r = new Reminder { Text = text, Due = due, Repeat = repeat };
        settings.Reminders.Add(r);
        settings.Reminders.Sort((a, b) => a.Due.CompareTo(b.Due));
        settings.Save();
        return r;
    }

    public void Remove(Reminder r)
    {
        settings.Reminders.Remove(r);
        settings.Save();
    }

    /// <summary>A quick countdown timer: a reminder due <paramref name="span"/> from now.</summary>
    public Reminder AddIn(TimeSpan span, string label)
    {
        var r = new Reminder { Text = label, Due = DateTime.Now + span };
        settings.Reminders.Add(r);
        settings.Reminders.Sort((a, b) => a.Due.CompareTo(b.Due));
        settings.Save();
        return r;
    }

    /// <summary>The reminder due soonest within <paramref name="window"/> (for the pill countdown), or null.</summary>
    public Reminder? Soonest(TimeSpan window)
    {
        var now = DateTime.Now;
        return settings.Reminders.Where(r => r.Due > now && r.Due - now <= window).MinBy(r => r.Due);
    }

    public void AddTodo(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        settings.Todos.Insert(0, new TodoItem { Text = text.Trim() });
        settings.Save();
    }

    public void ToggleTodo(TodoItem item)
    {
        item.Done = !item.Done;
        // Done items sink to the bottom.
        settings.Todos.Sort((a, b) => a.Done.CompareTo(b.Done));
        settings.Save();
    }

    public void RemoveTodo(TodoItem item)
    {
        settings.Todos.Remove(item);
        settings.Save();
    }

    public void ClearDoneTodos()
    {
        settings.Todos.RemoveAll(t => t.Done);
        settings.Save();
    }

    /// <summary>Fires reminders that have come due; repeating ones move to their next time.</summary>
    public void Tick()
    {
        var now = DateTime.Now;
        var fired = settings.Reminders.Where(r => r.Due <= now).ToList();
        if (fired.Count == 0) return;
        foreach (var r in fired)
        {
            Due?.Invoke(r);
            if (r.Repeat == ReminderRepeat.None) settings.Reminders.Remove(r);
            else r.Due = Next(r.Due, r.Repeat, now);
        }
        settings.Reminders.Sort((a, b) => a.Due.CompareTo(b.Due));
        settings.Save();
    }

    private static DateTime Next(DateTime due, ReminderRepeat repeat, DateTime now)
    {
        do
        {
            due = repeat switch
            {
                ReminderRepeat.Weekly => due.AddDays(7),
                ReminderRepeat.Weekdays => due.AddDays(due.DayOfWeek switch { DayOfWeek.Friday => 3, DayOfWeek.Saturday => 2, _ => 1 }),
                _ => due.AddDays(1),
            };
        } while (due <= now);
        return due;
    }

    [GeneratedRegex(@"\bin\s+(\d+)\s*(m|min|mins|minutes?|h|hr|hrs|hours?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RelativeTime();

    [GeneratedRegex(@"(?:\bat\s+)?\b(\d{1,2})(?::(\d{2}))?\s*(am|pm)\b|(?:\bat\s+)?\b(\d{1,2}):(\d{2})\b|\bat\s+(\d{1,2})\b", RegexOptions.IgnoreCase)]
    private static partial Regex ClockTime();

    [GeneratedRegex(@"\btomorrow\b", RegexOptions.IgnoreCase)]
    private static partial Regex Tomorrow();

    internal static (string Text, DateTime Due) Parse(string sentence, DateTime now)
    {
        var text = sentence.Trim();
        var rel = RelativeTime().Match(text);
        if (rel.Success)
        {
            var n = int.Parse(rel.Groups[1].Value, CultureInfo.InvariantCulture);
            var hours = rel.Groups[2].Value.StartsWith('h');
            return (Clean(text.Remove(rel.Index, rel.Length)), now + (hours ? TimeSpan.FromHours(n) : TimeSpan.FromMinutes(n)));
        }

        var tomorrow = Tomorrow().Match(text);
        if (tomorrow.Success) text = text.Remove(tomorrow.Index, tomorrow.Length);

        var clock = ClockTime().Match(text);
        if (clock.Success)
        {
            int hour, minute = 0;
            if (clock.Groups[1].Success)
            {
                hour = int.Parse(clock.Groups[1].Value, CultureInfo.InvariantCulture) % 12;
                if (clock.Groups[2].Success) minute = int.Parse(clock.Groups[2].Value, CultureInfo.InvariantCulture);
                if (clock.Groups[3].Value.Equals("pm", StringComparison.OrdinalIgnoreCase)) hour += 12;
            }
            else if (clock.Groups[4].Success)
            {
                hour = int.Parse(clock.Groups[4].Value, CultureInfo.InvariantCulture);
                minute = int.Parse(clock.Groups[5].Value, CultureInfo.InvariantCulture);
            }
            else hour = int.Parse(clock.Groups[6].Value, CultureInfo.InvariantCulture);

            if (hour is >= 0 and < 24 && minute is >= 0 and < 60)
            {
                var due = now.Date.AddHours(hour).AddMinutes(minute);
                if (tomorrow.Success) due = due.AddDays(1);
                else if (due <= now) due = due.AddDays(1); // "at 9" after nine means tomorrow
                return (Clean(text.Remove(clock.Index, clock.Length)), due);
            }
        }
        if (tomorrow.Success) return (Clean(text), now.Date.AddDays(1).AddHours(9));
        return (Clean(text), now.AddHours(1));
    }

    private static string Clean(string text)
    {
        var t = Regex.Replace(text, @"\s{2,}", " ").Trim(' ', ',', '-', '.');
        return t.Length == 0 ? "Reminder" : char.ToUpper(t[0], CultureInfo.CurrentCulture) + t[1..];
    }
}
