using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Application;

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
