namespace AllMyTasks.Providers.ICal.Tests;

/// <summary>Made-up iCalendar documents. Never a real calendar export.</summary>
internal static class Fixtures
{
    public static string Wrap(string body, string header = "") =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//EN\r\n" + header + body.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\nEND:VCALENDAR\r\n";

    public const string Basic = """
        BEGIN:VEVENT
        UID:utc-1
        DTSTAMP:20261001T000000Z
        DTSTART:20261019T090000Z
        DTEND:20261019T100000Z
        SUMMARY:UTC meeting
        DESCRIPTION:Bring notes\, slides\; and coffee
        LOCATION:Room 4
        URL:https://example.com/m
        CATEGORIES:work,team
        LAST-MODIFIED:20261002T120000Z
        END:VEVENT
        BEGIN:VEVENT
        UID:tz-1
        DTSTAMP:20261001T000000Z
        DTSTART;TZID=Europe/Warsaw:20261019T100000
        DTEND;TZID=Europe/Warsaw:20261019T113000
        SUMMARY:Warsaw meeting
        TRANSP:TRANSPARENT
        STATUS:TENTATIVE
        END:VEVENT
        BEGIN:VEVENT
        UID:allday-1
        DTSTAMP:20261001T000000Z
        DTSTART;VALUE=DATE:20261224
        DTEND;VALUE=DATE:20261227
        SUMMARY:Holidays
        END:VEVENT
        BEGIN:VEVENT
        UID:allday-2
        DTSTAMP:20261001T000000Z
        DTSTART;VALUE=DATE:20261101
        SUMMARY:One day, no end
        END:VEVENT
        BEGIN:VEVENT
        UID:allday-3
        DTSTAMP:20261001T000000Z
        DTSTART;VALUE=DATE:20261110
        DURATION:P3D
        SUMMARY:Duration days
        END:VEVENT
        BEGIN:VEVENT
        UID:float-1
        DTSTAMP:20261001T000000Z
        DTSTART:20261020T080000
        DTEND:20261020T081500
        SUMMARY:Floating
        END:VEVENT
        BEGIN:VEVENT
        UID:dur-1
        DTSTAMP:20261001T000000Z
        DTSTART:20261021T120000Z
        DURATION:PT1H30M
        SUMMARY:Duration
        END:VEVENT
        BEGIN:VEVENT
        UID:noend-1
        DTSTAMP:20261001T000000Z
        DTSTART:20261022T120000Z
        SUMMARY:No end
        END:VEVENT
        BEGIN:VEVENT
        UID:cancelled-1
        DTSTAMP:20261001T000000Z
        DTSTART:20261023T120000Z
        DTEND:20261023T130000Z
        SUMMARY:Cancelled
        STATUS:CANCELLED
        END:VEVENT
        """;

    public const string Recurring = """
        BEGIN:VEVENT
        UID:weekly-1
        DTSTAMP:20261001T000000Z
        DTSTART;TZID=Europe/Warsaw:20261019T090000
        DTEND;TZID=Europe/Warsaw:20261019T093000
        RRULE:FREQ=WEEKLY;BYDAY=MO
        EXDATE;TZID=Europe/Warsaw:20261102T090000
        RDATE;TZID=Europe/Warsaw:20261028T150000
        SUMMARY:Weekly sync
        END:VEVENT
        BEGIN:VEVENT
        UID:weekly-1
        DTSTAMP:20261001T000000Z
        RECURRENCE-ID;TZID=Europe/Warsaw:20261026T090000
        DTSTART;TZID=Europe/Warsaw:20261026T140000
        DTEND;TZID=Europe/Warsaw:20261026T143000
        SUMMARY:Weekly sync (moved)
        END:VEVENT
        BEGIN:VEVENT
        UID:weekly-1
        DTSTAMP:20261001T000000Z
        RECURRENCE-ID;TZID=Europe/Warsaw:20261109T090000
        DTSTART;TZID=Europe/Warsaw:20261109T090000
        DTEND;TZID=Europe/Warsaw:20261109T093000
        STATUS:CANCELLED
        SUMMARY:Weekly sync
        END:VEVENT
        BEGIN:VEVENT
        UID:yearly-1
        DTSTAMP:20261001T000000Z
        DTSTART;VALUE=DATE:20261224
        DTEND;VALUE=DATE:20261225
        RRULE:FREQ=YEARLY
        SUMMARY:Christmas Eve
        END:VEVENT
        BEGIN:VEVENT
        UID:daily-utc
        DTSTAMP:20261001T000000Z
        DTSTART:20261019T230000Z
        DTEND:20261020T000000Z
        RRULE:FREQ=DAILY;COUNT=3
        SUMMARY:Nightly job
        END:VEVENT
        BEGIN:VEVENT
        UID:until-1
        DTSTAMP:20261001T000000Z
        DTSTART:20261019T100000Z
        DTEND:20261019T110000Z
        RRULE:FREQ=DAILY;UNTIL=20261022T100000Z
        SUMMARY:Short series
        END:VEVENT
        """;

    public const string WindowsZones = """
        BEGIN:VEVENT
        UID:win-1
        DTSTAMP:20261001T000000Z
        DTSTART;TZID=W. Europe Standard Time:20261019T100000
        DTEND;TZID=W. Europe Standard Time:20261019T110000
        SUMMARY:Windows zone
        END:VEVENT
        BEGIN:VEVENT
        UID:win-2
        DTSTAMP:20261001T000000Z
        DTSTART;TZID=Eastern Standard Time:20261019T100000
        DTEND;TZID=Eastern Standard Time:20261019T110000
        SUMMARY:Eastern
        END:VEVENT
        BEGIN:VEVENT
        UID:unknown-1
        DTSTAMP:20261001T000000Z
        DTSTART;TZID=Mars/Olympus:20261019T100000
        DTEND;TZID=Mars/Olympus:20261019T110000
        SUMMARY:Unknown zone
        END:VEVENT
        """;

    public const string NoUid = """
        BEGIN:VEVENT
        DTSTAMP:20261001T000000Z
        DTSTART:20261019T090000Z
        DTEND:20261019T100000Z
        SUMMARY:No uid
        END:VEVENT
        """;

    public const string Malformed = """
        BEGIN:VEVENT
        UID:good-1
        DTSTAMP:20261001T000000Z
        DTSTART:20261019T090000Z
        DTEND:20261019T100000Z
        SUMMARY:Good one
        END:VEVENT
        BEGIN:VEVENT
        UID:bad-1
        DTSTAMP:20261001T000000Z
        DTSTART:not-a-date
        DTEND:also-bad
        SUMMARY:Bad one
        END:VEVENT
        BEGIN:VEVENT
        UID:nostart-1
        DTSTAMP:20261001T000000Z
        SUMMARY:No start at all
        END:VEVENT
        BEGIN:VEVENT
        UID:good-2
        DTSTAMP:20261001T000000Z
        DTSTART:20261020T090000Z
        DTEND:20261020T100000Z
        SUMMARY:Another good one
        END:VEVENT
        """;
}
