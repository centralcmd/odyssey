using System.ComponentModel.DataAnnotations;
using Odyssey.Dtos;

namespace Odyssey.Dtos.Journal;

// Per-endpoint list-query models for the Contacts module (issue #325, following the Finance
// ListQueries pattern). Closes the generic QueryParams base over its own sort-key enum (search +
// SortBy + sort direction + offset/limit) and adds only the filters the endpoint exposes.

/// <summary>Contacts list query: filter by contact type(s) and archival status.</summary>
public sealed class ContactsQueryParams : QueryParams<ContactSortBy>
{
    [MaxLength(ListDefaults.MaxFilterArrayLength)]
    public ContactType[]? Types { get; set; }

    public ArchivalStatus? Status { get; set; }
}
