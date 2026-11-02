namespace Odyssey.Core.Locations;

/// <summary>
/// What the caller may do with locations and which record kinds it may read (issue #380 §3, §5.5, §7.1).
/// A service has no <c>ClaimsPrincipal</c>, so the controller passes this in and the location rules decide
/// against it: linking by id, find-or-create, the in-place <c>AllUses</c> edit, the embed, the per-kind
/// usage counts and the merge's per-class claims.
/// </summary>
/// <remarks>
/// <see cref="Trusted"/> is for direct, non-HTTP callers — the seeder, a test that exercises a domain rule
/// rather than a permission. Every controller passes the caller's own claims; the API tests pin the
/// <c>403</c>s that this depends on.
/// </remarks>
public sealed record LocationClaims
{
    public bool LocationsRead { get; init; }

    public bool LocationsCreate { get; init; }

    public bool LocationsUpdate { get; init; }

    public bool ContactsRead { get; init; }

    public bool PropertiesRead { get; init; }

    public bool CalendarRead { get; init; }

    public bool JournalRead { get; init; }

    // The merge's per-class claims (§5.6): one per reference class actually present.
    public bool ContactsUpdate { get; init; }

    public bool PropertiesUpdate { get; init; }

    public bool CalendarUpdate { get; init; }

    public bool JournalUpdate { get; init; }

    /// <summary>Every capability — for a trusted, non-HTTP caller only.</summary>
    public static LocationClaims Trusted { get; } = new()
    {
        LocationsRead = true,
        LocationsCreate = true,
        LocationsUpdate = true,
        ContactsRead = true,
        PropertiesRead = true,
        CalendarRead = true,
        JournalRead = true,
        ContactsUpdate = true,
        PropertiesUpdate = true,
        CalendarUpdate = true,
        JournalUpdate = true,
    };

    /// <summary>No capability at all.</summary>
    public static LocationClaims None { get; } = new();
}
