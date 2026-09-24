using System.Linq.Expressions;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;

namespace Odyssey.Core.Finance;

/// <summary>
/// Business logic for properties (issue #167): a house, a cabin, a car, a boat — a thing the household
/// owns that holds value but generates no transactions. Owns the subtype invariant (exactly one detail
/// sub-record, matching <see cref="Property.Type"/>), the immutability of <c>Type</c> after creation,
/// currency validation, and the derived <see cref="PropertyStatus"/>.
///
/// <para>
/// <b>A type change is refused, unlike a contact's.</b> <c>ContactService</c> permits one and drops the
/// stale detail row, because a contact's identity survives the switch. A house that becomes a car is a
/// different asset whose estimate history no longer describes it, so the caller deletes and recreates.
/// </para>
///
/// <para>
/// Error messages echo the opaque route id and nothing else — never a name, an address, a registration
/// number or a VIN (§7.8): <c>GlobalExceptionHandler</c> logs every domain message.
/// </para>
/// </summary>
public class PropertyService
{
    private readonly OdysseyContext context;
    private readonly TimeProvider timeProvider;

    public PropertyService(OdysseyContext context, TimeProvider? timeProvider = null)
    {
        this.context = context;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Server-side paged list: search, subtype and derived-status filters, allowlisted sort. The
    /// <see cref="PropertySortBy.Value"/> key is a correlated subquery resolved by
    /// <see cref="EstimateEffectiveDating.CurrentValue{TOwner,T}"/>, so the list and the estimate
    /// endpoints cannot disagree about which entry is current, and ordering happens in SQL before the
    /// page slice.
    /// </summary>
    public async Task<PagedResult<ExistingProperty>> ListAsync(
        PropertiesQueryParams query, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var q = context.Properties.AsNoTracking().AsQueryable();

        var term = ListQuery.NormalizeSearch(query.Search);
        if (term is not null)
        {
            var pattern = ListQuery.ContainsPattern(term);
            q = q.Where(p =>
                EF.Functions.Like(p.Name, pattern) ||
                EF.Functions.Like(p.Description, pattern) ||
                (p.Notes != null && EF.Functions.Like(p.Notes, pattern)) ||
                (p.RealEstateDetails != null && (
                    (p.RealEstateDetails.AddressLine != null && EF.Functions.Like(p.RealEstateDetails.AddressLine, pattern)) ||
                    (p.RealEstateDetails.City != null && EF.Functions.Like(p.RealEstateDetails.City, pattern)) ||
                    (p.RealEstateDetails.CadastralNumber != null && EF.Functions.Like(p.RealEstateDetails.CadastralNumber, pattern)))) ||
                (p.VehicleDetails != null && (
                    (p.VehicleDetails.RegistrationNumber != null && EF.Functions.Like(p.VehicleDetails.RegistrationNumber, pattern)) ||
                    (p.VehicleDetails.Vin != null && EF.Functions.Like(p.VehicleDetails.Vin, pattern)) ||
                    (p.VehicleDetails.Make != null && EF.Functions.Like(p.VehicleDetails.Make, pattern)) ||
                    (p.VehicleDetails.Model != null && EF.Functions.Like(p.VehicleDetails.Model, pattern)))));
        }

        if (query.Types is { Length: > 0 } types)
        {
            var typeFilter = types.Distinct().ToList();
            q = q.Where(p => typeFilter.Contains(p.Type));
        }

        // Status is derived from the Archived/DisposedDate columns; translate the requested set to a
        // predicate over them, exactly as DeriveStatus reads them.
        if (query.Statuses is { Length: > 0 } statuses)
        {
            var wantOwned = statuses.Contains(PropertyStatus.Owned);
            var wantDisposed = statuses.Contains(PropertyStatus.Disposed);
            var wantArchived = statuses.Contains(PropertyStatus.Archived);
            q = q.Where(p =>
                (wantArchived && p.Archived != null) ||
                (wantDisposed && p.Archived == null && p.DisposedDate != null && p.DisposedDate <= now) ||
                (wantOwned && p.Archived == null && (p.DisposedDate == null || p.DisposedDate > now)));
        }

        var ascending = ListQuery.Ascending(
            query.SortDir, naturalDefaultAscending: query.SortBy is null or PropertySortBy.Name or PropertySortBy.Type);

        IOrderedQueryable<Property> sorted;
        if (query.SortBy == PropertySortBy.Value)
        {
            // Nulls last in BOTH directions, stated explicitly rather than left to the provider: MariaDB
            // and the EF InMemory provider disagree on null ordering. The TransactionService shape.
            var currentValue = EstimateEffectiveDating.CurrentValue<Property, PropertyEstimate>(
                p => p.Estimates, e => e.Value, now);
            var hasNoValue = NoValue(currentValue);
            var withNullsLast = q.OrderBy(hasNoValue);
            sorted = ascending ? withNullsLast.ThenBy(currentValue) : withNullsLast.ThenByDescending(currentValue);
        }
        else
        {
            sorted = query.SortBy switch
            {
                PropertySortBy.Type => ascending ? q.OrderBy(p => p.Type) : q.OrderByDescending(p => p.Type),
                PropertySortBy.Acquired => ascending ? q.OrderBy(p => p.AcquiredDate) : q.OrderByDescending(p => p.AcquiredDate),
                _ => ascending ? q.OrderBy(p => p.Name) : q.OrderByDescending(p => p.Name),
            };
        }

        var ordered = sorted.ThenBy(p => p.PropertyId);

        var totalCount = await ordered.CountAsync(cancellationToken);
        var (safeOffset, safeLimit) = ListQuery.ResolveWindow(query.Offset, query.Limit);

        var properties = await ordered
            .Include(p => p.RealEstateDetails)
            .Include(p => p.VehicleDetails)
            .Skip(safeOffset)
            .Take(safeLimit)
            .ToListAsync(cancellationToken);

        return new PagedResult<ExistingProperty>
        {
            Items = properties.Select(p => ToDto(p, now)).ToList(),
            Offset = safeOffset,
            Limit = safeLimit,
            TotalCount = totalCount,
        };
    }

    /// <summary>One property with its detail sub-object, or <c>null</c> when unknown.</summary>
    public async Task<ExistingProperty?> Get(Guid id, CancellationToken cancellationToken = default)
    {
        var property = await context.Properties
            .AsNoTracking()
            .Include(p => p.RealEstateDetails)
            .Include(p => p.VehicleDetails)
            .FirstOrDefaultAsync(p => p.PropertyId == id, cancellationToken);

        return property is null ? null : ToDto(property, timeProvider.GetUtcNow().UtcDateTime);
    }

    /// <summary>
    /// Creates a property and exactly one detail row in a single <c>SaveChangesAsync</c>; the detail row
    /// shares the parent key, so the pair cannot half-exist.
    /// </summary>
    /// <exception cref="DomainValidationException">A shape, currency, date or year rule fails.</exception>
    public async Task<ExistingProperty> Create(NewProperty newProperty, CancellationToken cancellationToken = default)
    {
        var type = ValidateShape(newProperty);
        var currencyCode = CurrencyValidationService.Normalize(newProperty.CurrencyCode);
        await CurrencyValidationService.EnsureSupportedAndActive(
            context, currencyCode, nameof(NewProperty.CurrencyCode), cancellationToken);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var property = new Property
        {
            Name = newProperty.Name,
            Description = newProperty.Description,
            Type = type,
            CurrencyCode = currencyCode,
            CreatedAt = now,
            UpdatedAt = now,
            Archived = newProperty.Archived ? now : null,
        };

        ApplyBaseAndDetails(property, newProperty, now);

        context.Properties.Add(property);
        await context.SaveChangesAsync(cancellationToken);

        return ToDto(property, now);
    }

    /// <summary>
    /// Full replace of an existing property. Returns <c>null</c> when unknown — this is <b>not</b> an
    /// upsert, so an unknown id creates nothing.
    /// </summary>
    /// <exception cref="DomainUnprocessableException">The body's type differs from the stored one.</exception>
    /// <exception cref="DomainValidationException">
    /// A shape, currency, date or year rule fails, or the currency changes while estimates exist.
    /// </exception>
    public async Task<ExistingProperty?> Update(Guid id, NewProperty putProperty, CancellationToken cancellationToken = default)
    {
        var property = await context.Properties
            .Include(p => p.RealEstateDetails)
            .Include(p => p.VehicleDetails)
            .FirstOrDefaultAsync(p => p.PropertyId == id, cancellationToken);
        if (property is null)
            return null;

        var type = ValidateShape(putProperty);

        // A cross-row bound — it depends on the persisted row — so it is a 422 from here, not a 400 from
        // model validation. The body is not silently re-typed: a caller who believed it was changing the
        // type is told it did not.
        if (type != property.Type)
        {
            throw new DomainUnprocessableException(
                "A property's type cannot be changed. Delete this property and create a new one of the required type.",
                nameof(NewProperty.Type));
        }

        var currencyCode = CurrencyValidationService.Normalize(putProperty.CurrencyCode);
        await CurrencyValidationService.EnsureSupportedAndActive(
            context, currencyCode, nameof(NewProperty.CurrencyCode), cancellationToken);

        // Every estimate is stored in the property currency, so changing it would silently reinterpret
        // the recorded values — the rule AccountService applies to account estimates.
        if (!string.Equals(property.CurrencyCode, currencyCode, StringComparison.Ordinal)
            && await context.PropertyEstimates.AnyAsync(e => e.PropertyId == id, cancellationToken))
        {
            throw new DomainValidationException(
                "Property currency cannot be changed when the property has value estimates.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        property.Name = putProperty.Name;
        property.Description = putProperty.Description;
        property.CurrencyCode = currencyCode;
        property.UpdatedAt = now;
        ApplyArchiveTransition(property, putProperty.Archived, now);
        ApplyBaseAndDetails(property, putProperty, now);

        await context.SaveChangesAsync(cancellationToken);
        return ToDto(property, now);
    }

    /// <summary>
    /// Hard-deletes a property. Returns <c>false</c> when unknown. The detail row, estimates and
    /// smart-tag links cascade — included here so the cascade also happens under the EF InMemory
    /// provider, which enforces no foreign keys. Nothing else references a property, so there is no
    /// blocker and no <c>409</c>.
    /// </summary>
    public async Task<bool> Delete(Guid id, CancellationToken cancellationToken = default)
    {
        var property = await context.Properties
            .Include(p => p.RealEstateDetails)
            .Include(p => p.VehicleDetails)
            .Include(p => p.Estimates)
            .Include(p => p.SmartTags)
            .FirstOrDefaultAsync(p => p.PropertyId == id, cancellationToken);
        if (property is null)
            return false;

        context.Properties.Remove(property);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// The status projection: <see cref="PropertyStatus.Archived"/> when archived, else
    /// <see cref="PropertyStatus.Disposed"/> when disposed on or before <paramref name="now"/>, else
    /// <see cref="PropertyStatus.Owned"/>. <see cref="ListAsync"/>'s status filter reads the same columns
    /// the same way.
    /// </summary>
    public static PropertyStatus DeriveStatus(DateTime? archived, DateTime? disposedDate, DateTime now) =>
        archived is not null ? PropertyStatus.Archived
        : disposedDate is { } disposed && disposed <= now ? PropertyStatus.Disposed
        : PropertyStatus.Owned;

    private static ExistingProperty ToDto(Property property, DateTime now)
    {
        var dto = property.Adapt<ExistingProperty>();
        dto.Status = DeriveStatus(property.Archived, property.DisposedDate, now);
        return dto;
    }

    private static Expression<Func<Property, bool>> NoValue(
        Expression<Func<Property, decimal?>> currentValue) =>
        Expression.Lambda<Func<Property, bool>>(
            Expression.Equal(
                currentValue.Body, Expression.Constant(null, typeof(decimal?))),
            currentValue.Parameters);

    /// <summary>
    /// Mirrors <see cref="NewProperty.Validate"/> for direct (non-HTTP) callers that bypass
    /// <c>[ApiController]</c> model validation, and returns the (required) type.
    /// </summary>
    private static PropertyType ValidateShape(NewProperty source)
    {
        var type = source.Type
            ?? throw new DomainValidationException("A property type is required.", null, nameof(NewProperty.Type));

        switch (type)
        {
            case PropertyType.RealEstate when source.RealEstateDetails is null || source.VehicleDetails is not null:
                throw new DomainValidationException(
                    "A RealEstate property requires real-estate details and no vehicle details.",
                    null, nameof(NewProperty.RealEstateDetails));
            case PropertyType.Vehicle when source.VehicleDetails is null || source.RealEstateDetails is not null:
                throw new DomainValidationException(
                    "A Vehicle property requires vehicle details and no real-estate details.",
                    null, nameof(NewProperty.VehicleDetails));
            case PropertyType.RealEstate or PropertyType.Vehicle:
                return type;
            default:
                throw new DomainValidationException(
                    $"'{(int)type}' is not a property type.", null, nameof(NewProperty.Type));
        }
    }

    private void ApplyBaseAndDetails(Property property, NewProperty source, DateTime now)
    {
        var acquired = NormalizeOptional(source.AcquiredDate);
        var disposed = NormalizeOptional(source.DisposedDate);
        if (acquired is not null && disposed is not null && disposed < acquired)
        {
            throw new DomainValidationException(
                "The disposed date must not precede the acquired date.", null, nameof(NewProperty.DisposedDate));
        }

        property.AcquiredDate = acquired;
        property.DisposedDate = disposed;
        property.Notes = source.Notes;

        // The type never changes after creation (Update refuses it), so only the matching detail row
        // is ever written and there is no stale sibling row to drop.
        if (property.Type == PropertyType.RealEstate)
        {
            var details = source.RealEstateDetails!;
            if (details.BuildYear is { } buildYear && buildYear > now.Year)
            {
                throw new DomainValidationException(
                    "The build year must not be in the future.", null,
                    $"{nameof(NewProperty.RealEstateDetails)}.{nameof(RealEstateDetailsDto.BuildYear)}");
            }

            property.RealEstateDetails ??= new RealEstateDetails();
            property.RealEstateDetails.Kind = details.Kind;
            property.RealEstateDetails.AddressLine = details.AddressLine;
            property.RealEstateDetails.PostalCode = details.PostalCode;
            property.RealEstateDetails.City = details.City;
            property.RealEstateDetails.CountryCode = details.CountryCode?.Trim().ToUpperInvariant();
            property.RealEstateDetails.CadastralNumber = details.CadastralNumber;
            property.RealEstateDetails.LivingAreaSqm = details.LivingAreaSqm;
            property.RealEstateDetails.PlotAreaSqm = details.PlotAreaSqm;
            property.RealEstateDetails.BuildYear = details.BuildYear;
        }
        else
        {
            var details = source.VehicleDetails!;
            if (details.ModelYear is { } modelYear && modelYear > now.Year + 1)
            {
                throw new DomainValidationException(
                    "The model year must not be more than one year in the future.", null,
                    $"{nameof(NewProperty.VehicleDetails)}.{nameof(VehicleDetailsDto.ModelYear)}");
            }

            property.VehicleDetails ??= new VehicleDetails();
            property.VehicleDetails.Kind = details.Kind;
            property.VehicleDetails.RegistrationNumber = NormalizeIdentifier(details.RegistrationNumber);
            property.VehicleDetails.Vin = NormalizeIdentifier(details.Vin);
            property.VehicleDetails.Make = details.Make;
            property.VehicleDetails.Model = details.Model;
            property.VehicleDetails.ModelYear = details.ModelYear;
            property.VehicleDetails.FirstRegisteredDate = NormalizeOptional(details.FirstRegisteredDate);
        }
    }

    private static void ApplyArchiveTransition(Property property, bool requestedArchived, DateTime now)
    {
        if (property.Archived is null && requestedArchived)
            property.Archived = now;
        else if (property.Archived is not null && !requestedArchived)
            property.Archived = null;
    }

    private static DateTime? NormalizeOptional(DateTime? value) =>
        value is { } v ? DateTimeNormalization.NormalizeToUtc(v) : null;

    // Uppercased and whitespace-stripped; an all-whitespace value is stored as null rather than "".
    private static string? NormalizeIdentifier(string? value)
    {
        if (value is null)
            return null;

        var stripped = string.Concat(value.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
        return stripped.Length == 0 ? null : stripped;
    }
}
