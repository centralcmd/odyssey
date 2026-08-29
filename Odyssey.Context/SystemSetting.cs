using System.ComponentModel.DataAnnotations;

namespace Odyssey.Context;

/// <summary>
/// A single admin-configurable runtime setting (issue #349), stored as a natural-keyed key-value row
/// rather than one wide row — each key's <see cref="UpdatedAt"/>/<see cref="UpdatedBy"/> tracks only
/// its own last write, independent of every other key. <see cref="Key"/> is the primary key (see
/// <see cref="SystemSettingsKeys"/>); no surrogate id is needed. <see cref="Value"/> is always a
/// string regardless of the setting's logical type — <c>Odyssey.Api.SystemSettings.SystemSettingsService</c>
/// is the single place that knows how to parse/format each known key.
/// </summary>
public sealed class SystemSetting
{
    [Key]
    [StringLength(100)]
    public string Key { get; set; } = null!;

    [StringLength(4000)]
    public string Value { get; set; } = null!;

    public DateTime UpdatedAt { get; set; }

    // Matches AspNetUsers.Id's actual column type in this repo, not the framework-default 450 — a
    // loose reference (ApplicationUser.Id) with no FK constraint required.
    [StringLength(255)]
    public string? UpdatedBy { get; set; }
}
