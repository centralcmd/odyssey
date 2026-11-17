using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace Odyssey.Core.Journal.Interop;

/// <summary>
/// Resolves the free-text country component of a vCard <c>ADR</c> to an ISO 3166-1 alpha-2 code.
///
/// <para>
/// RFC 6350 makes that component a country <i>name</i>, and most address books write one ("Norway",
/// "Norge", "Deutschland"), while an Odyssey address requires the two-letter code — so without this every
/// such address was dropped on import. Accepted: an alpha-2 or alpha-3 code, and a country's English,
/// native and display names from ICU's region data (which both hosts require, <c>GlobalizationGuard</c>),
/// compared case- and accent-insensitively. Anything else resolves to <see langword="null"/> and the
/// address is dropped exactly as before.
/// </para>
/// </summary>
internal static class VCardCountry
{
    private static readonly Lazy<FrozenDictionary<string, string>> Names = new(Build);

    /// <summary>The upper-case alpha-2 code <paramref name="value"/> names, or <see langword="null"/>.</summary>
    public static string? Resolve(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 2 && trimmed.All(char.IsAsciiLetter))
        {
            return trimmed.ToUpperInvariant();
        }

        return Names.Value.TryGetValue(Key(trimmed), out var code) ? code : null;
    }

    private static FrozenDictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        void Add(string? name, string code)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                map.TryAdd(Key(name), code);
            }
        }

        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            RegionInfo region;
            try
            {
                region = new RegionInfo(culture.Name);
            }
            catch (ArgumentException)
            {
                continue;
            }

            var code = region.TwoLetterISORegionName;
            if (code.Length != 2 || !code.All(char.IsAsciiLetter))
            {
                continue; // a numeric UN M.49 region such as 001/419, not a country
            }

            code = code.ToUpperInvariant();
            Add(region.ThreeLetterISORegionName, code);
            Add(region.EnglishName, code);
            Add(region.NativeName, code);
            Add(region.DisplayName, code);
        }

        // Common English forms ICU does not carry as a region name.
        foreach (var (name, code) in new[]
                 {
                     ("USA", "US"), ("United States of America", "US"), ("U.S.A.", "US"),
                     ("UK", "GB"), ("Great Britain", "GB"), ("England", "GB"), ("Scotland", "GB"), ("Wales", "GB"),
                 })
        {
            Add(name, code);
        }

        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>Upper-cased, whitespace-collapsed and stripped of combining marks.</summary>
    private static string Key(string value)
    {
        var decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var lastWasSpace = false;
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                }

                lastWasSpace = true;
                continue;
            }

            sb.Append(char.ToUpperInvariant(ch));
            lastWasSpace = false;
        }

        return sb.ToString().Trim();
    }
}
