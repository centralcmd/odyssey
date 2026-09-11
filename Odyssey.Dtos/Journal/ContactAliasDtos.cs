using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

/// <summary>
/// Shared character-set rule for a contact alias value and its label (issue #48 §9). Shaped after
/// <see cref="JournalEntryExternalUidRules"/> so the check runs in <c>[ApiController]</c> model
/// validation — which also means it reaches the WASM client, where the same attribute is evaluated
/// against the form model — with the service re-checking for non-HTTP callers.
///
/// <para>
/// Only control characters are rejected. The label is deliberately free text (Non-Goal 1): it is
/// matched against no vocabulary, not localised and not searched, so length and the C0 range are the
/// whole of its validation.
/// </para>
/// </summary>
public static class ContactAliasRules
{
    /// <summary>The per-contact cap. A compile-time constant, not an admin-editable setting
    /// (§14): a data-shape guard bounding the inline <c>GET /api/contacts</c> payload and the search
    /// <c>EXISTS</c>, an order of magnitude above the realistic maximum.</summary>
    public const int MaxPerContact = 32;

    public const int MaxValueLength = 128;

    public const int MaxLabelLength = 64;

    /// <summary>Accepts any value free of C0 control characters and DEL.</summary>
    public const string Pattern = "^[^\\u0000-\\u001F\\u007F]*$";

    public const string ValueErrorMessage = "An alias cannot contain control characters.";

    public const string LabelErrorMessage = "An alias label cannot contain control characters.";
}

/// <summary>Create/replace payload for one contact alias (issue #48 §7).</summary>
public sealed record NewContactAlias
{
    /// <summary>The alternative name. Trimmed and whitespace-collapsed on write; unique per contact,
    /// case- and accent-insensitively.</summary>
    [Required]
    [StringLength(ContactAliasRules.MaxValueLength)]
    [RegularExpression(ContactAliasRules.Pattern, ErrorMessage = ContactAliasRules.ValueErrorMessage)]
    public required string Value { get; set; }

    /// <summary>Optional free text saying what kind of alias this is ("maiden name", "nickname",
    /// "trading as"). Not an enum, not searched, not validated against a vocabulary. An empty or
    /// whitespace-only label stores as <see langword="null"/>, so <c>""</c> and absent are never two
    /// states.</summary>
    [StringLength(ContactAliasRules.MaxLabelLength)]
    [RegularExpression(ContactAliasRules.Pattern, ErrorMessage = ContactAliasRules.LabelErrorMessage)]
    public string? Label { get; set; }
}

/// <summary>Read projection of one contact alias (issue #48 §7).</summary>
public sealed record ExistingContactAlias
{
    public required Guid Id { get; set; }

    /// <summary>Carried for symmetry with <see cref="ExistingAddress"/>/<see cref="ExistingEmailAddress"/>/
    /// <see cref="ExistingPhoneNumber"/>, which all name their parent.</summary>
    public required Guid ContactId { get; set; }

    [StringLength(ContactAliasRules.MaxValueLength)]
    public required string Value { get; set; }

    [StringLength(ContactAliasRules.MaxLabelLength)]
    public string? Label { get; set; }
}
