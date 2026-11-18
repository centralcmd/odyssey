using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Journal;

/// <summary>
/// Core's name for the one location text normalization. Since issue #380 (§4, V7-1) the implementation
/// is <see cref="LocationTextCleaner"/> in <c>Odyssey.Dtos</c>, which the address key and the WASM client
/// also use; this type only delegates, so the existing call sites do not change. A source-lint in
/// <c>Odyssey.Core.Tests</c> fails if a second cleaning implementation appears.
/// </summary>
internal static class LocationText
{
    public static string? Clean(string? value, bool allowLineBreaks = false) =>
        LocationTextCleaner.Clean(value, allowLineBreaks);

    /// <summary>Cleans, then truncates to <paramref name="maxLength"/> (provider text only — never user input).</summary>
    public static string? CleanAndTruncate(string? value, int maxLength)
    {
        var cleaned = Clean(value);
        if (cleaned is null || cleaned.Length <= maxLength)
        {
            return cleaned;
        }

        // Never split a surrogate pair at the cut.
        var cut = char.IsHighSurrogate(cleaned[maxLength - 1]) ? maxLength - 1 : maxLength;
        return cleaned[..cut].TrimEnd();
    }
}
