using Odyssey.Core;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Odyssey.Context;
using ContextContractType = Odyssey.Context.ContractType;
using ContextContractFileType = Odyssey.Context.ContractFileType;
using DtoAccountType = Odyssey.Dtos.Finance.AccountType;
using DtoContractType = Odyssey.Dtos.Finance.ContractType;
using DtoContractFileType = Odyssey.Dtos.Finance.ContractFileType;
using Odyssey.Dtos.Finance;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Microsoft.Extensions.Logging;
using ContextContractPartyRole = Odyssey.Context.ContractPartyRole;
using ContextInterval = Odyssey.Context.Interval;
using ContextTermValueUnit = Odyssey.Context.TermValueUnit;
using ContextTermDirection = Odyssey.Context.TermDirection;
using DtoInterval = Odyssey.Dtos.Finance.Interval;
using DtoContractPartyRole = Odyssey.Dtos.Finance.ContractPartyRole;

namespace Odyssey.Core.Finance;

/// <summary>
/// The derived contract status (deterministic, ordered — issue #174 §6), shared by the contract list and
/// detail reads and by the summary rollup so they cannot disagree about a contract's state (issue #287 M1).
/// </summary>
internal static class ContractStatusRules
{
    internal static ContractStatus DeriveStatus(Contract contract, DateTime today) =>
        DeriveStatus(
            contract.StartDate, contract.EndDate, contract.CompletionDate,
            contract.Archived, contract.Paused, contract.Ready, contract.Signed, today);

    /// <summary>
    /// The full derivation: the pause-blind base status, then <c>Paused</c> applied <b>once, to its
    /// result</b> (issue #140 §8).
    ///
    /// <para>
    /// <b>Paused replaces Active and nothing else</b> — it is deliberately not a sixth step in the
    /// chain below. <see cref="DeriveBaseStatus"/> contains an early return for one-off contracts that
    /// resolves <i>both</i> of its outcomes before any later branch runs, so a pause check written
    /// inside that chain would be unreachable for a settled one-off: the stamp would be stored and
    /// every read would keep reporting <c>Active</c> while the contract kept contributing to the run
    /// rate. Applying it to the result closes that by construction rather than by careful placement.
    /// </para>
    ///
    /// <para>
    /// Read as precedence:
    /// <c>Archived &gt; Draft/Ready &gt; Upcoming &gt; Expired &gt; Paused &gt; Active</c>. A
    /// terminal fact outranks a temporary one, so a paused contract whose term has since run out reads
    /// <c>Expired</c> — its stamp is retained, so resuming it after fixing its dates is one write.
    /// </para>
    /// </summary>
    internal static ContractStatus DeriveStatus(
        DateTime? startDate, DateTime? endDate, DateTime? completionDate,
        DateTime? archived, DateTime? paused, DateTime? ready, DateTime? signed, DateTime today)
    {
        var status = DeriveBaseStatus(startDate, endDate, completionDate, archived, ready, signed, today);
        return status == ContractStatus.Active && paused is not null
            ? ContractStatus.Paused
            : status;
    }

    /// <summary>
    /// The pause-blind derivation: the archive check, then the <b>signature layer</b>, then the date
    /// chain (issue #174 §6, issue #145 §3).
    ///
    /// <para>
    /// <b>The signature layer sits between the archive check and the date chain, and short-circuits
    /// it.</b> An unsigned contract with a future start date reads <c>Draft</c>/<c>Ready</c>, not
    /// <c>Upcoming</c>: its dates describe a term nobody has agreed to, and reporting <c>Upcoming</c>
    /// would assert a commitment that does not exist — and would put it back into the upcoming
    /// charges. An unsigned contract whose end date has passed reads <c>Draft</c>/<c>Ready</c>, not
    /// <c>Expired</c>: a term cannot lapse before it begins, and describing a negotiation that stalled
    /// as an agreement that ran its course would make the row look retired rather than abandoned —
    /// which matters, because abandonment is the thing the reader has to act on.
    /// </para>
    ///
    /// <para>
    /// <c>Archived</c> still wins over both: a retired contract's signature history is no longer the
    /// thing a reader is acting on.
    /// </para>
    /// </summary>
    internal static ContractStatus DeriveBaseStatus(
        DateTime? startDate, DateTime? endDate, DateTime? completionDate, DateTime? archived,
        DateTime? ready, DateTime? signed, DateTime today)
    {
        if (archived is not null)
        {
            return ContractStatus.Archived;
        }
        // The signature layer. Nothing below runs for an unsigned contract, by design.
        if (signed is null)
        {
            return ready is not null ? ContractStatus.Ready : ContractStatus.Draft;
        }
        // One-off: a point-in-time agreement — Upcoming until its completion date, a settled record after.
        if (completionDate is { } completion)
        {
            return completion.Date > today ? ContractStatus.Upcoming : ContractStatus.Active;
        }
        if (startDate is { } start && start.Date > today)
        {
            return ContractStatus.Upcoming;
        }
        if (endDate is { } end && end.Date < today)
        {
            return ContractStatus.Expired;
        }
        return ContractStatus.Active;
    }
}
