using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Pins the user-attribution foreign keys at the database, where the only thing that can satisfy the
/// assertions is the constraint itself.
/// </summary>
/// <remarks>
/// <para>
/// These columns were bare strings for as long as identity lived in its own context, so deleting a
/// user left every one of them naming an account that no longer existed. Folding the contexts together
/// made them declarable, and each one is <c>SET NULL</c>: the rows are SHARED data — a household's
/// photos, journal and attachments — so they must survive the departure of whoever touched them, with
/// the attribution dropped rather than the record. <c>RESTRICT</c> would instead make anyone who has
/// ever created something undeletable, and <c>CASCADE</c> would destroy the shared record.
/// </para>
/// <para>
/// EF InMemory enforces no foreign keys at all, so the fast tiers cannot see any of this — which is
/// exactly why the rule is pinned here, against the real engine. The first test reads
/// <c>information_schema</c> so a single mistyped <c>DeleteBehavior</c> among twenty-three is caught by
/// name; the second deletes a user and watches the columns actually null, so the set of constraints is
/// backed by an observation of them firing.
/// </para>
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class UserAttributionForeignKeyTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_user_attribution";

    /// <summary>Every column that names the user who created, updated, attached, uploaded, requested
    /// or reviewed a row — the full set, so an omission fails as loudly as a wrong rule.</summary>
    private static readonly (string Table, string Column)[] AttributionColumns =
    [
        ("Calendars", "CreatedByUserId"), ("Calendars", "UpdatedByUserId"),
        ("CalendarEvents", "CreatedByUserId"), ("CalendarEvents", "UpdatedByUserId"),
        ("JournalEntries", "CreatedByUserId"), ("JournalEntries", "UpdatedByUserId"),
        ("JournalTasks", "CreatedByUserId"), ("JournalTasks", "UpdatedByUserId"),
        ("Photos", "CreatedByUserId"), ("Photos", "UpdatedByUserId"),
        ("PhotoAlbums", "CreatedByUserId"), ("PhotoAlbums", "UpdatedByUserId"),
        ("RecurrencePatterns", "CreatedByUserId"), ("RecurrencePatterns", "UpdatedByUserId"),
        ("AccountFiles", "AttachedByUserId"),
        ("ContractFiles", "AttachedByUserId"),
        ("TransactionFiles", "AttachedByUserId"),
        ("TaxStatementFiles", "AttachedByUserId"),
        ("PolicyRenewalFiles", "AttachedByUserId"),
        // The relocation ledger from issue #26. It is not an EF entity — it is an operational record
        // of what the migration did — but its attribution column follows the same rule as the other
        // twenty-three, and for the same reason: the ledger must outlive the departure of whoever
        // attached the document it records.
        ("_InsurancePolicyFileRelocation", "AttachedByUserId"),
        ("FileMetadata", "UploadedByUserId"),
        ("FileAnalysisJobs", "RequestedByUserId"),
        ("FileAnalysisCandidateTransactions", "ReviewedByUserId"),
    ];

    [SkippableFact]
    public async Task Every_user_attribution_column_is_a_set_null_foreign_key_to_AspNetUsers()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var connectionString = await MigratedSchemaAsync();
        try
        {
            var rules = await ReadUserForeignKeyRulesAsync(connectionString);

            var wrong = AttributionColumns
                .Select(column => (column, Rule: rules.GetValueOrDefault(column)))
                .Where(row => row.Rule != "SET NULL")
                .Select(row => $"{row.column.Table}.{row.column.Column} => {row.Rule ?? "no foreign key"}")
                .ToList();

            Assert.True(wrong.Count == 0,
                "Every user-attribution column must be a foreign key to AspNetUsers with ON DELETE SET " +
                "NULL, so deleting a user drops the attribution and keeps the shared record. Wrong: " +
                string.Join(", ", wrong));
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// The compliance logs are the deliberate exception and must stay FK-free: they outlive the account
    /// they reference and are pseudonymized in place by <c>UserAdministrationService.DeleteAsync</c>.
    /// A well-meaning follow-up "completing" the set would silently cascade or null them away instead.
    /// </summary>
    [SkippableFact]
    public async Task The_legal_acceptance_logs_carry_no_foreign_key_to_AspNetUsers()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var connectionString = await MigratedSchemaAsync();
        try
        {
            var rules = await ReadUserForeignKeyRulesAsync(connectionString);

            // Not vacuous: the same query must be finding the keys that DO exist, or "no key on the
            // acceptance logs" would pass just as well against a query that returns nothing at all.
            Assert.NotEmpty(rules);

            Assert.DoesNotContain(rules.Keys, key =>
                key.Table is "LicenseAcceptances" or "TermsOfServiceAcceptances");
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// The constraints firing, observed rather than declared: the user row is deleted with raw SQL, so
    /// nothing in application code — no EF fixup, no service sweep — can be what nulls the columns.
    /// </summary>
    [SkippableFact]
    public async Task Deleting_a_user_at_the_database_nulls_their_attribution_and_keeps_the_rows()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        const string userId = "attribution-target";
        var connectionString = await MigratedSchemaAsync();
        try
        {
            var fileId = Guid.NewGuid();
            var blobId = Guid.NewGuid();
            var entryId = Guid.NewGuid();

            await using (var seed = new OdysseyContext(OptionsFor(connectionString)))
            {
                await AttributionUsers.EnsureAsync(seed, userId);

                seed.FileBlob.Add(new FileBlob { Id = blobId, Content = [1, 2, 3] });
                seed.FileMetadata.Add(new FileMetadata
                {
                    Id = fileId,
                    UploadedByUserId = userId,
                    FileName = "statement.pdf",
                    ContentType = "application/pdf",
                    SizeBytes = 3,
                    Sha256Hash = new string('a', 64),
                    FileBlobId = blobId,
                    UploadedAtUtc = DateTime.UtcNow,
                });
                seed.JournalEntries.Add(new JournalEntry
                {
                    JournalEntryId = entryId,
                    ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
                    Title = "A shared entry",
                    Content = "Written by someone who later left.",
                    EntryDate = DateTime.UtcNow.Date,
                    CreatedByUserId = userId,
                    UpdatedByUserId = userId,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });
                await seed.SaveChangesAsync();
            }

            await using (var deleting = new OdysseyContext(OptionsFor(connectionString)))
            {
                await deleting.Database.ExecuteSqlAsync(
                    $"DELETE FROM `AspNetUsers` WHERE `Id` = {userId}");
            }

            await using var verify = new OdysseyContext(OptionsFor(connectionString));

            // The shared records survive — that is the whole point of SET NULL over CASCADE...
            var file = await verify.FileMetadata.AsNoTracking().SingleAsync(f => f.Id == fileId);
            var entry = await verify.JournalEntries.AsNoTracking().SingleAsync(e => e.JournalEntryId == entryId);
            Assert.True(await verify.FileBlob.AsNoTracking().AnyAsync(b => b.Id == blobId));

            // ...and only the attribution is gone.
            Assert.Null(file.UploadedByUserId);
            Assert.Null(entry.CreatedByUserId);
            Assert.Null(entry.UpdatedByUserId);
        }
        finally
        {
            await DropAsync();
        }
    }

    private static async Task<Dictionary<(string Table, string Column), string>> ReadUserForeignKeyRulesAsync(
        string connectionString)
    {
        await using var context = new OdysseyContext(OptionsFor(connectionString));

        var rows = await context.Database
            .SqlQuery<ForeignKeyRule>($"""
                SELECT k.TABLE_NAME AS TableName, k.COLUMN_NAME AS ColumnName, r.DELETE_RULE AS DeleteRule
                FROM information_schema.KEY_COLUMN_USAGE k
                JOIN information_schema.REFERENTIAL_CONSTRAINTS r
                  ON r.CONSTRAINT_SCHEMA = k.CONSTRAINT_SCHEMA
                 AND r.CONSTRAINT_NAME = k.CONSTRAINT_NAME
                WHERE k.CONSTRAINT_SCHEMA = DATABASE()
                  AND k.REFERENCED_TABLE_NAME = 'AspNetUsers'
                """)
            .ToListAsync();

        return rows.ToDictionary(row => (row.TableName, row.ColumnName), row => row.DeleteRule);
    }

    private async Task<string> MigratedSchemaAsync()
    {
        await DropAsync();

        await using (var admin = new OdysseyContext(OptionsFor(fixture.OdysseyConnectionString)))
        {
            await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
        }

        var connectionString = fixture.ConnectionStringFor(Database);
        await using var context = new OdysseyContext(OptionsFor(connectionString));
        await context.Database.MigrateAsync();

        return connectionString;
    }

    private async Task DropAsync()
    {
        await using var admin = new OdysseyContext(OptionsFor(fixture.OdysseyConnectionString));
        await admin.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
    }

    private static DbContextOptions<OdysseyContext> OptionsFor(string connectionString) =>
        new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(connectionString, ServerVersion.AutoDetect(connectionString))
            .Options;

    private sealed record ForeignKeyRule(string TableName, string ColumnName, string DeleteRule);
}
