using System.Globalization;
using Odyssey.Core.Journal;
using Odyssey.Dtos;

namespace Odyssey.Testing;

/// <summary>
/// A settable, non-degraded <see cref="IImportExportLimitsLookup"/> for tests that construct an
/// import/export service (or the size-limit middleware) directly rather than through a host's DI
/// container. Every default is computed from <see cref="SystemSettingsDefaults"/>, so a test that never
/// sets a property sees the shipped out-of-the-box caps, and a change to a shipped default reaches the
/// tests without a second edit. A test that needs a tight cap sets it explicitly.
/// </summary>
/// <remarks>
/// This replaces two hand-kept copies (one per test project) whose literals had drifted from the
/// shipped defaults: they capped the calendar, task and journal surfaces at 2,000 rows and 64 MB,
/// while the product ships those surfaces unlimited and at 10 GB.
/// </remarks>
public sealed class FakeImportExportLimitsLookup : IImportExportLimitsLookup
{
    public int? ContactVCardMaxExportRows { get; set; } = Count(SystemSettingsDefaults.ContactVCardMaxExportRows);
    public int? ContactVCardMaxImportEntries { get; set; } = Count(SystemSettingsDefaults.ContactVCardMaxImportEntries);
    public long ContactVCardMaxImportBytes { get; set; } = Bytes(SystemSettingsDefaults.ContactVCardMaxImportMegabytes);
    public long ContactVCardMaxExportBytes { get; set; } = Bytes(SystemSettingsDefaults.ContactVCardMaxExportMegabytes);
    public int? CalendarIcsMaxExportEvents { get; set; } = Count(SystemSettingsDefaults.CalendarIcsMaxExportEvents);
    public int? CalendarIcsMaxImportEvents { get; set; } = Count(SystemSettingsDefaults.CalendarIcsMaxImportEvents);
    public long CalendarIcsMaxImportBytes { get; set; } = Bytes(SystemSettingsDefaults.CalendarIcsMaxImportMegabytes);
    public long CalendarIcsMaxExportBytes { get; set; } = Bytes(SystemSettingsDefaults.CalendarIcsMaxExportMegabytes);
    public int? TaskIcsMaxExportTasks { get; set; } = Count(SystemSettingsDefaults.TaskIcsMaxExportTasks);
    public int? TaskIcsMaxImportTasks { get; set; } = Count(SystemSettingsDefaults.TaskIcsMaxImportTasks);
    public long TaskIcsMaxImportBytes { get; set; } = Bytes(SystemSettingsDefaults.TaskIcsMaxImportMegabytes);
    public long TaskIcsMaxExportBytes { get; set; } = Bytes(SystemSettingsDefaults.TaskIcsMaxExportMegabytes);
    public int? JournalIcsMaxExportRows { get; set; } = Count(SystemSettingsDefaults.JournalIcsMaxExportRows);
    public int? JournalIcsMaxImportEntries { get; set; } = Count(SystemSettingsDefaults.JournalIcsMaxImportEntries);
    public long JournalIcsMaxImportBytes { get; set; } = Bytes(SystemSettingsDefaults.JournalIcsMaxImportMegabytes);
    public long JournalIcsMaxExportBytes { get; set; } = Bytes(SystemSettingsDefaults.JournalIcsMaxExportMegabytes);
    public int CalendarIcsMaxAggregateExportRows { get; set; } = SystemSettingsDefaults.CalendarIcsMaxAggregateExportRows;
    public int CalendarIcsMaxAggregateOccurrences { get; set; } = SystemSettingsDefaults.CalendarIcsMaxAggregateOccurrences;
    public int CalendarIcsMaxAggregateExportWindowDays { get; set; } = SystemSettingsDefaults.CalendarIcsMaxAggregateExportWindowDays;
    public int ContactVCardMaxRepeatablePropertiesPerEntry { get; set; } = SystemSettingsDefaults.ContactVCardMaxRepeatablePropertiesPerEntry;
    public int ImportMaxSamplesPerSkipReason { get; set; } = SystemSettingsDefaults.ImportMaxSamplesPerSkipReason;
    public long NewsOpmlMaxImportBytes { get; set; } = Bytes(SystemSettingsDefaults.NewsOpmlMaxImportMegabytes);

    public Task<ImportExportLimits> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new ImportExportLimits(
            ContactVCardMaxExportRows, ContactVCardMaxImportEntries, ContactVCardMaxImportBytes, ContactVCardMaxExportBytes,
            CalendarIcsMaxExportEvents, CalendarIcsMaxImportEvents, CalendarIcsMaxImportBytes, CalendarIcsMaxExportBytes,
            TaskIcsMaxExportTasks, TaskIcsMaxImportTasks, TaskIcsMaxImportBytes, TaskIcsMaxExportBytes,
            JournalIcsMaxExportRows, JournalIcsMaxImportEntries, JournalIcsMaxImportBytes, JournalIcsMaxExportBytes,
            CalendarIcsMaxAggregateExportRows, CalendarIcsMaxAggregateOccurrences, CalendarIcsMaxAggregateExportWindowDays,
            ContactVCardMaxRepeatablePropertiesPerEntry, ImportMaxSamplesPerSkipReason, NewsOpmlMaxImportBytes,
            IsDegraded: false));

    // The same conversions the real lookup applies: "unlimited" is null, a megabyte is 1024 × 1024 bytes.
    private static int? Count(string stored) =>
        stored == SystemSettingsDefaults.Unlimited ? null : int.Parse(stored, CultureInfo.InvariantCulture);

    private static long Bytes(int megabytes) => megabytes * 1024L * 1024L;
}
