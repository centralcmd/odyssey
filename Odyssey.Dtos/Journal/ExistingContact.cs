using Odyssey.Dtos;
using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

/// <summary>
/// Read projection of a contact (issue #325). Returns both the raw <see cref="DisplayName"/>
/// (nullable — what the edit form shows) and the always-populated <see cref="ResolvedDisplayName"/>
/// (what every other surface renders), plus the type-specific detail sub-object and the three contact
/// collections inline (all gated by the same <c>contacts.read</c> claim, §10.6).
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

    public IReadOnlyList<ExistingAddress> Addresses { get; set; } = [];

    public IReadOnlyList<ExistingEmailAddress> EmailAddresses { get; set; } = [];

    public IReadOnlyList<ExistingPhoneNumber> PhoneNumbers { get; set; } = [];
}
