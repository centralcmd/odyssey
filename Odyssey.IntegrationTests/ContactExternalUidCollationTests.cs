using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Dtos;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Real-engine coverage for the <c>DropDeprecatedColumnsAndTuneIndexes</c> migration: the binary
/// collation on <see cref="Contact.ExternalUid"/>, the four dropped columns, and the three retuned
/// indexes.
/// </summary>
/// <remarks>
/// None of this is observable on EF InMemory, which has no collations, no column metadata and no
/// indexes — a regression in any of it would pass every fast tier silently.
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class ContactExternalUidCollationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_contact_uid_collation";

    /// <summary>
    /// vCard UIDs are case-sensitive and the import matches them with <c>StringComparer.Ordinal</c>,
    /// so the unique index has to be case-sensitive too. Under the default <c>utf8mb4_*_ci</c>
    /// collation the second row below is refused as a duplicate and vCard import silently resolves it
    /// onto the first as an <em>update</em> rather than creating a new contact.
    /// </summary>
    [SkippableFact]
    public async Task ExternalUid_DistinguishesUidsThatDifferOnlyInCase()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();

        const string lower = "urn:uuid:9f8b7a6c-1d2e-3f40-a5b6-c7d8e9f0a1b2";
        var upper = lower.ToUpperInvariant();

        await using (var context = new OdysseyContext(options))
        {
            Assert.Equal("utf8mb4_bin", await CollationAsync(context, "Contacts", "ExternalUid"));

            context.Contacts.Add(NewContact(lower, "LOWER CASE UID"));
            context.Contacts.Add(NewContact(upper, "UPPER CASE UID"));

            // Under a case-insensitive collation this SaveChanges throws on the unique index.
            await context.SaveChangesAsync();
        }

        await using (var context = new OdysseyContext(options))
        {
            Assert.Equal(2, await context.Contacts.CountAsync());

            // And each resolves to its own row — the lookup that backs vCard import's
            // create-vs-update decision (ContactService.FindIdByExternalUid) uses this same equality.
            var lowerRow = await context.Contacts.AsNoTracking().SingleAsync(c => c.ExternalUid == lower);
            var upperRow = await context.Contacts.AsNoTracking().SingleAsync(c => c.ExternalUid == upper);
            Assert.NotEqual(lowerRow.ContactId, upperRow.ContactId);
            Assert.Equal("LOWER CASE UID", lowerRow.NormalizedName);
            Assert.Equal("UPPER CASE UID", upperRow.NormalizedName);
        }

        // The unique constraint still bites on a genuine duplicate — the collation loosened case
        // sensitivity, not uniqueness.
        await using (var context = new OdysseyContext(options))
        {
            context.Contacts.Add(NewContact(lower, "DUPLICATE"));
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }

        await DropAsync();
    }

    /// <summary>
    /// The four columns the migration drops are gone from the live schema, and the three index
    /// changes landed — including the two superseded indexes being removed rather than left behind as
    /// write cost.
    /// </summary>
    [SkippableFact]
    public async Task TheMigration_DropsTheDeadColumnsAndRetunesTheIndexes()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();

        await using (var context = new OdysseyContext(options))
        {
            Assert.False(await ColumnExistsAsync(context, "Contacts", "LegacyType"));
            Assert.False(await ColumnExistsAsync(context, "Contacts", "OrganizationNumber"));
            Assert.False(await ColumnExistsAsync(context, "FileAnalysisCandidateTransactions", "SourceLineNumber"));
            Assert.False(await ColumnExistsAsync(context, "FileAnalysisCandidateTransactions", "SourcePageNumber"));

            // OrganizationDetails keeps the organisation number — the Contact-level column was a
            // denormalised mirror of it, not the record itself.
            Assert.True(await ColumnExistsAsync(context, "OrganizationDetails", "OrganizationNumber"));

            Assert.Equal(
                ["AccountId", "TimeStamp"],
                await IndexColumnsAsync(context, "Transactions", "IX_Transactions_AccountId_TimeStamp"));
            Assert.Equal(
                ["Status"],
                await IndexColumnsAsync(context, "Transactions", "IX_Transactions_Status"));
            Assert.Equal(
                ["Archived", "TakenAt"],
                await IndexColumnsAsync(context, "Photos", "IX_Photos_Archived_TakenAt"));

            // Both superseded indexes are strict prefixes of their replacements, so keeping them would
            // be pure write amplification.
            Assert.Empty(await IndexColumnsAsync(context, "Transactions", "IX_Transactions_AccountId"));
            Assert.Empty(await IndexColumnsAsync(context, "Photos", "IX_Photos_Archived"));
        }

        await DropAsync();
    }

    private static Contact NewContact(string externalUid, string normalizedName) => new()
    {
        ExternalUid = externalUid,
        NormalizedName = normalizedName,
        Type = ContactType.Organization,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private async Task<DbContextOptions<OdysseyContext>> MigratedSchemaAsync()
    {
        await DropAsync();

        await using (var admin = new OdysseyContext(OptionsFor(fixture.OdysseyConnectionString)))
        {
            await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
        }

        var options = OptionsFor(fixture.ConnectionStringFor(Database));
        await using (var context = new OdysseyContext(options))
        {
            await context.Database.MigrateAsync();
        }

        return options;
    }

    private async Task DropAsync()
    {
        await using var admin = new OdysseyContext(OptionsFor(fixture.OdysseyConnectionString));
        await admin.Database.ExecuteSqlRawAsync("DROP DATABASE IF EXISTS `" + Database + "`");
    }

    private static async Task<string> CollationAsync(OdysseyContext context, string table, string column) =>
        (await context.Database
            .SqlQueryRaw<string>(
                "SELECT COLLATION_NAME AS Value FROM INFORMATION_SCHEMA.COLUMNS "
                + $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}' AND COLUMN_NAME = '{column}'")
            .ToListAsync())
        .Single();

    private static async Task<bool> ColumnExistsAsync(OdysseyContext context, string table, string column) =>
        (await context.Database
            .SqlQueryRaw<string>(
                "SELECT COLUMN_NAME AS Value FROM INFORMATION_SCHEMA.COLUMNS "
                + $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}' AND COLUMN_NAME = '{column}'")
            .ToListAsync())
        .Count is 1;

    private static Task<List<string>> IndexColumnsAsync(OdysseyContext context, string table, string index) =>
        context.Database
            .SqlQueryRaw<string>(
                "SELECT COLUMN_NAME AS Value FROM INFORMATION_SCHEMA.STATISTICS "
                + $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}' AND INDEX_NAME = '{index}' "
                + "ORDER BY SEQ_IN_INDEX")
            .ToListAsync();

    private static DbContextOptions<OdysseyContext> OptionsFor(string connectionString) =>
        new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(connectionString, ServerVersion.AutoDetect(connectionString))
            .Options;
}
