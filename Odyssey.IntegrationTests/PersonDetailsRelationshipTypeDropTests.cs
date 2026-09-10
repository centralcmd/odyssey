using Microsoft.EntityFrameworkCore;
using Context = Odyssey.Context;
using Odyssey.Context;
using Odyssey.MigrationService;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The <c>PersonDetails.RelationshipType</c> column drop (issue #52 AC #3 and #4).
/// </summary>
[Collection(MariaDbCollection.Name)]
public class PersonDetailsRelationshipTypeDropTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_relationship_drop";

    /// <summary>
    /// AC #3 — applying every migration from empty leaves a <c>PersonDetails</c> table with no
    /// <c>RelationshipType</c> column, and with each surviving column still present and correctly
    /// typed.
    /// </summary>
    /// <remarks>
    /// Real MariaDB, because the assertion is about a SCHEMA: EF InMemory has no
    /// <c>information_schema</c>, no migrations and no columns to be missing, so it would report this
    /// green whatever the migration did.
    /// </remarks>
    [SkippableFact]
    public async Task Applying_the_migrations_leaves_PersonDetails_without_the_relationship_column()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateDatabaseAsync();

        try
        {
            await using var context = new OdysseyContext(OdysseyOptions());
            await MigrationRunner.MigrateAsync(context, CancellationToken.None);

            var columns = await ColumnsAsync(context, "PersonDetails");

            Assert.DoesNotContain("RelationshipType", columns.Keys);

            // The neighbours the drop must not have taken with it — including the shared primary key,
            // since PersonDetails has no Id of its own.
            Assert.Equal("char", columns["ContactId"]);
            Assert.Equal("varchar", columns["FirstName"]);
            Assert.Equal("varchar", columns["LastName"]);
            Assert.Equal("date", columns["DateOfBirth"]);
            Assert.Equal("int", columns["Sex"]);
            Assert.Equal("varchar", columns["Title"]);
            Assert.Equal("varchar", columns["Company"]);

            // The "and nothing else" half, derived from the model rather than hardcoded. A literal
            // count was the original spelling and it was wrong: it went stale the moment issue #48
            // added MiddleName and DateOfDeath to this table, and it would have gone stale again on
            // the next person field. Comparing against what EF maps keeps the assertion honest —
            // a column the migrations create but the model does not know about, or vice versa, still
            // fails — without re-breaking on every unrelated addition.
            Assert.Equal(MappedColumns(context), columns.Keys.Order(StringComparer.Ordinal));
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The column names EF maps for <see cref="Context.PersonDetails"/>, ordered — the model's own
    /// account of what this table should hold.
    /// </summary>
    private static IEnumerable<string> MappedColumns(OdysseyContext context) =>
        context.Model
            .FindEntityType(typeof(Context.PersonDetails))!
            .GetProperties()
            .Select(property => property.GetColumnName())
            .Order(StringComparer.Ordinal);

    /// <summary>Each column of <paramref name="table"/> mapped to its engine data type.</summary>
    private static async Task<Dictionary<string, string>> ColumnsAsync(OdysseyContext context, string table)
    {
        var connection = context.Database.GetDbConnection();
        await context.Database.OpenConnectionAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COLUMN_NAME, DATA_TYPE FROM information_schema.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table";

            var parameter = command.CreateParameter();
            parameter.ParameterName = "@table";
            parameter.Value = table;
            command.Parameters.Add(parameter);

            var columns = new Dictionary<string, string>(StringComparer.Ordinal);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns[reader.GetString(0)] = reader.GetString(1);
            }

            return columns;
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    private async Task RecreateDatabaseAsync()
    {
        await using var admin = AdminContext();

        await admin.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
        await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
    }

    private async Task DropDatabaseAsync()
    {
        await using var admin = AdminContext();

        await admin.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
    }

    private OdysseyContext AdminContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(
                fixture.OdysseyConnectionString,
                ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);

    private DbContextOptions<OdysseyContext> OdysseyOptions()
    {
        var connection = fixture.ConnectionStringFor(Database);
        return new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(connection, ServerVersion.AutoDetect(connection)).Options;
    }
}
