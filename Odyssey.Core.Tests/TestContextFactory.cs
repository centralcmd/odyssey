using Odyssey.Core.Finance;
using Odyssey.Dtos.Finance;
using Odyssey.Context;
using Odyssey.Core.Journal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Odyssey.Core.Tests;

/// <summary>
/// No-op <see cref="IContactReferenceGuard"/> for ContactService unit tests: the finance-side cleanup on
/// contact delete — and the foreign keys that now enforce the same behaviours in the database — is
/// covered by Odyssey.IntegrationTests against real MariaDB. The InMemory provider enforces neither, so
/// these fast tests only exercise ContactService's own CRUD logic.
/// </summary>
public sealed class NoopContactReferenceGuard : IContactReferenceGuard
{
    public Task<ContactDeleteBlockers> GetDeleteBlockersAsync(Guid contactId, CancellationToken cancellationToken = default) =>
        Task.FromResult(ContactDeleteBlockers.None);

    public Task<bool> IsReferencedByRestrictedLinkAsync(Guid contactId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task ClearAndCascadeReferencesAsync(Guid contactId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<ContactLinkDetachPlan> ReadLinkDetachPlanAsync(Guid contactId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ContactLinkDetachPlan { ContractBeneficiaries = [] });

    public DetachedContactLinks StageLinkDetach(ContactLinkDetachPlan plan) => new();
}

public static class TestContextFactory
{
    // A second, independent InMemory OdysseyContext. Finance and journal are one context now, so this
    // differs from Create() only in owning its own store and skipping the currency seed — which is what
    // the contact-lookup helpers below want, and what tests keeping contacts apart from finance rows
    // rely on. The transaction-ignore warning mirrors Create().
    public static OdysseyContext CreateJournal()
    {
        var options = new DbContextOptionsBuilder<OdysseyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new OdysseyContext(options);
    }

    // Convenience for the many service tests that don't exercise contact resolution: a lookup over a
    // fresh, empty context. Tests that DO need a seeded contact to resolve should create one context,
    // seed the contact into it, and pass ContactLookup(context) so both share it.
    public static Odyssey.Core.Finance.IContactLookup EmptyContactLookup() =>
        new Odyssey.Core.Journal.ContactLookup(CreateJournal());

    public static Odyssey.Core.Finance.IContactLookup ContactLookup(OdysseyContext journal) =>
        new Odyssey.Core.Journal.ContactLookup(journal);

    /// <summary>
    /// A second context over the SAME in-memory store as <paramref name="context"/> — another replica's view,
    /// for the queue's concurrency tests (issue #444).
    /// </summary>
    public static OdysseyContext Reopen(OdysseyContext context) =>
        new((DbContextOptions<OdysseyContext>)Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions
            .GetService<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptions>(context));

    public static OdysseyContext Create()
    {
        var options = new DbContextOptionsBuilder<OdysseyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            // EF Core InMemory doesn't support real transactions and throws by default rather than
            // silently no-opping (a real safety net for production code) — ContactVCardService's
            // import path opens one for atomicity (issue #338 review), which is a genuine no-op here.
            // The actual transactional guarantee is verified by Odyssey.IntegrationTests against real
            // MariaDB; this just lets that code path run in the fast InMemory-backed unit tests.
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        var context = new OdysseyContext(options);
        context.Currencies.AddRange(
            new Currency { CurrencyCode = "USD", Name = "US Dollar", MinorUnits = 2, Symbol = "$" },
            new Currency { CurrencyCode = "EUR", Name = "Euro", MinorUnits = 2, Symbol = "€" },
            new Currency { CurrencyCode = "SEK", Name = "Swedish Krona", MinorUnits = 2, Symbol = "kr" });
        context.SaveChanges();

        return context;
    }
}
