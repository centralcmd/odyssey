using Odyssey.Core;
using Odyssey.Core.Journal.Interop;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// The streaming calendar import's splitter (issue #516 §3 component 4, N1): one <c>VEVENT</c> at a time, the file's
/// <c>VTIMEZONE</c> blocks carried along, and hard caps on a line and a component.
/// </summary>
public class IcsComponentSplitterTests
{
    private const string Head = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\n";

    [Fact]
    public async Task Yields_each_vevent_as_its_own_calendar_with_the_files_time_zones()
    {
        var file = Head
            + "BEGIN:VTIMEZONE\r\nTZID:Europe/Oslo\r\nBEGIN:STANDARD\r\nDTSTART:19701025T030000\r\nTZOFFSETFROM:+0200\r\n"
            + "TZOFFSETTO:+0100\r\nEND:STANDARD\r\nEND:VTIMEZONE\r\n"
            + "BEGIN:VEVENT\r\nUID:a\r\nSUMMARY:A\r\nBEGIN:VALARM\r\nACTION:DISPLAY\r\nEND:VALARM\r\nEND:VEVENT\r\n"
            + "BEGIN:VTODO\r\nUID:skipped\r\nEND:VTODO\r\n"
            + "BEGIN:VEVENT\r\nUID:b\r\nSUMMARY:B\r\nEND:VEVENT\r\n"
            + "END:VCALENDAR\r\n";

        var events = await SplitAsync(file);

        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.StartsWith("BEGIN:VCALENDAR", e));
        Assert.All(events, e => Assert.Contains("TZID:Europe/Oslo", e));
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

        await Assert.ThrowsAsync<DomainValidationException>(() => SplitAsync(Head + zone + zone + "END:VCALENDAR\r\n"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a calendar")]
    public async Task A_file_that_is_not_a_calendar_is_refused(string file)
    {
        var error = await Assert.ThrowsAsync<DomainValidationException>(() => SplitAsync(file));

        Assert.Equal(IcsComponentSplitter.NotCalendarMessage, error.Message);
    }

    private static async Task<List<string>> SplitAsync(string file)
    {
        var splitter = new IcsComponentSplitter(new StringReader(file));
        var events = new List<string>();
        await foreach (var component in splitter.ReadEventsAsync())
        {
            events.Add(component);
        }

        return events;
    }
}
