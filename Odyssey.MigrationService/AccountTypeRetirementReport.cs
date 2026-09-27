using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Odyssey.MigrationService;

/// <summary>
/// What <c>RetirePropertyAndVehicleAccountTypes</c> will move (issue #218 §9), counted before it runs
/// and logged once after it succeeds. Counts only — never a name, number or amount.
/// </summary>
/// <remarks>
/// <para>
/// The migration cannot report this itself: a <c>migrationBuilder.Sql</c> step has no route to the
/// application log. And it has to be read <em>before</em> the migration — afterwards the accounts it
/// counts are gone — so it is a read-only query over the pre-migration schema, taken only while that
/// migration is pending.
/// </para>
/// <para>
/// The retained-account count matters beyond bookkeeping: each such account keeps its transaction
/// balance in the totals while its estimates now sit on a property, so the same asset can be counted
/// twice until the user closes or deletes the account. The log line is what makes that visible.
/// </para>
/// </remarks>
public sealed record AccountTypeRetirementReport(
    long RealEstateProperties,
    long VehicleProperties,
    long RetainedAccounts,
    long Estimates,
    long SmartTags,
    long FileLinks,
    long ContractParties,
    long FileAnalysisJobsRemoved,
    long ClosedBeforeOpened,
    long CurrencyMismatchedEstimates)
{
    /// <summary>The migration's name suffix; matched the way <c>MigrationSeam</c> matches it.</summary>
    public const string MigrationName = "_RetirePropertyAndVehicleAccountTypes";

    private const string Retired = "a.`AccountType` IN (6, 7)";

    private const string HasTransactions =
        "EXISTS (SELECT 1 FROM `Transactions` t WHERE t.`AccountId` = a.`AccountId`)";

    private const string Sql = $"""
        SELECT
            (SELECT COUNT(*) FROM `Accounts` a WHERE a.`AccountType` = 6),
            (SELECT COUNT(*) FROM `Accounts` a WHERE a.`AccountType` = 7),
            (SELECT COUNT(*) FROM `Accounts` a WHERE {Retired} AND {HasTransactions}),
            (SELECT COUNT(*) FROM `AccountEstimates` e JOIN `Accounts` a ON a.`AccountId` = e.`AccountId` WHERE {Retired}),
            (SELECT COUNT(*) FROM `AccountSmartTags` s JOIN `Accounts` a ON a.`AccountId` = s.`AccountId` WHERE {Retired}),
            (SELECT COUNT(*) FROM `AccountFiles` f JOIN `Accounts` a ON a.`AccountId` = f.`AccountId` WHERE {Retired}),
            (SELECT COUNT(*) FROM `ContractParties` p JOIN `Accounts` a ON a.`AccountId` = p.`AccountId` WHERE {Retired}),
            (SELECT COUNT(*) FROM `FileAnalysisJobs` j
               JOIN `AccountFiles` f ON f.`Id` = j.`AccountFileId`
               JOIN `Accounts` a ON a.`AccountId` = f.`AccountId`
              WHERE {Retired} AND NOT {HasTransactions}),
            (SELECT COUNT(*) FROM `Accounts` a WHERE {Retired} AND a.`Closed` < a.`Opened`),
            (SELECT COUNT(*) FROM `AccountEstimates` e JOIN `Accounts` a ON a.`AccountId` = e.`AccountId`
              WHERE {Retired} AND e.`CurrencyCode` IS NOT NULL AND e.`CurrencyCode` <> a.`CurrencyCode`)
        """;

    /// <summary>
    /// The counts, or <c>null</c> when there is nothing to report: a non-relational context, the
    /// migration already applied, or a database too new to hold the tables (an empty one, where the
    /// whole history is pending).
    /// </summary>
    public static async Task<AccountTypeRetirementReport?> ReadIfPendingAsync(
        DbContext dbContext, CancellationToken cancellationToken)
    {
        if (!dbContext.Database.IsRelational())
        {
            return null;
        }

        var pending = await dbContext.Database.GetPendingMigrationsAsync(cancellationToken);
        if (!pending.Any(id => id.EndsWith(MigrationName, StringComparison.Ordinal)))
        {
            return null;
        }

        // Every migration pending means an empty database: none of the tables exist yet to count.
        var all = dbContext.GetService<IMigrationsAssembly>().Migrations.Keys;
        if (pending.Count() == all.Count())
        {
            return null;
        }

        await dbContext.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = dbContext.Database.GetDbConnection().CreateCommand();
            command.CommandText = Sql;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);

            return new AccountTypeRetirementReport(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7),
                reader.GetInt64(8), reader.GetInt64(9));
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }

    /// <summary>The one <c>Information</c> line, after the migration has committed.</summary>
    public void Log(ILogger logger) =>
        logger.LogInformation(
            "Retired the Property and Vehicle account types: created {RealEstateProperties} real-estate and "
            + "{VehicleProperties} vehicle properties; kept {RetainedAccounts} accounts that hold transactions as "
            + "archived Other-asset accounts (their balance may now overlap the property's estimate); moved "
            + "{Estimates} estimates, {SmartTags} smart tags, {FileLinks} file links and {ContractParties} contract "
            + "parties; removed {FileAnalysisJobsRemoved} file-analysis jobs; kept {ClosedBeforeOpened} closed dates "
            + "earlier than the opened date in notes; {CurrencyMismatchedEstimates} estimates were in a currency "
            + "other than their account's.",
            RealEstateProperties, VehicleProperties, RetainedAccounts, Estimates, SmartTags, FileLinks,
            ContractParties, FileAnalysisJobsRemoved, ClosedBeforeOpened, CurrencyMismatchedEstimates);
}
