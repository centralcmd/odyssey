using Odyssey.Context;
using Odyssey.Core.Journal;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Api.Tests;

/// <summary>
/// A settable, non-degraded <see cref="IImportExportLimitsLookup"/> for controller unit tests that
/// construct a service directly rather than going through <c>OdysseyApiFactory</c>'s DI container
/// (issue #343). Defaults mirror the shipped System Settings defaults (§6).
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

public static class TestContextFactory
{
    public static OdysseyContext Create()
    {
        var options = new DbContextOptionsBuilder<OdysseyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var context = new OdysseyContext(options);
        context.Currencies.AddRange(
            new Currency { CurrencyCode = "USD", Name = "US Dollar", MinorUnits = 2, Symbol = "$" },
            new Currency { CurrencyCode = "EUR", Name = "Euro", MinorUnits = 2, Symbol = "€" },
            new Currency { CurrencyCode = "SEK", Name = "Swedish Krona", MinorUnits = 2, Symbol = "kr" });
        context.SaveChanges();

        return context;
    }

    public static OdysseyContext CreateJournal()
    {
        var options = new DbContextOptionsBuilder<OdysseyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new OdysseyContext(options);
    }
}
