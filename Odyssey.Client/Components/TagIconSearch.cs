using System.Globalization;
using System.Text;
using Odyssey.Dtos;

namespace Odyssey.Client.Components;

/// <summary>
/// The tag icon picker's search: a forgiving filter over the <see cref="TagIcons"/> catalogue.
/// </summary>
/// <remarks>
/// Every word of the query must match some word of an icon's label, keywords or key — exactly, as a
/// prefix, or as a substring (three letters or more). Only when no icon matches that way does a small
/// edit distance count (one typo from four letters, two from seven), so "vacaton" still finds Holiday
/// while "cafe" does not drag in "care". Case and diacritics fold ("cafe" finds
/// Café). Results rank label hits before keyword hits and closer matches before looser ones, ties in
/// catalogue order, so the list is stable while typing.
/// </remarks>
internal static class TagIconSearch
{
    private sealed record Entry(TagIconOption Option, int Order, string[] LabelWords, string[] OtherWords);

    private static readonly Entry[] Entries = TagIcons.All
        .Select((option, order) => new Entry(
            option,
            order,
            Words(option.Label),
            [.. Words(option.Keywords), .. Words(option.Key.Replace('_', ' '))]))
        .ToArray();

    /// <summary>The matching icons, best first. An empty or blank query matches nothing.</summary>
    public static IReadOnlyList<TagIconOption> Search(string? query)
    {
        var terms = Words(query);
        if (terms.Length == 0)
        {
            return [];
        }

        var hits = new List<(Entry Entry, int Score, bool Fuzzy)>();
        foreach (var entry in Entries)
        {
            var total = 0;
            var matched = true;
            var fuzzy = false;
            foreach (var term in terms)
            {
                var score = Best(term, entry);
                if (score is null)
                {
                    matched = false;
                    break;
                }

                fuzzy |= score.Value >= FuzzyScore;
                total += score.Value;
            }

            if (matched)
            {
                hits.Add((entry, total, fuzzy));
            }
        }

        // Typo tolerance is a fallback: once any icon matches every word without it, fuzzy-only hits drop.
        if (hits.Exists(hit => !hit.Fuzzy))
        {
            hits.RemoveAll(hit => hit.Fuzzy);
        }

        return hits
            .OrderBy(hit => hit.Score)
            .ThenBy(hit => hit.Entry.Order)
            .Select(hit => hit.Entry.Option)
            .ToList();
    }

    // Quality 3 (a typo), doubled: a label typo scores 6, a keyword typo 7.
    private const int FuzzyScore = 6;

    // Lower is better: the match quality (0 exact … 3 fuzzy), doubled, plus one for a keyword hit.
    private static int? Best(string term, Entry entry)
    {
        int? best = null;
        foreach (var word in entry.LabelWords)
        {
            if (Quality(term, word) is { } q && (best is null || q * 2 < best))
            {
                best = q * 2;
            }
        }

        foreach (var word in entry.OtherWords)
        {
            if (Quality(term, word) is { } q && (best is null || q * 2 + 1 < best))
            {
                best = q * 2 + 1;
            }
        }

        return best;
    }

    private static int? Quality(string term, string word)
    {
        if (word == term)
        {
            return 0;
        }

        if (word.StartsWith(term, StringComparison.Ordinal))
        {
            return 1;
        }

        if (term.Length >= 3 && word.Contains(term, StringComparison.Ordinal))
        {
            return 2;
        }

        var allowed = term.Length >= 7 ? 2 : term.Length >= 4 ? 1 : 0;
        if (allowed == 0)
        {
            return null;
        }

        // A typo in a whole word, or in a word still being typed (compared against the word's prefix).
        if (Distance(term, word, allowed) <= allowed)
        {
            return 3;
        }

        return word.Length > term.Length && Distance(term, word[..term.Length], allowed) <= allowed ? 3 : null;
    }

    /// <summary>Optimal-string-alignment distance (a swap of two neighbours costs one), capped past <paramref name="max"/>.</summary>
    internal static int Distance(string a, string b, int max)
    {
        if (Math.Abs(a.Length - b.Length) > max)
        {
            return max + 1;
        }

        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var value = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    value = Math.Min(value, d[i - 2, j - 2] + 1);
                }

                d[i, j] = value;
            }
        }

        return d[a.Length, b.Length];
    }

    /// <summary>Lower-cased, diacritic-free words of letters and digits.</summary>
    internal static string[] Words(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        return builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
}
