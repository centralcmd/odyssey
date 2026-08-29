using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Components;

/// <summary>
/// How a derived <see cref="ContractStatus"/> reads everywhere — the status chip, the summary status
/// pills and the status filter (issue #174). Mirrors the design-system contract status vocabulary: the
/// status meaning lives in the visible <see cref="Label"/>, never in colour or glyph alone (the
/// dot/icon is decorative). Tone follows the finance vocabulary — Active = income (mint),
/// Upcoming = info (sea), Expired = expense (coral), Archived = neutral outline.
/// </summary>
/// <param name="Label">Visible status word.</param>
/// <param name="Tone">Chip tone class — income · info · expense · outline.</param>
/// <param name="Dot">Lead with a status dot (when not showing the icon).</param>
/// <param name="Icon">Status glyph (used when an icon lead is requested).</param>
/// <param name="DotColor">CSS variable for the summary status-pill dot.</param>
public sealed record OdsContractStatusMeta(
    string Label, string Tone, bool Dot, string Icon, string DotColor);

/// <summary>The canonical contract-status registry, in display order (Active first).</summary>
public static class OdsContractStatus
{
    private static readonly IReadOnlyDictionary<ContractStatus, OdsContractStatusMeta> Registry =
        new Dictionary<ContractStatus, OdsContractStatusMeta>
        {
            [ContractStatus.Active]   = new("Active",   "income",  true,  "task_alt",     "var(--finance-income)"),
            [ContractStatus.Upcoming] = new("Upcoming", "info",    true,  "schedule",     "var(--sea-400)"),
            [ContractStatus.Expired]  = new("Expired",  "expense", false, "event_busy",   "var(--finance-expense)"),
            [ContractStatus.Archived] = new("Archived", "outline", true,  "inventory_2",  "var(--mud-palette-text-secondary)"),
        };

    /// <summary>Statuses in display order — Active · Upcoming · Expired · Archived.</summary>
    public static readonly IReadOnlyList<ContractStatus> Order =
        [ContractStatus.Active, ContractStatus.Upcoming, ContractStatus.Expired, ContractStatus.Archived];

    public static OdsContractStatusMeta Meta(ContractStatus status) =>
        Registry.TryGetValue(status, out var m) ? m : Registry[ContractStatus.Active];
}
