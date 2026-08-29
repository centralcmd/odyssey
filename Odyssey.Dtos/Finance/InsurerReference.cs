using Odyssey.Dtos;
namespace Odyssey.Dtos.Finance;

/// <summary>
/// Minimal, data-minimised projection of the insurer contact exposed through the insurance read
/// path (issue #175 §10 #4). Deliberately drops <c>OrganizationNumber</c> and free-text
/// <c>Description</c> so a caller holding only <c>insurance.read</c> cannot read the richer record
/// gated by <c>contacts.read</c>.
/// </summary>
public sealed record InsurerReference
{
    public required Guid ContactId { get; set; }

    public required string Name { get; set; }

    public ContactType Type { get; set; }
}
