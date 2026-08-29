using Odyssey.Core.Finance;
using Odyssey.Context;
using Odyssey.Core.Journal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Odyssey.Core.Tests;

/// <summary>
/// A settable, non-degraded <see cref="IImportExportLimitsLookup"/> for the four import/export
/// services' unit tests (issue #343 arch 15 — their tests live in <c>Odyssey.Core.Tests</c>, not
/// alongside the services in <c>Odyssey.Core.Journal</c>). Defaults mirror the shipped System Settings
/// defaults (issue #343 §6), so a test that never touches these properties sees exactly today's
/// out-of-the-box behavior. The real fail-safe/degrade logic is <c>ImportExportLimitsLookup</c>'s own
/// concern and is tested against that type directly, not this fake.
/// </summary>
public sealed class FakeImportExportLimitsLookup : IImportExportLimitsLookup
{
    public int? ContactVCardMaxExportRows { get; set; }
    public int? ContactVCardMaxImportEntries { get; set; }
    public long ContactVCardMaxImportBytes { get; set; } = 64L * 1024 * 1024;
    public long ContactVCardMaxExportBytes { get; set; } = 64L * 1024 * 1024;
    public int? CalendarIcsMaxExportEvents { get; set; } = 2000;
    public int? CalendarIcsMaxImportEvents { get; set; } = 2000;
    public long CalendarIcsMaxImportBytes { get; set; } = 64L * 1024 * 1024;
    public long CalendarIcsMaxExportBytes { get; set; } = 64L * 1024 * 1024;
    public int? TaskIcsMaxExportTasks { get; set; } = 2000;
    public int? TaskIcsMaxImportTasks { get; set; } = 2000;
    public long TaskIcsMaxImportBytes { get; set; } = 64L * 1024 * 1024;
    public long TaskIcsMaxExportBytes { get; set; } = 64L * 1024 * 1024;
    public int? JournalIcsMaxExportRows { get; set; } = 2000;
    public int? JournalIcsMaxImportEntries { get; set; } = 2000;
    public long JournalIcsMaxImportBytes { get; set; } = 64L * 1024 * 1024;
    public long JournalIcsMaxExportBytes { get; set; } = 64L * 1024 * 1024;

    public Task<ImportExportLimits> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new ImportExportLimits(
            ContactVCardMaxExportRows, ContactVCardMaxImportEntries, ContactVCardMaxImportBytes, ContactVCardMaxExportBytes,
            CalendarIcsMaxExportEvents, CalendarIcsMaxImportEvents, CalendarIcsMaxImportBytes, CalendarIcsMaxExportBytes,
            TaskIcsMaxExportTasks, TaskIcsMaxImportTasks, TaskIcsMaxImportBytes, TaskIcsMaxExportBytes,
            JournalIcsMaxExportRows, JournalIcsMaxImportEntries, JournalIcsMaxImportBytes, JournalIcsMaxExportBytes,
            20_000, 5_000, 92, 200, 100,
            IsDegraded: false));
}

/// <summary>
/// No-op <see cref="IContactReferenceGuard"/> for ContactService unit tests: the finance-side cleanup on
/// contact delete — and the foreign keys that now enforce the same behaviours in the database — is
/// covered by Odyssey.IntegrationTests against real MariaDB. The InMemory provider enforces neither, so
/// these fast tests only exercise ContactService's own CRUD logic.
/// </summary>
public sealed class NoopContactReferenceGuard : IContactReferenceGuard
{
    public Task<bool> IsReferencedAsInsurerAsync(Guid contactId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task ClearAndCascadeReferencesAsync(Guid contactId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
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
