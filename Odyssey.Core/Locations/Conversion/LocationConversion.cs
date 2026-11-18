using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Odyssey.Context;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Locations.Conversion;

/// <summary>Test seams for <see cref="LocationConversion"/>. Production uses the defaults.</summary>
public sealed class LocationConversionOptions
{
    /// <summary>Rows per transaction (§11: 1 000).</summary>
    public int BatchSize { get; init; } = 1000;

    /// <summary>
    /// Called inside each batch's transaction, after its writes and before its commit, with the source table
    /// and the batch's ordinal within it. A test throws here to interrupt a run mid-batch.
    /// </summary>
    public Action<string, int>? BeforeCommit { get; init; }
}

/// <summary>
/// The issue #380 §11 conversion, in raw parameterised SQL on the relational connection — never through the
/// EF model, which between the two migrations maps <c>Address.LocationId</c> as required while existing
/// rows still hold <c>NULL</c> (M1). Every batch of <see cref="LocationConversionOptions.BatchSize"/> rows is
/// one explicit transaction inside its own <c>CreateExecutionStrategy().ExecuteAsync</c>: it creates its
/// locations, sets the links and clears the converted text atomically.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order:</b> contact addresses (<see cref="LocationSource.ContactAddress"/>), then property addresses with
/// any field set (<see cref="LocationSource.PropertyAddress"/>), then the free-text places on
/// <c>CalendarEvents</c>, <c>RecurrencePatterns</c> and <c>JournalEntries</c>
/// (<see cref="LocationSource.Converted"/>), which are linked and cleared.
/// </para>
/// <para>
/// <b>Restart-safe:</b> batches select only rows whose <c>LocationId</c> is null, and matching checks both
/// <c>Name</c> and <c>AddressText</c>, so a restarted run finds what an interrupted one created (A-R7, R7).
/// <b>A no-op once the old columns are gone</b> (M2): any of the ten absent means a previous start got
/// part-way through the contract migration, and the drift guard reports that.
/// </para>
/// <para>
/// <b>Logs counts only</b> — never a name, an address or a detail (§7.1.11).
/// </para>
/// </remarks>
public sealed class LocationConversion : ILocationConversion
{
    /// <summary>Every old column the conversion reads (N2): six on <c>Addresses</c>, four on <c>RealEstateDetails</c>.</summary>
    internal static readonly (string Table, string Column)[] OldColumns =
    [
        ("Addresses", "Line1"), ("Addresses", "Line2"), ("Addresses", "City"),
        ("Addresses", "PostalCode"), ("Addresses", "Region"), ("Addresses", "CountryCode"),
        ("RealEstateDetails", "AddressLine"), ("RealEstateDetails", "PostalCode"),
        ("RealEstateDetails", "City"), ("RealEstateDetails", "CountryCode"),
    ];

    /// <summary>The free-text place tables, with their key column, in conversion order.</summary>
    private static readonly (string Table, string KeyColumn)[] TextTables =
    [
        ("CalendarEvents", "CalendarEventId"),
        ("RecurrencePatterns", "RecurrencePatternId"),
        ("JournalEntries", "JournalEntryId"),
    ];

    /// <summary>How often one batch may be retried after a duplicate name the collation comparer did not predict.</summary>
    private const int MaxDuplicateRetries = 25;

    private readonly OdysseyContext context;
    private readonly ILogger<LocationConversion> logger;
    private readonly LocationConversionOptions options;
    private readonly TimeProvider timeProvider;

    private LocationIndex index = new();
    private readonly HashSet<string> refusedNames = new(StringComparer.Ordinal);
    private string? lastInsertedName;
    private int created;
    private int linked;
    private int leftAsText;

    public LocationConversion(
        OdysseyContext context,
        ILogger<LocationConversion>? logger = null,
        LocationConversionOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        this.context = context;
        this.logger = logger ?? NullLogger<LocationConversion>.Instance;
        this.options = options ?? new LocationConversionOptions();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<LocationConversionResult> ConvertAsync(CancellationToken cancellationToken)
    {
        if (!context.Database.IsRelational())
        {
            return LocationConversionResult.NotRun;
        }

        if (!await OldColumnsPresentAsync(cancellationToken))
        {
            logger.LogWarning(
                "Location conversion skipped: the old address columns are no longer all present, so the contract migration has already started.");
            return LocationConversionResult.NotRun;
        }

        await LogStartCountsAsync(cancellationToken);
        await LoadIndexAsync(cancellationToken);

        await ConvertTableAsync("Addresses", ConvertAddressBatchAsync, cancellationToken);
        await ConvertTableAsync("RealEstateDetails", ConvertPropertyBatchAsync, cancellationToken);
        foreach (var (table, keyColumn) in TextTables)
        {
            await ConvertTableAsync(
                table, (last, ct) => ConvertTextBatchAsync(table, keyColumn, last, ct), cancellationToken);
        }

        logger.LogInformation(
            "Location conversion finished: {Created} locations created, {Linked} rows linked, {LeftAsText} places left as detail text.",
            created, linked, leftAsText);

        await DropUnlinkableAddressesAsync(cancellationToken);
        return new LocationConversionResult(true, created, linked, leftAsText);
    }

    /// <summary>
    /// The contract migration makes <c>Addresses.LocationId</c> <c>NOT NULL</c>, so an address row the
    /// conversion could not link would fail it on every start. Every address with any usable text was linked
    /// above, so a row still unlinked here has all six fields empty once cleaned (control characters and
    /// whitespace only) and carries no address to keep. It is deleted, in one transaction, so the upgrade
    /// completes: a product decision on PR #385 accepts that loss rather than holding the deployment down for
    /// a hand repair. The log names the count only, never a value or an id.
    /// </summary>
    private async Task DropUnlinkableAddressesAsync(CancellationToken cancellationToken)
    {
        var strategy = context.Database.CreateExecutionStrategy();
        var dropped = await strategy.ExecuteAsync(async ct =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(ct);
            var count = await context.Database.ExecuteSqlAsync(
                $"DELETE FROM `Addresses` WHERE `LocationId` IS NULL", ct);
            await transaction.CommitAsync(ct);
            return count;
        }, cancellationToken);

        if (dropped > 0)
        {
            logger.LogWarning(
                "Location conversion removed {Count} contact address rows that had no usable address text to link to a location.",
                dropped);
        }
    }

    // ── Batching ─────────────────────────────────────────────────────────────

    private delegate Task<Guid?> BatchStep(Guid? after, CancellationToken cancellationToken);

    /// <summary>
    /// Runs <paramref name="step"/> batch after batch, keyset-paged on the table's key so a row the batch
    /// leaves unlinked (a link-like value, an empty property address) is passed over rather than re-read
    /// forever. Each batch is one transaction inside its own execution strategy.
    /// </summary>
    private async Task ConvertTableAsync(string table, BatchStep step, CancellationToken cancellationToken)
    {
        Guid? after = null;
        for (var batch = 1; ; batch++)
        {
            var next = await RunBatchAsync(table, batch, after, step, cancellationToken);
            if (next is null)
            {
                return;
            }

            after = next;
        }
    }

    private async Task<Guid?> RunBatchAsync(
        string table, int batch, Guid? after, BatchStep step, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var counts = (created, linked, leftAsText);
            try
            {
                var strategy = context.Database.CreateExecutionStrategy();
                return await strategy.ExecuteAsync(async ct =>
                {
                    // A retried attempt starts from the committed state: undo whatever a failed one added.
                    index.Rollback();
                    (created, linked, leftAsText) = counts;

                    await using var transaction = await context.Database.BeginTransactionAsync(ct);
                    var last = await step(after, ct);
                    if (last is not null)
                    {
                        options.BeforeCommit?.Invoke(table, batch);
                    }

                    await transaction.CommitAsync(ct);
                    index.Commit();
                    return last;
                }, cancellationToken);
            }
            catch (MySqlException exception) when (exception.Number == 1062 && attempt < MaxDuplicateRetries)
            {
                // A duplicate the in-memory lookups did not predict: a name the collation folds differently
                // from the comparer, or a row a concurrent writer inserted. The batch rolled back; reserve the
                // refused name, reload what the database now holds and try the batch again.
                index.Rollback();
                (created, linked, leftAsText) = counts;
                if (lastInsertedName is not null)
                {
                    refusedNames.Add(lastInsertedName);
                }

                logger.LogWarning(
                    "Location conversion batch {Batch} of {Table} hit a duplicate key; reloading and retrying (attempt {Attempt}).",
                    batch, table, attempt);
                await LoadIndexAsync(cancellationToken);
            }
            catch
            {
                index.Rollback();
                (created, linked, leftAsText) = counts;
                throw;
            }
        }
    }

    // ── Contact addresses ────────────────────────────────────────────────────

    private async Task<Guid?> ConvertAddressBatchAsync(Guid? after, CancellationToken cancellationToken)
    {
        var rows = await context.Database.SqlQuery<AddressRow>($"""
            SELECT `Id`, `Line1`, `Line2`, `PostalCode`, `City`, `Region`, `CountryCode`
            FROM `Addresses`
            WHERE `LocationId` IS NULL AND ({after} IS NULL OR `Id` > {after})
            ORDER BY `Id`
            LIMIT {options.BatchSize}
            """).ToListAsync(cancellationToken);

        foreach (var row in rows)
        {
            var address = LocationAddress.Of(row.Line1, row.Line2, row.PostalCode, row.City, row.Region, row.CountryCode);
            if (address.IsEmpty)
            {
                continue;
            }

            var locationId = await FindOrCreateAddressAsync(address, LocationSource.ContactAddress, cancellationToken);
            await context.Database.ExecuteSqlAsync(
                $"UPDATE `Addresses` SET `LocationId` = {locationId} WHERE `Id` = {row.Id}", cancellationToken);
            linked++;
        }

        return rows.Count == 0 ? null : rows[^1].Id;
    }

    // ── Property addresses ───────────────────────────────────────────────────

    private async Task<Guid?> ConvertPropertyBatchAsync(Guid? after, CancellationToken cancellationToken)
    {
        var rows = await context.Database.SqlQuery<PropertyRow>($"""
            SELECT `PropertyId`, `AddressLine`, `PostalCode`, `City`, `CountryCode`
            FROM `RealEstateDetails`
            WHERE `LocationId` IS NULL
              AND (`AddressLine` IS NOT NULL OR `PostalCode` IS NOT NULL OR `City` IS NOT NULL OR `CountryCode` IS NOT NULL)
              AND ({after} IS NULL OR `PropertyId` > {after})
            ORDER BY `PropertyId`
            LIMIT {options.BatchSize}
            """).ToListAsync(cancellationToken);

        foreach (var row in rows)
        {
            var address = LocationAddress.Of(row.AddressLine, null, row.PostalCode, row.City, null, row.CountryCode);
            if (address.IsEmpty)
            {
                continue;
            }

            var locationId = await FindOrCreateAddressAsync(address, LocationSource.PropertyAddress, cancellationToken);
            await context.Database.ExecuteSqlAsync(
                $"UPDATE `RealEstateDetails` SET `LocationId` = {locationId} WHERE `PropertyId` = {row.PropertyId}",
                cancellationToken);
            linked++;
        }

        return rows.Count == 0 ? null : rows[^1].PropertyId;
    }

    /// <summary>
    /// By address key, else a new location named by composition. A keyless partial address (no line and no
    /// city) has nothing to deduplicate on and always gets its own location.
    /// </summary>
    private async Task<Guid> FindOrCreateAddressAsync(
        LocationAddress address, LocationSource source, CancellationToken cancellationToken)
    {
        var key = address.Key;
        if (key is not null && index.FindByKey(key) is { } match)
        {
            return match;
        }

        var composed = address.Composed!;
        var name = index.AllocateName(composed);
        var addressText = LocationNaming.Truncate(composed, LocationRules.AddressTextMaxLength);
        return await InsertAsync(name, addressText, address, key, source, cancellationToken);
    }

    // ── Free-text places ─────────────────────────────────────────────────────

    private async Task<Guid?> ConvertTextBatchAsync(
        string table, string keyColumn, Guid? after, CancellationToken cancellationToken)
    {
        // The table and key names are this class's own constants, never input; every value is a parameter.
#pragma warning disable EF1002
        var rows = await context.Database.SqlQueryRaw<TextRow>(
            $"""
            SELECT `{keyColumn}` AS `Id`, `Location` AS `Value`
            FROM `{table}`
            WHERE `LocationId` IS NULL AND `Location` IS NOT NULL AND (@after IS NULL OR `{keyColumn}` > @after)
            ORDER BY `{keyColumn}`
            LIMIT @limit
            """,
            new MySqlParameter("@after", (object?)after ?? DBNull.Value),
            new MySqlParameter("@limit", options.BatchSize)).ToListAsync(cancellationToken);
#pragma warning restore EF1002

        foreach (var row in rows)
        {
            var value = LocationTextCleaner.Clean(row.Value);
            if (value is null)
            {
                continue;
            }

            if (LinkLikeText.IsLinkLike(value))
            {
                leftAsText++;
                continue;
            }

            var locationId = index.FindByText(value)
                ?? await CreateFromTextAsync(value, cancellationToken);

#pragma warning disable EF1002
            await context.Database.ExecuteSqlRawAsync(
                $"UPDATE `{table}` SET `LocationId` = @location, `Location` = NULL WHERE `{keyColumn}` = @id AND `LocationId` IS NULL",
                [new MySqlParameter("@location", locationId), new MySqlParameter("@id", row.Id)],
                cancellationToken);
#pragma warning restore EF1002
            linked++;
        }

        return rows.Count == 0 ? null : rows[^1].Id;
    }

    /// <summary>
    /// <c>Name</c> is the value, truncated and suffixed to fit; the full value goes in <c>AddressText</c>
    /// whenever the name differs from it (R7), so every later occurrence matches this location instead of
    /// minting another suffix.
    /// </summary>
    private Task<Guid> CreateFromTextAsync(string value, CancellationToken cancellationToken)
    {
        var name = index.AllocateName(value);
        var addressText = string.Equals(name, value, StringComparison.Ordinal)
            ? null
            : LocationNaming.Truncate(value, LocationRules.AddressTextMaxLength);
        return InsertAsync(name, addressText, address: null, key: null, LocationSource.Converted, cancellationToken);
    }

    // ── Writes and reads ─────────────────────────────────────────────────────

    private async Task<Guid> InsertAsync(
        string name, string? addressText, LocationAddress? address, string? key, LocationSource source,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        lastInsertedName = name;

        await context.Database.ExecuteSqlAsync($"""
            INSERT INTO `Locations`
                (`LocationId`, `Name`, `AddressText`, `Line1`, `Line2`, `PostalCode`, `City`, `Region`, `CountryCode`,
                 `AddressKey`, `Source`, `CreatedAt`, `UpdatedAt`)
            VALUES
                ({id}, {name}, {addressText}, {address?.Line1}, {address?.Line2}, {address?.PostalCode}, {address?.City},
                 {address?.Region}, {address?.CountryCode}, {key}, {(int)source}, {now}, {now})
            """, cancellationToken);

        index.Add(id, name, addressText, key);
        created++;
        return id;
    }

    private async Task<bool> OldColumnsPresentAsync(CancellationToken cancellationToken)
    {
        var present = await context.Database.SqlQuery<ColumnRow>($"""
            SELECT `TABLE_NAME` AS `TableName`, `COLUMN_NAME` AS `ColumnName`
            FROM information_schema.COLUMNS
            WHERE `TABLE_SCHEMA` = DATABASE() AND `TABLE_NAME` IN ('Addresses', 'RealEstateDetails')
            """).ToListAsync(cancellationToken);

        var set = present.Select(c => (c.TableName, c.ColumnName)).ToHashSet();
        return OldColumns.All(set.Contains);
    }

    private async Task LoadIndexAsync(CancellationToken cancellationToken)
    {
        var rows = await context.Database.SqlQuery<LocationRow>($"""
            SELECT `LocationId`, `Name`, `AddressText`, `AddressKey`, `MergedIntoLocationId`
            FROM `Locations`
            ORDER BY `CreatedAt`, `LocationId`
            """).ToListAsync(cancellationToken);

        index = new LocationIndex();
        foreach (var row in rows)
        {
            index.Add(row.LocationId, row.Name, row.AddressText, row.AddressKey, row.MergedIntoLocationId);
        }

        foreach (var name in refusedNames)
        {
            index.ReserveName(name);
        }

        index.Commit();
    }

    private async Task LogStartCountsAsync(CancellationToken cancellationToken)
    {
        var counts = await context.Database.SqlQuery<CountsRow>($"""
            SELECT
                (SELECT COUNT(*) FROM `Addresses` WHERE `LocationId` IS NULL) AS `Addresses`,
                (SELECT COUNT(*) FROM `RealEstateDetails` WHERE `LocationId` IS NULL
                    AND (`AddressLine` IS NOT NULL OR `PostalCode` IS NOT NULL OR `City` IS NOT NULL OR `CountryCode` IS NOT NULL)) AS `Properties`,
                (SELECT COUNT(*) FROM `CalendarEvents` WHERE `LocationId` IS NULL AND `Location` IS NOT NULL) AS `CalendarEvents`,
                (SELECT COUNT(*) FROM `RecurrencePatterns` WHERE `LocationId` IS NULL AND `Location` IS NOT NULL) AS `RecurrencePatterns`,
                (SELECT COUNT(*) FROM `JournalEntries` WHERE `LocationId` IS NULL AND `Location` IS NOT NULL) AS `JournalEntries`
            """).SingleAsync(cancellationToken);

        logger.LogInformation(
            "Location conversion starting: {ContactAddressCount} contact addresses, {PropertyAddressCount} property addresses, "
            + "{CalendarEventCount} event places, {RecurrencePatternCount} series places and {JournalEntryCount} entry places to convert.",
            counts.Addresses, counts.Properties, counts.CalendarEvents, counts.RecurrencePatterns, counts.JournalEntries);
    }

    // Row shapes for the raw queries. Properties are settable so EF can materialise them.
    private sealed class AddressRow
    {
        public Guid Id { get; set; }
        public string? Line1 { get; set; }
        public string? Line2 { get; set; }
        public string? PostalCode { get; set; }
        public string? City { get; set; }
        public string? Region { get; set; }
        public string? CountryCode { get; set; }
    }

    private sealed class PropertyRow
    {
        public Guid PropertyId { get; set; }
        public string? AddressLine { get; set; }
        public string? PostalCode { get; set; }
        public string? City { get; set; }
        public string? CountryCode { get; set; }
    }

    private sealed class TextRow
    {
        public Guid Id { get; set; }
        public string? Value { get; set; }
    }

    private sealed class LocationRow
    {
        public Guid LocationId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? AddressText { get; set; }
        public string? AddressKey { get; set; }
        public Guid? MergedIntoLocationId { get; set; }
    }

    private sealed class ColumnRow
    {
        public string TableName { get; set; } = string.Empty;
        public string ColumnName { get; set; } = string.Empty;
    }

    private sealed class CountsRow
    {
        public long Addresses { get; set; }
        public long Properties { get; set; }
        public long CalendarEvents { get; set; }
        public long RecurrencePatterns { get; set; }
        public long JournalEntries { get; set; }
    }
}
