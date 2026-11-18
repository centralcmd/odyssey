using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Odyssey.Core.Journal.Interop;

/// <summary>
/// What an inline (RFC 5545 §3.8.1.1 <c>ENCODING=BASE64;VALUE=BINARY</c>) <c>ATTACH</c> carries besides its bytes.
/// <see cref="Id"/> and <see cref="Sha256Hash"/> travel as <c>X-ODYSSEY-*</c> parameters so a re-import into the
/// same instance can recognise the file or photo it came from instead of storing a second copy; <see cref="FileName"/>
/// uses the <c>FILENAME</c> parameter and <see cref="Size"/> the <c>SIZE</c> one (both RFC 8607). A library photo is
/// marked <c>X-ODYSSEY-KIND=PHOTO</c>, names itself by <c>X-ODYSSEY-PHOTO-ID</c> instead of <c>X-ODYSSEY-FILE-ID</c>,
/// and carries its <c>X-ODYSSEY-TITLE</c> and <c>X-ODYSSEY-CAPTION</c>, so a photo imported new keeps them.
/// </summary>
internal sealed record IcsBinaryAttachmentHeader(
    Guid? Id, string? FileName, string? ContentType, string? Sha256Hash, long Size,
    bool IsPhoto = false, string? Title = null, string? Caption = null);

/// <summary>
/// Writes inline binary <c>ATTACH</c> properties straight to a stream, base64-encoding and folding the content as it is
/// read, so an attachment of any size passes through in fixed memory. <c>Ical.Net</c> would hold the whole value as one
/// string — which also caps it near 1 G characters — so the task export serializes each VTODO without its binary
/// attachments and splices these lines in. <see cref="EncodedLength"/> predicts the exact octet count the writer
/// produces, which is what lets the export refuse an over-cap file before it sends a byte.
/// </summary>
internal static class IcsBinaryAttachmentWriter
{
    /// <summary>RFC 5545 §3.1: content lines SHOULD NOT exceed 75 octets, excluding the line break.</summary>
    public const int MaxLineOctets = 75;

    // Multiple of 3, so every full read encodes without a carried remainder.
    private const int ReadChunkBytes = 3 * 16 * 1024;

    public static string BuildHead(IcsBinaryAttachmentHeader header)
    {
        var builder = new StringBuilder("ATTACH");
        if (SafeToken(header.ContentType) is { } type)
        {
            builder.Append(";FMTTYPE=").Append(type);
        }

        builder.Append(";ENCODING=BASE64;VALUE=BINARY");
        if (SafeQuoted(header.FileName) is { } name)
        {
            builder.Append(";FILENAME=\"").Append(name).Append('"');
        }

        builder.Append(";SIZE=").Append(header.Size.ToString(CultureInfo.InvariantCulture));
        if (header.IsPhoto)
        {
            builder.Append(";X-ODYSSEY-KIND=PHOTO");
        }

        if (header.Id is { } id)
        {
            builder.Append(header.IsPhoto ? ";X-ODYSSEY-PHOTO-ID=" : ";X-ODYSSEY-FILE-ID=").Append(id.ToString("D"));
        }

        if (header.IsPhoto && SafeQuoted(header.Title, foldLineBreaks: true) is { } title)
        {
            builder.Append(";X-ODYSSEY-TITLE=\"").Append(title).Append('"');
        }

        if (header.IsPhoto && SafeQuoted(header.Caption, foldLineBreaks: true) is { } caption)
        {
            builder.Append(";X-ODYSSEY-CAPTION=\"").Append(caption).Append('"');
        }

        if (SafeToken(header.Sha256Hash) is { } hash)
        {
            builder.Append(";X-ODYSSEY-SHA256=").Append(hash);
        }

        return builder.Append(':').ToString();
    }

    /// <summary>The exact number of octets <see cref="WriteAsync"/> emits for <paramref name="head"/> and a body of
    /// <paramref name="contentBytes"/> bytes, the closing line break included.</summary>
    public static long EncodedLength(string head, long contentBytes)
    {
        var state = new FoldState();
        foreach (var octets in CharOctets(head))
        {
            state.Advance(octets);
        }

        state.AdvanceAscii(Base64Length(contentBytes));
        return state.Total + 2;
    }

    public static long Base64Length(long contentBytes) => (contentBytes + 2) / 3 * 4;

    /// <summary>Writes the property and returns the number of content bytes read from <paramref name="content"/>.</summary>
    public static async Task<long> WriteAsync(Stream output, string head, Stream content, CancellationToken cancellationToken)
    {
        await using var folded = new FoldingWriter(output);
        foreach (var rune in head.EnumerateRunes())
        {
            await folded.WriteRuneAsync(rune, cancellationToken);
        }

        var input = new byte[ReadChunkBytes];
        var encoded = new byte[Base64Length(ReadChunkBytes)];
        var carried = 0;
        long total = 0;
        int read;
        while ((read = await content.ReadAsync(input.AsMemory(carried), cancellationToken)) > 0)
        {
            total += read;
            var available = carried + read;
            var whole = available - available % 3;
            if (whole > 0)
            {
                Base64.EncodeToUtf8(input.AsSpan(0, whole), encoded, out _, out var written, isFinalBlock: false);
                await folded.WriteAsciiAsync(encoded.AsMemory(0, written), cancellationToken);
            }

            carried = available - whole;
            if (carried > 0)
            {
                input.AsSpan(whole, carried).CopyTo(input);
            }
        }

        if (carried > 0)
        {
            Base64.EncodeToUtf8(input.AsSpan(0, carried), encoded, out _, out var written, isFinalBlock: true);
            await folded.WriteAsciiAsync(encoded.AsMemory(0, written), cancellationToken);
        }

        await folded.EndLineAsync(cancellationToken);
        return total;
    }

    // A parameter value that is a plain token: media types and hex hashes only ever need these characters.
    private static string? SafeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.All(c => c < 128 && (char.IsLetterOrDigit(c) || "!#$&^_.+-/".Contains(c))) ? trimmed : null;
    }

    // RFC 5545 QSAFE-CHAR: anything but CTLs and DQUOTE. A file name is a label, so dropping those loses nothing; a
    // title or caption keeps its words apart by turning each line break into a space (a parameter cannot hold one).
    private static string? SafeQuoted(string? value, bool foldLineBreaks = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var cleaned = new string(value
            .Select(c => foldLineBreaks && c is '\r' or '\n' or '\t' ? ' ' : c)
            .Where(c => c != '"' && !char.IsControl(c))
            .ToArray()).Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static IEnumerable<int> CharOctets(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            yield return rune.Utf8SequenceLength;
        }
    }

    /// <summary>The one folding rule, shared by the writer and the estimate so the two cannot disagree. A fold is a
    /// CRLF plus one space; the space counts toward the next line's 75 octets; a multi-octet character never splits.</summary>
    private struct FoldState
    {
        private int lineOctets;

        public long Total { get; private set; }

        /// <summary>Accounts for one character of <paramref name="octets"/> octets; true when a fold precedes it.</summary>
        public bool Advance(int octets)
        {
            var fold = lineOctets + octets > MaxLineOctets;
            if (fold)
            {
                Total += 3;
                lineOctets = 1;
            }

            lineOctets += octets;
            Total += octets;
            return fold;
        }

        /// <summary>How many single-octet characters still fit on the current line.</summary>
        public int Room => MaxLineOctets - lineOctets;

        public void AdvanceAscii(long count)
        {
            if (count <= Room)
            {
                lineOctets += (int)count;
                Total += count;
                return;
            }

            var first = Room;
            var rest = count - first;
            var perLine = MaxLineOctets - 1;
            var continuationLines = (rest + perLine - 1) / perLine;
            Total += count + 3 * continuationLines;
            var last = rest - (continuationLines - 1) * perLine;
            lineOctets = 1 + (int)last;
        }

        public void Fold()
        {
            Total += 3;
            lineOctets = 1;
        }

        public void Take(int count)
        {
            lineOctets += count;
            Total += count;
        }
    }

    private sealed class FoldingWriter(Stream output) : IAsyncDisposable
    {
        private static readonly byte[] FoldBytes = "\r\n "u8.ToArray();
        private readonly byte[] buffer = new byte[64 * 1024];
        private int length;
        private FoldState state;

        public async ValueTask WriteRuneAsync(Rune rune, CancellationToken cancellationToken)
        {
            if (state.Advance(rune.Utf8SequenceLength))
            {
                await AppendAsync(FoldBytes, cancellationToken);
            }

            var bytes = new byte[rune.Utf8SequenceLength];
            rune.EncodeToUtf8(bytes);
            await AppendAsync(bytes, cancellationToken);
        }

        public async ValueTask WriteAsciiAsync(ReadOnlyMemory<byte> ascii, CancellationToken cancellationToken)
        {
            while (!ascii.IsEmpty)
            {
                if (state.Room == 0)
                {
                    state.Fold();
                    await AppendAsync(FoldBytes, cancellationToken);
                }

                var take = Math.Min(state.Room, ascii.Length);
                state.Take(take);
                await AppendAsync(ascii[..take], cancellationToken);
                ascii = ascii[take..];
            }
        }

        public ValueTask EndLineAsync(CancellationToken cancellationToken) => AppendAsync("\r\n"u8.ToArray(), cancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (length > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, length));
                length = 0;
            }
        }

        private async ValueTask AppendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            if (length + bytes.Length > buffer.Length)
            {
                await output.WriteAsync(buffer.AsMemory(0, length), cancellationToken);
                length = 0;
            }

            if (bytes.Length > buffer.Length)
            {
                await output.WriteAsync(bytes, cancellationToken);
                return;
            }

            bytes.CopyTo(buffer.AsMemory(length));
            length += bytes.Length;
        }
    }
}

/// <summary>One inline attachment lifted out of an imported file and spooled to a temporary file.</summary>
internal sealed class SpooledAttachment
{
    public required string Reference { get; init; }

    public string? FileName { get; init; }

    public string? ContentType { get; init; }

    public Guid? OriginalFileId { get; init; }

    /// <summary>Marked <c>X-ODYSSEY-KIND=PHOTO</c>: a library photo, not a file. Its id then names a photo.</summary>
    public bool IsPhoto { get; init; }

    public Guid? OriginalPhotoId { get; init; }

    public string? Title { get; init; }

    public string? Caption { get; init; }

    /// <summary>The <c>X-ODYSSEY-SHA256</c> the producer declared, if any — checked against what arrived.</summary>
    public string? DeclaredSha256 { get; init; }

    /// <summary>The <c>SIZE</c> the producer declared, if any and well-formed.</summary>
    public long? DeclaredSize { get; init; }

    /// <summary>
    /// Streamed past without being decoded (issue #521 v3 §B.2): the caller resolves it by its declared hash and size to
    /// content already verified. <see cref="Path"/>, <see cref="Size"/> and <see cref="Sha256Hash"/> stay unset.
    /// </summary>
    public bool Skimmed { get; set; }

    public string? Path { get; set; }

    public long Size { get; set; }

    public string? Sha256Hash { get; set; }

    /// <summary>Why the attachment cannot be used, or null when it can.</summary>
    public string? Problem { get; set; }
}

/// <summary>
/// The result of <see cref="IcsAttachmentSpooler.SpoolAsync"/>: the calendar text with every inline attachment replaced
/// by an <c>ATTACH:odyssey-spool:…</c> placeholder, and the spooled attachments by placeholder. Disposing deletes the
/// temporary files.
/// </summary>
internal sealed class IcsAttachmentSpool : IDisposable
{
    public const string Scheme = "odyssey-spool";

    private readonly Dictionary<string, SpooledAttachment> byReference = new(StringComparer.Ordinal);

    public IcsAttachmentSpool(string nonce) => Nonce = nonce;

    public string Nonce { get; }

    public string CalendarText { get; set; } = string.Empty;

    public int Count => byReference.Count;

    /// <summary><c>ATTACH</c> lines past <see cref="IcsAttachmentSpooler.Options.MaxAttachLines"/>, dropped unparsed.</summary>
    public int DroppedAttachLines { get; set; }

    public IEnumerable<SpooledAttachment> Attachments => byReference.Values;

    public void Add(SpooledAttachment attachment) => byReference[attachment.Reference] = attachment;

    /// <summary>The spooled attachment a placeholder URI names — only this import's own placeholders resolve.</summary>
    public SpooledAttachment? Find(Uri? uri)
    {
        if (uri is null || !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var raw = uri.OriginalString;
        var value = raw.Length > Scheme.Length + 1 ? raw[(Scheme.Length + 1)..] : string.Empty;
        return byReference.GetValueOrDefault(value);
    }

    public void Dispose()
    {
        foreach (var attachment in byReference.Values)
        {
            Delete(attachment);
        }
    }

    public static void Delete(SpooledAttachment attachment)
    {
        if (attachment.Path is not { } path)
        {
            return;
        }

        attachment.Path = null;
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: the OS temp cleaner gets whatever is left.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// The streaming pre-pass of an <c>.ics</c> import: copies the calendar's text, except that each inline binary
/// <c>ATTACH</c> (<c>ENCODING=BASE64</c>) is decoded straight to a temporary file — hashed and size-checked as it goes —
/// and replaced by a short placeholder. <c>Ical.Net</c> then parses only the remaining text, so an attachment's size is
/// bounded by disk rather than by memory or the runtime's string length limit. Folded lines are unfolded on the fly and a
/// value never sits in memory whole.
/// </summary>
internal static class IcsAttachmentSpooler
{
    public const string TooLargeProblem = "too-large";
    public const string DamagedProblem = "damaged";
    public const string ChecksumProblem = "checksum";
    public const string EmptyProblem = "empty";

    private const int MaxHeadChars = 16 * 1024;
    private const int DecodeChars = 16 * 1024; // multiple of 4

    /// <summary>Limits and gates for one <see cref="SpoolAsync"/> run.</summary>
    /// <param name="AllowAttachments">False refuses the file (<c>403</c>) at its first inline file, before a byte
    /// of it is decoded — a caller who may not import files never makes the server spool them.</param>
    /// <param name="AllowPhotos">The same for an inline library photo (<c>X-ODYSSEY-KIND=PHOTO</c>); null means
    /// photos are gated like files (a surface without photos treats one as a file).</param>
    /// <param name="MaxAttachments">Inline attachments one file may carry. Each costs a temp file and an entry, so a
    /// file of many tiny attachments is refused rather than allowed to exhaust inodes and memory.</param>
    /// <param name="SpoolDirectory">Where temp files go; null means <c>ASPNETCORE_TEMP</c>, else the OS temp path.</param>
    /// <param name="MaxAttachLines">
    /// <c>ATTACH</c> properties of any form parsed; the rest are dropped from the text unparsed and counted in
    /// <see cref="IcsAttachmentSpool.DroppedAttachLines"/> (issue #521 §7.4). Null parses every one.
    /// </param>
    /// <param name="SkipDecode">True streams an inline value past undecoded and marks the attachment skimmed.</param>
    /// <param name="BeforeDecode">Runs before each decode — the calendar import's free-disk check.</param>
    /// <param name="TopLevelOnly">
    /// The text is ONE component: an inline <c>ATTACH</c> nested in a sub-component (a <c>VALARM</c>) is dropped unread
    /// rather than lifted, so only the component's own attachments are ever decoded (PR #529 review S2).
    /// </param>
    /// <param name="OnSpooled">
    /// Runs right after each decode, before the next one, with the text spooled so far (issue #521 v4 §1): the caller
    /// resolves, stores and deletes the temp file, so at most one attachment is on disk at a time.
    /// </param>
    public sealed record Options(
        long MaxFileBytes, long MaxTextChars, bool AllowAttachments = true, int MaxAttachments = int.MaxValue,
        string? SpoolDirectory = null, bool? AllowPhotos = null, int? MaxAttachLines = null,
        Func<SpooledAttachment, bool>? SkipDecode = null, Action? BeforeDecode = null,
        Func<SpooledAttachment, Func<string>, CancellationToken, Task>? OnSpooled = null, bool TopLevelOnly = false);

    /// <summary>Temp files older than this are leftovers of a crashed process, swept on the next import.</summary>
    internal static readonly TimeSpan StaleAfter = ImportSpool.StaleAfter;

    private const string FilePrefix = "odyssey-import-";

    public static async Task<IcsAttachmentSpool> SpoolAsync(
        TextReader reader, Options options, CancellationToken cancellationToken)
    {
        var directory = options.SpoolDirectory ?? ImportSpool.Directory();
        ImportSpool.SweepStale(directory, FilePrefix + "*.bin");
        var maxTextChars = options.MaxTextChars;
        var spool = new IcsAttachmentSpool(Guid.NewGuid().ToString("N"));
        try
        {
            var source = new CharSource(reader);
            var text = new StringBuilder();
            var head = new StringBuilder();
            var attachLines = 0;
            var depth = 0;
            while (await source.EnsureAsync(1, cancellationToken))
            {
                head.Clear();
                var headComplete = await ReadHeadAsync(source, head, cancellationToken);
                if (headComplete && options.TopLevelOnly)
                {
                    var name = head.ToString().Trim();
                    if (string.Equals(name, "BEGIN", StringComparison.OrdinalIgnoreCase))
                    {
                        depth++;
                    }
                    else if (string.Equals(name, "END", StringComparison.OrdinalIgnoreCase))
                    {
                        depth--;
                    }
                    else if (depth > 1 && IsInlineAttachment(head, out _))
                    {
                        await SkipValueAsync(source, cancellationToken);
                        continue;
                    }
                }

                if (headComplete && options.MaxAttachLines is { } maxLines && IsAttachProperty(head) && ++attachLines > maxLines)
                {
                    spool.DroppedAttachLines++;
                    await SkipValueAsync(source, cancellationToken);
                    continue;
                }

                if (headComplete && IsInlineAttachment(head, out var parameters))
                {
                    var isPhoto = IsPhotoKind(parameters);
                    if (isPhoto && !(options.AllowPhotos ?? options.AllowAttachments))
                    {
                        throw new DomainForbiddenException(
                            "This file carries photos, and importing photos needs permission to read and upload photos.");
                    }

                    if (!isPhoto && !options.AllowAttachments)
                    {
                        throw new DomainForbiddenException(
                            "This file carries attached files, and importing attached files needs permission to read and upload files.");
                    }

                    if (spool.Count >= options.MaxAttachments)
                    {
                        throw new DomainValidationException(
                            $"The file carries more than {options.MaxAttachments} attached files.");
                    }

                    var attachment = NewAttachment(spool, parameters);
                    // Added before decoding, so a failure part-way (disk full, cancellation, the byte cap) still
                    // leaves the partial temp file to the spool's disposal rather than orphaning it.
                    spool.Add(attachment);
                    if (options.SkipDecode?.Invoke(attachment) == true)
                    {
                        attachment.Skimmed = true;
                        await SkipValueAsync(source, cancellationToken);
                    }
                    else
                    {
                        options.BeforeDecode?.Invoke();
                        await DecodeValueAsync(source, attachment, directory, options.MaxFileBytes, cancellationToken);
                        if (options.OnSpooled is { } onSpooled)
                        {
                            await onSpooled(attachment, text.ToString, cancellationToken);
                        }
                    }

                    text.Append("ATTACH:").Append(IcsAttachmentSpool.Scheme).Append(':').Append(attachment.Reference).Append("\r\n");
                }
                else
                {
                    text.Append(head);
                    if (headComplete)
                    {
                        text.Append(':');
                        await CopyValueAsync(source, text, maxTextChars, cancellationToken);
                    }
                    else
                    {
                        await CopyValueAsync(source, text, maxTextChars, cancellationToken);
                    }
                }

                if (text.Length > maxTextChars)
                {
                    throw TextTooLarge(maxTextChars);
                }
            }

            spool.CalendarText = text.ToString();
            return spool;
        }
        catch
        {
            spool.Dispose();
            throw;
        }
    }

    private static DomainValidationException TextTooLarge(long maxTextChars) =>
        new($"The calendar text, not counting attached files, exceeds the {maxTextChars / (1024 * 1024)} MB limit.");

    // Reads a property's name and parameters, unfolding as it goes, up to the ':' that starts its value. Returns false
    // when the line ends first or the head is implausibly long — the line is then copied through as plain text.
    private static async Task<bool> ReadHeadAsync(CharSource source, StringBuilder head, CancellationToken cancellationToken)
    {
        var quoted = false;
        while (await source.EnsureAsync(3, cancellationToken) || source.Available > 0)
        {
            var c = source.Current;
            if (c is '\r' or '\n')
            {
                if (source.IsFold())
                {
                    source.SkipFold();
                    continue;
                }

                return false;
            }

            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == ':' && !quoted)
            {
                source.Advance(1);
                return true;
            }

            head.Append(c);
            source.Advance(1);
            if (head.Length >= MaxHeadChars)
            {
                return false;
            }
        }

        return false;
    }

    private static bool IsAttachProperty(StringBuilder head) => IcsAttachHead.Parse(head.ToString()).IsAttach;

    // Consumes the rest of a logical line, folds included, keeping nothing.
    private static async Task SkipValueAsync(CharSource source, CancellationToken cancellationToken)
    {
        while (await source.EnsureAsync(3, cancellationToken) || source.Available > 0)
        {
            var c = source.Current;
            if (c is '\r' or '\n')
            {
                if (source.IsFold())
                {
                    source.SkipFold();
                    continue;
                }

                source.SkipLineBreak();
                return;
            }

            source.Advance(source.RunUntilLineBreak().Length);
        }
    }

    // The one head reader both calendar passes and the task and journal imports share (PR #529 review S1).
    private static bool IsInlineAttachment(StringBuilder head, out Dictionary<string, string> parameters)
    {
        var parsed = IcsAttachHead.Parse(head.ToString());
        parameters = new Dictionary<string, string>(parsed.Parameters, StringComparer.OrdinalIgnoreCase);
        return parsed.IsInline;
    }

    private static SpooledAttachment NewAttachment(IcsAttachmentSpool spool, Dictionary<string, string> parameters)
    {
        string? Param(params string[] names) =>
            names.Select(name => parameters.GetValueOrDefault(name)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

        return new SpooledAttachment
        {
            Reference = $"{spool.Nonce}-{spool.Count}",
            FileName = Param("FILENAME", "X-FILENAME", "X-APPLE-FILENAME", "X-ORACLE-FILENAME"),
            ContentType = Param("FMTTYPE")?.ToLowerInvariant(),
            OriginalFileId = Guid.TryParse(Param("X-ODYSSEY-FILE-ID"), out var id) ? id : null,
            DeclaredSha256 = Param("X-ODYSSEY-SHA256")?.ToLowerInvariant(),
            DeclaredSize = long.TryParse(Param("SIZE"), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var size) ? size : null,
            IsPhoto = IsPhotoKind(parameters),
            OriginalPhotoId = Guid.TryParse(Param("X-ODYSSEY-PHOTO-ID"), out var photoId) ? photoId : null,
            Title = Param("X-ODYSSEY-TITLE"),
            Caption = Param("X-ODYSSEY-CAPTION"),
        };
    }

    private static bool IsPhotoKind(Dictionary<string, string> parameters) =>
        parameters.TryGetValue("X-ODYSSEY-KIND", out var kind) && string.Equals(kind, "PHOTO", StringComparison.OrdinalIgnoreCase);

    // Decodes the rest of the logical line into a temp file. Whatever goes wrong with the content, the line is still
    // consumed to its end, so one bad attachment never derails the parse of the rest of the file.
    private static async Task DecodeValueAsync(
        CharSource source, SpooledAttachment attachment, string directory, long maxFileBytes, CancellationToken cancellationToken)
    {
        var path = System.IO.Path.Combine(directory, $"{FilePrefix}{Guid.NewGuid():N}.bin");
        attachment.Path = path;
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var chars = new char[DecodeChars];
        var bytes = new byte[DecodeChars / 4 * 3];
        var pending = 0;
        long size = 0;
        string? problem = null;

        async Task FlushAsync(bool final)
        {
            if (problem is not null || pending == 0)
            {
                pending = 0;
                return;
            }

            var count = pending;
            if (final && count % 4 != 0)
            {
                if (count % 4 == 1)
                {
                    problem = DamagedProblem;
                    return;
                }

                while (count % 4 != 0)
                {
                    chars[count++] = '=';
                }
            }

            if (!Convert.TryFromBase64Chars(chars.AsSpan(0, count), bytes, out var written))
            {
                problem = DamagedProblem;
                return;
            }

            pending = 0;
            size += written;
            if (size > maxFileBytes)
            {
                problem = TooLargeProblem;
                return;
            }

            hash.AppendData(bytes, 0, written);
            try
            {
                await file.WriteAsync(bytes.AsMemory(0, written), cancellationToken);
            }
            catch (IOException ex) when (ex.HResult is 28 or unchecked((int)0x80070070) or unchecked((int)0x80070027))
            {
                // ENOSPC is the volume's state, not the file's: a 507, never a 500 (issue #521 v4 §1).
                throw new Odyssey.Core.FileStorage.FileStorageFullException(
                    "There is not enough free temporary disk space to read an attached file. Try again later.");
            }
        }

        try
        {
            while (await source.EnsureAsync(3, cancellationToken) || source.Available > 0)
            {
                var c = source.Current;
                if (c is '\r' or '\n')
                {
                    if (source.IsFold())
                    {
                        source.SkipFold();
                        continue;
                    }

                    source.SkipLineBreak();
                    break;
                }

                source.Advance(1);
                if (problem is not null || c is ' ' or '\t')
                {
                    continue;
                }

                chars[pending++] = c;
                if (pending == chars.Length)
                {
                    await FlushAsync(final: false);
                }
            }

            await FlushAsync(final: true);
        }
        finally
        {
            await file.DisposeAsync();
        }

        if (problem is null && size == 0)
        {
            problem = EmptyProblem;
        }

        attachment.Size = size;
        attachment.Sha256Hash = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (problem is null && attachment.DeclaredSha256 is { } declared && declared != attachment.Sha256Hash)
        {
            problem = ChecksumProblem;
        }

        attachment.Problem = problem;
        if (problem is not null)
        {
            IcsAttachmentSpool.Delete(attachment);
        }
    }

    // The directory and the sweep of a crashed process's leftovers are ImportSpool's, shared with the calendar grouper,
    // so every import's buffers share the volume ASP.NET Core spools the upload on (/tmp can be a RAM-backed tmpfs).

    // Copies the rest of a logical line through, folds included, then a CRLF.
    private static async Task CopyValueAsync(CharSource source, StringBuilder text, long maxTextChars, CancellationToken cancellationToken)
    {
        while (await source.EnsureAsync(3, cancellationToken) || source.Available > 0)
        {
            var c = source.Current;
            if (c is '\r' or '\n')
            {
                if (source.IsFold())
                {
                    var whitespace = source.SkipFold();
                    text.Append("\r\n").Append(whitespace);
                    continue;
                }

                source.SkipLineBreak();
                break;
            }

            var run = source.RunUntilLineBreak();
            text.Append(run.Span);
            source.Advance(run.Length);
            if (text.Length > maxTextChars)
            {
                throw TextTooLarge(maxTextChars);
            }
        }

        text.Append("\r\n");
    }

    /// <summary>A buffered character reader with the few-character lookahead fold detection needs.</summary>
    private sealed class CharSource(TextReader reader)
    {
        private readonly char[] buffer = new char[64 * 1024];
        private int position;
        private int length;
        private bool eof;

        public int Available => length - position;

        public char Current => buffer[position];

        public void Advance(int count) => position += count;

        /// <summary>Ensures at least <paramref name="count"/> characters are buffered; false when the input ends first.</summary>
        public async ValueTask<bool> EnsureAsync(int count, CancellationToken cancellationToken)
        {
            while (Available < count && !eof)
            {
                if (position > 0)
                {
                    Array.Copy(buffer, position, buffer, 0, Available);
                    length = Available;
                    position = 0;
                }

                var read = await reader.ReadAsync(buffer.AsMemory(length), cancellationToken);
                if (read == 0)
                {
                    eof = true;
                }

                length += read;
            }

            return Available >= count;
        }

        private int BreakLength()
        {
            if (buffer[position] == '\r')
            {
                return Available > 1 && buffer[position + 1] == '\n' ? 2 : 1;
            }

            return 1;
        }

        /// <summary>Whether the line break at the cursor is a fold — followed by a space or a tab.</summary>
        public bool IsFold()
        {
            var after = position + BreakLength();
            return after < length && buffer[after] is ' ' or '\t';
        }

        public char SkipFold()
        {
            position += BreakLength();
            return buffer[position++];
        }

        public void SkipLineBreak() => position += BreakLength();

        public ReadOnlyMemory<char> RunUntilLineBreak()
        {
            var span = buffer.AsSpan(position, Available);
            var end = span.IndexOfAny('\r', '\n');
            return buffer.AsMemory(position, end < 0 ? Available : end);
        }
    }
}
