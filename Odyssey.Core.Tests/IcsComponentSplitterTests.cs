using System.Text;
using Odyssey.Core;
using Odyssey.Core.Journal.Interop;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// The streaming calendar import's splitter (issue #516 §3 component 4, N1): one <c>VEVENT</c> at a time, the file's
/// <c>VTIMEZONE</c> blocks kept once (never re-parsed per event), and hard caps on a line and a component.
/// </summary>
public class IcsComponentSplitterTests
{
    private const string Head = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\n";

    [Fact]
    public async Task Yields_each_vevent_as_its_own_calendar_and_keeps_the_time_zones_once()
    {
        var file = Head
            + "BEGIN:VTIMEZONE\r\nTZID:Europe/Oslo\r\nBEGIN:STANDARD\r\nDTSTART:19701025T030000\r\nTZOFFSETFROM:+0200\r\n"
            + "TZOFFSETTO:+0100\r\nEND:STANDARD\r\nEND:VTIMEZONE\r\n"
            + "BEGIN:VEVENT\r\nUID:a\r\nSUMMARY:A\r\nBEGIN:VALARM\r\nACTION:DISPLAY\r\nEND:VALARM\r\nEND:VEVENT\r\n"
            + "BEGIN:VTODO\r\nUID:skipped\r\nEND:VTODO\r\n"
            + "BEGIN:VEVENT\r\nUID:b\r\nSUMMARY:B\r\nEND:VEVENT\r\n"
            + "END:VCALENDAR\r\n";

        var splitter = new IcsComponentSplitter(new StringReader(file));
        var events = await SplitAsync(splitter);

        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.StartsWith("BEGIN:VCALENDAR", e));
        // Security finding 2: no event carries the zone text, so per-event parse cost never scales with it.
        Assert.All(events, e => Assert.DoesNotContain("VTIMEZONE", e));
        Assert.Contains("TZID:Europe/Oslo", splitter.TimeZoneText);
        Assert.Contains("BEGIN:VALARM", events[0]);
        Assert.DoesNotContain("VTODO", string.Concat(events));
        Assert.Contains("UID:b", events[1]);
        Assert.DoesNotContain("UID:a", events[1]);
    }

    [Fact]
    public async Task Unfolds_continuation_lines()
    {
        var events = await SplitAsync(Head + "BEGIN:VEVENT\r\nUID:a\r\nSUMMARY:Long\r\n  title\r\n\tcontinued\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");

        Assert.Contains("SUMMARY:Long titlecontinued", Assert.Single(events));
    }

    [Fact]
    public async Task Accepts_bare_line_feeds()
    {
        var events = await SplitAsync("BEGIN:VCALENDAR\nBEGIN:VEVENT\nUID:a\nEND:VEVENT\nEND:VCALENDAR\n");

        Assert.Contains("UID:a", Assert.Single(events));
    }

    [Fact]
    public async Task An_unfolded_line_over_the_cap_is_refused()
    {
        var folded = string.Join("\r\n ", Enumerable.Repeat(new string('a', 1000), 1100));
        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            SplitAsync(Head + "BEGIN:VEVENT\r\nDESCRIPTION:" + folded + "\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n"));

        Assert.Equal(IcsComponentSplitter.TooLargeMessage, error.Message);
    }

    [Fact]
    public async Task A_physical_line_over_the_cap_is_refused_without_reading_it_whole()
    {
        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            SplitAsync(Head + "BEGIN:VEVENT\r\nDESCRIPTION:" + new string('a', IcsComponentSplitter.MaxLineChars + 10)));

        Assert.Equal(IcsComponentSplitter.TooLargeMessage, error.Message);
    }

    [Fact]
    public async Task A_component_over_the_cap_is_refused()
    {
        var padding = string.Concat(Enumerable.Repeat("X-PAD:" + new string('b', 1000) + "\r\n", 4300));
        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            SplitAsync(Head + "BEGIN:VEVENT\r\n" + padding + "END:VEVENT\r\nEND:VCALENDAR\r\n"));

        Assert.Equal(IcsComponentSplitter.TooLargeMessage, error.Message);
    }

    [Theory]
    [InlineData("BEGIN:VEVENT\r\nUID:a\r\n")]
    [InlineData("BEGIN:VEVENT\r\nUID:a\r\nEND:VCALENDAR\r\n")]
    [InlineData("BEGIN:VEVENT\r\nUID:a\r\nBEGIN:VEVENT\r\nUID:b\r\nEND:VEVENT\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n")]
    public async Task An_unclosed_or_nested_vevent_is_refused(string body)
    {
        var error = await Assert.ThrowsAsync<DomainValidationException>(() => SplitAsync(Head + body));

        Assert.Equal(IcsComponentSplitter.TooLargeMessage, error.Message);
    }

    [Fact]
    public async Task Time_zones_over_their_cap_are_refused()
    {
        var zone = "BEGIN:VTIMEZONE\r\nTZID:Z\r\nX-PAD:" + new string('z', 600_000) + "\r\nEND:VTIMEZONE\r\n";

        var error = await Assert.ThrowsAsync<DomainValidationException>(() => SplitAsync(Head + zone + zone + "END:VCALENDAR\r\n"));

        Assert.Equal("The file's time zone definitions (VTIMEZONE) exceed 1 MB.", error.Message);
    }

    [Fact]
    public async Task A_large_time_zone_block_is_never_carried_into_any_event()
    {
        // A near-cap VTIMEZONE followed by many tiny events: the yielded text stays the size of each event alone.
        var zone = "BEGIN:VTIMEZONE\r\nTZID:Big\r\nX-PAD:" + new string('z', 900_000) + "\r\nEND:VTIMEZONE\r\n";
        var file = Head + zone
            + string.Concat(Enumerable.Range(0, 200).Select(i => $"BEGIN:VEVENT\r\nUID:{i}\r\nDTSTART;TZID=Big:20300101T090000\r\nEND:VEVENT\r\n"))
            + "END:VCALENDAR\r\n";
        var splitter = new IcsComponentSplitter(new StringReader(file));

        var events = await SplitAsync(splitter);

        Assert.Equal(200, events.Count);
        Assert.All(events, e => Assert.True(e.Length < 512, $"An event carried {e.Length} characters."));
        Assert.True(splitter.TimeZoneText.Length > 900_000);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Ical_net_resolves_a_tzid_without_reading_the_files_vtimezone(bool withZone)
    {
        // This is what licenses not carrying the zones: if an Ical.Net upgrade starts honouring VTIMEZONE, the
        // custom zone below resolves and this test fails, and the splitter must parse TimeZoneText once per import.
        var zones = withZone
            ? "BEGIN:VTIMEZONE\r\nTZID:Custom/Zone\r\nBEGIN:STANDARD\r\nDTSTART:19700101T000000\r\nTZOFFSETFROM:+0530\r\n"
              + "TZOFFSETTO:+0530\r\nEND:STANDARD\r\nEND:VTIMEZONE\r\n"
            : string.Empty;
        Ical.Net.CalendarComponents.CalendarEvent Load(string tzid) => Ical.Net.Calendar.Load(
            Head + zones + $"BEGIN:VEVENT\r\nUID:a\r\nDTSTART;TZID={tzid}:20300101T120000\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n")!
            .Events.Single();

        Assert.Equal(new DateTime(2030, 1, 1, 11, 0, 0, DateTimeKind.Utc), Load("Europe/Oslo").Start!.AsUtc);
        Assert.ThrowsAny<ArgumentException>(() => Load("Custom/Zone").Start!.AsUtc);
    }

    [Fact]
    public async Task A_line_exactly_at_the_cap_and_a_component_exactly_at_the_cap_are_accepted()
    {
        var line = "DESCRIPTION:" + new string('a', IcsComponentSplitter.MaxLineChars - "DESCRIPTION:".Length);
        var lineEvents = await SplitAsync(Head + "BEGIN:VEVENT\r\n" + line + "\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
        Assert.Contains(line, Assert.Single(lineEvents));

        // The component counts "BEGIN:VEVENT\r\n", every line with its CRLF, and "END:VEVENT\r\n".
        const string begin = "BEGIN:VEVENT\r\n", end = "END:VEVENT\r\n";
        var room = IcsComponentSplitter.MaxComponentChars - begin.Length - end.Length;
        var pad = new string('p', 1000);
        var lines = new StringBuilder();
        while (room - lines.Length >= ("X-PAD:" + pad + "\r\n").Length * 2)
        {
            lines.Append("X-PAD:").Append(pad).Append("\r\n");
        }

        var last = room - lines.Length - "X-LAST:".Length - 2;
        lines.Append("X-LAST:").Append('q', last).Append("\r\n");
        Assert.Equal(room, lines.Length);

        var events = await SplitAsync(Head + begin + lines + end + "END:VCALENDAR\r\n");
        Assert.Single(events);

        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            SplitAsync(Head + begin + lines + "X:1\r\n" + end + "END:VCALENDAR\r\n"));
        Assert.Equal(IcsComponentSplitter.TooLargeMessage, error.Message);
    }

    [Fact]
    public async Task Component_names_are_matched_case_insensitively()
    {
        var events = await SplitAsync("begin:vcalendar\r\nbegin:vevent\r\nUID:a\r\nend:vevent\r\nBegin:VEvent\r\nUID:b\r\nEnd:VEvent\r\nend:vcalendar\r\n");

        Assert.Equal(2, events.Count);
        Assert.Contains("UID:b", events[1]);
    }

    [Fact]
    public async Task A_single_time_zone_within_the_cap_is_accepted()
    {
        var zone = "BEGIN:VTIMEZONE\r\nTZID:Z\r\nX-PAD:" + new string('z', 600_000) + "\r\nEND:VTIMEZONE\r\n";

        var events = await SplitAsync(Head + zone + "BEGIN:VEVENT\r\nUID:a\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");

        Assert.Single(events);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a calendar")]
    public async Task A_file_that_is_not_a_calendar_is_refused(string file)
    {
        var error = await Assert.ThrowsAsync<DomainValidationException>(() => SplitAsync(file));

        Assert.Equal(IcsComponentSplitter.NotCalendarMessage, error.Message);
    }

    private static Task<List<string>> SplitAsync(string file) => SplitAsync(new IcsComponentSplitter(new StringReader(file)));

    private static async Task<List<string>> SplitAsync(IcsComponentSplitter splitter)
    {
        var events = new List<string>();
        await foreach (var component in splitter.ReadEventsAsync())
        {
            events.Add(component);
        }

        return events;
    }
}
