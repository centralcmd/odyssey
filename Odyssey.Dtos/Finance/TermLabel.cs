namespace Odyssey.Dtos.Finance;

/// <summary>
/// The one normalization rule for an account term's user-authored <c>Label</c>, shared by the
/// server's write path and the client's duplicate pre-check. It lives in <c>Odyssey.Dtos</c> — a leaf
/// assembly with no project references, reachable from both halves of the stack including the WASM
/// client — so the two sides run the <em>same</em> delegate rather than two implementations that can
/// drift.
///
/// <para>
/// A term's series key is <c>(AccountId, TermKind, LabelKey)</c>: <see cref="Normalize"/> produces the
/// display form the user sees, and <see cref="Key"/> the comparison form that decides which series a
/// term joins. Folding is done on write rather than at query time because the comparison has to mean
/// the same thing on MariaDB (case-insensitive collation) and the EF InMemory provider (ordinal).
/// </para>
/// </summary>
public static class TermLabel
{
    /// <summary>The maximum stored length of a label, matching the entity and every request DTO.</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// The display form: trimmed, with internal whitespace runs collapsed to a single space. A null,
    /// empty or whitespace-only value is the unnamed series and normalizes to <c>null</c>.
    /// </summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var span = raw.AsSpan().Trim();
        var builder = new System.Text.StringBuilder(span.Length);
        var lastWasSpace = false;
        foreach (var ch in span)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace)
                    builder.Append(' ');
                lastWasSpace = true;
                continue;
            }

            builder.Append(ch);
            lastWasSpace = false;
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    /// <summary>
    /// The comparison form: the display form, case-folded. Two labels differing only in case or
    /// internal spacing are one series, so a typo continues the series it meant to rather than
    /// silently forking a second one.
    /// </summary>
    public static string? Key(string? raw) =>
        Normalize(raw)?.ToLowerInvariant();

    /// <summary>
    /// Whether a kind takes a label. Refused on the rate kinds, so the headline rate stays
    /// unambiguous; required on <see cref="TermKind.Fee"/>, since with one fee kind two unnamed fees
    /// could not be told apart.
    /// </summary>
    public static TermLabelRule RuleFor(TermKind kind) => kind switch
    {
        TermKind.InterestRate or TermKind.ExpectedReturn => TermLabelRule.Refused,
        TermKind.Fee => TermLabelRule.Required,
        _ => TermLabelRule.Refused,
    };
}

/// <summary>Whether a <see cref="TermKind"/> refuses a label or requires one. There is no third case.</summary>
public enum TermLabelRule
{
    /// <summary>A rate kind: the field is not rendered, and a supplied label is a 400.</summary>
    Refused,

    /// <summary>A fee: the field is required, and a missing label is a 400.</summary>
    Required,
}
