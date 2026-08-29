namespace Odyssey.Api;

/// <summary>
/// Refuses to boot a real (network-listening) host under the <c>Testing</c> environment name.
/// </summary>
/// <remarks>
/// <para>
/// <c>Testing</c> is not a deployment environment — it is the in-process name
/// <c>WebApplicationFactory</c> hosts run under, and four separate places key off it to move the app
/// to a materially weaker posture: antiforgery enforcement is skipped on the Identity group and every
/// controller (<c>Program.cs</c>), the database becomes in-memory (<c>DatabaseExtension</c>), demo
/// seeding is allowed (<c>DemoDataSeeder</c>), and password-reset links are written to the log instead
/// of being relayed (<c>SmtpEmailSender</c>). None of that is an authorization bypass —
/// <c>RequireAuthorization()</c> still applies — but "Testing" is a plausible thing for a self-hoster
/// to type meaning "staging", and typing it is a one-variable CSRF kill switch.
/// </para>
/// <para>
/// The signal is the server implementation rather than a configuration flag, because a flag is exactly
/// what the operator in that scenario would also set. <c>WebApplicationFactory</c> substitutes
/// <c>Microsoft.AspNetCore.TestHost.TestServer</c> for Kestrel, so an in-process test host is
/// recognisable without <c>Odyssey.Api</c> taking a reference on the test-host package — the type name
/// is compared as a string on purpose.
/// </para>
/// <para>
/// Deliberately not applied to <c>Odyssey.MigrationService</c>: it is a console host with no server to
/// inspect, and its two <c>Testing</c> branches (in-memory database, demo seeding) leave a real
/// deployment with an empty in-memory database that the API then fails to reach, rather than a weakened
/// live one. <c>docs/deployment.md</c> carries the operator-facing warning for both.
/// </para>
/// </remarks>
public static class TestingEnvironmentGuard
{
    /// <summary>The environment name reserved for in-process test hosts.</summary>
    public const string EnvironmentName = "Testing";

    /// <summary>The server <c>WebApplicationFactory</c> substitutes for Kestrel.</summary>
    public const string TestServerTypeName = "Microsoft.AspNetCore.TestHost.TestServer";

    /// <summary>
    /// Throws when the host is running as <see cref="EnvironmentName"/> without being an in-process
    /// test host. A no-op for every other environment name, and for a genuine test host.
    /// </summary>
    /// <param name="environmentName">The resolved <c>IHostEnvironment.EnvironmentName</c>.</param>
    /// <param name="serverTypeName">
    /// The full type name of the registered <c>IServer</c>, or <see langword="null"/> when none is
    /// registered — which is not a test host either, so it is refused the same way.
    /// </param>
    public static void Validate(string environmentName, string? serverTypeName)
    {
        if (!string.Equals(environmentName, EnvironmentName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.Equals(serverTypeName, TestServerTypeName, StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            $"ASPNETCORE_ENVIRONMENT='{EnvironmentName}' is reserved for the in-process test host and "
            + "must never be used for a deployment: it disables antiforgery (CSRF) enforcement on the "
            + "Identity endpoints and every controller, switches the database to in-memory, enables "
            + "demo-data seeding, and writes password-reset links to the log instead of emailing them. "
            + $"The running server is '{serverTypeName ?? "<none>"}', not "
            + $"'{TestServerTypeName}'. Use 'Staging' for a pre-production deployment, or 'Production' "
            + "for a real one.");
    }
}
