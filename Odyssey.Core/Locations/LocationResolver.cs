using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Odyssey.Context;
using Odyssey.Core.Journal;
using Odyssey.Core.Locations.Conversion;
using Odyssey.Core.Pagination;
using Odyssey.Dtos.Journal;
using LocationEntity = Odyssey.Context.Location;

namespace Odyssey.Core.Locations;

/// <summary>
/// The one service every write path calls to link a record to a place (issue #380 §3, §8.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>By id</b> (<see cref="LinkByIdAsync"/>): the place must exist, and the caller must hold
/// <c>locations.read</c> <b>when the link changes</b> — re-sending the stored id needs no claim (v7, F3). A
/// <i>new</i> link to an archived place is refused; an unchanged one is kept. A contact or property link
/// also needs <c>Line1</c>, <c>City</c> and <c>CountryCode</c> on the place.
/// </para>
/// <para>
/// <b>By fields</b> (<see cref="FindOrCreateAsync"/>): <c>locations.create</c> is checked <b>before</b> the
/// lookup, so a match and a miss look the same to a caller without it. The fields are cleaned and keyed;
/// a match is followed through <c>MergedIntoLocationId</c> to its survivor and linked even when archived,
/// and stays archived; a miss creates a place, staged on the caller's context so it commits with the link.
/// </para>
/// <para>
/// <b>Saving</b> goes through <see cref="SaveChangesAsync"/>: a <c>1062</c> on <c>AddressKey</c> (a concurrent
/// create of the same address) re-reads the winner and relinks to it; a <c>1062</c> on <c>Name</c>
/// re-allocates the suffix once, then is a <c>409</c>; a <c>1452</c> (the place was deleted under the link)
/// is a <c>422</c> keyed <c>locationId</c>.
/// </para>
/// <para>
/// <b>Logs ids and counts only</b> (§7.1.11) — never a name, an address or a detail.
/// </para>
/// </remarks>
public sealed class LocationResolver
{
    public const string LocationIdField = "locationId";

    /// <summary>Room left for a <c> (n)</c> suffix when matching taken names by prefix.</summary>
    private const int SuffixRoom = 16;

    private readonly OdysseyContext context;
    private readonly TimeProvider timeProvider;
    private readonly ILogger logger;
    private readonly List<LocationEntity> created = [];
    private readonly List<LocationEntity> editedInPlace = [];

    public LocationResolver(OdysseyContext context, TimeProvider? timeProvider = null, ILogger? logger = null)
    {
        this.context = context;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.logger = logger ?? NullLogger.Instance;
        Guard = new LocationReferenceGuard(context);
    }

    public LocationReferenceGuard Guard { get; }

    /// <summary>The places this resolver created and has not yet seen saved.</summary>
    public int PendingCreates => created.Count;

    public sealed record FindOrCreateResult(LocationEntity Location, bool Created);

    // ── By id ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The tracked place for a link by id. <paramref name="currentId"/> is the record's stored link: the same
    /// id is kept with no claim and no archived check. <paramref name="requireAddress"/> applies the
    /// contact/property completeness rule (A-R3) to a <i>new</i> link.
    /// </summary>
    public async Task<LocationEntity> LinkByIdAsync(
        Guid id, Guid? currentId, bool requireAddress, LocationClaims claims, CancellationToken cancellationToken)
    {
        if (id == currentId)
        {
            return await context.Locations.FirstOrDefaultAsync(l => l.LocationId == id, cancellationToken)
                ?? throw UnknownLocation(id);
        }

        RequireRead(claims);
        var location = await context.Locations.FirstOrDefaultAsync(l => l.LocationId == id, cancellationToken)
            ?? throw UnknownLocation(id);

        if (location.Archived is not null)
        {
            throw new DomainUnprocessableException(
                $"Location ID {id} is archived. Restore it, or choose another place.", LocationIdField);
        }

        if (requireAddress && LocationAddress.FromLocation(location).FirstMissingRequiredField is not null)
        {
            throw new DomainUnprocessableException(
                $"Location ID {id} has no complete street address (line 1, city and country), so it cannot be an address.",
                LocationIdField);
        }

        return location;
    }

    /// <summary>
    /// The <c>LocationId</c> an event, series or entry write stores (§5.2): by id only. An unchanged id is
    /// kept without a claim; any change — a new id, or clearing one — needs <c>locations.read</c>.
    /// </summary>
    public async Task<Guid?> ResolveRecordLinkAsync(
        Guid? requested, Guid? current, LocationClaims claims, CancellationToken cancellationToken)
    {
        if (requested == current)
        {
            return current;
        }

        if (requested is not { } id)
        {
            RequireRead(claims);
            return null;
        }

        return (await LinkByIdAsync(id, current, requireAddress: false, claims, cancellationToken)).LocationId;
    }

    // ── By fields ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Finds the place holding <paramref name="address"/> or stages a new one. <c>locations.create</c> is
    /// checked first, whatever the lookup would find (§7.1.4). A keyless (partial) address always creates.
    /// <paramref name="geocode"/> (§5.3a, v9) is applied <b>only to a place this call creates</b>, whose source
    /// then becomes the geocode's; a match — a merge forwarder's survivor included — is linked unchanged, so
    /// a geocode never overwrites a place someone else curated.
    /// </summary>
    public async Task<FindOrCreateResult> FindOrCreateAsync(
        LocationAddress address, LocationSource source, string? userId, LocationClaims claims,
        CancellationToken cancellationToken, LocationGeocode? geocode = null)
    {
        RequireCreate(claims);

        if (address.Key is { } key && await FindByKeyAsync(key, excludeId: null, cancellationToken) is { } match)
        {
            return new FindOrCreateResult(match, false);
        }

        var composed = address.Composed ?? throw new InvalidOperationException("An empty address has no location.");
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var location = new LocationEntity
        {
            LocationId = Guid.NewGuid(),
            Name = await AllocateNameAsync(composed, excludeId: null, cancellationToken),
            AddressText = LocationNaming.Truncate(composed, LocationRules.AddressTextMaxLength),
            Source = geocode?.Source ?? source,
            SourceReference = geocode?.SourceReference,
            Latitude = geocode?.Latitude,
            Longitude = geocode?.Longitude,
            TimeZoneId = geocode?.TimeZoneId,
            CreatedByUserId = userId,
            UpdatedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Apply(location, address);
        context.Locations.Add(location);
        created.Add(location);
        return new FindOrCreateResult(location, true);
    }

    /// <summary>
    /// The place a free-text value names, for ICS import (§8.4): its <c>Name</c> exactly on letters, else its
    /// <c>AddressText</c>, archived included, following a forwarder — or a new <see cref="LocationSource.Imported"/>
    /// place named by the value. The caller has checked both claims and that the value is not link-like.
    /// <paramref name="index"/> is loaded once per import with <see cref="LoadIndexAsync"/>.
    /// </summary>
    public (Guid LocationId, bool Created) FindOrCreateByText(LocationIndex index, string value, string? userId)
    {
        if (index.FindByText(value) is { } id)
        {
            return (id, false);
        }

        var name = index.AllocateName(value);
        var addressText = string.Equals(name, value, StringComparison.Ordinal)
            ? null
            : LocationNaming.Truncate(value, LocationRules.AddressTextMaxLength);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var location = new LocationEntity
        {
            LocationId = Guid.NewGuid(),
            Name = name,
            AddressText = addressText,
            Source = LocationSource.Imported,
            CreatedByUserId = userId,
            UpdatedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.Locations.Add(location);
        created.Add(location);
        index.Add(location.LocationId, location.Name, location.AddressText, addressKey: null);
        index.Commit();
        return (location.LocationId, true);
    }

    /// <summary>Every place's name, address text, key and forwarder, for <see cref="FindOrCreateByText"/>.</summary>
    public async Task<LocationIndex> LoadIndexAsync(CancellationToken cancellationToken)
    {
        var index = new LocationIndex();
        var rows = await context.Locations.AsNoTracking()
            .Select(l => new { l.LocationId, l.Name, l.AddressText, l.AddressKey, l.MergedIntoLocationId })
            .ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            index.Add(row.LocationId, row.Name, row.AddressText, row.AddressKey, row.MergedIntoLocationId);
        }

        index.Commit();
        return index;
    }

    // ── In place (AllUses) ───────────────────────────────────────────────────────────────────────

    /// <summary>An <c>AllUses</c> edit of a linked place's structured fields (§5.3, v6–v8).</summary>
    /// <param name="LocationId">The place the record is linked to now.</param>
    /// <param name="Fields">The submitted fields, cleaned.</param>
    /// <param name="PropertyFields">
    /// A property writes only <c>Line1</c>, <c>PostalCode</c>, <c>City</c> and <c>CountryCode</c>; the place's
    /// <c>Line2</c> and <c>Region</c> are kept, and the same-key and duplicate tests use that combination (V7-3).
    /// </param>
    /// <param name="ExpectedOtherUses">The <c>otherUses</c> the client showed; required.</param>
    /// <param name="Self">The kind of the record making the edit, excluded once from the recount.</param>
    public sealed record InPlaceEdit(
        Guid LocationId, LocationAddress Fields, bool PropertyFields, int? ExpectedOtherUses,
        LocationReferenceGuard.Kind Self);

    /// <summary>
    /// The address the edit would leave on <paramref name="location"/>: all six submitted fields, or for a
    /// property the four submitted plus the place's own <c>Line2</c>/<c>Region</c>.
    /// </summary>
    public static LocationAddress Resulting(LocationEntity location, LocationAddress fields, bool propertyFields) =>
        propertyFields
            ? LocationAddress.Of(fields.Line1, location.Line2, fields.PostalCode, fields.City, location.Region, fields.CountryCode)
            : fields;

    /// <summary>
    /// Edits the linked place in place. Must run inside the caller's transaction (see
    /// <see cref="LocationTransactions"/>), which then saves the edit with the record in one commit. Takes
    /// <c>FOR UPDATE</c> on the place, re-reads it, refuses a merged-away place (<c>422</c> keyed
    /// <c>locationId</c>), recounts the other uses (<c>409</c> keyed <c>applyTo</c> on a mismatch), and then
    /// applies the completeness (<c>422</c>) and duplicate (<c>409</c> keyed <c>line1</c>) rules. Coordinates,
    /// time zone, altitude, description, source and a typed name are never touched; an auto-composed
    /// <c>Name</c> or <c>AddressText</c> is recomposed (decision 8, V6-1, D2).
    /// </summary>
    public async Task<LocationEntity> EditInPlaceAsync(
        InPlaceEdit edit, LocationClaims claims, string? userId, CancellationToken cancellationToken)
    {
        if (!claims.LocationsUpdate)
        {
            throw new DomainForbiddenException("Changing this address for every use needs permission to update places.");
        }

        if (edit.ExpectedOtherUses is not { } expected)
        {
            throw new DomainUnprocessableException(
                "Say how many other uses of this place you reviewed before changing it for every use.", "expectedOtherUses");
        }

        await LocationTransactions.LockAsync(context, [edit.LocationId], cancellationToken);
        var location = await context.Locations.FirstOrDefaultAsync(l => l.LocationId == edit.LocationId, cancellationToken)
            ?? throw UnknownLocation(edit.LocationId);
        if (context.Database.IsRelational())
        {
            await context.Entry(location).ReloadAsync(cancellationToken);
        }

        if (location.MergedIntoLocationId is not null)
        {
            throw new DomainUnprocessableException(
                $"Location ID {edit.LocationId} was merged into another place. Review the address again.", LocationIdField);
        }

        var counts = (await Guard.CountAsync([edit.LocationId], cancellationToken))[edit.LocationId];
        if (LocationReferenceGuard.OtherUses(counts, edit.Self, claims) != expected)
        {
            throw new DomainConflictException(
                "The place's uses changed while you were editing — review again.", "applyTo");
        }

        var target = Resulting(location, edit.Fields, edit.PropertyFields);
        await EnsureAddressWriteAllowedAsync(location.LocationId, target, cancellationToken);
        await ApplyEditAsync(location, target, recompose: true, userId, cancellationToken);

        logger.LogInformation(
            "Location {LocationId} address edited in place for every use by {ActorUserId}; {OtherUses} other use(s).",
            location.LocationId, userId, expected);
        return location;
    }

    /// <summary>
    /// The two rules every in-place address write obeys — this one and <c>PUT /api/locations/{id}</c>
    /// (§5.3, §5.5): a place a contact address uses must keep <c>Line1</c>, <c>City</c> and
    /// <c>CountryCode</c> (<c>422</c> keyed on the first one missing), and the new key may not be another
    /// place's, a merge forwarder's included (<c>409</c> keyed <c>line1</c>). The place itself is excluded.
    /// </summary>
    public async Task EnsureAddressWriteAllowedAsync(Guid locationId, LocationAddress target, CancellationToken cancellationToken)
    {
        if (target.FirstMissingRequiredField is { } missing
            && await context.Addresses.AnyAsync(a => a.LocationId == locationId, cancellationToken))
        {
            throw new DomainUnprocessableException(
                "A contact uses this place as an address, so it needs line 1, a city and a country.", missing);
        }

        if (target.Key is { } key
            && await context.Locations.AnyAsync(l => l.AddressKey == key && l.LocationId != locationId, cancellationToken))
        {
            throw DuplicateAddress();
        }
    }

    /// <summary>
    /// Writes <paramref name="target"/>'s six fields and key onto <paramref name="location"/>. With
    /// <paramref name="recompose"/>, a <c>Name</c> equal to the old fields' auto-composition (suffixed or not,
    /// after the composer's truncation) is recomposed with a free suffix, and an <c>AddressText</c> equal to
    /// it follows; text a person typed is left alone.
    /// </summary>
    public async Task ApplyEditAsync(
        LocationEntity location, LocationAddress target, bool recompose, string? userId, CancellationToken cancellationToken)
    {
        var oldComposed = LocationAddress.FromLocation(location).Composed;
        var newComposed = target.Composed;

        if (recompose && oldComposed is not null && newComposed is not null)
        {
            if (IsAutoComposedName(location.Name, oldComposed))
            {
                location.Name = await AllocateNameAsync(newComposed, location.LocationId, cancellationToken);
            }

            if (location.AddressText is { } text
                && string.Equals(text, LocationNaming.Truncate(oldComposed, LocationRules.AddressTextMaxLength), StringComparison.Ordinal))
            {
                location.AddressText = LocationNaming.Truncate(newComposed, LocationRules.AddressTextMaxLength);
            }
        }

        Apply(location, target);
        location.UpdatedByUserId = userId;
        location.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        if (!editedInPlace.Contains(location))
        {
            editedInPlace.Add(location);
        }
    }

    /// <summary>Whether <paramref name="name"/> is <paramref name="composed"/> as the composer would have named it.</summary>
    public static bool IsAutoComposedName(string name, string composed)
    {
        if (string.Equals(name, LocationNaming.Candidate(composed, 1), StringComparison.Ordinal))
        {
            return true;
        }

        var open = name.LastIndexOf(" (", StringComparison.Ordinal);
        if (open < 0 || !name.EndsWith(')')
            || !int.TryParse(name.AsSpan(open + 2, name.Length - open - 3), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var ordinal)
            || ordinal < 2)
        {
            return false;
        }

        return string.Equals(name, LocationNaming.Candidate(composed, ordinal), StringComparison.Ordinal);
    }

    // ── Shared ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The first free name for <paramref name="baseName"/> (truncated, then suffixed <c> (2)</c>, <c> (3)</c>…),
    /// from one query over the names sharing its prefix — the <c>_ci</c> collation decides "taken", as the
    /// unique index does. <paramref name="excludeId"/> is a place being renamed.
    /// </summary>
    public async Task<string> AllocateNameAsync(string baseName, Guid? excludeId, CancellationToken cancellationToken)
    {
        var prefix = LocationNaming.Truncate(baseName, LocationRules.NameMaxLength - SuffixRoom);
        var pattern = ListQuery.EscapeLike(prefix) + "%";
        var names = await context.Locations.AsNoTracking()
            .Where(l => (excludeId == null || l.LocationId != excludeId) && EF.Functions.Like(l.Name, pattern))
            .Select(l => l.Name)
            .ToListAsync(cancellationToken);

        var taken = new HashSet<string>(names, LocationNaming.CollationComparer);
        taken.UnionWith(context.Locations.Local.Where(l => l.LocationId != excludeId).Select(l => l.Name));
        return LocationNaming.Allocate(baseName, taken.Contains);
    }

    /// <summary>
    /// Saves the caller's context with the §8.1 recovery: a <c>1062</c> on <c>AddressKey</c> relinks to the
    /// winner (or, for an in-place edit, is a <c>409</c> keyed <c>line1</c>); a <c>1062</c> on <c>Name</c>
    /// re-allocates once, then is a <c>409</c>; a <c>1452</c> naming <c>Locations</c> is a <c>422</c> keyed
    /// <c>locationId</c>. Any other failure is the caller's to map.
    /// </summary>
    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        var nameRetried = false;
        while (true)
        {
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                LogCreated();
                created.Clear();
                editedInPlace.Clear();
                return;
            }
            catch (DbUpdateException ex) when (IsMissingLocation(ex))
            {
                throw new DomainUnprocessableException("That place no longer exists. Choose another.", LocationIdField);
            }
            catch (DbUpdateException ex) when (DbErrors.IsDuplicateKey(ex) && IsLocationIndex(ex, "AddressKey"))
            {
                if (!await RelinkToWinnersAsync(cancellationToken))
                {
                    throw DuplicateAddress();
                }
            }
            catch (DbUpdateException ex) when (DbErrors.IsDuplicateKey(ex) && IsLocationIndex(ex, "Name")
                && (created.Count > 0 || editedInPlace.Count > 0))
            {
                if (nameRetried)
                {
                    throw new DomainConflictException(
                        "Another place with this name was saved at the same moment. Try again.", "line1");
                }

                nameRetried = true;
                await ReallocateNamesAsync(cancellationToken);
            }
        }
    }

    public static bool IsAddressComplete(LocationEntity location) =>
        LocationAddress.FromLocation(location).FirstMissingRequiredField is null;

    public static void RequireRead(LocationClaims claims)
    {
        if (!claims.LocationsRead)
        {
            throw new DomainForbiddenException("Choosing a place needs permission to read places.");
        }
    }

    public static void RequireCreate(LocationClaims claims)
    {
        if (!claims.LocationsCreate)
        {
            throw new DomainForbiddenException("Entering an address needs permission to create places.");
        }
    }

    public static DomainConflictException DuplicateAddress() =>
        new("Another place already has this address. Merge the two places, or link to that one.", "line1");

    private static DomainUnprocessableException UnknownLocation(Guid id) =>
        new($"Location ID {id} not found.", LocationIdField);

    private async Task<LocationEntity?> FindByKeyAsync(string key, Guid? excludeId, CancellationToken cancellationToken)
    {
        var match = context.Locations.Local.FirstOrDefault(l => l.AddressKey == key && l.LocationId != excludeId)
            ?? await context.Locations.FirstOrDefaultAsync(l => l.AddressKey == key && l.LocationId != excludeId, cancellationToken);
        if (match?.MergedIntoLocationId is not { } survivor)
        {
            return match;
        }

        // A merged-away place forwards to its survivor; chains are at most one hop (§5.6).
        return context.Locations.Local.FirstOrDefault(l => l.LocationId == survivor)
            ?? await context.Locations.FirstAsync(l => l.LocationId == survivor, cancellationToken);
    }

    private static void Apply(LocationEntity location, LocationAddress address)
    {
        location.Line1 = address.Line1;
        location.Line2 = address.Line2;
        location.PostalCode = address.PostalCode;
        location.City = address.City;
        location.Region = address.Region;
        location.CountryCode = address.CountryCode;
        location.AddressKey = address.Key;
    }

    /// <summary>
    /// A concurrent request created the same address first: drop each staged place whose key now exists and
    /// point everything staged at it to the winner instead. False when nothing could be relinked.
    /// </summary>
    private async Task<bool> RelinkToWinnersAsync(CancellationToken cancellationToken)
    {
        var relinked = false;
        foreach (var pending in created.Where(l => l.AddressKey is not null).ToList())
        {
            var winner = await context.Locations.AsNoTracking()
                .Where(l => l.AddressKey == pending.AddressKey && l.LocationId != pending.LocationId)
                .Select(l => new { l.LocationId, l.MergedIntoLocationId })
                .FirstOrDefaultAsync(cancellationToken);
            if (winner is null)
            {
                continue;
            }

            // Repoint first, detach after: detaching the loser while a required link (a contact address)
            // still points at it would sever that relationship.
            var survivorId = winner.MergedIntoLocationId ?? winner.LocationId;
            var survivor = await context.Locations.FirstAsync(l => l.LocationId == survivorId, cancellationToken);
            Repoint(pending, survivor);
            context.Entry(pending).State = EntityState.Detached;
            created.Remove(pending);
            relinked = true;
        }

        return relinked;
    }

    private void Repoint(LocationEntity from, LocationEntity to)
    {
        foreach (var entry in context.ChangeTracker.Entries().ToList())
        {
            if (entry.Metadata.FindProperty("LocationId") is { } property
                && entry.Entity is not LocationEntity
                && entry.Property(property.Name).CurrentValue is Guid value && value == from.LocationId)
            {
                entry.Property(property.Name).CurrentValue = to.LocationId;
            }

            foreach (var reference in entry.References)
            {
                if (ReferenceEquals(reference.CurrentValue, from))
                {
                    reference.CurrentValue = to;
                }
            }
        }
    }

    private async Task ReallocateNamesAsync(CancellationToken cancellationToken)
    {
        foreach (var location in created.Concat(editedInPlace))
        {
            var baseName = LocationAddress.FromLocation(location).Composed ?? location.AddressText ?? location.Name;
            location.Name = await AllocateNameAsync(baseName, location.LocationId, cancellationToken);
        }
    }

    private void LogCreated()
    {
        foreach (var location in created)
        {
            logger.LogInformation(
                "Location {LocationId} created from a record write ({Source}) by {ActorUserId}.",
                location.LocationId, location.Source, location.CreatedByUserId);
        }
    }

    private static bool IsLocationIndex(DbUpdateException exception, string column) =>
        DbErrors.IsDuplicateKeyOn(exception, $"IX_Locations_{column}");

    /// <summary>MariaDB 1452: a child row names a parent that does not exist — here, a deleted place.</summary>
    public static bool IsMissingLocation(DbUpdateException exception) =>
        exception.InnerException is MySqlException { Number: 1452 } mysql
        && mysql.Message.Contains("REFERENCES `Locations`", StringComparison.Ordinal);
}
