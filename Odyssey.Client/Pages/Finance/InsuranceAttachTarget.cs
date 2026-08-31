using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// Which renewal period a <b>row-menu</b> attach targets (issue #26 §3).
///
/// <para>
/// The period-panel entry point is unambiguous — it attaches to the period whose panel is open. The row
/// action menu has no such context, so the target is inferred: the <b>current</b> period if there is
/// one, otherwise the period with the latest <c>ToDate</c>, ties broken by the latest
/// <c>CreatedAtUtc</c>.
/// </para>
///
/// <para>
/// The fallback is not a branch nobody reaches. <c>CurrentRenewal</c> is null whenever no period's
/// window contains today, so <b>every lapsed and every upcoming policy</b> arrives there — and lapsed is
/// precisely the bucket this change's own migration creates, since a placeholder period dated in the
/// past makes an orphaned policy report `Lapsed`.
/// </para>
///
/// <para>
/// Note this targets the <b>latest</b> period while the relocation migration targets the
/// <b>earliest</b>. Both are deliberate and neither should be "fixed" to match the other: a document a
/// user attaches today belongs to the cover in force now, while a legacy document that had no period of
/// its own belongs to the start of cover.
/// </para>
///
/// <para>
/// It is a static here, rather than a private method on the card, so the fallback ordering is testable
/// without a rendered record — the tie-break and the null-current case are exactly the branches that
/// are invisible by eye.
/// </para>
/// </summary>
public static class InsuranceAttachTarget
{
    /// <summary>The period to attach to, or <see cref="Guid.Empty"/> when the policy has none.</summary>
    public static Guid For(ExistingInsurancePolicy policy) =>
        policy.CurrentRenewal?.PolicyRenewalId
        ?? policy.Renewals
            .OrderByDescending(r => r.ToDate)
            .ThenByDescending(r => r.CreatedAtUtc)
            .Select(r => r.PolicyRenewalId)
            .FirstOrDefault();
}
