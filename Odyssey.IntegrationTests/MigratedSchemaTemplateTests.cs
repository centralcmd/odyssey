using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Odyssey.Context;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// A copy of the migrated template is the same database as one migrated from empty: every private-database test that
/// uses <see cref="MariaDbFixture.CreateMigratedDatabaseAsync"/> rests on this, so it is checked rather than assumed.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class MigratedSchemaTemplateTests(MariaDbFixture fixture)
{
    private const string Migrated = "odyssey_template_check_migrated";
    private const string Copied = "odyssey_template_check_copied";

    [SkippableFact]
    public async Task A_copy_matches_a_database_migrated_from_empty_in_schema_and_rows()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        await using (var server = new MySqlConnection(fixture.OdysseyConnectionString))
        {
            await server.OpenAsync();
            await using var reset = new MySqlCommand($"DROP DATABASE IF EXISTS `{Migrated}`; CREATE DATABASE `{Migrated}`;", server);
            await reset.ExecuteNonQueryAsync();
        }

        var migratedConnection = fixture.ConnectionStringFor(Migrated);
        await using (var context = new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(migratedConnection, ServerVersion.AutoDetect(migratedConnection)).Options))
        {
            await context.Database.MigrateAsync();
        }

        await fixture.CreateMigratedDatabaseAsync(Copied);

        await using var connection = new MySqlConnection(fixture.OdysseyConnectionString);
        await connection.OpenAsync();
        foreach (var (aspect, query) in Aspects)
        {
            var expected = await ReadAsync(connection, query, Migrated);
            var actual = await ReadAsync(connection, query, Copied);
            Assert.True(expected.Count > 0 || aspect is "check constraints", $"No {aspect} read from the migrated database.");
            Assert.True(expected.SequenceEqual(actual),
                $"The copy's {aspect} differ from a migrated database's.\n" +
                $"Only in migrated: {string.Join("; ", expected.Except(actual).Take(10))}\n" +
                $"Only in copy: {string.Join("; ", actual.Except(expected).Take(10))}");
        }

        foreach (var table in await ReadAsync(connection,
                     "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = @schema AND TABLE_TYPE = 'BASE TABLE'", Migrated))
        {
            var rows = await ReadAsync(connection, $"CHECKSUM TABLE `{Migrated}`.`{table}`, `{Copied}`.`{table}`", Migrated, column: 1);
            Assert.True(rows[0] == rows[1], $"The copy's `{table}` rows differ from a migrated database's.");
        }
    }

    /// <summary>Each aspect of a schema, read for one database (<c>@schema</c>) as sorted lines that name no schema.</summary>
    private static readonly (string Aspect, string Query)[] Aspects =
    [
        ("database options",
            "SELECT CONCAT_WS('|', DEFAULT_CHARACTER_SET_NAME, DEFAULT_COLLATION_NAME) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = @schema"),
        ("tables",
            "SELECT CONCAT_WS('|', TABLE_NAME, TABLE_TYPE, ENGINE, ROW_FORMAT, TABLE_COLLATION, CREATE_OPTIONS, TABLE_COMMENT) FROM information_schema.TABLES WHERE TABLE_SCHEMA = @schema"),
        ("columns",
            "SELECT CONCAT_WS('|', TABLE_NAME, ORDINAL_POSITION, COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, IFNULL(COLUMN_DEFAULT, '<null>'), EXTRA, " +
            "IFNULL(CHARACTER_SET_NAME, ''), IFNULL(COLLATION_NAME, ''), IS_GENERATED, IFNULL(GENERATION_EXPRESSION, ''), COLUMN_COMMENT) " +
            "FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = @schema"),
        ("indexes",
            "SELECT CONCAT_WS('|', TABLE_NAME, INDEX_NAME, SEQ_IN_INDEX, COLUMN_NAME, NON_UNIQUE, IFNULL(SUB_PART, ''), IFNULL(COLLATION, ''), INDEX_TYPE, IGNORED) " +
            "FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = @schema"),
        ("constraints",
            "SELECT CONCAT_WS('|', TABLE_NAME, CONSTRAINT_NAME, CONSTRAINT_TYPE) FROM information_schema.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = @schema"),
        ("check constraints",
            "SELECT CONCAT_WS('|', TABLE_NAME, CONSTRAINT_NAME, LEVEL, CHECK_CLAUSE) FROM information_schema.CHECK_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = @schema"),
        ("foreign keys",
            "SELECT CONCAT_WS('|', TABLE_NAME, CONSTRAINT_NAME, REFERENCED_TABLE_NAME, UNIQUE_CONSTRAINT_NAME, UPDATE_RULE, DELETE_RULE, MATCH_OPTION) " +
            "FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = @schema"),
        ("key columns",
            "SELECT CONCAT_WS('|', TABLE_NAME, CONSTRAINT_NAME, ORDINAL_POSITION, COLUMN_NAME, IFNULL(REFERENCED_TABLE_NAME, ''), IFNULL(REFERENCED_COLUMN_NAME, '')) " +
            "FROM information_schema.KEY_COLUMN_USAGE WHERE CONSTRAINT_SCHEMA = @schema"),
    ];

    private static async Task<List<string>> ReadAsync(MySqlConnection connection, string query, string schema, int column = 0)
    {
        await using var command = new MySqlCommand(query, connection);
        command.Parameters.AddWithValue("@schema", schema);
        await using var reader = await command.ExecuteReaderAsync();
        var lines = new List<string>();
        while (await reader.ReadAsync())
        {
            lines.Add(reader.IsDBNull(column) ? "<null>" : Convert.ToString(reader.GetValue(column))!);
        }

        return column == 0 ? lines.Order(StringComparer.Ordinal).ToList() : lines;
    }
}
