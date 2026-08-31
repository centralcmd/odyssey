namespace Odyssey.Client.Components;

/// <summary>
/// How a subscription's lifecycle state reads — the sibling of <see cref="OdsContractStatus"/> and
/// <see cref="OdsCoverageStatus"/>. The state meaning lives in the visible <see cref="Label"/>, never
/// in colour or glyph alone (the dot/icon is decorative).
/// </summary>
/// <param name="Label">Visible status word.</param>
/// <param name="Tone">Chip tone — pending · expense · outline · income.</param>
/// <param name="Icon">Status glyph, used when an icon lead is requested.</param>
public sealed record OdsSubscriptionStatusMeta(string Label, OdsChipTone Tone, string Icon);

/// <summary>
/// The canonical subscription-status registry and the precedence that picks one state from the three
/// stored/derived flags.
///
/// <para>
/// A subscription has exactly ONE lifecycle state. The states are ordered rather than orthogonal:
/// only an ended subscription can be archived, so Archived implies Ended, and Ended makes a pause
/// moot. That ordering is also what the server enforces on the archive transition, so the chip and
/// the API agree by construction.
/// </para>
///
/// <para>
/// It lives here rather than inline in the chip so the precedence is testable on its own — it
/// replaced a version that rendered several chips at once, and a silent regression back to that would
/// otherwise only be visible by eye.
/// </para>
/// </summary>
public static class OdsSubscriptionStatus
{
    public static readonly OdsSubscriptionStatusMeta Paused = new("Paused", OdsChipTone.Pending, "pause_circle");
    public static readonly OdsSubscriptionStatusMeta Ended = new("Ended", OdsChipTone.Expense, "event_busy");
    public static readonly OdsSubscriptionStatusMeta Archived = new("Archived", OdsChipTone.Outline, "inventory_2");
    public static readonly OdsSubscriptionStatusMeta Active = new("Active", OdsChipTone.Income, "autorenew");

    /// <summary>
    /// The single state to render, by precedence: Archived → Ended → Paused → Active. Returns null for
    /// a plain active subscription unless <paramref name="showActive"/> is set, so an untouched row
    /// carries no chip at all.
    /// </summary>
    public static OdsSubscriptionStatusMeta? Resolve(bool paused, bool ended, bool archived, bool showActive) =>
        archived ? Archived
        : ended ? Ended
        : paused ? Paused
        : showActive ? Active
        : null;
}
