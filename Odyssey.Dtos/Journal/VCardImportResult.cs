namespace Odyssey.Dtos.Journal;

/// <summary>
/// Outcome of a vCard import (issue #338). Created/updated contacts succeed and are reported as
/// bare counts; skipped entries are grouped by reason so the client can render a bounded, readable
/// summary — mirrors <c>IcsImportResult</c> (issue #330).
/// </summary>
public sealed record VCardImportResult
{
    public int CreatedCount { get; set; }

    public int UpdatedCount { get; set; }

    /// <summary>Addresses linked to a place, found or created (issue #380 §8.4).</summary>
    public int AddressesLinked { get; set; }

    /// <summary>
    /// Places this import created. <see langword="null"/> for a caller without <c>locations.read</c>:
    /// <see cref="AddressesLinked"/> minus this is the number of addresses that matched an existing place,
    /// which only a <c>locations.read</c> holder may learn (v8-L3).
    /// </summary>
    public int? PlacesCreated { get; set; }

    /// <summary>
    /// <c>ADR</c> properties skipped because the importer lacks <c>locations.create</c> (v8-L4). A count,
    /// never a list: the contact itself is still imported, and a list would echo names.
    /// </summary>
    public int SkippedAddressCount { get; set; }

    public IReadOnlyList<VCardImportSkipGroup> Skipped { get; set; } = [];
}

public sealed record VCardImportSkipGroup
{
    public required string Reason { get; set; }

    public int Count { get; set; }

    // Sample display names for this reason, capped at 100 regardless of file size.
    public IReadOnlyList<string> SampleNames { get; set; } = [];
}
