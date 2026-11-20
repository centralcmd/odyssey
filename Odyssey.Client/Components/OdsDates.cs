using System.Globalization;

namespace Odyssey.Client.Components;

/// <summary>
/// The one formatter for an absolute calendar date shown in the UI ("Apr 02, 2026").
/// </summary>
/// <remarks>
/// Dates are always formatted with the invariant culture. They sit inside English copy ("Opened …",
/// "in force since …"), so a browser culture's month abbreviation would mix two languages in one
/// line, and before this helper existed each page picked its own culture, so the same date read
/// differently from page to page. Convert to local time at the call site when the value is UTC.
/// It covers calendar dates only: a date with a time of day, or a month-only label, keeps its own
/// format at its call site.
/// </remarks>
public static class OdsDates
{
    /// <summary>"Apr 02, 2026" — zero-padded day.</summary>
    public static string Long(DateTime date) => date.ToString("MMM dd, yyyy", CultureInfo.InvariantCulture);

    /// <summary>"Apr 2, 2026" — unpadded day, for compact cells and chips.</summary>
    public static string LongUnpadded(DateTime date) => date.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);

    /// <inheritdoc cref="Long(DateTime)"/>
    public static string Long(DateOnly date) => date.ToString("MMM dd, yyyy", CultureInfo.InvariantCulture);

    /// <inheritdoc cref="LongUnpadded(DateTime)"/>
    public static string LongUnpadded(DateOnly date) => date.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
}
