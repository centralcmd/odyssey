namespace Odyssey.Dtos;

/// <summary>
/// The two kinds of contact a record can be (issue #325 — collapsed from the earlier six-value
/// taxonomy). Ordinals are pinned to the pre-#325 wire values (<c>Person = 1</c>,
/// <c>Organization = 2</c>) so existing rows never silently remap; the dropped
/// <c>Merchant/Company/Institution/Other</c> values are folded into <c>Organization</c> by the
/// migration backfill (§15).
/// </summary>
public enum ContactType
{
    Person = 1,
    Organization = 2,
}
