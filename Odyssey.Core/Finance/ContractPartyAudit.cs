using Microsoft.Extensions.Logging;
using Odyssey.Context;

namespace Odyssey.Core.Finance;

/// <summary>
/// The one structured <c>Information</c> line written per contract-party write (issue #121 §7.7), shared
/// by every site that writes or removes a party row — <c>ContractService</c>'s add/edit/detach and
/// <c>PropertyService.Delete</c>'s cascade (issue #208) — so there is one line format and never a
/// hand-copied second one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Static and parameterised, not an injected service</b>, in <see cref="ContractEventRecorder"/>'s
/// style: each caller passes its own <see cref="ILogger"/>, so the category still names the service that
/// performed the write.
/// </para>
/// <para>
/// Every value is an opaque identifier or a closed enum — never a name, an address, a registration
/// number or any free text — so the line identifies the rows a reader would then have to hold
/// <c>contracts.read</c> to resolve, and discloses nothing by itself. The target is read back off the
/// persisted <see cref="ContractParty"/> rather than from a request, so it records what was actually
/// written. <c>TargetKind</c> says which column held the id, so a property GUID is never mistaken for an
/// account one.
/// </para>
/// </remarks>
public static class ContractPartyAudit
{
    /// <summary>What the role slots read when there is no role on that side of the write.</summary>
    public const string NoRole = "(none)";

    /// <summary>Written by the property-delete cascade (issue #208 §7.7).</summary>
    public const string DetachedByPropertyDelete = "detached-by-property-delete";

    public static void Log(
        ILogger logger,
        string action,
        ContractParty party,
        ContractPartyRole? previousRole,
        string? userId,
        string? roleAfter = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(party);

        // A Guid, so it cannot carry the CR/LF a forged log line would need, and an opaque row id
        // rather than a credential. Both are why this is safe to record verbatim.
        Guid? targetId = party.AccountId ?? party.ContactId ?? party.PropertyId;

        logger.LogInformation(
            "Contract party {Action}: contract {ContractId}, party {ContractPartyId}, target {TargetKind} {TargetId}, " +
            "role {RoleBefore} -> {RoleAfter}, by user {UserId}.",
            Safe(action),
            party.ContractId,
            party.ContractPartyId,
            TargetKind(party),
            targetId,
            // No fabricated "previous role" on an add. Unspecified is gone and substituting Other
            // would assert a role the party never held, so the slot reads as genuinely absent.
            previousRole?.ToString() ?? NoRole,
            Safe(roleAfter ?? party.Role.ToString()),
            Safe(userId ?? "(unknown)"));
    }

    /// <summary>
    /// Strips CR/LF from the three string slots. Every other value is a <see cref="Guid"/> or a closed
    /// enum, which cannot carry a line break. <paramref name="value"/> here is a constant or a claim
    /// value in practice, but the user id is read off the principal three frames away, so the guarantee
    /// is made a property of the log site — and this is the form CodeQL recognises as a
    /// <c>cs/log-forging</c> barrier, as on <c>TermService</c>'s term line.
    /// </summary>
    private static string Safe(string value) =>
        value.Replace("\r", string.Empty, StringComparison.Ordinal)
             .Replace("\n", string.Empty, StringComparison.Ordinal);

    /// <summary>The wire kind's name for the column that is set — the same names the read path reports.</summary>
    private static string TargetKind(ContractParty party) =>
        party.AccountId is not null ? nameof(Dtos.Finance.ContractPartyKind.Account)
        : party.ContactId is not null ? nameof(Dtos.Finance.ContractPartyKind.Institution)
        : party.PropertyId is not null ? nameof(Dtos.Finance.ContractPartyKind.Property)
        : "(none)";
}
