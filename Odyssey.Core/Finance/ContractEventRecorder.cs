using Odyssey.Context;

namespace Odyssey.Core.Finance;

/// <summary>
/// One transition, already described: the event type it becomes plus the generated prose and the
/// moment it happened. Produced by <see cref="ContractEventCatalogue"/> and consumed by
/// <see cref="ContractEventRecorder.Stage"/> — nothing else constructs one, so the wire text has a
/// single origin and cannot drift between the call sites that record it (issue #154 §3).
/// </summary>
/// <remarks>
/// <see cref="Title"/> and <see cref="Description"/> are already bounded by the catalogue: a generated
/// title must never be able to fail the entity's <c>[StringLength]</c> and abort the write it is
/// describing (§8.3).
/// </remarks>
public sealed record TransitionDescriptor(
    ContractEventType Type,
    string Title,
    string? Description,
    DateTime OccurredAt);

/// <summary>
/// The one place that turns a described transition into a <see cref="ContractEvent"/> row
/// (issue #154 §3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Static and parameterised, not an injected service</b>, and both halves of that matter. It must
/// write onto <em>the caller's own</em> <see cref="OdysseyContext"/> instance — an injected service
/// holding a second handle could drift from the one the caller is about to save, which is exactly the
/// atomicity §8.6 requires — and it must use the caller's <see cref="TimeProvider"/>-derived clock, so
/// two services' timestamps cannot disagree within one request.
/// </para>
/// <para>
/// <b>It stages only.</b> It never calls <c>SaveChangesAsync</c>: the row rides the caller's existing
/// save, so the log can neither miss a transition that happened nor claim one that did not. A
/// validation failure after staging discards the staged row with the rest of the change tracker.
/// </para>
/// </remarks>
public static class ContractEventRecorder
{
    /// <summary>
    /// Stages one system event onto <paramref name="context"/>'s change tracker. Returns the entity so
    /// a caller can assert on it; nothing is written until the caller saves.
    /// </summary>
    /// <param name="userId">
    /// The <b>acting user</b>, not a service identity — the person who paused the contract is who the
    /// event is attributed to. Stored through the same <c>SET NULL</c> column every other attribution
    /// uses, so deleting them leaves the line reading "Unknown user".
    /// </param>
    /// <param name="nowUtc">The caller's own clock reading, stamped onto <c>CreatedAtUtc</c>.</param>
    public static ContractEvent Stage(
        OdysseyContext context,
        Guid contractId,
        TransitionDescriptor descriptor,
        string? userId,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(descriptor);

        var entity = new ContractEvent
        {
            ContractId = contractId,
            Type = descriptor.Type,
            // Server-owned and never bound from a request body — which is what makes forging a System
            // row a compile-time impossibility rather than a validation rule (§7.4).
            Source = ContractEventSource.System,
            Title = descriptor.Title,
            Description = descriptor.Description,
            // Always null on a system event: Notes is the user's own field.
            Notes = null,
            OccurredAt = descriptor.OccurredAt,
            CreatedByUserId = string.IsNullOrWhiteSpace(userId) ? null : userId,
            CreatedAtUtc = nowUtc,
        };

        context.ContractEvents.Add(entity);
        return entity;
    }

    /// <summary>
    /// Stages a whole run of descriptors — the shape the stamp detector produces, where one
    /// <c>PUT</c> may legitimately sign and archive a contract and write two events with the same
    /// <c>CreatedAtUtc</c>.
    /// </summary>
    public static void StageAll(
        OdysseyContext context,
        Guid contractId,
        IEnumerable<TransitionDescriptor> descriptors,
        string? userId,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(descriptors);

        foreach (var descriptor in descriptors)
        {
            Stage(context, contractId, descriptor, userId, nowUtc);
        }
    }
}
