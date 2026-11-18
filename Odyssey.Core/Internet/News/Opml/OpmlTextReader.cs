using System.Globalization;
using AngleSharp.Html;
using Odyssey.Dtos.Internet;

namespace Odyssey.Core.Internet.News.Opml;

/// <summary>A run or construct in an OPML file exceeded <see cref="NewsOpmlLimits.MaxTokenLength"/> (issue #508 §7.5).</summary>
public sealed class OpmlTokenLimitException() : Exception("The OPML document has a token longer than the reader accepts.");

/// <summary>
/// The chunked text reader every OPML import reads through (issue #508 §3, §7.5). It sits between the decoded upload and
/// the <c>XmlReader</c> and does two things as it reads, never holding more than one chunk and its lookahead:
/// <list type="number">
/// <item><b>The entity rule</b> of <see cref="Parsing.NewsEntityPrepass"/>, applied chunk by chunk with its state —
/// inside a comment, a CDATA section, a processing instruction, the DOCTYPE, or a partial reference — carried across
/// chunk boundaries. Every reference other than the five XML entities and the HTML5 named entities becomes literal
/// text, so nothing a DOCTYPE declares is ever expanded. A differential test pins the output to the pre-pass's
/// (AC 37).</item>
/// <item><b>The token cap.</b> A run of more than <see cref="NewsOpmlLimits.MaxTokenLength"/> characters without a
/// <c>&lt;</c> is refused. The counter is kept <em>independently</em> of the entity rule's construct state and resets
/// only on <c>&lt;</c>, which XML forbids inside an attribute value and inside character data, so no attribute value
/// or text node can grow past the cap however many <c>&gt;</c> or quotes it holds. A comment, CDATA section,
/// processing instruction or DOCTYPE (internal subset included), where <c>&lt;</c> is legal, is capped as a whole from
/// its opening delimiter. Either refusal throws <see cref="OpmlTokenLimitException"/> before the <c>XmlReader</c>
/// materialises the value.</item>
/// </list>
/// </summary>
public sealed class OpmlTextReader : TextReader
{
    public const int DefaultChunkSize = 8192;

    private const int MaxNameLength = 40;

    /// <summary>The longest lookahead any step needs: <c>&amp;</c>, a 40-character name and <c>;</c>, or <c>&lt;!DOCTYPE</c>.</summary>
    private const int Lookahead = 64;

    private readonly TextReader inner;
    private readonly int chunkSize;
    private readonly int maxToken;

    private char[] input;
    private int inputStart;
    private int inputEnd;
    private bool innerEof;

    private char[] output;
    private int outputStart;
    private int outputEnd;

    private Construct construct;
    private int constructLength;
    private int doctypeDepth;
    private char doctypeQuote;
    private int runSinceLessThan;

    public OpmlTextReader(TextReader inner, int chunkSize = DefaultChunkSize, int maxTokenLength = NewsOpmlLimits.MaxTokenLength)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);
        this.inner = inner;
        this.chunkSize = chunkSize;
        maxToken = maxTokenLength;
        input = new char[chunkSize + Lookahead * 2];
        output = new char[Math.Max(256, chunkSize * 2)];
    }

    private enum Construct : byte
    {
        None,
        Comment,
        CData,
        ProcessingInstruction,
        Doctype,
    }

    public override int Peek()
    {
        if (outputStart == outputEnd && !Fill())
        {
            return -1;
        }

        return output[outputStart];
    }

    public override int Read()
    {
        var next = Peek();
        if (next >= 0)
        {
            outputStart++;
        }

        return next;
    }

    public override int Read(char[] buffer, int index, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(index, count));
    }

    public override int Read(Span<char> buffer)
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        if (outputStart == outputEnd && !Fill())
        {
            return 0;
        }

        var n = Math.Min(buffer.Length, outputEnd - outputStart);
        output.AsSpan(outputStart, n).CopyTo(buffer);
        outputStart += n;
        return n;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Processes input until some output exists or the input is exhausted.</summary>
    private bool Fill()
    {
        outputStart = outputEnd = 0;
        while (outputEnd == 0)
        {
            EnsureLookahead();
            if (inputStart == inputEnd)
            {
                return false;
            }

            // Process at most a chunk, each step keeping its lookahead inside the buffer (or at the end of input).
            var budget = chunkSize;
            while (budget > 0 && inputStart < inputEnd && (innerEof || inputEnd - inputStart >= Lookahead))
            {
                var before = inputStart;
                Step();
                budget -= inputStart - before;
            }
        }

        return true;
    }

    /// <summary>Tops the input buffer up to a chunk plus lookahead, compacting first.</summary>
    private void EnsureLookahead()
    {
        if (innerEof || inputEnd - inputStart >= chunkSize + Lookahead)
        {
            return;
        }

        if (inputStart > 0)
        {
            Array.Copy(input, inputStart, input, 0, inputEnd - inputStart);
            inputEnd -= inputStart;
            inputStart = 0;
        }

        while (!innerEof && inputEnd < chunkSize + Lookahead)
        {
            var read = inner.Read(input, inputEnd, Math.Min(chunkSize, input.Length - inputEnd));
            if (read == 0)
            {
                innerEof = true;
            }

            inputEnd += read;
        }
    }

    /// <summary>The character <paramref name="offset"/> past the current one, or <c>-1</c> past the end of input.</summary>
    private int At(int offset) => inputStart + offset < inputEnd ? input[inputStart + offset] : -1;

    private bool Matches(int offset, string text)
    {
        for (var k = 0; k < text.Length; k++)
        {
            if (At(offset + k) != text[k])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Consumes <paramref name="count"/> input characters, copying them to the output and counting them.</summary>
    private void Copy(int count)
    {
        for (var k = 0; k < count; k++)
        {
            Emit(Count(input[inputStart++]));
        }
    }

    /// <summary>Consumes <paramref name="count"/> input characters without copying them (they are re-emitted rewritten).</summary>
    private void Skip(int count)
    {
        for (var k = 0; k < count; k++)
        {
            Count(input[inputStart++]);
        }
    }

    /// <summary>The token cap: unconditional, independent of the construct state, reset only on <c>&lt;</c>.</summary>
    private char Count(char c)
    {
        if (c == '<')
        {
            runSinceLessThan = 0;
        }
        else if (++runSinceLessThan > maxToken)
        {
            throw new OpmlTokenLimitException();
        }

        if (construct != Construct.None && ++constructLength > maxToken)
        {
            throw new OpmlTokenLimitException();
        }

        return c;
    }

    private void Emit(char c)
    {
        if (outputEnd == output.Length)
        {
            Array.Resize(ref output, output.Length * 2);
        }

        output[outputEnd++] = c;
    }

    private void Emit(string text)
    {
        foreach (var c in text)
        {
            Emit(c);
        }
    }

    private void Step()
    {
        switch (construct)
        {
            case Construct.Comment:
                StepUntil("-->");
                return;
            case Construct.CData:
                StepUntil("]]>");
                return;
            case Construct.ProcessingInstruction:
                StepUntil("?>");
                return;
            case Construct.Doctype:
                StepDoctype();
                return;
        }

        var c = input[inputStart];
        if (c == '<')
        {
            if (Matches(0, "<!--"))
            {
                Open(Construct.Comment, 4);
            }
            else if (Matches(0, "<![CDATA["))
            {
                Open(Construct.CData, 9);
            }
            else if (Matches(0, "<?"))
            {
                Open(Construct.ProcessingInstruction, 2);
            }
            else if (Matches(0, "<!DOCTYPE"))
            {
                doctypeDepth = 0;
                doctypeQuote = '\0';
                Open(Construct.Doctype, 9);
            }
            else
            {
                Copy(1);
            }

            return;
        }

        if (c != '&')
        {
            Copy(1);
            return;
        }

        Reference();
    }

    private void Open(Construct kind, int delimiterLength)
    {
        construct = kind;
        constructLength = 0;
        Copy(delimiterLength);
    }

    /// <summary>Inside a comment, CDATA section or PI: verbatim to the terminator, which ends the construct.</summary>
    private void StepUntil(string terminator)
    {
        if (Matches(0, terminator))
        {
            Copy(terminator.Length);
            construct = Construct.None;
            return;
        }

        Copy(1);
    }

    /// <summary>Inside the DOCTYPE: verbatim to the <c>&gt;</c> that closes it, past an internal subset and any quoted literal.</summary>
    private void StepDoctype()
    {
        var c = input[inputStart];
        Copy(1);
        if (doctypeQuote != '\0')
        {
            if (c == doctypeQuote)
            {
                doctypeQuote = '\0';
            }
        }
        else if (c is '"' or '\'')
        {
            doctypeQuote = c;
        }
        else if (c == '[')
        {
            doctypeDepth++;
        }
        else if (c == ']')
        {
            doctypeDepth = Math.Max(0, doctypeDepth - 1);
        }
        else if (c == '>' && doctypeDepth == 0)
        {
            construct = Construct.None;
        }
    }

    /// <summary>The pre-pass's reference rule, on the lookahead window at an <c>&amp;</c>.</summary>
    private void Reference()
    {
        // A numeric character reference is kept as written. More than eight digits never qualifies, so the scan stops at nine.
        if (At(1) == '#')
        {
            var k = 2;
            var hex = At(k) is 'x' or 'X';
            if (hex)
            {
                k++;
            }

            var digitsStart = k;
            while (k - digitsStart <= 8 && At(k) is var d and >= 0 && (hex ? char.IsAsciiHexDigit((char)d) : char.IsAsciiDigit((char)d)))
            {
                k++;
            }

            if (k > digitsStart && At(k) == ';' && k - digitsStart <= 8)
            {
                Copy(k + 1);
                return;
            }

            Skip(1);
            Emit("&amp;");
            return;
        }

        var nameEnd = 1;
        while (nameEnd - 1 < MaxNameLength && At(nameEnd) is var n and >= 0 && char.IsAsciiLetterOrDigit((char)n))
        {
            nameEnd++;
        }

        if (nameEnd == 1 || At(nameEnd) != ';' || !char.IsAsciiLetter((char)At(1)))
        {
            Skip(1);
            Emit("&amp;");
            return;
        }

        var name = new string(input, inputStart + 1, nameEnd - 1);
        if (name is "amp" or "lt" or "gt" or "quot" or "apos")
        {
            Copy(nameEnd + 1);
            return;
        }

        var symbol = HtmlEntityProvider.Resolver.GetSymbol(name + ";");
        if (string.IsNullOrEmpty(symbol))
        {
            Skip(1);
            Emit("&amp;");
            return;
        }

        Skip(nameEnd + 1);
        foreach (var rune in symbol.EnumerateRunes())
        {
            Emit("&#x");
            Emit(rune.Value.ToString("X", CultureInfo.InvariantCulture));
            Emit(';');
        }
    }
}
