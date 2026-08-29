namespace Odyssey.Dtos.Application;

/// <summary>
/// The effective, read-only projection of the import/export volume caps (issue #343 §7 item 3,
/// extended post-#343 with a "maximum export file size" per surface and a Tasks export row cap),
/// served by <c>GET /api/import-limits</c> to any authenticated caller — no permission claim, no
/// <c>UpdatedAt</c>/<c>UpdatedBy</c>/<c>UpdatedByDisplayName</c>, none of the five Security/Insurance
/// fields. Deliberately not a reuse of <see cref="SystemSettingsDto"/> (admin-gated, carries an
/// administrator's identity via <c>UpdatedByDisplayName</c> — issue #343 §10 item 2). Count fields are
/// <see langword="null"/> when unlimited.
/// </summary>
public sealed record ImportLimitsDto
{
    public int? ContactVCardMaxExportRows { get; set; }

    public int? ContactVCardMaxImportEntries { get; set; }

    public int ContactVCardMaxImportMegabytes { get; set; }

    public int ContactVCardMaxExportMegabytes { get; set; }

    public int? CalendarIcsMaxExportEvents { get; set; }

    public int? CalendarIcsMaxImportEvents { get; set; }

    public int CalendarIcsMaxImportMegabytes { get; set; }

    public int CalendarIcsMaxExportMegabytes { get; set; }

    public int? TaskIcsMaxExportTasks { get; set; }

    public int? TaskIcsMaxImportTasks { get; set; }

    public int TaskIcsMaxImportMegabytes { get; set; }

    public int TaskIcsMaxExportMegabytes { get; set; }

    public int? JournalIcsMaxExportRows { get; set; }

    public int? JournalIcsMaxImportEntries { get; set; }

    public int JournalIcsMaxImportMegabytes { get; set; }

    public int JournalIcsMaxExportMegabytes { get; set; }
}
