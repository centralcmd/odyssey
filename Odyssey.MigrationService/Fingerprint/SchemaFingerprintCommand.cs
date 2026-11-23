using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Odyssey.Core.Configuration;

namespace Odyssey.MigrationService.Fingerprint;

/// <summary>
/// <c>Odyssey.MigrationService --schema-fingerprint</c> (issue #581 §5): prints the operator sections of the
/// schema fingerprint for the configured database and exits, without building the host — so no hosted service,
/// migration or seeder starts, and no logging provider writes to stdout.
/// </summary>
/// <remarks>
/// The session is <c>READ ONLY</c>, the reads are <c>information_schema</c>, <c>SHOW CREATE TABLE</c> and the
/// classified seeded tables, and a failure prints a fixed line and the exception's type name: a connector message
/// can carry the host, port and user.
/// </remarks>
internal static class SchemaFingerprintCommand
{
    public const string Switch = "--schema-fingerprint";

    public const int Succeeded = 0;
    public const int Failed = 1;
    public const int Refused = 2;

    public static bool IsRequested(string[] args) => args.Contains(Switch, StringComparer.Ordinal);

    /// <summary>
    /// The sources the host itself reads the connection string from — the content root's <c>appsettings.json</c>
    /// and its environment-specific sibling, environment variables and the command line — without the host.
    /// </summary>
    public static IConfiguration BuildConfiguration(string[] args)
    {
        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
        return new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddEnvironmentVariables()
            .AddCommandLine(args.Where(arg => arg != Switch).ToArray())
            .Build();
    }

    /// <summary>
    /// Opens the session the mode reads through: every statement after this one runs in a <c>READ ONLY</c>
    /// transaction, so a write to a table fails even when the account could make it.
    /// </summary>
    internal static async Task<MySqlConnection> OpenReadOnlyAsync(string connectionString, CancellationToken ct = default)
    {
        var connection = new MySqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct);
            await using var readOnly = connection.CreateCommand();
            readOnly.CommandText = "SET SESSION TRANSACTION READ ONLY";
            await readOnly.ExecuteNonQueryAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public static async Task<int> RunAsync(
        IConfiguration configuration, TextWriter output, TextWriter error, CancellationToken ct = default)
    {
        if (configuration.GetValue<bool>("UseInMemoryDatabase"))
        {
            await error.WriteLineAsync(
                "Schema fingerprint refused: UseInMemoryDatabase is set, and an in-memory database has no schema.");
            return Refused;
        }

        try
        {
            var connectionString = configuration.GetRequiredConnectionString("OdysseyConnection");
            await using var connection = await OpenReadOnlyAsync(connectionString, ct);

            // Buffered, so a failure part-way prints nothing rather than a truncated fingerprint.
            var buffer = new StringWriter();
            await SchemaFingerprint.WriteOperatorSectionsAsync(connection, buffer, ct);
            await output.WriteAsync(buffer.ToString());
            return Succeeded;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await error.WriteLineAsync(
                $"Schema fingerprint failed: the database could not be read ({exception.GetType().Name}).");
            return Failed;
        }
    }
}
