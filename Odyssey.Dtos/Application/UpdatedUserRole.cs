using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Application;

/// <summary>The role to assign. 256 matches the Identity <c>AspNetRoles.Name</c> column.</summary>
public sealed record UpdatedUserRole
{
    [Required]
    [StringLength(256)]
    public string? Role { get; set; }
}
