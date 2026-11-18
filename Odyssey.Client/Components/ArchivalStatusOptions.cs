using Odyssey.Dtos;

namespace Odyssey.Client.Components;

/// <summary>
/// The Active / Archived filter every archivable list offers (journal entries, locations), with the glyphs
/// and tones the finance "By status" tiles give the same two states (Odyssey Design System ·
/// docs/components-data-table-menu-form-controls.md · Dropdowns). Values are the
/// <see cref="ArchivalStatus"/> names the API binds.
/// </summary>
public static class ArchivalStatusOptions
{
    public static readonly OdsBreakdownDef<ArchivalStatus>[] Defs =
    [
        new(ArchivalStatus.Active, "Active", "income", "task_alt"),
        new(ArchivalStatus.Archived, "Archived", "outline", "inventory_2"),
    ];

    public static readonly IReadOnlyList<OdsOption> Options = OdsBreakdown.Options(Defs);
}
