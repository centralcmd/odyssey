using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Odyssey.Core.Locations;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using LocationEntity = Odyssey.Context.Location;

namespace Odyssey.Core.Journal;

/// <summary>
/// The household's places (issue #350), since issue #380 the one store for every place and address in the
/// app: list, get, create, update, merge and delete.
/// </summary>
/// <remarks>
/// <para>
/// <b>Audit, never values (§7.7, S4; issue #380 §7.1.11).</b> Every write logs the location id, the acting
/// user, the operation and the NAMES of the fields that changed — never their values — so a household can
/// answer "who changed the cabin's address and when" without the log keeping the old address.
/// </para>
/// <para>
/// <b>Last writer wins</b> (Non-Goal 7): there is no concurrency token on a location. Merge and delete take
/// <c>FOR UPDATE</c> on the rows they decide about, inside the execution strategy.
/// </para>
/// </remarks>
public class LocationService
{
    private const string NameField = "name";
    private const string SourceField = "source";
    private const string LatitudeField = "latitude";

    private readonly OdysseyContext context;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<LocationService> logger;
    private readonly LocationReferenceGuard guard;

    public LocationService(OdysseyContext context, TimeProvider? timeProvider = null, ILogger<LocationService>? logger = null)
    {
        this.context = context;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.logger = logger ?? NullLogger<LocationService>.Instance;
        guard = new LocationReferenceGuard(context);
    }

    /// <param name="claims">Decides which <see cref="ExistingLocation.Usage"/> counts are filled; null is trusted.</param>
    public async Task<PagedResult<ExistingLocation>> ListAsync(
        LocationsQueryParams query,
        CancellationToken cancellationToken = default,
        LocationClaims? claims = null)
    {
        var q = context.Locations.AsNoTracking();

        q = query.Status switch
        {
            ArchivalStatus.Active => q.Where(l => l.Archived == null),
            ArchivalStatus.Archived => q.Where(l => l.Archived != null),
            _ => q,
        };

        if (query.HasCoordinates is { } hasCoordinates)
        {
            q = hasCoordinates ? q.Where(l => l.Latitude != null) : q.Where(l => l.Latitude == null);
        }

        if (query.Referenced is { } referenced)
        {
            q = guard.WhereReferenced(q, referenced, await guard.NonRecordReferencesAsync(cancellationToken));
        }

        var term = ListQuery.NormalizeSearch(query.Search);
        if (term is not null)
        {
            var pattern = ListQuery.ContainsPattern(term);
            q = q.Where(l =>
                EF.Functions.Like(l.Name, pattern)
                || (l.Description != null && EF.Functions.Like(l.Description, pattern))
                || (l.AddressText != null && EF.Functions.Like(l.AddressText, pattern))
                || (l.Line1 != null && EF.Functions.Like(l.Line1, pattern))
                || (l.City != null && EF.Functions.Like(l.City, pattern)));
        }

        var ascending = ListQuery.Ascending(query.SortDir, naturalDefaultAscending: query.SortBy is null or LocationSortBy.Name);
        IOrderedQueryable<LocationEntity> sorted = query.SortBy switch
        {
            LocationSortBy.CreatedAt => ascending ? q.OrderBy(l => l.CreatedAt) : q.OrderByDescending(l => l.CreatedAt),
            LocationSortBy.UpdatedAt => ascending ? q.OrderBy(l => l.UpdatedAt) : q.OrderByDescending(l => l.UpdatedAt),
            _ => ascending ? q.OrderBy(l => l.Name) : q.OrderByDescending(l => l.Name),
        };
        q = sorted.ThenBy(l => l.LocationId);

        var page = await q.ToPagedResultAsync(query.Offset, query.Limit, ToDto, cancellationToken);
        await FillUsageAsync(page.Items, claims ?? LocationClaims.Trusted, cancellationToken);
        return page;
    }

    public async Task<ExistingLocation?> Get(Guid id, CancellationToken cancellationToken = default, LocationClaims? claims = null)
    {
        var location = await context.Locations.AsNoTracking()
            .FirstOrDefaultAsync(l => l.LocationId == id, cancellationToken);
        if (location is null)
        {
            return null;
        }

        var dto = ToDto(location);
        await FillUsageAsync([dto], claims ?? LocationClaims.Trusted, cancellationToken);
        return dto;
    }

    public async Task<ExistingLocation> Create(NewLocation request, string userId, CancellationToken cancellationToken = default)
    {
        var source = request.Source ?? throw new DomainValidationException("A source is required.", code: null, field: SourceField);
        if (!LocationRules.IsAssertable(source))
        {
            throw new DomainUnprocessableException(
                "A place you create is entered by hand or found by a search; the other sources are set by Odyssey.", SourceField);
        }

        var fields = Normalize(request.Name, request.Description, request.AddressText, request.Line1, request.Line2,
            request.PostalCode, request.City, request.Region, request.CountryCode, request.Latitude, request.Longitude,
            request.AltitudeMeters, request.TimeZoneId, source, request.SourceReference);
        await EnsureNameAvailable(fields.Name, null, cancellationToken);
        if (fields.Address.Key is { } key && await context.Locations.AnyAsync(l => l.AddressKey == key, cancellationToken))
        {
            throw LocationResolver.DuplicateAddress();
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var location = new LocationEntity
        {
            Name = fields.Name,
            CreatedByUserId = userId,
            UpdatedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        fields.ApplyTo(location);

        context.Locations.Add(location);
        await SaveAsync(fields.Name, null, cancellationToken);

        logger.LogInformation(
            "Location {LocationId} created by {ActorUserId}.", location.LocationId, userId);
        return ToDto(location);
    }

    /// <summary>
    /// Full replacement (issue #350 §5; issue #380 §5.5). A merged-away place is frozen (<c>422</c> keyed
    /// <c>id</c>); the source may be kept or changed to one a caller may assert (<c>422</c> keyed
    /// <c>source</c>); coordinates are all-or-none and cannot be cleared while weather uses them (<c>422</c>
    /// keyed <c>latitude</c>); an address another place holds is a <c>409</c> keyed <c>line1</c>; and a
    /// place a contact address uses keeps a complete street address.
    /// </summary>
    public async Task<ExistingLocation?> Update(
        Guid id, UpdateLocation request, string userId, CancellationToken cancellationToken = default, LocationClaims? claims = null)
    {
        var location = await context.Locations.FirstOrDefaultAsync(l => l.LocationId == id, cancellationToken);
        if (location is null)
        {
            return null;
        }

        if (location.MergedIntoLocationId is not null)
        {
            throw new DomainUnprocessableException(
                $"Location ID {id} was merged into another place. It can only be deleted.", "id");
        }

        var source = request.Source ?? throw new DomainValidationException("A source is required.", code: null, field: SourceField);
        if (source != location.Source && !LocationRules.IsAssertable(source))
        {
            throw new DomainUnprocessableException(
                "Keep the place's source, or set it to one a person or a search provides.", SourceField);
        }

        var fields = Normalize(request.Name, request.Description, request.AddressText, request.Line1, request.Line2,
            request.PostalCode, request.City, request.Region, request.CountryCode, request.Latitude, request.Longitude,
            request.AltitudeMeters, request.TimeZoneId, source, request.SourceReference);
        await EnsureNameAvailable(fields.Name, id, cancellationToken);

        if (fields.Latitude is null && location.Latitude is not null
            && await context.WeatherLocations.AnyAsync(w => w.LocationId == id, cancellationToken))
        {
            throw new DomainUnprocessableException(
                "Weather uses this place's coordinates. Turn weather off and remove it before clearing them.", LatitudeField);
        }

        var addressChanged = LocationAddress.FromLocation(location) != fields.Address;
        if (addressChanged)
        {
            var resolver = new LocationResolver(context, timeProvider, logger);
            await resolver.EnsureAddressWriteAllowedAsync(id, fields.Address, cancellationToken);
        }

        var changed = fields.ChangedFields(location);
        var wasArchived = location.Archived is not null;
        var now = timeProvider.GetUtcNow().UtcDateTime;

        location.Name = fields.Name;
        fields.ApplyTo(location);
        if (request.Archived != wasArchived)
        {
            location.Archived = request.Archived ? now : null;
        }

        if (changed.Count > 0 || request.Archived != wasArchived)
        {
            location.UpdatedByUserId = userId;
            location.UpdatedAt = now;
        }

        await SaveAsync(fields.Name, id, cancellationToken);

        var operation = request.Archived == wasArchived ? "updated" : request.Archived ? "archived" : "restored";
        logger.LogInformation(
            "Location {LocationId} {Operation} by {ActorUserId}; changed: [{ChangedFields}].",
            location.LocationId, operation, userId, string.Join(", ", changed));
        var dto = ToDto(location);
        await FillUsageAsync([dto], claims ?? LocationClaims.Trusted, cancellationToken);
        return dto;
    }

    /// <summary>
    /// Merges <paramref name="sourceId"/> into <paramref name="targetId"/> (issue #380 §5.6): every reference
    /// is repointed, a contact listing both keeps one row with <c>IsPrimary</c> surviving, forwarders to the
    /// source are collapsed onto the target, and the source is archived and forwards to the target. The
    /// target's own fields are never changed. Null when either place is unknown.
    /// </summary>
    /// <remarks>
    /// One transaction, <c>REPEATABLE READ</c>, its first statement <c>FOR UPDATE</c> on both rows in
    /// ascending id order — so opposing merges cannot deadlock and the per-class claim check reads the same
    /// snapshot the repointing then changes (the <c>DomainForbiddenException</c> shape). A missing class claim
    /// is a <c>403</c> whose message does not name the class.
    /// </remarks>
    public async Task<ExistingLocation?> MergeAsync(
        Guid sourceId, Guid targetId, LocationClaims claims, string? userId, CancellationToken cancellationToken = default)
    {
        LocationEntity? result = null;
        var repointed = 0;

        await LocationTransactions.ExecuteAsync(context, async () =>
        {
            result = null;
            repointed = 0;
            await LocationTransactions.LockAsync(context, [sourceId, targetId], cancellationToken);

            var source = await context.Locations.FirstOrDefaultAsync(l => l.LocationId == sourceId, cancellationToken);
            var target = await context.Locations.FirstOrDefaultAsync(l => l.LocationId == targetId, cancellationToken);
            if (source is null || target is null)
            {
                return;
            }

            if (sourceId == targetId)
            {
                throw new DomainUnprocessableException("A place cannot be merged into itself.", "targetLocationId");
            }

            if (target.Archived is not null || target.MergedIntoLocationId is not null)
            {
                throw new DomainUnprocessableException(
                    "Merge into a place that is in use: the target is archived or was merged away.", "targetLocationId");
            }

            if (source.Archived is not null)
            {
                throw new DomainUnprocessableException("Restore this place before merging it.", "sourceId");
            }

            if (await context.WeatherLocations.AnyAsync(w => w.LocationId == sourceId, cancellationToken))
            {
                throw new DomainUnprocessableException(
                    "Weather is set up for this place. Remove its weather before merging it away.", "sourceId");
            }

            var events = await context.CalendarEvents.Where(e => e.LocationId == sourceId).ToListAsync(cancellationToken);
            var series = await context.RecurrencePatterns.Where(p => p.LocationId == sourceId).ToListAsync(cancellationToken);
            var entries = await context.JournalEntries.Where(e => e.LocationId == sourceId).ToListAsync(cancellationToken);
            var addresses = await context.Addresses.Where(a => a.LocationId == sourceId).ToListAsync(cancellationToken);
            var properties = await context.RealEstateDetails.Where(d => d.LocationId == sourceId).ToListAsync(cancellationToken);

            if (((events.Count > 0 || series.Count > 0) && !claims.CalendarUpdate)
                || (entries.Count > 0 && !claims.JournalUpdate)
                || (addresses.Count > 0 && !claims.ContactsUpdate)
                || (properties.Count > 0 && !claims.PropertiesUpdate))
            {
                throw new DomainForbiddenException("You do not have permission to merge these places.");
            }

            events.ForEach(e => e.LocationId = targetId);
            series.ForEach(p => p.LocationId = targetId);
            entries.ForEach(e => e.LocationId = targetId);
            properties.ForEach(d => d.LocationId = targetId);

            var contactIds = addresses.Select(a => a.ContactId).ToList();
            var alreadyAtTarget = await context.Addresses
                .Where(a => a.LocationId == targetId && contactIds.Contains(a.ContactId))
                .ToDictionaryAsync(a => a.ContactId, cancellationToken);
            foreach (var address in addresses)
            {
                if (alreadyAtTarget.TryGetValue(address.ContactId, out var kept))
                {
                    kept.IsPrimary |= address.IsPrimary;
                    context.Addresses.Remove(address);
                }
                else
                {
                    address.LocationId = targetId;
                }
            }

            // Chains are at most one hop: whatever forwarded to the source now forwards to the target.
            var forwarders = await context.Locations.Where(l => l.MergedIntoLocationId == sourceId).ToListAsync(cancellationToken);
            forwarders.ForEach(f => f.MergedIntoLocationId = targetId);

            var now = timeProvider.GetUtcNow().UtcDateTime;
            source.Archived = now;
            source.MergedIntoLocationId = targetId;
            source.UpdatedByUserId = userId;
            source.UpdatedAt = now;

            await context.SaveChangesAsync(cancellationToken);
            repointed = events.Count + series.Count + entries.Count + addresses.Count + properties.Count + forwarders.Count;
            result = target;
        }, IsolationLevel.RepeatableRead, cancellationToken);

        if (result is null)
        {
            return null;
        }

        logger.LogInformation(
            "Location {SourceLocationId} merged into {TargetLocationId} by {ActorUserId}; {ReferenceCount} reference(s) repointed.",
            sourceId, targetId, userId, repointed);
        var dto = ToDto(result);
        await FillUsageAsync([dto], claims, cancellationToken);
        return dto;
    }

    /// <summary>
    /// Deletes an unused place outright — the erasure path (issue #380 §5.7). <c>FOR UPDATE</c> on the row,
    /// then the reference guard: any reference, a merge forwarder included, is a <c>409</c> whose body does
    /// not say what uses it. False when unknown.
    /// </summary>
    public async Task<bool> DeleteAsync(Guid id, string? userId, CancellationToken cancellationToken = default)
    {
        var found = false;
        await LocationTransactions.ExecuteAsync(context, async () =>
        {
            found = false;
            await LocationTransactions.LockAsync(context, [id], cancellationToken);
            var location = await context.Locations.FirstOrDefaultAsync(l => l.LocationId == id, cancellationToken);
            if (location is null)
            {
                return;
            }

            found = true;
            if (await guard.IsReferencedAsync(id, cancellationToken))
            {
                throw InUse();
            }

            context.Locations.Remove(location);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (ex.InnerException is MySqlConnector.MySqlException { Number: 1451 })
            {
                // A reference the guard does not know about: the RESTRICT key is the backstop (§4).
                throw InUse();
            }
        }, IsolationLevel.RepeatableRead, cancellationToken);

        if (found)
        {
            logger.LogInformation("Location {LocationId} deleted by {ActorUserId}.", id, userId);
        }

        return found;
    }

    private static DomainConflictException InUse() =>
        new("This place is in use, so it cannot be deleted. Remove what uses it first.");

    private async Task FillUsageAsync(IReadOnlyList<ExistingLocation> dtos, LocationClaims claims, CancellationToken cancellationToken)
    {
        var counts = await guard.CountAsync(dtos.Select(d => d.LocationId).ToList(), cancellationToken);
        foreach (var dto in dtos)
        {
            dto.Usage = LocationReferenceGuard.ToUsage(
                counts.GetValueOrDefault(dto.LocationId) ?? LocationReferenceGuard.Counts.Zero, claims);
        }
    }

    internal static ExistingLocation ToDto(LocationEntity location) => new()
    {
        LocationId = location.LocationId,
        Name = location.Name,
        Description = location.Description,
        AddressText = location.AddressText,
        Line1 = location.Line1,
        Line2 = location.Line2,
        PostalCode = location.PostalCode,
        City = location.City,
        Region = location.Region,
        CountryCode = location.CountryCode,
        Latitude = location.Latitude,
        Longitude = location.Longitude,
        AltitudeMeters = location.AltitudeMeters,
        TimeZoneId = location.TimeZoneId,
        HasCoordinates = location.Latitude is not null && location.Longitude is not null && location.TimeZoneId is not null,
        MergedIntoLocationId = location.MergedIntoLocationId,
        Source = location.Source,
        SourceReference = location.SourceReference,
        Attribution = LocationAttributions.For(location.Source),
        Archived = location.Archived,
        CreatedAt = location.CreatedAt,
        UpdatedAt = location.UpdatedAt,
    };

    /// <summary>
    /// The §3 write normalization: strings cleaned (NFC, control and bidi characters removed, trimmed), an
    /// all-whitespace optional field stored as null, the structured address cleaned and whitespace-collapsed
    /// with the country upper-cased, coordinates rounded to 5 decimals away from zero and all-or-none.
    /// </summary>
    private static LocationFields Normalize(
        string name, string? description, string? addressText, string? line1, string? line2, string? postalCode,
        string? city, string? region, string? countryCode, decimal? latitude, decimal? longitude,
        int? altitude, string? timeZoneId, LocationSource source, string? sourceReference)
    {
        var cleanName = LocationText.Clean(name)
            ?? throw new DomainValidationException("Give the location a name.", code: null, field: NameField);

        var reference = LocationText.Clean(sourceReference);
        if (!LocationRules.IsValidReference(source, reference))
        {
            throw new DomainValidationException("The source reference is not valid for this source.", code: null, field: "sourceReference");
        }

        var zone = LocationText.Clean(timeZoneId);
        var present = (latitude is not null ? 1 : 0) + (longitude is not null ? 1 : 0) + (zone is not null ? 1 : 0);
        if (present is not (0 or 3))
        {
            throw new DomainUnprocessableException(
                "Give the latitude, longitude and time zone together, or none of them.", LatitudeField);
        }

        if (zone is not null && !IanaTimeZones.IsValid(zone))
        {
            throw new DomainValidationException("The time zone must be an IANA time zone, e.g. Europe/Oslo.", code: null, field: "timeZoneId");
        }

        var address = LocationAddress.Of(line1, line2, postalCode, city, region, countryCode);
        if (address.CountryCode is { } country && (country.Length != 2 || !country.All(char.IsAsciiLetter)))
        {
            throw new DomainValidationException("The country must be a two-letter code, e.g. NO.", code: null, field: "countryCode");
        }

        return new LocationFields(
            cleanName,
            LocationText.Clean(description, allowLineBreaks: true),
            LocationText.Clean(addressText),
            address,
            latitude is { } lat ? Round(lat) : null,
            longitude is { } lon ? Round(lon) : null,
            altitude,
            zone,
            source,
            reference);
    }

    internal static decimal Round(decimal value) =>
        Math.Round(value, LocationRules.CoordinateScale, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Case- <b>and accent</b>-insensitive, archived rows included — the folding MariaDB's <c>_ci</c>
    /// collation applies on the unique index, so <c>Cabin</c>, <c>cabin</c> and <c>Cabín</c> all conflict
    /// (B1). The same ICU-dependent rule as <c>ContactService</c>'s aliases, which
    /// <c>GlobalizationGuard</c> guarantees in the container.
    /// </summary>
    private async Task EnsureNameAvailable(string name, Guid? excludeId, CancellationToken cancellationToken)
    {
        var clash = await FindClash(name, excludeId, cancellationToken);
        if (clash is not null)
        {
            throw NameConflict(clash.Value);
        }
    }

    private async Task<bool?> FindClash(string name, Guid? excludeId, CancellationToken cancellationToken)
    {
        var candidates = await context.Locations.AsNoTracking()
            .Where(l => excludeId == null || l.LocationId != excludeId)
            .Select(l => new { l.Name, l.Archived })
            .ToListAsync(cancellationToken);

        var hit = candidates.FirstOrDefault(c => NamesEqual(c.Name, name));
        return hit is null ? null : hit.Archived is not null;
    }

    private static bool NamesEqual(string left, string right) =>
        string.Compare(left, right, CultureInfo.InvariantCulture,
            CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) == 0;

    // The 409 names the conflicting name back only to the caller who just typed it, and says whether the
    // existing row is archived so the user knows to restore it rather than look for it (N8).
    private static DomainConflictException NameConflict(bool archived) => new(
        archived
            ? "An archived location with this name exists — restore it or choose another name."
            : "A location with this name already exists.",
        NameField);

    /// <summary>
    /// Saves, mapping a concurrent insert that won the race on a unique index (MariaDB 1062) to the same
    /// 409 the pre-check raises — <c>name</c> for the name, <c>line1</c> for the address key — never a 500,
    /// and never the driver text, which would echo the value.
    /// </summary>
    private async Task SaveAsync(string name, Guid? excludeId, CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (DbErrors.IsDuplicateKey(ex))
        {
            context.ChangeTracker.Clear();
            if (ex.InnerException?.Message.Contains("IX_Locations_AddressKey'", StringComparison.Ordinal) == true)
            {
                throw LocationResolver.DuplicateAddress();
            }

            throw NameConflict(await FindClash(name, excludeId, cancellationToken) ?? false);
        }
    }

    private sealed record LocationFields(
        string Name,
        string? Description,
        string? AddressText,
        LocationAddress Address,
        decimal? Latitude,
        decimal? Longitude,
        int? AltitudeMeters,
        string? TimeZoneId,
        LocationSource Source,
        string? SourceReference)
    {
        public void ApplyTo(LocationEntity location)
        {
            location.Description = Description;
            location.AddressText = AddressText;
            location.Line1 = Address.Line1;
            location.Line2 = Address.Line2;
            location.PostalCode = Address.PostalCode;
            location.City = Address.City;
            location.Region = Address.Region;
            location.CountryCode = Address.CountryCode;
            location.AddressKey = Address.Key;
            location.Latitude = Latitude;
            location.Longitude = Longitude;
            location.AltitudeMeters = AltitudeMeters;
            location.TimeZoneId = TimeZoneId;
            location.Source = Source;
            location.SourceReference = SourceReference;
        }

        /// <summary>The entity property NAMES this write changes — the audit line's payload, never values.</summary>
        public List<string> ChangedFields(LocationEntity location)
        {
            var changed = new List<string>();
            if (!string.Equals(location.Name, Name, StringComparison.Ordinal)) changed.Add(nameof(LocationEntity.Name));
            if (!string.Equals(location.Description, Description, StringComparison.Ordinal)) changed.Add(nameof(LocationEntity.Description));
            if (!string.Equals(location.AddressText, AddressText, StringComparison.Ordinal)) changed.Add(nameof(LocationEntity.AddressText));
            if (!string.Equals(location.Line1, Address.Line1, StringComparison.Ordinal)) changed.Add(nameof(LocationEntity.Line1));
            if (!string.Equals(location.Line2, Address.Line2, StringComparison.Ordinal)) changed.Add(nameof(LocationEntity.Line2));
            if (!string.Equals(location.PostalCode, Address.PostalCode, StringComparison.Ordinal)) changed.Add(nameof(LocationEntity.PostalCode));
            if (!string.Equals(location.City, Address.City, StringComparison.Ordinal)) changed.Add(nameof(LocationEntity.City));
            if (!string.Equals(location.Region, Address.Region, StringComparison.Ordinal)) changed.Add(nameof(LocationEntity.Region));
            if (!string.Equals(location.CountryCode, Address.CountryCode, StringComparison.Ordinal)) changed.Add(nameof(LocationEntity.CountryCode));
            if (location.Latitude != Latitude) changed.Add(nameof(LocationEntity.Latitude));
            if (location.Longitude != Longitude) changed.Add(nameof(LocationEntity.Longitude));
            if (location.AltitudeMeters != AltitudeMeters) changed.Add(nameof(LocationEntity.AltitudeMeters));
            if (!string.Equals(location.TimeZoneId, TimeZoneId, StringComparison.Ordinal)) changed.Add(nameof(LocationEntity.TimeZoneId));
            if (location.Source != Source) changed.Add(nameof(LocationEntity.Source));
            if (!string.Equals(location.SourceReference, SourceReference, StringComparison.Ordinal)) changed.Add(nameof(LocationEntity.SourceReference));
            return changed;
        }
    }
}
