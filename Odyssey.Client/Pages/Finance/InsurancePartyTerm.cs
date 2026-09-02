using System.Globalization;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// A policy party's term in its role, formatted for the tile caption that carries it
/// (Odyssey Design System · <c>Insurance.jsx</c> <c>termText</c> / <c>insRangeShort</c>).
///
/// <para>
/// Deliberately the COMPACT form: no zero-padding, and the year written once where both ends share
/// it. The party tiles are a fixed-height grid, and "Sep 1 – Dec 31 2026" fits the line where
/// "Sep 01, 2026 – Dec 31, 2026" wrapped it.
/// </para>
///
/// <para>
/// Both dates absent is the DEFAULT term — the policy's own extent — and returns <c>null</c>, which
/// renders no term line at all. Absence is the healthy, common case here, not a missing value.
/// </para>
/// </summary>
public static class InsurancePartyTerm
{
    /// <summary>The term line, or null when the party simply follows the policy.</summary>
    public static string? Format(DateTime? fromDate, DateTime? toDate) => (fromDate, toDate) switch
    {
        ({ } from, { } to) => Range(from, to),
        ({ } from, null) => $"from {Long(from)}",
        (null, { } to) => $"to {Long(to)}",
        _ => null,
    };

    private static string Range(DateTime from, DateTime to) =>
        from.Year == to.Year
            ? $"{Short(from)} – {Short(to)} {to.Year.ToString(CultureInfo.InvariantCulture)}"
            : $"{Long(from)} – {Long(to)}";

    /// <summary>Day and month only — for the end of a range whose year is stated once, at the end.</summary>
    private static string Short(DateTime date) => date.ToString("MMM d", CultureInfo.CurrentCulture);

    private static string Long(DateTime date) => date.ToString("MMM d yyyy", CultureInfo.CurrentCulture);
}
