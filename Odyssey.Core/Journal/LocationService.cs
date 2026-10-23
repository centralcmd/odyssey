using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using LocationEntity = Odyssey.Context.Location;

namespace Odyssey.Core.Journal;

/// <summary>
/// The household's locations (issue #350): list, get, create and update. There is no delete — a location
/// is archived through <see cref="Update"/>, and an archived row still reserves its name.
/// </summary>
/// <remarks>
/// <para>
/// <b>Audit, never values (§7.7, S4).</b> Every write logs the location id, the acting user, the operation
/// and the NAMES of the fields that changed — never their values — so a household can answer "who changed
/// the cabin's address and when" without the log keeping the old address.
/// </para>
/// <para>
/// <b>Last writer wins</b> (Non-Goal 7): there is no concurrency token on a location.
/// </para>
/// </remarks>
public class LocationService
{
    private const string NameField = "name";

    private readonly OdysseyContext context;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<LocationService> logger;

    public LocationService(OdysseyContext context, TimeProvider? timeProvider = null, ILogger<LocationService>? logger = null)
    {
        this.context = context;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.logger = logger ?? NullLogger<LocationService>.Instance;
    }

    public async Task<PagedResult<ExistingLocation>> ListAsync(
        LocationsQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var q = context.Locations.AsNoTracking();

        q = query.Status switch
        {
            ArchivalStatus.Active => q.Where(l => l.Archived == null),
            ArchivalStatus.Archived => q.Where(l => l.Archived != null),
            _ => q,
        };

        var term = ListQuery.NormalizeSearch(query.Search);
        if (term is not null)
        {
            var pattern = ListQuery.ContainsPattern(term);
            q = q.Where(l =>
                EF.Functions.Like(l.Name, pattern)
                || (l.Description != null && EF.Functions.Like(l.Description, pattern))
                || (l.AddressText != null && EF.Functions.Like(l.AddressText, pattern)));
        }

        var ascending = ListQuery.Ascending(query.SortDir, naturalDefaultAscending: query.SortBy is null or LocationSortBy.Name);
        IOrderedQueryable<LocationEntity> sorted = query.SortBy switch
        {
            LocationSortBy.CreatedAt => ascending ? q.OrderBy(l => l.CreatedAt) : q.OrderByDescending(l => l.CreatedAt),
            LocationSortBy.UpdatedAt => ascending ? q.OrderBy(l => l.UpdatedAt) : q.OrderByDescending(l => l.UpdatedAt),
            _ => ascending ? q.OrderBy(l => l.Name) : q.OrderByDescending(l => l.Name),
        };
        q = sorted.ThenBy(l => l.LocationId);

        return await q.ToPagedResultAsync(query.Offset, query.Limit, ToDto, cancellationToken);
    }

    public async Task<ExistingLocation?> Get(Guid id, CancellationToken cancellationToken = default)
    {
        var location = await context.Locations.AsNoTracking()
            .FirstOrDefaultAsync(l => l.LocationId == id, cancellationToken);
        return location is null ? null : ToDto(location);
    }

    public async Task<ExistingLocation> Create(NewLocation request, string userId, CancellationToken cancellationToken = default)
    {
        var fields = Normalize(request.Name, request.Description, request.AddressText, request.Latitude, request.Longitude,
            request.AltitudeMeters, request.TimeZoneId, request.Source, request.SourceReference);
        await EnsureNameAvailable(fields.Name, null, cancellationToken);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var location = new LocationEntity
        {
            Name = fields.Name,
            TimeZoneId = fields.TimeZoneId,
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

    public async Task<ExistingLocation?> Update(Guid id, UpdateLocation request, string userId, CancellationToken cancellationToken = default)
    {
        var location = await context.Locations.FirstOrDefaultAsync(l => l.LocationId == id, cancellationToken);
        if (location is null)
        {
            return null;
        }

        var fields = Normalize(request.Name, request.Description, request.AddressText, request.Latitude, request.Longitude,
            request.AltitudeMeters, request.TimeZoneId, request.Source, request.SourceReference);
        await EnsureNameAvailable(fields.Name, id, cancellationToken);

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
        return ToDto(location);
    }

    internal static ExistingLocation ToDto(LocationEntity location) => new()
    {
        LocationId = location.LocationId,
        Name = location.Name,
        Description = location.Description,
        AddressText = location.AddressText,
        Latitude = location.Latitude,
        Longitude = location.Longitude,
        AltitudeMeters = location.AltitudeMeters,
        TimeZoneId = location.TimeZoneId,
        Source = location.Source,
        SourceReference = location.SourceReference,
        Attribution = LocationAttributions.For(location.Source),
        Archived = location.Archived,
        CreatedAt = location.CreatedAt,
        UpdatedAt = location.UpdatedAt,
    };

    /// <summary>
    /// The §3 write normalization: strings cleaned (NFC, control and bidi characters removed, trimmed), an
    /// all-whitespace optional field stored as null, coordinates rounded to 5 decimals away from zero.
    /// Model validation has already run, so the nullable value types are present here.
    /// </summary>
    private static LocationFields Normalize(
        string name, string? description, string? addressText, decimal? latitude, decimal? longitude,
        int? altitude, string? timeZoneId, LocationSource? source, string? sourceReference)
    {
        var cleanName = LocationText.Clean(name)
            ?? throw new DomainValidationException("Give the location a name.", code: null, field: NameField);

        var resolvedSource = source ?? throw new DomainValidationException("A source is required.", code: null, field: "source");
        var reference = LocationText.Clean(sourceReference);
        if (!LocationRules.IsValidReference(resolvedSource, reference))
        {
            throw new DomainValidationException("The source reference is not valid for this source.", code: null, field: "sourceReference");
        }

        if (!IanaTimeZones.IsValid(timeZoneId))
        {
            throw new DomainValidationException("The time zone must be an IANA time zone, e.g. Europe/Oslo.", code: null, field: "timeZoneId");
        }

        return new LocationFields(
            cleanName,
            LocationText.Clean(description, allowLineBreaks: true),
            LocationText.Clean(addressText),
            Round(latitude ?? throw new DomainValidationException("Latitude is required.", code: null, field: "latitude")),
            Round(longitude ?? throw new DomainValidationException("Longitude is required.", code: null, field: "longitude")),
            altitude,
            timeZoneId!,
            resolvedSource,
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
    /// Saves, mapping a concurrent insert that won the race on the unique name index (MariaDB 1062) to the
    /// same 409 the pre-check raises — never a 500, and never the driver text, which would echo the name.
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
            throw NameConflict(await FindClash(name, excludeId, cancellationToken) ?? false);
        }
    }

    private sealed record LocationFields(
        string Name,
        string? Description,
        string? AddressText,
        decimal Latitude,
        decimal Longitude,
        int? AltitudeMeters,
        string TimeZoneId,
        LocationSource Source,
        string? SourceReference)
    {
        public void ApplyTo(LocationEntity location)
        {
            location.Description = Description;
            location.AddressText = AddressText;
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
