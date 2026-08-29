namespace Odyssey.Client.Components;

/// <summary>
/// The transient-feedback durations shared by every page, so a "Saved" chip or a jump-to-record
/// highlight lasts the same time everywhere instead of being re-guessed per page. These replace
/// nine hand-written <c>Task.Delay</c> literals that had drifted to five different values
/// (1600 / 1800 / 2000 / 2200 / 4000 ms) for what are only two affordances.
/// </summary>
/// <remarks>
/// These are deliberately not one single number. A control that confirms an action and a record
/// that flashes for attention are different affordances with different reading tasks, and
/// <see cref="RowFlashMs"/> additionally has to agree with a CSS animation. Anything genuinely
/// transient belongs here rather than inline; nothing else does.
/// </remarks>
public static class OdsTiming
{
    /// <summary>
    /// How long a control shows its post-action confirmation — the "Saved" chip, the save button's
    /// check glyph, the "Copied" state on a clipboard button — before reverting to its resting
    /// label. Matches <see cref="OdsRecordTable{TRow}.SavedFlashMs"/>'s default so an inline row
    /// edit and a page-level save agree.
    /// </summary>
    public const int ConfirmFlashMs = 2200;

    /// <summary>
    /// How long a record card stays highlighted after the page scrolls to it (the jump-to-record
    /// ring on the Accounts / Contracts / Insurance / Subscriptions / Tax statements lists).
    /// </summary>
    /// <remarks>
    /// MUST stay in step with the <c>--duration-flash</c> token that drives the <c>acct-flash</c>
    /// keyframes in <c>odyssey-components.css</c>: this delay is what removes the <c>.flash</c>
    /// class, so a shorter value truncates the animation mid-fade and a longer one leaves the ring
    /// sitting on a finished animation.
    /// </remarks>
    public const int RowFlashMs = 2000;
}
