using System.ComponentModel.DataAnnotations;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos;

namespace Odyssey.Api;

// List-query records whose handlers live in this project (issue #277). The finance-resource records
// sit in Odyssey.Dtos.Finance so their services can take them directly; these two — users and the
// file-analysis audit log — are handled here (UserAdministrationService / the audit controller), so
// they live alongside them. Each closes the generic QueryParams base over its own sort-key enum.

/// <summary>Sortable keys for the users admin list.</summary>
public enum UserSortBy
{
    Name,
    Email,
    Role,
    EmailStatus,
    Account,
    FullName,
    BirthDate,
}

/// <summary>Users list query: filter by role and enabled state.</summary>
public sealed class UsersQueryParams : QueryParams<UserSortBy>
{
    [StringLength(64)]
    public string? Role { get; set; }

    public bool? Enabled { get; set; }
}

/// <summary>File-analysis audit-log list query: filter by outcome status bucket(s).</summary>
public sealed class FileAnalysisAuditQueryParams : QueryParams<FileAnalysisAuditSortBy>
{
    [MaxLength(ListDefaults.MaxFilterArrayLength)]
    public FileAnalysisAuditStatus[]? Statuses { get; set; }
}
