using System.Globalization;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>The collapsed row's headline figure: the date the record turns on, and how far away it is.</summary>
/// <param name="Value">The figure itself — a date, or the status word for the one state with no date.</param>
/// <param name="Word">The caption under it.</param>
/// <param name="Cls">Tone key: "soon" · "lapsed" · "" (neutral).</param>
public sealed record InsuranceHeadlineFigure(string Value, string Word, string Cls);

/// <summary>
/// The insurance list row's headline, mirroring the design system's <c>insHeadline</c>.
///
/// <para>
/// Every state that HAS a renewal period headlines on a date, not on a repeat of the status word the
/// chip beside the name already shows: the figure earns its place by saying <b>when</b>. A lapsed
/// policy shows the end of its last period and how long ago that was; an upcoming one shows when
/// cover begins; an archived one shows the last period it had.
/// <see cref="CoverageStatus.NoCoverage"/> is the single state with no date to show, because it is
/// exactly the case where no period was ever recorded — so it alone reads "No coverage".
/// </para>
///
/// <para>
/// It is a static here, rather than a private method on the card, so the eight branches are testable
/// on their own — the day-count wording and the never-covered/cover-ran-out distinction are the kind
/// of thing that is only visible by eye otherwise.
/// </para>
/// </summary>
public static class InsuranceHeadline
{
    public static InsuranceHeadlineFigure Compute(InsurancePolicyListItem p, DateTime today)
    {
        switch (p.CoverageStatus)
        {
            case CoverageStatus.Active:
            case CoverageStatus.ExpiringSoon:
                if (p.CurrentRenewalEndDate is { } end)
                {
                    var days = (end.Date - today.Date).Days;
                    var word = days <= 0 ? "expires today" : $"expires in {days} day{Plural(days)}";
                    return new(Format(end), word, p.CoverageStatus == CoverageStatus.ExpiringSoon ? "soon" : "");
                }

                return new("Active", "currently covered", "");

            case CoverageStatus.Upcoming when p.EarliestRenewalStartDate is { } start:
            {
                var days = (start.Date - today.Date).Days;
                return new(Format(start), days <= 0 ? "starts today" : $"starts in {days} day{Plural(days)}", "");
            }

            case CoverageStatus.Lapsed when p.LatestRenewalEndDate is { } lapsed:
            {
                var days = (today.Date - lapsed.Date).Days;
                return new(Format(lapsed), days <= 0 ? "expired today" : $"expired {days} day{Plural(days)} ago", "lapsed");
            }

            // An archived policy may genuinely have no period on record, so this one keeps a fallback.
            case CoverageStatus.Archived:
                return p.LatestRenewalEndDate is { } archivedEnd
                    ? new(Format(archivedEnd), "archived", "")
                    : new("Archived", "archived", "");

            // Upcoming / Lapsed without a period cannot arise — both derive from having one — but the
            // switch stays total rather than relying on that.
            case CoverageStatus.Upcoming:
                return new("Upcoming", "starts later", "");
            case CoverageStatus.Lapsed:
                return new("Expired", "coverage expired", "lapsed");

            default:
                return new("No coverage", "no coverage yet", "");
        }
    }

    private static string Plural(int days) => days == 1 ? "" : "s";

    private static string Format(DateTime date) => date.ToString("MMM dd, yyyy", CultureInfo.CurrentCulture);
}
