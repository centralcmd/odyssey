using System.Text.RegularExpressions;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// The normalization rules for an <see cref="ExistingAccountTerm.Label"/> — the user-supplied name
/// that, together with the <see cref="TermKind"/>, identifies one term series on an account.
/// </summary>
/// <remarks>
/// This lives in <c>Odyssey.Dtos</c> (zero project references, reachable from the WASM client) so the
/// server's write path and the client's duplicate pre-check apply the <em>same</em> delegate rather
/// than two implementations that can drift — the same reasoning as the settings shape rules.
/// <para>
/// The two forms are deliberately separate. <see cref="Normalize"/> is what is stored and displayed:
/// it keeps the author's casing, because "ATM · Abroad" is their wording. <see cref="KeyOf"/> is what
/// the series is keyed and de-duplicated on, and it case-folds, so a later "atm · abroad" continues
/// the same series instead of silently forking a second one. Persisting the key rather than folding
/// at query time also keeps the two test tiers honest: MariaDB's default collation is
/// case-insensitive while the EF InMemory provider compares ordinally, so a comparison written
/// against <c>Label</c> would mean different things on each.
/// </para>
/// </remarks>
public static partial class TermLabel
{
    /// <summary>The maximum stored length, matching the entity and the DTO annotations.</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// The stored/display form: trimmed, with internal whitespace runs collapsed to a single space.
    /// Blank (or <c>null</c>) becomes <c>null</c>, which is the unnamed series for the kind — the
    /// shape every term written before labels existed already has.
    /// </summary>
    public static string? Normalize(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return null;

        var collapsed = Whitespace().Replace(label.Trim(), " ");
        return collapsed.Length == 0 ? null : collapsed;
    }

    /// <summary>
    /// The comparison form: <see cref="Normalize"/> lowercased invariantly. <c>null</c> for the
    /// unnamed series. This is the value that participates in the series key and the duplicate guard.
    /// </summary>
    public static string? KeyOf(string? label) => Normalize(label)?.ToLowerInvariant();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
