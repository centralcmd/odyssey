using System.Globalization;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// What the insurance page announces through its live region after a write (issue #26 §3).
///
/// <para>
/// Two state changes are invisible to assistive technology without this. A document count is written
/// to the period's chip <c>aria-label</c>, and a changed label on an <b>unfocused</b> control is never
/// read out — focus is back on the Attach button, not the chip. And "Attach document" going from
/// disabled to enabled after the first period is saved changes only a menu item the user is not
/// currently in.
/// </para>
///
/// <para>
/// It is a static here, rather than private methods on the card, so the wording and — more to the
/// point — the <b>conditions</b> are testable without a rendered record. The enable transition in
/// particular is a comparison across a reload, which is exactly the kind of thing that silently stops
/// firing.
/// </para>
/// </summary>
public static class InsuranceAnnouncements
{
    /// <summary>
    /// The period's document count after an attach, naming the period rather than saying "this
    /// period": the row menu may have inferred the target and the dialog's picker may have changed
    /// it, so the user needs to hear which one received the documents.
    /// </summary>
    public static string DocumentsOnPeriod(ExistingPolicyRenewal period)
    {
        var count = period.Files.Count;
        return $"{count} document{(count == 1 ? "" : "s")} on {LongDate(period.FromDate)} → {LongDate(period.ToDate)}.";
    }

    /// <summary>
    /// The enable transition, or null when there is nothing to announce.
    ///
    /// <para>
    /// Only the <b>first</b> period is announced. Every later one leaves "Attach document" enabled as
    /// it already was, and announcing an unchanged state on every save is noise that trains a
    /// screen-reader user to tune the region out — which would cost them the announcements that do
    /// matter.
    /// </para>
    /// </summary>
    public static string? PeriodsBecameAvailable(int countBefore, int countAfter) =>
        countBefore == 0 && countAfter > 0
            ? "First renewal period added. Attach document is now available."
            : null;

    private static string LongDate(DateTime date) =>
        date.ToString("MMM dd, yyyy", CultureInfo.CurrentCulture);
}
