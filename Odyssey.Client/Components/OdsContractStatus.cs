using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Components;

/// <summary>
/// How a derived <see cref="ContractStatus"/> reads everywhere — the status chip, the summary status
/// pills and the status filter (issue #174). Mirrors the design-system contract status vocabulary: the
/// status meaning lives in the visible <see cref="Label"/>, never in colour or glyph alone (the
/// dot/icon is decorative). Tone follows the finance vocabulary — Active = income (mint),
/// Upcoming = info (sea), Expired = expense (coral), Paused = pending (amber, the tone Subscriptions
/// already gives a pause), Ready = pending (the same amber: it is waiting on something), Draft and
/// Archived = neutral outline. No new hue enters for the two signature states (issue #145).
/// </summary>
/// <param name="Label">Visible status word.</param>
/// <param name="Tone">Chip tone class — income · info · expense · pending · outline.</param>
/// <param name="Dot">Lead with a status dot (when not showing the icon).</param>
/// <param name="Icon">Status glyph (used when an icon lead is requested).</param>
/// <param name="DotColor">CSS variable for the summary status-pill dot.</param>
/// <param name="Unknown">
/// True when the member is not in this client's vocabulary — a newer server enum. Callers that
/// enumerate the registry never see it; only <see cref="OdsContractStatus.Meta"/> produces one.
/// </param>
public sealed record OdsContractStatusMeta(
    string Label, string Tone, bool Dot, string Icon, string DotColor, bool Unknown = false);

/// <summary>
/// The canonical contract-status registry, in lifecycle reading order (Draft first) — see
/// <see cref="Order"/>.
/// </summary>
public static class OdsContractStatus
{
    private static readonly IReadOnlyDictionary<ContractStatus, OdsContractStatusMeta> Registry =
        new Dictionary<ContractStatus, OdsContractStatusMeta>
        {
            [ContractStatus.Active]   = new("Active",   "income",  true,  "task_alt",     "var(--finance-income)"),
            [ContractStatus.Paused]   = new("Paused",   "pending", true,  "pause_circle", "var(--finance-pending)"),
            [ContractStatus.Upcoming] = new("Upcoming", "info",    true,  "schedule",     "var(--sea-400)"),
            [ContractStatus.Expired]  = new("Expired",  "expense", false, "event_busy",   "var(--finance-expense)"),
            [ContractStatus.Archived] = new("Archived", "outline", true,  "inventory_2",  "var(--mud-palette-text-secondary)"),
            // The two signature states (issue #145). Draft is neutral — it is on file and nothing has
            // been agreed; Ready takes the pending amber a pause gets, because it is waiting on
            // something a person has to do.
            [ContractStatus.Draft]    = new("Draft",    "outline", true,  "edit_note",    "var(--mud-palette-text-secondary)"),
            [ContractStatus.Ready]    = new("Ready",    "pending", true,  "draw",         "var(--finance-pending)"),
        };

    /// <summary>
    /// Statuses in lifecycle order — Draft · Ready · Upcoming · Active · Paused · Expired · Archived.
    ///
    /// <para>
    /// A READING order, not the persisted one: <see cref="ContractStatus"/>'s ordinals are a wire and
    /// persistence contract, so <c>Paused</c> is appended at 4 and <c>Draft</c>/<c>Ready</c> at 5 and
    /// 6, and none is ever renumbered to buy a nicer sort.
    /// </para>
    ///
    /// <para>
    /// It is the SHARED rank from <see cref="ContractStatusOrder"/>, not a local copy, and the list
    /// sort on the server reads the same one (issue #145 §8). A client-side re-implementation of a
    /// server rule is the defect class CLAUDE.md forbids — and here it would show the status filter
    /// and the summary pills in one order while the sorted list came back in another.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<ContractStatus> Order = ContractStatusOrder.Order;

    /// <summary>
    /// The display row for a status. An <b>unrecognised</b> member fails NEUTRALLY, under its own
    /// name — never resolved into <see cref="ContractStatus.Active"/>.
    ///
    /// <para>
    /// This is not defensive tidiness: <see cref="ContractStatus"/> lives in <c>Odyssey.Dtos</c> and
    /// appends server-side, so a client running behind the deployment can be handed a member it has
    /// never heard of. Falling back to the Active row would render a green "Active" pill on a
    /// contract the server has just excluded from the run rate — a <i>wrong</i> state rather than a
    /// degraded one, and precisely the state the reader is looking for.
    /// </para>
    /// </summary>
    public static OdsContractStatusMeta Meta(ContractStatus status) =>
        Registry.TryGetValue(status, out var m)
            ? m
            : new(status.ToString(), "outline", true, "help",
                "var(--mud-palette-text-secondary)", Unknown: true);
}
