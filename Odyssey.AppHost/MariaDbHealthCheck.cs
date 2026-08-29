using Microsoft.Extensions.Diagnostics.HealthChecks;
using MySqlConnector;

namespace Odyssey.AppHost;

/// <summary>
/// Readiness probe for the MariaDB container. A bare TCP connect is not enough: during MariaDB's
/// first-time initialization the port can begin accepting sockets before the server can complete the
/// protocol handshake, so anything that <c>WaitFor</c>s the container (notably the migration runner's
/// eager <c>ServerVersion.AutoDetect</c>) races in and fails with
/// <c>"An incomplete response was received from the server"</c>. This opens a real authenticated
/// connection and pings, so the resource only reports healthy once queries actually work — closing
/// that race for the migration and API resources.
/// </summary>
public sealed class MariaDbHealthCheck : IHealthCheck
{
    private readonly string connectionString;

    public MariaDbHealthCheck(string connectionString)
    {
        this.connectionString = connectionString;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(timeoutCts.Token);
            var responded = await connection.PingAsync(timeoutCts.Token);

            return responded
                ? HealthCheckResult.Healthy("MariaDB is accepting authenticated connections.")
                : HealthCheckResult.Unhealthy("MariaDB did not respond to ping.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("MariaDB is not ready for connections.", ex);
        }
    }
}
