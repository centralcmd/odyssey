namespace Odyssey.Dtos.Finance;

/// <summary>
/// Whether a policy link's target can be named on the read path (issue #27 §9).
///
/// <para>
/// A link whose target is archived or no longer resolves keeps its row and <b>loses its name</b>: the
/// id survives a read-modify-write round trip — so an ordinary save can never silently delete it —
/// while the name, which is the personal data, never enters the insurance read model.
/// </para>
/// </summary>
public enum LinkAvailability
{
    /// <summary>The target exists and is not archived; <c>Name</c> is populated.</summary>
    Available = 0,

    /// <summary>The target exists but is archived; <c>Name</c> is null, the type is real.</summary>
    Archived = 1,

    /// <summary>The target no longer resolves at all; both <c>Name</c> and the type are null.</summary>
    Unresolvable = 2,
}
