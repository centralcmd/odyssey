namespace Odyssey.Core.Journal;

/// <summary>
/// The effective import/export volume caps (issue #343 §6, extended post-#343 with a "maximum
/// export file size" per surface and a Tasks export row cap), already resolved from their
/// string-serialized <c>SystemSetting</c> rows: counts are <see langword="null"/> when unlimited,
/// sizes are pre-converted to <see langword="long"/> bytes (1 MB = 1024 × 1024 bytes) so no consumer
/// repeats the megabyte→byte arithmetic (issue #343 §9 "Mapping to the internal model", sec F2).
/// <see cref="IsDegraded"/> is <see langword="true"/> when one or more fields could not be read fresh
/// and fell back to the §11 monotonic fail-safe — the four import/export services still use the
/// (possibly degraded) numbers to enforce their caps (that is the point of the fail-safe), but a
/// read-only display surface (<c>GET /api/import-limits</c>) must not present a fallback as
/// configuration and instead fails closed (arch N1, AC 27e).
/// </summary>
public sealed record ImportExportLimits(
    int? ContactVCardMaxExportRows,
    int? ContactVCardMaxImportEntries,
    long ContactVCardMaxImportBytes,
    long ContactVCardMaxExportBytes,
    int? CalendarIcsMaxExportEvents,
    int? CalendarIcsMaxImportEvents,
    long CalendarIcsMaxImportBytes,
    long CalendarIcsMaxExportBytes,
    int? TaskIcsMaxExportTasks,
    int? TaskIcsMaxImportTasks,
    long TaskIcsMaxImportBytes,
    long TaskIcsMaxExportBytes,
    int? JournalIcsMaxExportRows,
    int? JournalIcsMaxImportEntries,
    long JournalIcsMaxImportBytes,
    long JournalIcsMaxExportBytes,
    // ── The aggregate/algorithmic bounds migrated in issue #434 (keys 8-13) ──────────────────────
    //
    // These sit on the same four services that already hold this lookup, which is why they live here
    // rather than becoming a lookup of their own: no service gains a second dependency and no request
    // performs a second cache read. Every one is a cap, so a degraded read resolves each downward.
    int CalendarIcsMaxAggregateExportRows,
    int CalendarIcsMaxAggregateOccurrences,
    int CalendarIcsMaxAggregateExportWindowDays,
    /// <summary>Repeatable vCard properties per entry. Tighten-only, clamped on read for the same reason.</summary>
    int ContactVCardMaxRepeatablePropertiesPerEntry,
    int ImportMaxSamplesPerSkipReason,
    bool IsDegraded);

/// <summary>
/// Narrow cross-domain lookup (issue #343 §5 "arch 15"), following the same pattern as
/// <see cref="Odyssey.Core.Finance.ISystemSettingsLookup"/>: the interface lives here, in
/// <c>Odyssey.Core.Journal</c>, because all four import/export services that consume it live here too
/// (unlike the Insurance settings, which are Finance-owned). The real implementation —
/// <c>Odyssey.Api.SystemSettings.ImportExportLimitsLookup</c>, backed by the <c>SystemSetting</c>
/// table, a 30s <c>IMemoryCache</c> TTL, and the §11 monotonic fail-safe — is wired at the API
/// composition root (<c>Odyssey.Api/Program.cs</c>). Deliberately not an extra method on
/// <see cref="Odyssey.Core.Finance.ISystemSettingsLookup"/> (Journal already references Finance, so it
/// would compile, but Finance consumes none of this), and deliberately not moved into
/// <c>Odyssey.Core</c> (which would drag the Finance-specific <c>InsurancePolicySettings</c> along).
/// </summary>
public interface IImportExportLimitsLookup
{
    Task<ImportExportLimits> GetAsync(CancellationToken cancellationToken = default);
}
