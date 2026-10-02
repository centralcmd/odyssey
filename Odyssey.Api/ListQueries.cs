using System.ComponentModel.DataAnnotations;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos;

namespace Odyssey.Api;

// List-query records whose handlers live in this project (issue #277). Records whose services take
// them directly sit in Odyssey.Dtos beside their module (the users list is Odyssey.Dtos.Application,
// since UserAdministrationService moved to Odyssey.Core); the file-analysis audit log is handled by its
// controller here, so it stays. Each closes the generic QueryParams base over its own sort-key enum.

/// <summary>File-analysis audit-log list query: filter by outcome status bucket(s).</summary>
public sealed class FileAnalysisAuditQueryParams : QueryParams<FileAnalysisAuditSortBy>
{
    [MaxLength(ListDefaults.MaxFilterArrayLength)]
    public FileAnalysisAuditStatus[]? Statuses { get; set; }
}
