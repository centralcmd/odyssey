using Ical.Net.Serialization;
using IcalCalendar = Ical.Net.Calendar;

namespace Odyssey.Core.Journal.Interop;

/// <summary>
/// Splices a multi-component .ics document together from independently-serialized chunks (issue #343
/// §5 Goal 8), so a chunked export never holds more than one chunk's worth of <c>Ical.Net</c> object
/// graph in memory at a time — unlike building one big <see cref="IcalCalendar"/> and serializing it
/// once at the end. <c>Ical.Net</c> has no public API to serialize a bare component list without its
/// enclosing <c>VCALENDAR</c> wrapper, so this gets there by serializing a real (tiny) calendar per
/// chunk and slicing out the inner component block — a real engine-round-trip check, not a hand-rolled
/// re-implementation of RFC 5545 folding/escaping.
/// </summary>
internal static class IcsChunkSerializer
{
    /// <summary>
    /// The document's fixed head (<c>BEGIN:VCALENDAR</c> through the calendar-level properties) and
    /// tail (<c>END:VCALENDAR</c>), computed once by serializing an empty calendar carrying
    /// <paramref name="productId"/> — so it's guaranteed to match whatever this exact <c>Ical.Net</c>
    /// version actually emits for calendar-level properties, rather than a hand-written guess.
    /// </summary>
    public static (string Head, string Tail) BuildEnvelope(string productId)
    {
        var empty = new IcalCalendar { ProductId = productId };
        var text = new CalendarSerializer(empty).SerializeToString() ?? string.Empty;
        var tailIndex = text.LastIndexOf("END:VCALENDAR", StringComparison.Ordinal);
        return tailIndex < 0 ? (text, string.Empty) : (text[..tailIndex], text[tailIndex..]);
    }

    /// <summary>
    /// Serializes <paramref name="chunk"/> (a throwaway calendar holding only this chunk's components)
    /// and returns just the inner component block(s) — everything between the calendar-level
    /// properties and <c>END:VCALENDAR</c> — discarding the chunk's own (irrelevant) <c>VCALENDAR</c>
    /// wrapper. Returns an empty string if <paramref name="chunk"/> has no components.
    /// </summary>
    public static string SerializeComponents(IcalCalendar chunk)
    {
        var text = new CalendarSerializer(chunk).SerializeToString() ?? string.Empty;

        var wrapperStart = text.IndexOf("BEGIN:VCALENDAR", StringComparison.Ordinal);
        var searchFrom = wrapperStart < 0 ? 0 : wrapperStart + "BEGIN:VCALENDAR".Length;
        // The first component after the calendar-level properties — VEVENT/VTODO/VJOURNAL all start
        // "BEGIN:V", and searching from just past the calendar's own "BEGIN:VCALENDAR" excludes it.
        var componentStart = text.IndexOf("BEGIN:V", searchFrom, StringComparison.Ordinal);
        var componentEnd = text.LastIndexOf("END:VCALENDAR", StringComparison.Ordinal);

        return componentStart < 0 || componentEnd < 0 || componentEnd <= componentStart
            ? string.Empty
            : text[componentStart..componentEnd];
    }

    /// <summary>Writes a (possibly empty) chunk of text to the response stream as UTF-8.</summary>
    public static async Task WriteAsync(Stream output, string text, CancellationToken cancellationToken)
    {
        if (text.Length == 0)
        {
            return;
        }

        await output.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text), cancellationToken);
    }
}
