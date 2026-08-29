using Odyssey.Dtos;
using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// Minimal, data-minimised projection of the linked company contact exposed through the
/// subscriptions read path (issue #293 §Security). Deliberately drops <c>OrganizationNumber</c>,
/// free-text <c>Description</c> and <c>NormalizedName</c> so a caller holding only
/// <c>subscriptions.read</c> cannot read the richer record gated by <c>contacts.read</c>.
/// Mirrors insurance's <c>InsurerReference</c>.
/// </summary>
public sealed record SubscriptionContactReference
{
    public required Guid ContactId { get; set; }

    [StringLength(128)]
    public required string Name { get; set; }

    public ContactType Type { get; set; }
}
