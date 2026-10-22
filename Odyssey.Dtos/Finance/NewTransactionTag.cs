using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record NewTransactionTag
{
    [StringLength(64)]
    public required string Name { get; set; }
    [StringLength(256)]
    public string? Description { get; set; }
    public required bool Archived { get; set; }

    /// <summary>
    /// A <see cref="TagIcons"/> key, or <c>null</c> for the default icon (issue #279). The
    /// default key itself is rejected — send <c>null</c>. <c>PUT</c> is full replacement, so an omitted
    /// icon resets the tag to the default.
    /// </summary>
    [StringLength(TagIcons.MaxKeyLength, ErrorMessage = TagIcons.InvalidIconMessage)]
    [TagIcon]
    public string? Icon { get; set; }
}
