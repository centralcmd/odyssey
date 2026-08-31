using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Odyssey.Context;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The <c>migrate to N−1 → seed → migrate to head</c> seam (issue #26 Goal 12).
///
/// <para>
/// A data migration cannot be tested by the ordinary fixtures: <c>MigrateAsync()</c> takes the database
/// straight to head, so by the time a test can write a row the migration it is meant to exercise has
/// already run against an empty schema. Everything about a relocation — what it moves, what it skips,
/// what it refuses — is invisible from there. This seam stops at a chosen migration so the "before"
/// state can be built, then completes the run.
/// </para>
///
/// <para>
/// It is also the only place these behaviours are observable at all. The EF InMemory provider has no
/// schema, no migrations, no foreign keys and no <c>CHECK</c> constraints, so every migration criterion
/// in the spec is unrunnable on the fast tiers by construction.
/// </para>
///
/// <para>
/// Migration ids are resolved by their NAME suffix rather than written out with their timestamps. A
/// re-scaffold changes the timestamp and nothing else; a test that hardcoded it would fail for a reason
/// that has nothing to do with what it asserts.
/// </para>
/// </summary>
internal static class MigrationSeam
{
    /// <summary>Runs the database up (or down) to exactly the migration whose name ends with
    /// <paramref name="nameSuffix"/>, leaving every later migration pending.</summary>
    public static async Task MigrateToAsync(OdysseyContext context, string nameSuffix, CancellationToken ct = default)
    {
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(IdOf(context, nameSuffix), ct);
    }

    /// <summary>Reverts everything, i.e. migrates down past the first migration.</summary>
    public static async Task MigrateToEmptyAsync(OdysseyContext context, CancellationToken ct = default)
    {
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(Migration.InitialDatabase, ct);
    }

    /// <summary>The full id (timestamp + name) of the migration whose name ends with the suffix.</summary>
    public static string IdOf(OdysseyContext context, string nameSuffix)
    {
        var ids = context.GetService<IMigrationsAssembly>().Migrations.Keys
            .Where(id => id.EndsWith(nameSuffix, StringComparison.Ordinal))
            .ToList();

        return ids.Count == 1
            ? ids[0]
            : throw new InvalidOperationException(
                $"Expected exactly one migration whose name ends with '{nameSuffix}', found {ids.Count}.");
    }

    /// <summary>Deletes a migration's history row, leaving its schema changes in place — the state an
    /// interrupted run leaves behind, and the only way to ask for a replay of one migration.</summary>
    public static Task ForgetAsync(OdysseyContext context, string nameSuffix, CancellationToken ct = default) =>
        context.Database.ExecuteSqlRawAsync(
            "DELETE FROM `__EFMigrationsHistory` WHERE MigrationId = {0}", [IdOf(context, nameSuffix)], ct);

    public static async Task<bool> HasRunAsync(OdysseyContext context, string nameSuffix, CancellationToken ct = default) =>
        (await context.Database.GetAppliedMigrationsAsync(ct)).Contains(IdOf(context, nameSuffix));

    public static async Task<bool> TableExistsAsync(OdysseyContext context, string table, CancellationToken ct = default)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@table";
        parameter.Value = table;
        command.Parameters.Add(parameter);

        await context.Database.OpenConnectionAsync(ct);
        try
        {
            return Convert.ToInt64(await command.ExecuteScalarAsync(ct)) > 0;
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    /// <summary>One scalar from a raw query — the ledger and the dropped source table have no entity
    /// types, so they can only be read this way.</summary>
    public static async Task<object?> ScalarAsync(OdysseyContext context, string sql, CancellationToken ct = default)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;

        await context.Database.OpenConnectionAsync(ct);
        try
        {
            var value = await command.ExecuteScalarAsync(ct);
            return value == DBNull.Value ? null : value;
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    public static async Task<long> CountAsync(OdysseyContext context, string sql, CancellationToken ct = default) =>
        Convert.ToInt64(await ScalarAsync(context, sql, ct) ?? 0L);

    /// <summary>One row as a dictionary, or null when the query matched nothing.</summary>
    public static async Task<Dictionary<string, object?>?> RowAsync(
        OdysseyContext context, string sql, CancellationToken ct = default)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;

        await context.Database.OpenConnectionAsync(ct);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }

            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
            }

            return row;
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }
}
