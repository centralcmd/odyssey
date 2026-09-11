using Odyssey.Dtos;
using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

/// <summary>
/// Read projection of a contact (issue #325). Returns both the raw <see cref="DisplayName"/>
/// (nullable — what the edit form shows) and the always-populated <see cref="ResolvedDisplayName"/>
/// (what every other surface renders), plus the type-specific detail sub-object, the alias list and
/// the three contact-method collections inline.
///
/// <para>
/// <b>Every member of this projection is gated by <c>contacts.read</c> and is reachable through no
/// other claim</b> (issue #48 §10.2). That was previously stated as if it followed from the DTO's
/// own shape; it does not. <c>IContactLookup.ResolveContactsAsync</c> used to hand this whole record
/// to the finance read paths, so a caller holding only <c>transactions.read</c>, <c>accounts.read</c>
/// or <c>budgets.read</c> received it. That path now returns <see cref="ContactEmbed"/> — two members
/// and nothing else — which is what makes the sentence above true. Do not widen it back.
/// </para>
/// </summary>
public sealed record ExistingContact
{
    public required Guid ContactId { get; set; }

    /// <summary>Raw override, nullable — distinguishes "cleared" from the fallback value in the edit form.</summary>
    [StringLength(128)]
    public string? DisplayName { get; set; }

    /// <summary>The resolved display value (never null): DisplayName, else the type-appropriate fallback.</summary>
    [StringLength(256)]
    public required string ResolvedDisplayName { get; set; }

    [StringLength(256)]
    public required string NormalizedName { get; set; }

    /// <summary>External identity anchor (issue #338 §6) — the vCard <c>UID</c> this contact
    /// exports/matches under.</summary>
    [StringLength(255)]
    public required string ExternalUid { get; set; }

    public ContactType Type { get; set; }

    [StringLength(1024)]
    public string? Notes { get; set; }

    public DateTime? Archived { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public PersonDetailsDto? PersonDetails { get; set; }

    public OrganizationDetailsDto? OrganizationDetails { get; set; }

    /// <summary>
    /// The contact's alternative names (issue #48), inline in the same shape as the three
    /// contact-method collections and ordered the same way the dedicated
    /// <c>GET /api/contacts/{id}/aliases</c> orders them — by value, then id — modulo collation.
    /// </summary>
    public IReadOnlyList<ExistingContactAlias> Aliases { get; set; } = [];

    public IReadOnlyList<ExistingAddress> Addresses { get; set; } = [];

    public IReadOnlyList<ExistingEmailAddress> EmailAddresses { get; set; } = [];

    public IReadOnlyList<ExistingPhoneNumber> PhoneNumbers { get; set; } = [];
}
