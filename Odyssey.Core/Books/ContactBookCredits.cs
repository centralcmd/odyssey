using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.Journal;
using Odyssey.Dtos.Books;

namespace Odyssey.Core.Books;

/// <inheritdoc />
public sealed class ContactBookCredits(OdysseyContext context) : IContactBookCredits
{
    public async Task<bool> HasCreditsAsync(Guid contactId, CancellationToken cancellationToken = default) =>
        await context.Books.AnyAsync(b => b.MainAuthorContactId == contactId, cancellationToken)
        || await context.BookContributors.AnyAsync(c => c.ContactId == contactId, cancellationToken);

    public async Task<ContactBookCreditBlockers?> GetCreditsAsync(Guid contactId, CancellationToken cancellationToken = default)
    {
        var authored = await context.Books.AsNoTracking()
            .Where(b => b.MainAuthorContactId == contactId)
            .Select(b => new { b.BookId, b.Title })
            .ToListAsync(cancellationToken);
        var contributed = await context.BookContributors.AsNoTracking()
            .Where(c => c.ContactId == contactId)
            .Select(c => new { c.BookId, c.Book!.Title })
            .ToListAsync(cancellationToken);

        var total = authored.Count + contributed.Count;
        if (total == 0)
        {
            return null;
        }

        var books = authored.Concat(contributed)
            .GroupBy(b => b.BookId)
            .Select(g => new BookCreditReference { BookId = g.Key, Title = g.First().Title })
            .OrderBy(b => b.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(b => b.BookId)
            .ToList();

        // The list is capped so a prolific author's 409 stays small; BookCount still says how many there are.
        return new ContactBookCreditBlockers
        {
            TotalCredits = total,
            BookCount = books.Count,
            Books = [.. books.Take(BookLimits.MaxCreditBooksListed)],
        };
    }
}
