using System.Globalization;
using System.Text.RegularExpressions;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Components;

/// <summary>
/// The client half of the contract reference number's rules (issue #181) — the design system's
/// <c>REFERENCE_NUMBER_RULES</c>. Not new rules: the limit, the character class and the
/// normalisation are the ones <see cref="ContractReferenceNumber"/> declares for the write DTOs, named
/// here rather than copied, so the field and the <c>400</c> cannot disagree.
/// </summary>
/// <remarks>
/// The character check uses the SAME .NET pattern the server's <c>[RegularExpression]</c> runs, over
/// the same UTF-16 code units, so a non-BMP character (emoji, rare CJK) passes here exactly as it
/// passes there. Lengths are counted in UTF-16 code units for the same reason — it is what
/// <c>[StringLength]</c> counts.
/// </remarks>
public static class OdsReferenceNumberRules
{
    public const int MaxLength = ContractReferenceNumber.MaxLength;

    /// <summary>The denied categories as a character class — the DTO's pattern without its anchors.</summary>
    private static readonly Regex Hidden = new(@"[\p{Cc}\p{Cf}\p{Co}\p{Cn}]", RegexOptions.CultureInvariant);

    /// <summary>
    /// The characters a user most often pastes in without seeing, by name. Anything else in the four
    /// categories is called a control character and identified by its code point alone.
    /// </summary>
    private static readonly IReadOnlyDictionary<int, string> Names = new Dictionary<int, string>
    {
        [0x09] = "tab", [0x0A] = "line break", [0x0D] = "carriage return", [0x00] = "null character",
        [0x200B] = "zero-width space", [0x200C] = "zero-width non-joiner", [0x200D] = "zero-width joiner",
        [0x200E] = "left-to-right mark", [0x200F] = "right-to-left mark", [0x2060] = "word joiner",
        [0xFEFF] = "byte-order mark", [0x202A] = "left-to-right embedding", [0x202B] = "right-to-left embedding",
        [0x202C] = "directional formatting end", [0x202D] = "left-to-right override",
        [0x202E] = "right-to-left override", [0x00AD] = "soft hyphen",
    };

    /// <summary>A denied character, described without the value around it.</summary>
    public sealed record HiddenCharacter(string CodePoint, string Name, int Count);

    /// <summary>A broken rule: the DTO failure class it predicts, and copy that never echoes the value.</summary>
    public sealed record Violation(string Code, string Message, HiddenCharacter? Hidden = null);

    /// <summary>Trim; blank becomes null — the service's normalisation, verbatim.</summary>
    public static string? Normalize(string? value) => ContractReferenceNumber.Normalize(value);

    /// <summary>Removes every denied character and keeps everything else as typed.</summary>
    public static string StripHidden(string? value) => Hidden.Replace(value ?? string.Empty, string.Empty);

    /// <summary>The first denied character and how many there are, or null.</summary>
    public static HiddenCharacter? FindHidden(string? value)
    {
        var matches = Hidden.Matches(value ?? string.Empty);
        if (matches.Count == 0)
        {
            return null;
        }

        var codePoint = (int)matches[0].Value[0];
        return new HiddenCharacter(
            "U+" + codePoint.ToString("X4", CultureInfo.InvariantCulture),
            Names.GetValueOrDefault(codePoint, "control character"),
            matches.Count);
    }

    /// <summary>
    /// The first rule the (untrimmed) value breaks, or null. The character rule runs first because it
    /// is the one the user cannot see; the length is judged on the TRIMMED value, since the field trims
    /// on blur and the dialog sends the normalised value.
    /// </summary>
    public static Violation? Validate(string? value)
    {
        var text = value ?? string.Empty;

        if (FindHidden(text) is { } hidden)
        {
            var more = hidden.Count > 1 ? $" and {hidden.Count - 1} more" : string.Empty;
            return new Violation(
                "reference_number_invalid_characters",
                $"Contains a hidden {hidden.Name} ({hidden.CodePoint}){more}. Control and formatting characters can’t be stored.",
                hidden);
        }

        var length = text.Trim().Length;
        return length > MaxLength
            ? new Violation(
                "reference_number_too_long",
                $"Must be {MaxLength} characters or fewer — this one is {length}.")
            : null;
    }
}
