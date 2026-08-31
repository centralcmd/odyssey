using System.Globalization;
using Odyssey.Client.Components;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// How a tax statement's review state reads — the sibling of <see cref="OdsSubscriptionStatus"/> and
/// <see cref="OdsContractStatus"/>.
///
/// <para>
/// A statement has exactly ONE state, and archiving outranks the review status: an archived year is
/// archived whatever it was flagged as. The state meaning is always in the visible label, never in the
/// tone or the glyph alone.
/// </para>
///
/// <para>
/// It is a static here, rather than private methods on the card, so the precedence and the Status
/// tile's foot are testable on their own — the archived-outranks-status rule and the two-part foot
/// join are otherwise only visible by eye.
/// </para>
/// </summary>
public static class TaxStatementStatusVisuals
{
    public static string Label(ExistingTaxStatement s) => s.Archived is not null
        ? "Archived"
        : s.Status switch
        {
            TaxStatementStatus.Approved => "Approved",
            TaxStatementStatus.Flagged => "Flagged",
            _ => "New",
        };

    /// <summary>Chip tone for the collapsed header.</summary>
    public static OdsChipTone ChipTone(ExistingTaxStatement s) => s.Archived is not null
        ? OdsChipTone.Outline
        : s.Status switch
        {
            TaxStatementStatus.Approved => OdsChipTone.Income,
            TaxStatementStatus.Flagged => OdsChipTone.Expense,
            _ => OdsChipTone.Info,
        };

    /// <summary>An archived row's chip drops the live dot — it is not a running state.</summary>
    public static bool Dot(ExistingTaxStatement s) => s.Archived is null;

    /// <summary>The Status tile's glyph — the same lifecycle the chip shows, at tile scale.</summary>
    public static string Icon(ExistingTaxStatement s) => s.Archived is not null
        ? "inventory_2"
        : s.Status switch
        {
            TaxStatementStatus.Approved => "check_circle",
            TaxStatementStatus.Flagged => "flag",
            _ => "fiber_new",
        };

    public static OdsInfoTileTone TileTone(ExistingTaxStatement s) => s.Archived is not null
        ? OdsInfoTileTone.Muted
        : s.Status switch
        {
            TaxStatementStatus.Approved => OdsInfoTileTone.Income,
            TaxStatementStatus.Flagged => OdsInfoTileTone.Expense,
            _ => OdsInfoTileTone.Info,
        };

    /// <summary>
    /// When the state began, and a pointer to the note above when there is one. The derived tile
    /// carries its OWN date rather than borrowing one from the fields it summarises — an archived
    /// statement dates from its archival, not from its last review. Returns null when there is
    /// neither, so the tile renders no foot at all.
    /// </summary>
    public static string? Foot(ExistingTaxStatement s)
    {
        var parts = new List<string>();
        if (s.Archived is { } archivedAt)
            parts.Add(LongDate(archivedAt));
        else if (s.StatusChangedAt != default)
            parts.Add(LongDate(s.StatusChangedAt));
        if (!string.IsNullOrWhiteSpace(s.StatusComment))
            parts.Add("see note above");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static string LongDate(DateTime date) => date.ToString("MMM dd, yyyy", CultureInfo.CurrentCulture);
}

/// <summary>
/// The collapsed row's headline figure for a tax statement: what the year settles at, and whether that
/// is a fact or an estimate.
///
/// <para>
/// A DECLARED settlement is stated on the official assessment and reads plainly. Without one, the
/// figure falls back to the reconciliation's estimated outstanding tax and is marked "(est.)" — and an
/// estimate carries the same three readings its declared counterpart would, because the sign means the
/// same thing either way: money owed, money coming back, or nothing left to settle.
/// </para>
///
/// <para>
/// It is a static here so those readings are testable: the wording used to collapse every non-null
/// estimate into "outstanding (est.)", which made an estimated refund read as tax owed.
/// </para>
/// </summary>
public static class TaxSettlementFigure
{
    /// <summary>The caption under the headline figure.</summary>
    public static string Word(decimal? declared, decimal? settle)
    {
        if (declared is { } stated)
            return stated > 0 ? "additional tax to pay" : stated < 0 ? "refund" : "settled";

        return settle switch
        {
            null => "awaiting assessment",
            > 0 => "outstanding (est.)",
            < 0 => "refund (est.)",
            _ => "settled (est.)",
        };
    }

    /// <summary>
    /// The figure takes the finance vocabulary, never the record's accent: tax still to pay is an
    /// expense, a refund is income, and a settled — or not yet assessed — year is neither.
    /// </summary>
    public static OdsRecordFigureTone Tone(decimal? settle) => settle switch
    {
        > 0 => OdsRecordFigureTone.Expense,
        < 0 => OdsRecordFigureTone.Income,
        _ => OdsRecordFigureTone.Neutral,
    };
}
