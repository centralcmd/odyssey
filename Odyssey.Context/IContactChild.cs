namespace Odyssey.Context;

/// <summary>
/// A row in one of a contact's child collections — aliases, addresses, email addresses, phone numbers.
/// Lets <c>ContactService</c> resolve, list and remove all four through one parent-scoped code path
/// (issue #287 M7).
/// </summary>
public interface IContactChild
{
    Guid Id { get; }

    Guid ContactId { get; }
}

/// <summary>
/// A contact-method row (address, email, phone): a child collection in which exactly one row is
/// primary whenever the collection is non-empty.
/// </summary>
public interface IContactMethod : IContactChild
{
    bool IsPrimary { get; set; }
}
