using System.Runtime.CompilerServices;
using System.Text;
using Odyssey.Core;

namespace Odyssey.Core.Journal.Interop;

/// <summary>
/// Reads an uploaded <c>.ics</c> file one <c>VEVENT</c> at a time (issue #516 §3 component 4), so a calendar import's
/// memory never grows with the file or its event count. It unfolds lines itself, keeps the file's <c>VTIMEZONE</c>
/// blocks (at most <see cref="MaxTimeZoneChars"/> in total) and yields each <c>VEVENT</c> wrapped in a minimal
/// <c>VCALENDAR</c> carrying them, for <c>Ical.Net</c> to parse on its own. Every other top-level component is read past
/// without being held.
/// </summary>
/// <remarks>
/// The caps are compile-time constants and count characters: an unfolded content line is at most
/// <see cref="MaxLineChars"/> and a <c>VEVENT</c> at most <see cref="MaxComponentChars"/>. A component over either, a
/// <c>BEGIN:VEVENT</c> nested in another, or one the file never closes stops the import with <c>400</c>
/// (<see cref="TooLargeMessage"/>) — never a skip, because the splitter cannot tell where such a component ends.
/// </remarks>
internal sealed class IcsComponentSplitter(TextReader reader)
{
    /// <summary>The longest unfolded content line accepted: 1 Mi characters.</summary>
    public const int MaxLineChars = 1024 * 1024;

    /// <summary>The largest <c>VEVENT</c> accepted: 4 Mi characters.</summary>
    public const int MaxComponentChars = 4 * 1024 * 1024;

    /// <summary>The <c>VTIMEZONE</c> blocks kept for parsing, in total: 1 Mi characters.</summary>
    public const int MaxTimeZoneChars = 1024 * 1024;

    public const string TooLargeMessage = "The file contains an event that is too large or not closed.";

    public const string NotCalendarMessage = "The file could not be parsed as a valid iCalendar (.ics) file.";

    private const string Envelope = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Odyssey//Calendar import//EN\r\n";

    private readonly char[] buffer = new char[64 * 1024];
    private readonly StringBuilder physical = new();
    private readonly StringBuilder logical = new();
    private readonly StringBuilder component = new();
    private readonly StringBuilder timeZones = new();
    private string timeZoneText = string.Empty;
    private int position;
    private int length;
    private bool endOfFile;
    private string? lookahead;
    private bool hasLookahead;

    /// <summary>Yields each <c>VEVENT</c> as a complete, minimal <c>VCALENDAR</c> document.</summary>
    public async IAsyncEnumerable<string> ReadEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sawCalendar = false;
        while (await ReadLogicalLineAsync(cancellationToken) is { } line)
        {
            var trimmed = line.TrimEnd();
            if (!sawCalendar)
            {
                if (trimmed.Length == 0)
                {
                    continue;
                }

                if (!trimmed.Equals("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
                {
                    throw new DomainValidationException(NotCalendarMessage);
                }

                sawCalendar = true;
                continue;
            }

            if (trimmed.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                await ReadEventAsync(cancellationToken);
                yield return Envelope + timeZoneText + component + "END:VCALENDAR\r\n";
                component.Clear();
            }
            else if (trimmed.Equals("BEGIN:VTIMEZONE", StringComparison.OrdinalIgnoreCase))
            {
                await ReadTimeZoneAsync(cancellationToken);
            }
            else if (trimmed.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase)
                && !trimmed.Equals("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
            {
                await SkipComponentAsync(cancellationToken);
            }
        }

        if (!sawCalendar)
        {
            throw new DomainValidationException(NotCalendarMessage);
        }
    }

    private async Task ReadEventAsync(CancellationToken cancellationToken)
    {
        component.Append("BEGIN:VEVENT\r\n");
        var depth = 0;
        while (await ReadLogicalLineAsync(cancellationToken) is { } line)
        {
            var trimmed = line.TrimEnd();
            if (trimmed.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("END:VCALENDAR", StringComparison.OrdinalIgnoreCase))
            {
                throw new DomainValidationException(TooLargeMessage);
            }

            if (component.Length + line.Length + 2 > MaxComponentChars)
            {
                throw new DomainValidationException(TooLargeMessage);
            }

            component.Append(line).Append("\r\n");
            if (trimmed.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase))
            {
                depth++;
            }
            else if (trimmed.StartsWith("END:", StringComparison.OrdinalIgnoreCase))
            {
                if (depth == 0)
                {
                    if (!trimmed.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new DomainValidationException(TooLargeMessage);
                    }

                    return;
                }

                depth--;
            }
        }

        throw new DomainValidationException(TooLargeMessage);
    }

    private async Task ReadTimeZoneAsync(CancellationToken cancellationToken)
    {
        var start = timeZones.Length;
        timeZones.Append("BEGIN:VTIMEZONE\r\n");
        var depth = 0;
        while (await ReadLogicalLineAsync(cancellationToken) is { } line)
        {
            var trimmed = line.TrimEnd();
            if (timeZones.Length + line.Length + 2 > MaxTimeZoneChars)
            {
                throw new DomainValidationException("The file's time zone definitions (VTIMEZONE) exceed 1 MB.");
            }

            timeZones.Append(line).Append("\r\n");
            if (trimmed.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase))
            {
                depth++;
            }
            else if (trimmed.StartsWith("END:", StringComparison.OrdinalIgnoreCase))
            {
                if (depth == 0)
                {
                    timeZoneText = timeZones.ToString();
                    return;
                }

                depth--;
            }
        }

        // An unterminated VTIMEZONE at the end of the file names no event; drop it rather than poison none.
        timeZones.Length = start;
    }

    private async Task SkipComponentAsync(CancellationToken cancellationToken)
    {
        var depth = 0;
        while (await ReadLogicalLineAsync(cancellationToken) is { } line)
        {
            var trimmed = line.TrimEnd();
            if (trimmed.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase))
            {
                depth++;
            }
            else if (trimmed.StartsWith("END:", StringComparison.OrdinalIgnoreCase))
            {
                if (depth == 0)
                {
                    return;
                }

                depth--;
            }
        }
    }

    // One content line with its folded continuations joined (RFC 5545 §3.1): a physical line beginning with a space
    // or a tab continues the previous one, less that first character.
    private async Task<string?> ReadLogicalLineAsync(CancellationToken cancellationToken)
    {
        var first = await NextPhysicalAsync(cancellationToken);
        if (first is null)
        {
            return null;
        }

        logical.Clear();
        logical.Append(first);
        while (true)
        {
            var next = await PeekPhysicalAsync(cancellationToken);
            if (next is not { Length: > 0 } || next[0] is not (' ' or '\t'))
            {
                break;
            }

            await NextPhysicalAsync(cancellationToken);
            if (logical.Length + next.Length - 1 > MaxLineChars)
            {
                throw new DomainValidationException(TooLargeMessage);
            }

            logical.Append(next, 1, next.Length - 1);
        }

        return logical.ToString();
    }

    private async Task<string?> PeekPhysicalAsync(CancellationToken cancellationToken)
    {
        if (!hasLookahead)
        {
            lookahead = await ReadPhysicalAsync(cancellationToken);
            hasLookahead = true;
        }

        return lookahead;
    }

    private async Task<string?> NextPhysicalAsync(CancellationToken cancellationToken)
    {
        if (hasLookahead)
        {
            hasLookahead = false;
            return lookahead;
        }

        return await ReadPhysicalAsync(cancellationToken);
    }

    private async Task<string?> ReadPhysicalAsync(CancellationToken cancellationToken)
    {
        physical.Clear();
        var any = false;
        while (true)
        {
            if (position == length)
            {
                if (endOfFile)
                {
                    return any ? Trimmed() : null;
                }

                length = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
                position = 0;
                if (length == 0)
                {
                    endOfFile = true;
                    continue;
                }
            }

            any = true;
            var span = buffer.AsSpan(position, length - position);
            var newline = span.IndexOf('\n');
            var take = newline < 0 ? span.Length : newline;
            if (physical.Length + take > MaxLineChars + 1)
            {
                throw new DomainValidationException(TooLargeMessage);
            }

            physical.Append(span[..take]);
            if (newline < 0)
            {
                position = length;
                continue;
            }

            position += newline + 1;
            return Trimmed();
        }

        string Trimmed()
        {
            if (physical.Length > 0 && physical[^1] == '\r')
            {
                physical.Length--;
            }

            return physical.ToString();
        }
    }
}
