namespace Odyssey.Dtos.Finance;

/// <summary>
/// The contract reference number's rules, declared once (issue #181). This project has zero project
/// references and is reachable from the WASM client, so the write DTOs' attributes and the edit
/// dialog's field name the same constants rather than a client-side copy that could drift.
/// </summary>
public static class ContractReferenceNumber
{
    public const int MaxLength = 64;

    /// <summary>
    /// A deny-list of four Unicode categories: <c>Cc</c> (controls, CR/LF included), <c>Cf</c>
    /// (format — the bidi overrides), <c>Co</c> (private use) and <c>Cn</c> (unassigned).
    /// <b>Not</b> <c>\p{C}</c>: that supercategory also holds <c>Cs</c>, and .NET regex classifies
    /// each half of a surrogate pair individually, so it would reject every non-BMP character.
    /// <c>*</c> rather than <c>+</c> so an empty string validates and normalises to null.
    /// </summary>
    public const string Pattern = @"^[^\p{Cc}\p{Cf}\p{Co}\p{Cn}]*$";

    /// <summary>Names the constraint, never the submitted value.</summary>
    public const string InvalidCharactersMessage =
        "The reference number must not contain control, formatting or unassigned characters.";

    /// <summary>
    /// Trim, then blank to null — the one normalisation both write paths run, so "no reference
    /// number" has a single stored representation. Interior content is preserved verbatim.
    /// </summary>
    public static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
