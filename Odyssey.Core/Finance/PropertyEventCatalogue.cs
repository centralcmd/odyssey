using System.Globalization;
using Odyssey.Context;

namespace Odyssey.Core.Finance;

/// <summary>
/// Which of a property's three lifecycle fields a transition concerns (issue #209 §8.5).
/// </summary>
public enum PropertyStamp
{
    Archived,
    Acquired,
    Disposed,
}

/// <summary>
/// One property transition, already described: the event type it becomes plus the generated prose
/// and the moment it happened. Produced by <see cref="PropertyEventCatalogue"/> only, so the wire text
/// has a single origin.
/// </summary>
public sealed record PropertyTransitionDescriptor(
    PropertyEventType Type,
    string Title,
    string? Description,
    DateTime OccurredAt);

/// <summary>
/// The fixed, per-transition mapping to a system property event's <c>Title</c> and <c>Description</c>
/// (issue #209 §8.5). One table, so the wire text lives in one place.
/// </summary>
/// <remarks>
/// <para>
/// <b>The allowed inputs are closed: the transition kind and its date.</b> No property name,
/// description, notes, address, cadastral number, registration number, VIN, make, model or estimate
/// figure may appear here, ever (§7.3). An event is frozen prose in a free-text column: a personal
/// detail copied into it would survive the erasure of the field it came from, and an estimate figure
/// would disclose, under <c>properties.read</c>, what <c>properties.estimates.read</c> withholds. The
/// method signature is the enforcement — it takes nothing a property could leak through.
/// </para>
/// <para>
/// Every string is bounded here and truncated rather than left to throw, with the contract
/// catalogue's own <see cref="ContractEventCatalogue.Bound"/>: a generated title must never be able to
/// fail the entity's <c>[StringLength]</c> and abort the write it describes.
/// </para>
/// </remarks>
public static class PropertyEventCatalogue
{
    /// <summary>Invariant culture — the server writes the sentence, so it does not vary by reader.</summary>
    private const string DateFormat = "d MMMM yyyy";

    /// <summary>
    /// One of the six transitions. <paramref name="set"/> distinguishes <c>null</c> → non-null from its
    /// opposite; the detector never calls this for an unchanged field or a non-null → different
    /// non-null re-date, both of which write nothing.
    /// </summary>
    /// <param name="occurredAt">
    /// The already-resolved moment; the description's date is read off the same value, so the prose and
    /// the timeline position cannot disagree.
    /// </param>
    public static PropertyTransitionDescriptor Stamp(PropertyStamp stamp, bool set, DateTime occurredAt)
    {
        var date = occurredAt.ToString(DateFormat, CultureInfo.InvariantCulture);

        var (type, title, description) = (stamp, set) switch
        {
            (PropertyStamp.Archived, true) => (PropertyEventType.Archived, "Property archived", $"Archived on {date}."),
            (PropertyStamp.Archived, false) => (PropertyEventType.Unarchived, "Property restored from the archive", $"Restored on {date}."),
            (PropertyStamp.Acquired, true) => (PropertyEventType.Acquired, "Property acquired", $"Acquired on {date}."),
            (PropertyStamp.Acquired, false) => (PropertyEventType.AcquisitionDateCleared, "Acquired date cleared", $"Cleared on {date}."),
            (PropertyStamp.Disposed, true) => (PropertyEventType.Disposed, "Property disposed of", $"Disposed of on {date}."),
            _ => (PropertyEventType.DisposalReversed, "Disposal reversed", $"Reversed on {date}."),
        };

        return new PropertyTransitionDescriptor(
            type,
            ContractEventCatalogue.Bound(title, ContractEventCatalogue.MaxTitleLength),
            ContractEventCatalogue.Bound(description, ContractEventCatalogue.MaxDescriptionLength),
            occurredAt);
    }

    /// <summary>
    /// Compares the three lifecycle fields as they stood against the three as the write leaves them and
    /// emits one descriptor per changed field — at most three (§8.5). A create compares against an
    /// all-null "before".
    /// </summary>
    /// <remarks>
    /// It fires on the <b>change</b>, never the value: an unchanged field and a non-null → different
    /// non-null re-date both write nothing. <c>Archived</c> is a server stamp and carries its own value;
    /// the two caller-supplied dates are clamped to <c>min(date, now)</c>, since a future date would
    /// otherwise produce an event the log's own future bound refuses; every clear takes the server clock.
    /// </remarks>
    public static IReadOnlyList<PropertyTransitionDescriptor> Detect(
        PropertyStamps before, PropertyStamps after, DateTime nowUtc)
    {
        var descriptors = new List<PropertyTransitionDescriptor>(3);

        Compare(PropertyStamp.Archived, before.Archived, after.Archived);
        Compare(PropertyStamp.Acquired, before.Acquired, after.Acquired);
        Compare(PropertyStamp.Disposed, before.Disposed, after.Disposed);

        return descriptors;

        void Compare(PropertyStamp stamp, DateTime? was, DateTime? now)
        {
            if (was is null && now is { } set)
            {
                var occurredAt = stamp == PropertyStamp.Archived || set < nowUtc ? set : nowUtc;
                descriptors.Add(Stamp(stamp, set: true, occurredAt));
            }
            else if (was is not null && now is null)
            {
                descriptors.Add(Stamp(stamp, set: false, nowUtc));
            }
        }
    }
}

/// <summary>A property's three lifecycle fields at one moment, for <see cref="PropertyEventCatalogue.Detect"/>.</summary>
public readonly record struct PropertyStamps(DateTime? Archived, DateTime? Acquired, DateTime? Disposed)
{
    /// <summary>The "before" of a create.</summary>
    public static readonly PropertyStamps None = new(null, null, null);

    public static PropertyStamps Of(Property property) =>
        new(property.Archived, property.AcquiredDate, property.DisposedDate);
}

/// <summary>
/// The one place that turns a described property transition into a <see cref="PropertyEvent"/> row
/// (issue #209 §3.1) — the <see cref="ContractEventRecorder"/> contract.
/// </summary>
/// <remarks>
/// <b>Static and parameterised, and it stages only.</b> It writes onto the <em>caller's</em>
/// <see cref="OdysseyContext"/> and never saves, so the row rides the caller's own
/// <c>SaveChangesAsync</c>: the log can neither miss a transition that happened nor claim one that did
/// not, and a failure after staging discards both.
/// </remarks>
public static class PropertyEventRecorder
{
    /// <summary>Stages one system event. Nothing is written until the caller saves.</summary>
    /// <param name="userId">The acting user, stored through the <c>SET NULL</c> attribution column.</param>
    /// <param name="nowUtc">The caller's own clock reading, stamped onto <c>CreatedAtUtc</c>.</param>
    public static PropertyEvent Stage(
        OdysseyContext context,
        Property property,
        PropertyTransitionDescriptor descriptor,
        string? userId,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(descriptor);

        var entity = new PropertyEvent
        {
            // Through the navigation as well as the key: on a create the property's id is not assigned
            // until the save, and the navigation is what lets EF fix the key up in the same save.
            Property = property,
            PropertyId = property.PropertyId,
            Type = descriptor.Type,
            Source = ContractEventSource.System,
            Title = descriptor.Title,
            Description = descriptor.Description,
            Notes = null,
            OccurredAt = descriptor.OccurredAt,
            CreatedByUserId = string.IsNullOrWhiteSpace(userId) ? null : userId,
            CreatedAtUtc = nowUtc,
        };

        context.PropertyEvents.Add(entity);
        return entity;
    }

    /// <summary>Stages a whole run of descriptors — the shape <see cref="PropertyEventCatalogue.Detect"/> produces.</summary>
    public static void StageAll(
        OdysseyContext context,
        Property property,
        IEnumerable<PropertyTransitionDescriptor> descriptors,
        string? userId,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(descriptors);

        foreach (var descriptor in descriptors)
        {
            Stage(context, property, descriptor, userId, nowUtc);
        }
    }
}
