using System.Globalization;
using System.Text;

namespace Odyssey.Dtos;

/// <summary>
/// The one rule for the two administrator prompt additions (issue #433 §8). Shared by the settings
/// descriptor and the settings page, so the client validates with this code rather than a copy of it.
///
/// <para>
/// The pipeline order is pinned: normalise line breaks, NFC, trim, then check the length, the
/// characters and the reserved delimiter on the result. The length is measured on what is stored, so
/// the counter, the validator and the database all agree on one number.
/// </para>
/// </summary>
public static class PromptAdditionRule
{
    /// <summary>The maximum length of a normalised addition, in UTF-16 code units.</summary>
    public const int MaxLength = 4000;

    /// <summary>
    /// The transport bound for a raw request value: a <see cref="MaxLength"/> value sent with CRLF line
    /// breaks can be up to twice as long before normalisation.
    /// </summary>
    public const int MaxRawLength = MaxLength * 2;

    /// <summary>The literal that opens and closes the section an addition is placed in.</summary>
    public const string Delimiter = "admin_guidance";

    /// <summary>
    /// Steps 1–3: line breaks to LF, Unicode NFC, then trim. A value that is only whitespace becomes
    /// <c>""</c>. Never throws, including on a lone surrogate (which NFC refuses), so a bad request
    /// reaches <see cref="Validate"/> and becomes a <c>400</c>, never a <c>500</c>.
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var lf = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        string composed;
        try
        {
            composed = lf.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            // Invalid UTF-16 (a lone surrogate). Keep the text as is; the character check rejects it.
            composed = lf;
        }

        return composed.Trim();
    }

    /// <summary>The length the server will store, for the client's counter.</summary>
    public static int NormalisedLength(string? value) => Normalize(value).Length;

    /// <summary>
    /// Steps 4–6 on the normalised value. Returns the message to show, or <see langword="null"/> when the
    /// value is acceptable. The empty value is acceptable: it means "no addition". The message never
    /// echoes the value.
    /// </summary>
    public static string? Validate(string? value)
    {
        var normalised = Normalize(value);

        if (normalised.Length == 0)
        {
            return null;
        }

        if (normalised.Length > MaxLength)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"Guidance can be at most {MaxLength} characters; this is {normalised.Length}.");
        }

        if (FirstDisallowedLine(normalised) is { } line)
        {
            return "Line " + line.ToString(CultureInfo.InvariantCulture)
                + " contains an invisible or control character (this includes the joiner inside some emoji). "
                + "Remove it and save again.";
        }

        if (ContainsDelimiter(normalised))
        {
            return $"Guidance can't contain the text \"{Delimiter}\"; it is reserved.";
        }

        return null;
    }

    /// <summary>
    /// The 1-based line of the first character outside the allowed set, or <see langword="null"/>. Lines
    /// are counted by LF only: U+2028, U+2029 and U+0085 are themselves rejected, so they are reported
    /// on the line they sit on rather than starting a new one.
    /// </summary>
    private static int? FirstDisallowedLine(string value)
    {
        var line = 1;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\n')
            {
                line++;
                continue;
            }

            if (c == '\t')
            {
                continue;
            }

            // A valid surrogate pair is one character, not two lone surrogates.
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(value, i);
                i++;
                if (IsDisallowed(category))
                {
                    return line;
                }

                continue;
            }

            if (IsDisallowed(CharUnicodeInfo.GetUnicodeCategory(c)))
            {
                return line;
            }
        }

        return null;
    }

    private static bool IsDisallowed(UnicodeCategory category) => category is
        UnicodeCategory.Control
        or UnicodeCategory.Format
        or UnicodeCategory.LineSeparator
        or UnicodeCategory.ParagraphSeparator
        or UnicodeCategory.PrivateUse
        or UnicodeCategory.Surrogate;

    /// <summary>
    /// Whether the value names the delimiter in any spelling that could close the section: compatibility
    /// forms (fullwidth letters), any case, any whitespace inside it, with or without the underscore.
    /// </summary>
    private static bool ContainsDelimiter(string value)
    {
        string folded;
        try
        {
            folded = value.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            folded = value;
        }

        var builder = new StringBuilder(folded.Length);
        foreach (var c in folded)
        {
            if (!char.IsWhiteSpace(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        var compact = builder.ToString();
        return compact.Contains(Delimiter, StringComparison.Ordinal)
               || compact.Contains("adminguidance", StringComparison.Ordinal);
    }
}
