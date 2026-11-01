namespace Odyssey.Dtos.Application;

/// <summary>
/// Who last changed one system setting, and when — the per-row stamp on the System settings page
/// (Odyssey Design System · SystemSettings, "Last changed by X, date").
///
/// <para>
/// Carries the resolved display name only, never the raw user id: the row stamp needs nothing more,
/// and the name goes through the same claim-aware resolver the page-level
/// <see cref="SystemSettingsDto.UpdatedByDisplayName"/> already uses, so it discloses nothing that
/// line did not.
/// </para>
/// </summary>
public sealed record SettingProvenance
{
    /// <summary>When the key's row was last written (UTC).</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>The writer's display label. "Unknown user" for an account that no longer resolves.</summary>
    public string UpdatedByDisplayName { get; set; } = string.Empty;
}
