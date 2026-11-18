using System.Globalization;
using System.Text.RegularExpressions;

namespace Odyssey.Core.Internet.News.Parsing;

/// <summary>
/// Tolerant feed date parsing (§8.6): RFC 822/1123 as RSS writes it (with the usual mistakes — a missing weekday, a
/// two-digit year, a named or military zone, no seconds, full month names) and ISO 8601 / RFC 3339 as Atom, Dublin Core
/// and JSON Feed write it. An unparseable date is <c>null</c>, never an error: it costs the item its date, not its place.
/// </summary>
public static partial class NewsDates
{
    private static readonly Dictionary<string, int> Months = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jan"] = 1, ["feb"] = 2, ["mar"] = 3, ["apr"] = 4, ["may"] = 5, ["jun"] = 6,
        ["jul"] = 7, ["aug"] = 8, ["sep"] = 9, ["sept"] = 9, ["oct"] = 10, ["nov"] = 11, ["dec"] = 12,
    };

    private static readonly Dictionary<string, int> NamedZones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UT"] = 0, ["UTC"] = 0, ["GMT"] = 0, ["Z"] = 0,
        ["EST"] = -5 * 60, ["EDT"] = -4 * 60, ["CST"] = -6 * 60, ["CDT"] = -5 * 60,
        ["MST"] = -7 * 60, ["MDT"] = -6 * 60, ["PST"] = -8 * 60, ["PDT"] = -7 * 60,
        ["CET"] = 60, ["CEST"] = 120, ["BST"] = 60, ["IST"] = 330, ["JST"] = 540, ["AEST"] = 600,
    };

    /// <summary>Parses a feed date, or returns null.</summary>
    public static DateTimeOffset? Parse(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 128)
        {
            return null;
        }

        return TryRfc822(text) ?? TryIso(text);
    }

    private static DateTimeOffset? TryRfc822(string text)
    {
        var match = Rfc822().Match(text);
        if (!match.Success || !Months.TryGetValue(match.Groups["mon"].Value[..3], out var month))
        {
            return null;
        }

        var day = int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture);
        var year = int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture);
        if (match.Groups["year"].Value.Length == 2)
        {
            year += year < 50 ? 2000 : 1900;
        }

        var hour = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
        var minute = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        var second = match.Groups["s"].Success ? int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture) : 0;
        var offset = Offset(match.Groups["zone"].Value);
        if (offset is null || day is < 1 or > 31 || hour > 23 || minute > 59 || second > 60 || year is < 1 or > 9999)
        {
            return null;
        }

        try
        {
            return new DateTimeOffset(year, month, day, hour, minute, Math.Min(second, 59), TimeSpan.FromMinutes(offset.Value));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static int? Offset(string zone)
    {
        if (zone.Length == 0)
        {
            return 0;
        }

        if (zone[0] is '+' or '-')
        {
            var digits = zone[1..].Replace(":", string.Empty, StringComparison.Ordinal);
            if (digits.Length is not (2 or 4) || !digits.All(char.IsAsciiDigit))
            {
                return null;
            }

            var hours = int.Parse(digits[..2], CultureInfo.InvariantCulture);
            var minutes = digits.Length == 4 ? int.Parse(digits[2..], CultureInfo.InvariantCulture) : 0;
            if (hours > 14 || minutes > 59)
            {
                return null;
            }

            var total = (hours * 60) + minutes;
            return zone[0] == '-' ? -total : total;
        }

        if (NamedZones.TryGetValue(zone, out var named))
        {
            return named;
        }

        // A single military letter other than Z carries no reliable meaning (RFC 1123 §5.2.14): read it as UTC.
        return zone.Length == 1 && char.IsAsciiLetter(zone[0]) ? 0 : null;
    }

    private static DateTimeOffset? TryIso(string text) =>
        DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;

    [GeneratedRegex(
        """^(?:[A-Za-z]{2,9}\.?,?\s*)?(?<day>\d{1,2})[\s\-]+(?<mon>[A-Za-z]{3,9})\.?[\s\-]+(?<year>\d{4}|\d{2})\s+(?<h>\d{1,2}):(?<m>\d{2})(?::(?<s>\d{2}))?(?:\.\d+)?\s*(?<zone>[+\-]\d{2}:?\d{2}|[+\-]\d{2}|[A-Za-z]{1,4})?$""",
        RegexOptions.CultureInvariant)]
    private static partial Regex Rfc822();
}
