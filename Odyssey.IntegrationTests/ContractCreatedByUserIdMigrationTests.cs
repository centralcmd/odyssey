using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Xunit;
using ContextContractType = Odyssey.Context.ContractType;

namespace Odyssey.IntegrationTests;

/// <summary>
/// <c>AddContractCreatedByUserId</c> applied over a database that already holds contracts — the state
/// every production database is in. The rows must survive untouched and read back with no author,
/// which the read path turns into "no Added-by line" rather than "Unknown user".
/// </summary>
/// <remarks>
/// Unobservable on the fast tiers: the EF InMemory provider has no schema and no migrations, so
/// "an existing row gains a nullable column and keeps everything else" can only be shown here.
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class ContractCreatedByUserIdMigrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_contract_created_by";

    /// <summary>The migration immediately before the one under test.</summary>
    private const string Baseline = "_AddTransactionContactTimeStampIndex";

    private const string Subject = "_AddContractCreatedByUserId";

    [SkippableFact]
    public async Task A_pre_existing_contract_survives_the_upgrade_with_no_author()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var contractId = Guid.NewGuid();
        var createdAt = new DateTime(2026, 3, 1, 9, 30, 0, DateTimeKind.Utc);

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                Assert.False(await ColumnExistsAsync(context));
                await MigrationSeam.InsertContractAsync(context, new Contract
                {
                    ContractId = contractId,
                    Name = "Pre-existing lease",
                    Type = ContextContractType.Rental,
                    StartDate = createdAt,
                    CreatedAtUtc = createdAt,
                });
            }

            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Subject);
                Assert.True(await ColumnExistsAsync(context));

                var contract = await context.Contracts.AsNoTracking().SingleAsync(c => c.ContractId == contractId);
                Assert.Null(contract.CreatedByUserId);
                Assert.Equal("Pre-existing lease", contract.Name);
                Assert.Equal(ContextContractType.Rental, contract.Type);
                Assert.Equal(createdAt, contract.CreatedAtUtc);
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    private static async Task<bool> ColumnExistsAsync(OdysseyContext context) =>
        await MigrationSeam.CountAsync(context, """
            SELECT COUNT(*) FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Contracts' AND COLUMN_NAME = 'CreatedByUserId'
            """) > 0;

    private OdysseyContext NewContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);

    private async Task RecreateAsync()
    {
        await DropAsync();
        await using var server = ServerContext();
        await server.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
    }

    private async Task DropAsync()
    {
        await using var server = ServerContext();
        await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
    }

    private OdysseyContext ServerContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);
}
