using System.Runtime.CompilerServices;
using System.Text;

namespace Odyssey.Core.Journal.Interop;

/// <summary>One <c>VEVENT</c> as pass 1 of the import grouper sees it: where it sits in the upload, and its UID.</summary>
internal sealed record ScannedEvent(long Offset, int Length, string? Uid, bool HasRecurrenceId, string? RecurrenceId);

/// <summary>
/// The byte-level twin of <see cref="IcsComponentSplitter"/> (issue #519 §3 component 6): the same structural rules and
/// character caps — a 1 Mi-character content line, a 4 Mi-character <c>VEVENT</c>, 1 Mi characters of <c>VTIMEZONE</c>,
/// a nested or unclosed <c>VEVENT</c> refused with <c>400</c> — but over the raw upload, so it can report each
/// component's byte offset and length for pass 2 to re-read. It reads only the properties grouping needs, never
/// parses a component, and holds at most one content line.
/// </summary>
/// <remarks>
/// Characters are counted as UTF-8 code points (every byte that is not a continuation byte), which is what the
/// splitter's <see cref="TextReader"/> counted for any file that is valid UTF-8. <c>CR</c> and <c>LF</c> never occur
/// inside a multi-byte sequence, so lines split on bytes exactly as on characters.
/// </remarks>
internal sealed class IcsByteScanner(Stream stream)
{
    private readonly byte[] buffer = new byte[64 * 1024];
    private int position;
    private int length;
    private long bufferStart;
    private bool endOfFile;

    private Line current = new();
    private Line lookahead = new();
    private bool hasLookahead;

    private readonly byte[] logicalBuffer = new byte[256];
    private byte[] logical = [];
    private int logicalLength;
    private int logicalChars;
    private long logicalStart;
    private long logicalEnd;

    public async IAsyncEnumerable<ScannedEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        logical = logicalBuffer;
        var sawCalendar = false;
        long timeZoneChars = 0;
        var first = true;
        while (await ReadLogicalLineAsync(cancellationToken))
        {
            if (first)
            {
                StripBom();
                first = false;
            }

            if (!sawCalendar)
            {
                if (TrimmedLength() == 0)
                {
                    continue;
                }

                if (!TrimmedEquals("BEGIN:VCALENDAR"))
                {
                    throw new DomainValidationException(IcsComponentSplitter.NotCalendarMessage);
                }

                sawCalendar = true;
                continue;
            }

            if (TrimmedEquals("BEGIN:VEVENT"))
            {
                yield return await ReadEventAsync(cancellationToken);
            }
            else if (TrimmedEquals("BEGIN:VTIMEZONE"))
            {
                timeZoneChars = await ReadTimeZoneAsync(timeZoneChars, cancellationToken);
            }
            else if (StartsWith("BEGIN:") && !TrimmedEquals("BEGIN:VCALENDAR"))
            {
                await SkipComponentAsync(cancellationToken);
            }
        }

        if (!sawCalendar)
        {
            throw new DomainValidationException(IcsComponentSplitter.NotCalendarMessage);
        }
    }

    private async Task<ScannedEvent> ReadEventAsync(CancellationToken cancellationToken)
    {
        var offset = logicalStart;
        long chars = "BEGIN:VEVENT\r\n".Length;
        var depth = 0;
        string? uid = null;
        string? recurrenceId = null;
        var hasRecurrenceId = false;
        while (await ReadLogicalLineAsync(cancellationToken))
        {
            if (TrimmedEquals("BEGIN:VEVENT") || TrimmedEquals("END:VCALENDAR"))
            {
                throw new DomainValidationException(IcsComponentSplitter.TooLargeMessage);
            }

            if (chars + logicalChars + 2 > IcsComponentSplitter.MaxComponentChars)
            {
                throw new DomainValidationException(IcsComponentSplitter.TooLargeMessage);
            }

            chars += logicalChars + 2;
            if (StartsWith("BEGIN:"))
            {
                depth++;
            }
            else if (StartsWith("END:"))
            {
                if (depth == 0)
                {
                    if (!TrimmedEquals("END:VEVENT"))
                    {
                        throw new DomainValidationException(IcsComponentSplitter.TooLargeMessage);
                    }

                    return new ScannedEvent(offset, checked((int)(logicalEnd - offset)), uid, hasRecurrenceId, recurrenceId);
                }

                depth--;
            }
            else if (depth == 0)
            {
                if (IsProperty("UID"))
                {
                    var value = Value()?.Trim();
                    uid = string.IsNullOrEmpty(value) ? null : value;
                }
                else if (IsProperty("RECURRENCE-ID"))
                {
                    hasRecurrenceId = true;
                    recurrenceId = Value();
                }
            }
        }

        throw new DomainValidationException(IcsComponentSplitter.TooLargeMessage);
    }

    private async Task<long> ReadTimeZoneAsync(long total, CancellationToken cancellationToken)
    {
        var start = total;
        total += "BEGIN:VTIMEZONE\r\n".Length;
        var depth = 0;
        var observances = 0;
        while (await ReadLogicalLineAsync(cancellationToken))
        {
            if (total + logicalChars + 2 > IcsComponentSplitter.MaxTimeZoneChars)
            {
                throw new DomainValidationException("The file's time zone definitions (VTIMEZONE) exceed 1 MB.");
            }

            total += logicalChars + 2;
            if (StartsWith("BEGIN:"))
            {
                if (depth == 0 && (TrimmedEquals("BEGIN:STANDARD") || TrimmedEquals("BEGIN:DAYLIGHT"))
                    && ++observances > IcsImportGrouper.MaxObservancesPerTimeZone)
                {
                    throw new DomainValidationException(
                        $"A time zone definition (VTIMEZONE) has more than {IcsImportGrouper.MaxObservancesPerTimeZone} observances.");
                }

                depth++;
            }
            else if (StartsWith("END:"))
            {
                if (depth == 0)
                {
                    return total;
                }

                depth--;
            }
        }

        // An unterminated VTIMEZONE at the end of the file names no event; it is dropped, as the splitter drops it.
        return start;
    }

    private async Task SkipComponentAsync(CancellationToken cancellationToken)
    {
        var depth = 0;
        while (await ReadLogicalLineAsync(cancellationToken))
        {
            if (StartsWith("BEGIN:"))
            {
                depth++;
            }
            else if (StartsWith("END:"))
            {
                if (depth == 0)
                {
                    return;
                }

                depth--;
            }
        }
    }

    // ------------------------------------------------------------------ The current logical line

    private void StripBom()
    {
        if (logicalLength >= 3 && logical[0] == 0xEF && logical[1] == 0xBB && logical[2] == 0xBF)
        {
            Buffer.BlockCopy(logical, 3, logical, 0, logicalLength - 3);
            logicalLength -= 3;
            logicalChars--;
        }
    }

    private int TrimmedLength()
    {
        var end = logicalLength;
        while (end > 0 && logical[end - 1] is (byte)' ' or (byte)'\t' or (byte)'\r')
        {
            end--;
        }

        return end;
    }

    private bool TrimmedEquals(string ascii)
    {
        var end = TrimmedLength();
        if (end != ascii.Length)
        {
            return false;
        }

        for (var i = 0; i < end; i++)
        {
            if (char.ToUpperInvariant((char)logical[i]) != ascii[i])
            {
                return false;
            }
        }

        return true;
    }

    private bool StartsWith(string ascii)
    {
        if (logicalLength < ascii.Length)
        {
            return false;
        }

        for (var i = 0; i < ascii.Length; i++)
        {
            if (char.ToUpperInvariant((char)logical[i]) != ascii[i])
            {
                return false;
            }
        }

        return true;
    }

    // The line names the property: its name, then ':' or ';'.
    private bool IsProperty(string name) =>
        StartsWith(name) && logicalLength > name.Length && logical[name.Length] is (byte)':' or (byte)';';

    // The value after the first ':' outside a quoted parameter value.
    private string? Value()
    {
        var quoted = false;
        for (var i = 0; i < logicalLength; i++)
        {
            var b = logical[i];
            if (b == (byte)'"')
            {
                quoted = !quoted;
            }
            else if (b == (byte)':' && !quoted)
            {
                return Encoding.UTF8.GetString(logical, i + 1, logicalLength - i - 1);
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ Line reading

    // One content line with its folded continuations joined (RFC 5545 §3.1), as the splitter joins it.
    private async Task<bool> ReadLogicalLineAsync(CancellationToken cancellationToken)
    {
        if (!await NextPhysicalAsync(cancellationToken))
        {
            return false;
        }

        logicalLength = 0;
        logicalChars = 0;
        logicalStart = current.Start;
        logicalEnd = current.End;
        Append(current.Data, 0, current.Length, current.Chars);
        while (await PeekPhysicalAsync(cancellationToken) && lookahead.Length > 0 && lookahead.Data[0] is (byte)' ' or (byte)'\t')
        {
            await NextPhysicalAsync(cancellationToken);
            if (logicalChars + current.Chars - 1 > IcsComponentSplitter.MaxLineChars)
            {
                throw new DomainValidationException(IcsComponentSplitter.TooLargeMessage);
            }

            Append(current.Data, 1, current.Length - 1, current.Chars - 1);
            logicalEnd = current.End;
        }

        return true;
    }

    private void Append(byte[] data, int offset, int count, int chars)
    {
        if (logicalLength + count > logical.Length)
        {
            Array.Resize(ref logical, Math.Max(logical.Length * 2, logicalLength + count));
        }

        Buffer.BlockCopy(data, offset, logical, logicalLength, count);
        logicalLength += count;
        logicalChars += chars;
    }

    private async Task<bool> PeekPhysicalAsync(CancellationToken cancellationToken)
    {
        if (!hasLookahead)
        {
            hasLookahead = await ReadPhysicalAsync(lookahead, cancellationToken);
            if (!hasLookahead)
            {
                return false;
            }
        }

        return true;
    }

    private async Task<bool> NextPhysicalAsync(CancellationToken cancellationToken)
    {
        if (hasLookahead)
        {
            hasLookahead = false;
            (current, lookahead) = (lookahead, current);
            return true;
        }

        return await ReadPhysicalAsync(current, cancellationToken);
    }

    // One physical line, without its CR LF; its byte range in the file includes the line break.
    private async Task<bool> ReadPhysicalAsync(Line line, CancellationToken cancellationToken)
    {
        line.Length = 0;
        line.Chars = 0;
        line.Start = bufferStart + position;
        var any = false;
        while (true)
        {
            if (position == length)
            {
                if (endOfFile)
                {
                    line.End = bufferStart + position;
                    if (!any)
                    {
                        return false;
                    }

                    TrimCarriageReturn(line);
                    return true;
                }

                bufferStart += length;
                length = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
                position = 0;
                if (length == 0)
                {
                    endOfFile = true;
                }

                continue;
            }

            any = true;
            var span = buffer.AsSpan(position, length - position);
            var newline = span.IndexOf((byte)'\n');
            var take = newline < 0 ? span.Length : newline;
            var chars = CountChars(span[..take]);
            if (line.Chars + chars > IcsComponentSplitter.MaxLineChars + 1)
            {
                throw new DomainValidationException(IcsComponentSplitter.TooLargeMessage);
            }

            line.Add(span[..take], chars);
            if (newline < 0)
            {
                position = length;
                continue;
            }

            position += newline + 1;
            line.End = bufferStart + position;
            TrimCarriageReturn(line);
            return true;
        }
    }

    private static void TrimCarriageReturn(Line line)
    {
        if (line.Length > 0 && line.Data[line.Length - 1] == (byte)'\r')
        {
            line.Length--;
            line.Chars--;
        }
    }

    private static int CountChars(ReadOnlySpan<byte> bytes)
    {
        var count = 0;
        foreach (var b in bytes)
        {
            if ((b & 0xC0) != 0x80)
            {
                count++;
            }
        }

        return count;
    }

    private sealed class Line
    {
        public byte[] Data = new byte[256];
        public int Length;
        public int Chars;
        public long Start;
        public long End;

        public void Add(ReadOnlySpan<byte> bytes, int chars)
        {
            if (Length + bytes.Length > Data.Length)
            {
                Array.Resize(ref Data, Math.Max(Data.Length * 2, Length + bytes.Length));
            }

            bytes.CopyTo(Data.AsSpan(Length));
            Length += bytes.Length;
            Chars += chars;
        }
    }
}
