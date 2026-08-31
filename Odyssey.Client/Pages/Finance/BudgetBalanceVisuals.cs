using Odyssey.Client.Components;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// How a budget's money reads on the record card — the sibling of <see cref="InsuranceHeadline"/>.
///
/// <para>
/// <b>Two vocabularies, deliberately, and they differ at zero.</b> The record card's balances carry the
/// design system's two-branch rule: a plan is either under water (expense) or it is not (income), so a
/// balance of exactly zero — a plan that spends precisely what it takes in — reads as income, the
/// same as any plan that balances or better. The page header's <c>BalanceColor</c> is a THREE-branch
/// scale over the whole portfolio's planned balance, where zero is genuinely "nothing either way" and
/// takes the neutral ink. Pinned by tests on both sides so the divergence stays a decision rather than
/// a drift.
/// </para>
/// </summary>
public static class BudgetBalanceVisuals
{
    /// <summary>The collapsed header's Expected balance figure.</summary>
    public static OdsRecordFigureTone FigureTone(decimal value) =>
        value < 0 ? OdsRecordFigureTone.Expense : OdsRecordFigureTone.Income;

    /// <summary>The Expected / Actual balance tiles in the body's Details grid.</summary>
    public static OdsInfoTileTone TileTone(decimal value) =>
        value < 0 ? OdsInfoTileTone.Expense : OdsInfoTileTone.Income;

    /// <summary>
    /// "1 income line" / "3 income lines" — the count a planned tile's foot carries. The noun is
    /// passed singular; only a count of exactly one stays that way.
    /// </summary>
    public static string Lines(int count, string noun) =>
        $"{count} {noun}{(count == 1 ? "" : "s")}";
}
