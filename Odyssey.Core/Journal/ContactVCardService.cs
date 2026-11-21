using Odyssey.Core;
using Odyssey.Core.Finance;
using Odyssey.Core.Imaging;
using Odyssey.Dtos;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.Journal.Avatar;
using Odyssey.Dtos.Journal;
using Odyssey.Core.Journal.Interop;

namespace Odyssey.Core.Journal;

/// <summary>
/// vCard (RFC 6350 v4.0) import/export for Contacts (issue #338), mirroring the shape of
/// <see cref="CalendarIcsService"/> (issue #330): export/import methods, <c>Max*</c> caps, an
/// <see cref="IsAcceptedContentType"/> helper. A minimal hand-rolled encoder/decoder is used rather than
/// a third-party vCard library — the property set this feature emits/consumes (§9) is small and fixed.
/// Both reads and writes flow exclusively through <see cref="ContactService"/>'s existing
/// get/create/update/child-CRUD methods (§10 item 3: no parallel read or write path, no new
/// mass-assignment surface).
/// </summary>
public partial class ContactVCardService
{
    // The repeatable-property bound was a `private const 200` here until issue #434 (key 12). It is
    // now admin-editable and TIGHTEN-ONLY, because the cost it guards is worse than the O(N^2) sibling
    // re-query the original comment described: ContactService.CreateAddress/CreateEmail/CreatePhone each
    // perform a sibling ToListAsync AND their own SaveChangesAsync per property, and that is multiplied
    // by ContactVCardMaxImportEntries, which ships "unlimited". Any numeric ceiling above 200 would be a
    // guess about a product of three unbounded terms, so the raise direction is removed instead.
    //
    // MaxVCardEntries only bounds the number of top-level VCARD blocks, never repeatable properties
    // (ADR/EMAIL/TEL) within a single one — which is why this bound exists separately at all.
    private const string UnnamedContact = "(unnamed)";

    private static readonly string[] AcceptedContentTypes =
        ["text/vcard", "text/x-vcard", "text/directory", "application/octet-stream", "text/plain"];

    private readonly OdysseyContext context;
    private readonly ContactService contactService;
    private readonly ContactAvatarService avatarService;
    private readonly IImportExportLimitsLookup limits;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<ContactVCardService> logger;

    public ContactVCardService(
        OdysseyContext context, ContactService contactService, ContactAvatarService avatarService,
        IImportExportLimitsLookup limits,
        ILogger<ContactVCardService> logger, TimeProvider? timeProvider = null)
    {
        this.context = context;
        this.contactService = contactService;
        this.avatarService = avatarService;
        this.limits = limits;
        this.logger = logger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    // ---------------------------------------------------------------- Parsing primitives

    /// <summary>
    /// One parsed content line. <paramref name="Group"/> is the RFC 6350 §3.2 group prefix
    /// (<c>item1</c> in <c>item1.NICKNAME:…</c>), <see langword="null"/> when the property is
    /// ungrouped — added in issue #48 so an alias can be paired with the label sharing its group.
    ///
    /// <para>
    /// <b>The dictionary key stays the BARE property name.</b> Keying it group-qualified would break
    /// every existing lookup — <c>RawValue(props, "N")</c> and
    /// <c>TextValue(props, "UID"|"FN"|"NOTE"|"BDAY"|"URL")</c> — and Apple and iCloud emit grouped
    /// properties routinely (<c>item1.EMAIL</c> + <c>item1.X-ABLabel</c>), so those cards would stop
    /// importing. The group is carried alongside the value, not folded into the key.
    /// </para>
    /// </summary>
    private readonly record struct VCardProperty(IReadOnlyDictionary<string, string> Params, string RawValue, string? Group);

    private static string? RawValue(Dictionary<string, List<VCardProperty>> props, string name) =>
        props.TryGetValue(name, out var list) && list.Count > 0 ? list[0].RawValue : null;

    // The unescaped, trimmed text of a property's first occurrence, or null if absent/blank.
    private static string? TextValue(Dictionary<string, List<VCardProperty>> props, string name)
    {
        var raw = RawValue(props, name);
        if (raw is null)
        {
            return null;
        }

        var value = UnescapeText(raw).Trim();
        return value.Length == 0 ? null : value;
    }

    private static IReadOnlyList<VCardProperty> Properties(Dictionary<string, List<VCardProperty>> props, string name) =>
        props.TryGetValue(name, out var list) ? list : [];

    // Bounds the repeatable properties of one type (ADR/EMAIL/TEL) actually processed for a single
    // entry — each one costs a sibling ToListAsync plus its own SaveChangesAsync, so processing all of
    // an unbounded count is quadratic in queries. The excess is reported once per entry rather than
    // once per dropped property, to keep the result payload from ballooning to match.
    //
    // The cap is a PARAMETER, not read inside this method: it is `static`, and the caller resolves one
    // settings snapshot for the whole import so a concurrent admin write cannot change the bound
    // between two entries of the same file (issue #434 key 12).
    private static IReadOnlyList<VCardProperty> CappedProperties(
        Dictionary<string, List<VCardProperty>> props, string name, string label, string sampleName,
        ImportSkipCollector skipped, int maxRepeatableProperties)
    {
        var all = Properties(props, name);
        if (all.Count <= maxRepeatableProperties)
        {
            return all;
        }

        skipped.Add(
            $"{label} dropped: more than {maxRepeatableProperties} in one entry — the rest were not imported.",
            sampleName);
        return all.Take(maxRepeatableProperties).ToList();
    }

    // Splits the file into logical BEGIN:VCARD/END:VCARD blocks (each a list of unfolded, raw property
    // lines), reading and unfolding lazily from the TextReader rather than materializing the whole
    // file as one string first (issue #343 §5): a block is yielded the moment its END:VCARD line is
    // recognized, so the caller's entry-count cap can reject a file at the entry that crosses it
    // instead of only after every block has been read. Lines outside any BEGIN/END pair are ignored
    // rather than treated as an error — mirrors the lenient "extension + successful parse are the real
    // gate" posture used for ICS import.
    private static async IAsyncEnumerable<List<string>> SplitBlocksAsync(
        TextReader reader, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<string>? current = null;
        // RFC 6350 §3.2 unfolding: a line starting with a space or tab is a continuation of the
        // previous line (with that one leading character stripped). One physical line of lookahead is
        // held in `pending` — a logical line isn't complete (and therefore isn't ready to apply) until
        // either the next physical line proves NOT to be a continuation of it, or EOF is reached.
        StringBuilder? pending = null;

        string? raw;
        while ((raw = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t') && pending is not null)
            {
                // Accumulate in the existing StringBuilder rather than `logicalLine += raw[1..]` —
                // repeated += allocates and copies the ENTIRE line built so far, making reconstruction
                // of a single line folded across N continuation lines O(N^2). A vCard producer can fold
                // a property value across arbitrarily many continuation lines (RFC 6350 §3.2 permits
                // this anywhere), so this was reachable with a well-formed file, not just a malicious
                // one (issue #338 review).
                pending.Append(raw, 1, raw.Length - 1);
                continue;
            }

            if (pending is not null && ApplyLine(pending.ToString(), ref current) is { } completed)
            {
                yield return completed;
            }

            pending = new StringBuilder(raw);
        }

        if (pending is not null && ApplyLine(pending.ToString(), ref current) is { } last)
        {
            yield return last;
        }
    }

    // Applies one complete (already-unfolded) logical line to the in-progress block, returning the
    // just-completed block when `line` was an END:VCARD that closes one, or null otherwise.
    private static List<string>? ApplyLine(string line, ref List<string>? current)
    {
        if (line.Equals("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase))
        {
            current = [];
            return null;
        }

        if (line.Equals("END:VCARD", StringComparison.OrdinalIgnoreCase))
        {
            var completed = current;
            current = null;
            return completed;
        }

        current?.Add(line);
        return null;
    }

    private static Dictionary<string, List<VCardProperty>> ParseProperties(List<string> lines)
    {
        var result = new Dictionary<string, List<VCardProperty>>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var colonIndex = line.IndexOf(':');
            if (colonIndex < 0)
            {
                continue; // not a well-formed content line — ignore rather than fail the whole block
            }

            var left = line[..colonIndex];
            var rawValue = line[(colonIndex + 1)..];

            var segments = left.Split(';');
            var namePart = segments[0];
            var dotIndex = namePart.LastIndexOf('.');
            var name = (dotIndex >= 0 ? namePart[(dotIndex + 1)..] : namePart).Trim();
            if (name.Length == 0)
            {
                continue;
            }

            // Retained ALONGSIDE the bare name, never folded into it (see VCardProperty).
            var group = dotIndex >= 0 ? namePart[..dotIndex].Trim() : null;
            if (group is { Length: 0 })
            {
                group = null;
            }

            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 1; i < segments.Length; i++)
            {
                var eq = segments[i].IndexOf('=');
                if (eq < 0)
                {
                    continue;
                }

                parameters[segments[i][..eq].Trim()] = segments[i][(eq + 1)..].Trim().Trim('"');
            }

            if (!result.TryGetValue(name, out var list))
            {
                list = [];
                result[name] = list;
            }

            list.Add(new VCardProperty(parameters, rawValue, group));
        }

        return result;
    }

    // Splits a raw structured-property value on unescaped ';' — an escaped "\;" is kept intact for the
    // subsequent per-component UnescapeText call rather than treated as a separator.
    private static List<string> SplitUnescaped(string value) => SplitUnescapedOn(value, ';');

    // The same split on any separator. NICKNAME's value list is comma-separated (issue #48 §9), and an
    // escaped "\," must not be treated as a separator any more than an escaped "\;" is.
    private static List<string> SplitUnescapedOn(string value, char separator)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                sb.Append(value[i]).Append(value[i + 1]);
                i++;
                continue;
            }

            if (value[i] == separator)
            {
                parts.Add(sb.ToString());
                sb.Clear();
                continue;
            }

            sb.Append(value[i]);
        }

        parts.Add(sb.ToString());
        return parts;
    }

    // RFC 6350 §3.3 text escaping: backslash, comma, semicolon, newline.
    private static string EscapeText(string value)
    {
        var normalized = value.Replace("\r\n", "\n").Replace("\r", "\n");
        var sb = new StringBuilder(normalized.Length + 8);
        foreach (var ch in normalized)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case ',': sb.Append("\\,"); break;
                case ';': sb.Append("\\;"); break;
                case '\n': sb.Append("\\n"); break;
                default: sb.Append(ch); break;
            }
        }

        return sb.ToString();
    }

    private static string UnescapeText(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                switch (value[i + 1])
                {
                    case 'n' or 'N': sb.Append('\n'); i++; continue;
                    case ',': sb.Append(','); i++; continue;
                    case ';': sb.Append(';'); i++; continue;
                    case '\\': sb.Append('\\'); i++; continue;
                }
            }

            sb.Append(value[i]);
        }

        return sb.ToString();
    }

    // RFC 6350 §3.2 line folding: a content line longer than 75 octets is wrapped by inserting CRLF +
    // a single leading space before each continuation segment. Folding never splits a multi-byte UTF-8
    // sequence.
    private static void AppendFolded(StringBuilder sb, string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line);
        if (bytes.Length <= 75)
        {
            sb.Append(line).Append("\r\n");
            return;
        }

        var pos = 0;
        var first = true;
        while (pos < bytes.Length)
        {
            var budget = first ? 75 : 74;
            var end = Math.Min(pos + budget, bytes.Length);
            while (end > pos + 1 && end < bytes.Length && (bytes[end] & 0xC0) == 0x80)
            {
                end--;
            }

            sb.Append(first ? "" : " ").Append(Encoding.UTF8.GetString(bytes, pos, end - pos)).Append("\r\n");
            pos = end;
            first = false;
        }
    }

    /// <summary>Whether the multipart part's content type is acceptable for a <c>.vcf</c> upload — the
    /// extension and the parse itself are the real validity gates (mirrors
    /// <see cref="CalendarIcsService.IsAcceptedContentType"/>).</summary>
    public static bool IsAcceptedContentType(string? contentType) =>
        ImportFileReader.IsAcceptedContentType(contentType, AcceptedContentTypes);
}

public sealed record VCardExport(string FileName, string Content);

/// <summary>
/// A vCard import's wire result plus the contact ids whose type it changed or was refused to change
/// (issue #335 §7.5). Core-internal to the API: the id lists are never serialized.
/// </summary>
public sealed record VCardImportOutcome(
    VCardImportResult Result, IReadOnlyList<Guid> TypeConvertedContactIds, IReadOnlyList<Guid> TypeChangeBlockedContactIds);
