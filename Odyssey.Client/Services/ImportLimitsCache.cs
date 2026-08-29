using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Application;

namespace Odyssey.Client.Services;

/// <summary>
/// A per-session cache for the effective import/export volume caps (issue #343 §3 "How the client
/// gets the limits", fe R9) — the same established <see cref="IReferenceDataCache"/> shape, not a
/// bespoke one.
/// </summary>
/// <remarks>
/// <para>
/// Registered scoped, which in Blazor WebAssembly means one instance for the life of the app, so the
/// limits are fetched once and every later reader is served from memory. Concurrent readers share the
/// single in-flight request rather than racing two of them.
/// </para>
/// <para>
/// <b>Failures are never cached, and never surfaced as null.</b> A failed load leaves the slot empty so
/// the next reader retries, and this attempt resolves to <see cref="Fallback"/> — the shipped-default
/// caps, sourced once from the same per-surface <c>MaxImportBytes</c> constants the four typed export
/// clients already carry (issue #343 fe C4: "the fallback constants live in one place in
/// Odyssey.ApiClient, not per-dialog"). Callers always get a usable <see cref="ImportLimitsDto"/> and
/// never need to know whether it came from the server or the fallback; no page under <c>Pages/</c>
/// references a <c>MaxImportBytes</c> constant directly (fe C3, AC 31) — this is the one place that does.
/// </para>
/// <para>
/// <b>Mutations must invalidate.</b> A successful save on <c>/settings</c> calls
/// <see cref="Invalidate"/>. Without this, a session-lifetime cache means an admin who lowers a limit
/// and then opens an import dialog in the same session would pre-validate against the old value
/// indefinitely — defeating the accuracy promise this feature exists for, in precisely the flow it
/// exists for.
/// </para>
/// </remarks>
public interface IImportLimitsCache
{
    /// <summary>The effective import/export limits — the live server values, or
    /// <see cref="ImportLimitsCache.Fallback"/> when the load fails (including a degraded <c>503</c>).</summary>
    Task<ImportLimitsDto> GetAsync(CancellationToken ct = default);

    /// <summary>Drops the cached limits; the next reader re-fetches.</summary>
    void Invalidate();
}

/// <inheritdoc cref="IImportLimitsCache" />
public sealed class ImportLimitsCache(IImportLimitsApiClient api) : IImportLimitsCache
{
    /// <summary>
    /// The shipped defaults, expressed once from the per-surface <c>MaxImportBytes</c> constants
    /// already on <see cref="ContactVCardApiClient"/>/<see cref="CalendarApiClient"/>/
    /// <see cref="TaskApiClient"/>/<see cref="JournalIcsApiClient"/> — the single source of truth
    /// fe C4 asks for. The count fields mirror the migration-seeded defaults (issue #343 §6); dialogs
    /// don't consume them today (they only pre-check file size), but a fallback DTO is complete
    /// regardless of what today's callers happen to read.
    /// </summary>
    public static readonly ImportLimitsDto Fallback = new()
    {
        ContactVCardMaxExportRows = null,
        ContactVCardMaxImportEntries = null,
        ContactVCardMaxImportMegabytes = (int)(ContactVCardApiClient.MaxImportBytes / (1024 * 1024)),
        // No ApiClient MaxExportBytes constant exists for these five (a post-#343 follow-up; exports
        // were never client-side size-bound before), so — like the count fields above — these mirror
        // the migration-seeded defaults as plain literals.
        ContactVCardMaxExportMegabytes = 64,
        CalendarIcsMaxExportEvents = 2000,
        CalendarIcsMaxImportEvents = 2000,
        CalendarIcsMaxImportMegabytes = (int)(CalendarApiClient.MaxImportBytes / (1024 * 1024)),
        CalendarIcsMaxExportMegabytes = 64,
        TaskIcsMaxExportTasks = 2000,
        TaskIcsMaxImportTasks = 2000,
        TaskIcsMaxImportMegabytes = (int)(TaskApiClient.MaxImportBytes / (1024 * 1024)),
        TaskIcsMaxExportMegabytes = 64,
        JournalIcsMaxExportRows = 2000,
        JournalIcsMaxImportEntries = 2000,
        JournalIcsMaxImportMegabytes = (int)(JournalIcsApiClient.MaxImportBytes / (1024 * 1024)),
        JournalIcsMaxExportMegabytes = 64,
    };

    private Task<ImportLimitsDto?>? pending;

    public async Task<ImportLimitsDto> GetAsync(CancellationToken ct = default)
    {
        // Readers that arrive while a fetch is in flight await that same task rather than issuing
        // their own — two import dialogs opening back to back cost one request, not two.
        var task = pending ??= LoadAsync(ct);
        var result = await task;

        // Only clear the slot if it still holds the failed task: an Invalidate (or a retry that
        // already succeeded) may have replaced it while this one was in flight.
        if (result is null && ReferenceEquals(pending, task))
        {
            pending = null;
        }

        return result ?? Fallback;
    }

    public void Invalidate() => pending = null;

    private async Task<ImportLimitsDto?> LoadAsync(CancellationToken ct)
    {
        var result = await api.GetAsync(ct);
        return result.IsSuccess ? result.Value : null;
    }
}
