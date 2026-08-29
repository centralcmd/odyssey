using System.Diagnostics.CodeAnalysis;

namespace Odyssey.Core.Journal;

/// <summary>Shared plain-text helpers for the journal read projections.</summary>
public static class JournalText
{
    /// <summary>Truncate <paramref name="value"/> to at most <paramref name="maxLength"/> UTF-16 code units
    /// for a snippet preview, without splitting a surrogate pair at the cut point (a split would leave a
    /// lone surrogate that renders as "�"). Null and already-short values pass through unchanged.</summary>
    [return: NotNullIfNotNull(nameof(value))]
    public static string? Truncate(string? value, int maxLength)
    {
        if (value is null || value.Length <= maxLength)
        {
            return value;
        }

        // If the boundary lands between the two halves of a surrogate pair, step back one unit so the
        // incomplete pair is dropped rather than split.
        var end = char.IsHighSurrogate(value[maxLength - 1]) ? maxLength - 1 : maxLength;
        return value[..end];
    }
}
