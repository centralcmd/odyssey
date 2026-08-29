using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Dtos.Application;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Real-engine coverage for the parts of issue #354's data model that EF InMemory structurally cannot
/// check: the three delete rules (a <em>missing</em> FK is as load-bearing here as the two present
/// ones), the <c>longtext</c> column actually holding 50,000 characters, the composite indexes landing
/// in MariaDB, and the pseudonymize-with-delete transaction rolling back as one unit.
/// </summary>
/// <remarks>
/// InMemory has no foreign keys, no column types and no real transactions, so every assertion below
/// would either pass vacuously or be untestable there. The API-tier tests
/// (<c>Odyssey.Api.Tests.LegalAcceptancePseudonymizationTests</c>) cover the application behaviour on
/// top of these guarantees.
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class LegalAcceptanceRelationalTests(MariaDbFixture fixture)
{
    private const string LegalDatabase = "odyssey_legal_acceptance";

    [SkippableFact]
    public async Task TheSchema_LandsWithItsCompositeIndexesAndAFiftyThousandCharacterContentColumn()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();

        await using (var context = new OdysseyContext(options))
        {
            Assert.Equal("longtext", await ColumnTypeAsync(context, "TermsOfServiceVersions", "Content"));
            Assert.Equal(255, await ColumnLengthAsync(context, "LicenseAcceptances", "UserId"));
            Assert.Equal(64, await ColumnLengthAsync(context, "LicenseAcceptances", "LicenseHash"));
            Assert.Equal(255, await ColumnLengthAsync(context, "TermsOfServiceAcceptances", "UserId"));

            Assert.Equal(
                ["UserId", "LicenseHash", "RespondedAt"],
                await IndexColumnsAsync(context, "LicenseAcceptances", "IX_LicenseAcceptances_UserId_LicenseHash_RespondedAt"));
            Assert.Equal(
                ["UserId", "TermsOfServiceVersionId", "RespondedAt"],
                await IndexColumnsAsync(
                    context,
                    "TermsOfServiceAcceptances",
                    "IX_TermsOfServiceAcceptances_UserId_TermsOfServiceVersionId_Res~"));
        }

        // AC 14 — the full cap round-trips, so the validation limit is the real limit and not a column
        // truncation waiting to happen.
        var content = new string('x', LegalLimits.MaxTermsOfServiceContentLength);
        await using (var context = new OdysseyContext(options))
        {
            context.TermsOfServiceVersions.Add(new TermsOfServiceVersion
            {
                Content = content,
                PublishedAt = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        await using (var context = new OdysseyContext(options))
        {
            Assert.Equal(
                LegalLimits.MaxTermsOfServiceContentLength,
                (await context.TermsOfServiceVersions.AsNoTracking().SingleAsync()).Content.Length);
        }

        await DropAsync();
    }

    /// <summary>
    /// The three delete rules, asserted as behaviour rather than as metadata:
    /// acceptance rows have no FK at all and survive their user; a published version survives its
    /// publisher with a null publisher id (<c>SetNull</c>); and a version that has been responded to
    /// cannot be deleted (<c>Restrict</c>).
    /// </summary>
    [SkippableFact]
    public async Task TheDeleteRules_LetComplianceRecordsOutliveAccountsAndPinVersionsThatWereRespondedTo()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();

        const string publisherId = "legal-publisher";
        const string responderId = "legal-responder";
        int versionId;

        await using (var context = new OdysseyContext(options))
        {
            await InsertUserAsync(context, publisherId, "publisher@legal.test");
            await InsertUserAsync(context, responderId, "responder@legal.test");

            var version = new TermsOfServiceVersion
            {
                Content = "Terms v1",
                PublishedAt = DateTime.UtcNow,
                PublishedByUserId = publisherId,
            };
            context.TermsOfServiceVersions.Add(version);
            await context.SaveChangesAsync();
            versionId = version.Id;

            context.LicenseAcceptances.Add(new LicenseAcceptance
            {
                UserId = responderId,
                LicenseHash = new string('a', 64),
                Accepted = true,
                RespondedAt = DateTime.UtcNow,
            });
            context.TermsOfServiceAcceptances.Add(new TermsOfServiceAcceptance
            {
                UserId = responderId,
                TermsOfServiceVersionId = versionId,
                Accepted = true,
                RespondedAt = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        // Deleting both users: the acceptance rows have no FK, so nothing cascades them away...
        await using (var context = new OdysseyContext(options))
        {
            context.Users.RemoveRange(
                await context.Users.Where(user => user.Id == publisherId || user.Id == responderId).ToListAsync());
            await context.SaveChangesAsync();
        }

        await using (var context = new OdysseyContext(options))
        {
            Assert.Equal(1, await context.LicenseAcceptances.CountAsync());
            Assert.Equal(1, await context.TermsOfServiceAcceptances.CountAsync());

            // ...while the version survives its publisher with the id nulled out rather than the row gone.
            var version = await context.TermsOfServiceVersions.AsNoTracking().SingleAsync();
            Assert.Equal("Terms v1", version.Content);
            Assert.Null(version.PublishedByUserId);
        }

        // Restrict: a version someone has responded to cannot be deleted out from under that record.
        await using (var context = new OdysseyContext(options))
        {
            context.TermsOfServiceVersions.Remove(
                await context.TermsOfServiceVersions.SingleAsync(version => version.Id == versionId));

            await Assert.ThrowsAnyAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }

        await DropAsync();
    }

    /// <summary>
    /// AC 12 (rollback half) — the pseudonymization and the deletion commit together or not at all. A
    /// real transaction is the only way to observe this; InMemory's is a no-op, so a regression here
    /// would be invisible to every other tier.
    /// </summary>
    [SkippableFact]
    public async Task PseudonymizationAndDeletion_RollBackTogetherWhenTheTransactionFails()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();

        const string userId = "legal-rollback-user";
        const string pseudonym = "0123456789abcdef";

        await using (var context = new OdysseyContext(options))
        {
            await InsertUserAsync(context, userId, "rollback@legal.test");
            context.LicenseAcceptances.Add(new LicenseAcceptance
            {
                UserId = userId,
                LicenseHash = new string('b', 64),
                Accepted = true,
                RespondedAt = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        await using (var context = new OdysseyContext(options))
        {
            var strategy = context.Database.CreateExecutionStrategy();

            await Assert.ThrowsAsync<InvalidOperationException>(() => strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await context.Database.BeginTransactionAsync();

                var row = await context.LicenseAcceptances.SingleAsync(entry => entry.UserId == userId);
                row.UserId = pseudonym;
                await context.SaveChangesAsync();

                context.Users.Remove(await context.Users.SingleAsync(user => user.Id == userId));
                await context.SaveChangesAsync();

                // Stands in for the deletion failing after the pseudonymization already wrote.
                throw new InvalidOperationException("Simulated deletion failure.");
            }));
        }

        await using (var context = new OdysseyContext(options))
        {
            // Neither half survived: the user is still live and their record still points at them.
            Assert.True(await context.Users.AnyAsync(user => user.Id == userId));
            Assert.Equal(userId, (await context.LicenseAcceptances.AsNoTracking().SingleAsync()).UserId);
        }

        await DropAsync();
    }

    private async Task<DbContextOptions<OdysseyContext>> MigratedSchemaAsync()
    {
        await DropAsync();

        await using (var admin = new OdysseyContext(OptionsFor(fixture.OdysseyConnectionString)))
        {
            await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{LegalDatabase}`");
        }

        var options = OptionsFor(fixture.ConnectionStringFor(LegalDatabase));
        await using (var context = new OdysseyContext(options))
        {
            await context.Database.MigrateAsync();
        }

        return options;
    }

    private async Task DropAsync()
    {
        await using var admin = new OdysseyContext(OptionsFor(fixture.OdysseyConnectionString));
        await admin.Database.ExecuteSqlRawAsync("DROP DATABASE IF EXISTS `" + LegalDatabase + "`");
    }

    private static Task InsertUserAsync(OdysseyContext context, string id, string email) =>
        context.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO `AspNetUsers`
                 (`Id`, `UserName`, `NormalizedUserName`, `Email`, `NormalizedEmail`, `EmailConfirmed`,
                  `PasswordHash`, `SecurityStamp`, `ConcurrencyStamp`, `PhoneNumberConfirmed`,
                  `TwoFactorEnabled`, `LockoutEnabled`, `AccessFailedCount`, `MustChangePassword`)
             VALUES
                 ({id}, {email}, {email.ToUpperInvariant()}, {email}, {email.ToUpperInvariant()}, 1,
                  'hash', 'stamp', 'concurrency', 0, 0, 1, 0, 0)
             """);

    private static async Task<string> ColumnTypeAsync(OdysseyContext context, string table, string column) =>
        (await context.Database
            .SqlQueryRaw<string>(
                "SELECT DATA_TYPE AS Value FROM INFORMATION_SCHEMA.COLUMNS "
                + $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}' AND COLUMN_NAME = '{column}'")
            .ToListAsync())
        .Single();

    private static async Task<long> ColumnLengthAsync(OdysseyContext context, string table, string column) =>
        (await context.Database
            .SqlQueryRaw<long>(
                "SELECT CHARACTER_MAXIMUM_LENGTH AS Value FROM INFORMATION_SCHEMA.COLUMNS "
                + $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}' AND COLUMN_NAME = '{column}'")
            .ToListAsync())
        .Single();

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
