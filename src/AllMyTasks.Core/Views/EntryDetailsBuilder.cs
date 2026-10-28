using System.Globalization;
using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Tasks;

namespace AllMyTasks.Core.Views;

/// <summary>Builds the text of the details pane from a calendar entry. Pure, so it is tested without a UI.</summary>
public static class EntryDetailsBuilder
{
    /// <summary>Longest description shown. Feeds can contain very large values that would stall the UI.</summary>
    public const int MaxDescriptionLength = 20_000;

    public static EntryDetails Create(AgendaEntry entry, TimeZoneInfo zone, string? sourceName, CultureInfo? culture = null)
    {
        Guard.NotNull(entry);
        Guard.NotNull(zone);
        culture ??= CultureInfo.CurrentCulture;
        var rows = new List<DetailRow>();
        var isTask = entry is TaskEntry;
        Add(rows, isTask ? "Due" : "When", When(entry, zone, culture, isTask));

        string? description;
        Uri? url;
        Uri? meeting = null;
        IReadOnlyList<PersonLine> people = [];
        var more = 0;
        switch (entry)
        {
            case EventEntry e:
                var ev = e.Occurrence.Event;
                description = ev.Description;
                url = ev.Url;
                Add(rows, "Repeats", ev.Key.IsOverride
                    ? "Part of a repeating event; this occurrence was changed"
                    : ev.Recurrence is { } r ? RecurrenceDescriber.Describe(r, culture) : null);
                Add(rows, "Where", ev.Location);
                Add(rows, "Calendar", CalendarText(entry.CollectionName, sourceName));
                Add(rows, "Status", ev.Status == EventStatus.Confirmed ? null : ev.Status.ToString());
                Add(rows, "Show as", ev.Availability == Availability.Free ? "Free" : null);
                Add(rows, "Categories", string.Join(", ", ev.Categories));
                AddLink(rows, url);
                AddModified(rows, ev.LastModified, zone, culture);
                meeting = MeetingLinks.IsSafe(ev.MeetingUrl) ? ev.MeetingUrl : null;
                people = People(ev);
                more = Math.Max(0, ev.AttendeeCount - ev.Attendees.Count);
                break;
            case TaskEntry t:
                var task = t.Task;
                description = task.Notes;
                url = task.Url;
                Add(rows, "Repeats", task.Recurrence is { } tr ? RecurrenceDescriber.Describe(tr, culture) : null);
                Add(rows, "List", CalendarText(entry.CollectionName, sourceName));
                Add(rows, "Status", task.Status == TaskItemStatus.Open ? null : TaskStatusText(task, zone, culture));
                Add(rows, "Priority", task.Priority == Priority.None ? null : task.Priority.ToString());
                Add(rows, "Labels", string.Join(", ", task.Labels));
                AddLink(rows, url);
                AddModified(rows, task.LastModified, zone, culture);
                break;
            default:
                description = null;
                url = null;
                break;
        }

        return new EntryDetails(
            string.IsNullOrWhiteSpace(entry.Title) ? "(no title)" : entry.Title,
            isTask,
            ColorUtil.OrDefault(entry.Color),
            rows,
            SafeLink(url),
            Cap(description),
            meeting,
            meeting is null ? null : MeetingLinks.JoinLabel(meeting),
            meeting?.IdnHost,
            people,
            more);
    }

    /// <summary>The organiser first, then required attendees, optional ones and resources, each in provider order.</summary>
    private static List<PersonLine> People(CalendarEvent ev)
    {
        var lines = new List<PersonLine>();
        var organizer = ev.Organizer is { } o ? Attendee.Clean(o) : null;
        var rest = ev.Attendees.AsEnumerable();
        if (organizer is not null)
        {
            // Providers list the organiser among the attendees too; the matching entry carries the answer.
            var match = ev.Attendees.FirstOrDefault(a => SameEmail(a, organizer));
            lines.Add(Line(match is null ? organizer : organizer with { Response = match.Response, IsSelf = organizer.IsSelf || match.IsSelf }, "Organiser"));
            rest = rest.Where(a => !SameEmail(a, organizer));
        }

        var others = rest.Select(Attendee.Clean).OfType<Attendee>().ToList();
        lines.AddRange(others.Where(a => a.Role == AttendeeRole.Required).Select(a => Line(a, null)));
        lines.AddRange(others.Where(a => a.Role == AttendeeRole.Optional).Select(a => Line(a, "Optional")));
        lines.AddRange(others.Where(a => a.Role == AttendeeRole.Resource).Select(a => Line(a, "Room")));
        return lines;
    }

    private static bool SameEmail(Attendee a, Attendee b) =>
        !string.IsNullOrWhiteSpace(a.Email) && string.Equals(a.Email.Trim(), b.Email, StringComparison.OrdinalIgnoreCase);

    private static PersonLine Line(Attendee a, string? note)
    {
        var name = a.DisplayText ?? string.Empty;
        var (glyph, response) = a.Response switch
        {
            ResponseStatus.Accepted => ("✓", "Accepted"),
            ResponseStatus.Tentative => ("?", "Maybe"),
            ResponseStatus.Declined => ("✗", "Declined"),
            _ => ("…", "No answer yet"),
        };
        if (a.IsSelf)
        {
            return new PersonLine("You", a.DisplayText is null ? null : string.Join(", ", new[] { a.Name, a.Email }.Where(t => !string.IsNullOrWhiteSpace(t))), glyph, note, response);
        }

        // The e-mail is the tooltip; it is the text itself only when there is no name.
        return new PersonLine(name, !string.IsNullOrWhiteSpace(a.Name) ? a.Email : null, glyph, note, response);
    }

    private static string When(AgendaEntry entry, TimeZoneInfo zone, CultureInfo culture, bool isTask)
    {
        const string DateFormat = "ddd d MMM yyyy";
        if (entry.IsAllDay)
        {
            var days = entry.LastDayExclusive.DayNumber - entry.FirstDay.DayNumber;
            var first = entry.FirstDay.ToString(DateFormat, culture);
            if (isTask || days <= 1)
            {
                return isTask ? first : $"{first}, all day";
            }

            var last = entry.LastDayExclusive.AddDays(-1).ToString(DateFormat, culture);
            return $"{first} – {last} ({days} days)";
        }

        var start = TimeZoneInfo.ConvertTime(entry.Start, zone);
        var end = TimeZoneInfo.ConvertTime(entry.End, zone);
        var startDate = start.ToString(DateFormat, culture);
        var startTime = start.ToString("t", culture);
        if (end == start)
        {
            return $"{startDate}, {startTime}";
        }

        var endTime = end.ToString("t", culture);
        return DateOnly.FromDateTime(start.DateTime) == DateOnly.FromDateTime(end.DateTime)
            ? $"{startDate}, {startTime} – {endTime}"
            : $"{startDate}, {startTime} – {end.ToString(DateFormat, culture)}, {endTime}";
    }

    private static string? CalendarText(string collection, string? source) =>
        string.IsNullOrWhiteSpace(source) || string.Equals(source, collection, StringComparison.Ordinal)
            ? collection
            : $"{collection}, from {source}";

    private static string TaskStatusText(TaskItem task, TimeZoneInfo zone, CultureInfo culture)
    {
        var text = task.Status == TaskItemStatus.InProgress ? "In progress" : task.Status.ToString();
        return task.Status == TaskItemStatus.Completed && task.CompletedAt is { } done
            ? $"{text} on {TimeZoneInfo.ConvertTime(done, zone).ToString("d MMM yyyy", culture)}"
            : text;
    }

    private static void AddLink(List<DetailRow> rows, Uri? url) => Add(rows, "Link", url?.ToString());

    private static void AddModified(List<DetailRow> rows, DateTimeOffset? modified, TimeZoneInfo zone, CultureInfo culture) =>
        Add(rows, "Last changed", modified is { } m ? DateTimeText(TimeZoneInfo.ConvertTime(m, zone), culture) : null);

    // "t" only means short time as a standalone format, so date and time are formatted separately.
    private static string DateTimeText(DateTimeOffset value, CultureInfo culture) =>
        $"{value.ToString("ddd d MMM yyyy", culture)}, {value.ToString("t", culture)}";

    private static void Add(List<DetailRow> rows, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            rows.Add(new DetailRow(label, value));
        }
    }

    private static Uri? SafeLink(Uri? url) =>
        url is { IsAbsoluteUri: true } && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps) ? url : null;

    private static string? Cap(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return text.Length <= MaxDescriptionLength ? text : text[..MaxDescriptionLength] + "…";
    }
}
