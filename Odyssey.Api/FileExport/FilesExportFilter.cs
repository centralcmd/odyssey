using System.ComponentModel.DataAnnotations;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos;

namespace Odyssey.Api.FileExport;

/// <summary>
/// Query filter for <c>GET /api/admin/files/export/filtered</c> (Odyssey Design System · Files.jsx
/// "Export filtered"). Mirrors <c>FilesQueryParams.Search</c>, but <see cref="Kind"/> is multi-value
/// here: the general list endpoint's <c>Kind</c> filter is single-value, while the Files page's Type
/// filter is a multi-select — export re-runs that same client-side filter server-side, unpaginated,
/// so it must match ANY of the selected kinds, not just one.
/// </summary>
public sealed class FilesExportFilter
{
    [StringLength(ListDefaults.MaxSearchLength)]
    public string? Search { get; set; }

    public FileKind[]? Kind { get; set; }
}
