using Odyssey.Context;

namespace Odyssey.Core.Finance;

/// <summary>
/// Which kind of record a <see cref="Term"/> prices. Not persisted — the owner is carried by whichever
/// of <c>Term.AccountId</c>/<c>Term.ContractId</c> is populated, because a discriminator plus one
/// untyped owner id could carry a real foreign key in neither direction (issue #135 §4).
/// </summary>
public enum TermOwnerKind
{
    Account = 0,
    Contract = 1,
}

/// <summary>
/// Everything <c>TermService</c>'s single validation path needs to know about the record a term is
/// being written against, resolved once per request from the route. One resolver per owner (see
/// <see cref="TermOwnerResolver"/>) keeps the validator owner-agnostic, so a third owner is a new
/// resolver rather than a second copy of the validator — and two copies of a validator diverge, with
/// the copy that has fewer eyes on it being the one that will.
/// </summary>
/// <param name="Kind">Which owner this is.</param>
/// <param name="Id">The owner's primary key.</param>
/// <param name="Noun">The owner's name in prose, for error messages ("account", "contract").</param>
/// <param name="DefaultCurrencyCode">
/// The currency an <c>Amount</c> term falls back to when the request omits one, or <see langword="null"/>
/// when the owner has none of its own and an explicit code is therefore required. A contract has no
/// currency: the two defaulting alternatives (its first account party's currency, an instance-wide base
/// currency) both assign a meaning nobody chose, and the first changes retroactively when parties are
/// detached or re-ordered.
/// </param>
/// <param name="IsTermCapped">
/// Whether a create is refused beyond a per-owner cap. Only WHETHER, not the number: the cap is a
/// system setting, and reading it on the four routes that never consult it would be a settings lookup
/// bought for nothing — so <c>CreateFor</c> resolves the value where it is about to be enforced.
/// Accounts are uncapped; that pre-existing gap is left to its own issue rather than being widened or
/// closed here (issue #135 Non-Goal 4).
/// </param>
public sealed record TermOwnerFacts(
    TermOwnerKind Kind,
    Guid Id,
    string Noun,
    string? DefaultCurrencyCode,
    bool IsTermCapped);
