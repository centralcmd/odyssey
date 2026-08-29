using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Components;

/// <summary>
/// How a derived <see cref="CoverageStatus"/> reads everywhere — the coverage chip, the portfolio
/// status pills and the status filter (issue #175). Mirrors the design-system COVERAGE_STATUSES
/// registry: the status meaning lives in the visible <see cref="Label"/>, never in colour or glyph
/// alone (the dot/icon is decorative). Tone follows the finance vocabulary — Active = income (mint),
/// ExpiringSoon = pending (amber), Lapsed/"Expired" = expense (coral), Upcoming = info (sea),
/// NoCoverage / Archived = neutral outline. Archived is the terminal lifecycle state (mirrors the
/// Contracts status chip). Icons for the states shared with Contracts match so a status reads
/// identically across both pages.
/// </summary>
/// <param name="Label">Visible status word (e.g. "Expiring soon").</param>
/// <param name="Tone">Chip tone class — income · pending · expense · info · outline.</param>
/// <param name="Dot">Lead with a status dot (when not showing the icon).</param>
/// <param name="Icon">Status glyph (used when an icon lead is requested, and on the alert rollup).</param>
/// <param name="DotColor">CSS variable for the portfolio status-pill dot.</param>
public sealed record OdsCoverageStatusMeta(
    string Label, string Tone, bool Dot, string Icon, string DotColor);

/// <summary>The canonical coverage-status registry, in display order (Active first).</summary>
public static class OdsCoverageStatus
{
    private static readonly IReadOnlyDictionary<CoverageStatus, OdsCoverageStatusMeta> Registry =
        new Dictionary<CoverageStatus, OdsCoverageStatusMeta>
        {
            [CoverageStatus.Active]       = new("Active",        "income",  true,  "task_alt",          "var(--finance-income)"),
            [CoverageStatus.ExpiringSoon] = new("Expiring soon", "pending", true,  "hourglass_bottom",  "var(--finance-pending)"),
            [CoverageStatus.Lapsed]       = new("Expired",       "expense", true,  "event_busy",        "var(--finance-expense)"),
            [CoverageStatus.Upcoming]     = new("Upcoming",      "info",    true,  "schedule",          "var(--sea-400)"),
            [CoverageStatus.NoCoverage]   = new("No coverage",   "outline", true,  "remove_moderator",  "var(--mud-palette-text-secondary)"),
            [CoverageStatus.Archived]     = new("Archived",      "outline", true,  "inventory_2",       "var(--mud-palette-text-secondary)"),
        };

    /// <summary>Statuses in display order — Active · ExpiringSoon · Expired · Upcoming · NoCoverage · Archived.</summary>
    public static readonly IReadOnlyList<CoverageStatus> Order =
        [CoverageStatus.Active, CoverageStatus.ExpiringSoon, CoverageStatus.Lapsed, CoverageStatus.Upcoming, CoverageStatus.NoCoverage, CoverageStatus.Archived];

    public static OdsCoverageStatusMeta Meta(CoverageStatus status) =>
        Registry.TryGetValue(status, out var m) ? m : Registry[CoverageStatus.NoCoverage];
}
