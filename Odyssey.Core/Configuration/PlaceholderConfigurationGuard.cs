using Microsoft.Extensions.Configuration;

namespace Odyssey.Core.Configuration;

/// <summary>
/// Refuses to start a Production host that is still carrying a placeholder value copied out of
/// <c>.env.prod.example</c> (issue #451 §1.4).
/// </summary>
/// <remarks>
/// <para>
/// The compose-level <c>${VAR:?...}</c> guards only catch <em>unset or empty</em>. A placeholder such
/// as <c>CHANGE_ME_strong_app_password</c> is a perfectly valid value, so an operator who edited only
/// <c>ODYSSEY_DOMAIN</c> and <c>GHCR_OWNER</c> would boot with a database password published in this
/// repository, and create the first administrator from a published one-time credential.
/// </para>
/// <para>
/// This guards <em>configuration</em>, so it covers only what is still read from configuration. The
/// five application credentials moved into the encrypted secret store (issue #445), and their
/// configuration properties were deleted rather than kept — a placeholder in those variables is inert,
/// and refusing a deploy over a value nothing reads would be a false alarm. What remains is the
/// database connection strings and the bootstrap administrator.
/// </para>
/// <para>
/// Production only. The dev stack ships none of these values, and a developer pasting an example value
/// locally should not be blocked by a guard aimed at a public deployment. The environment name is a
/// <em>parameter</em> rather than an <c>if</c> at the call site, following
/// <c>TestingEnvironmentGuard.Validate</c>: a gate written into the caller is a branch no test can
/// reach, and inverting it would be silent. Passing the name also keeps this project free of a
/// hosting-abstractions reference.
/// </para>
/// <para>
/// The message names <em>keys</em> and never values: several of the guarded keys are secrets, and this
/// text reaches logs and container output. Same rule the secret settings surface follows.
/// </para>
/// </remarks>
public static class PlaceholderConfigurationGuard
{
    /// <summary>The marker every placeholder in <c>.env.prod.example</c> carries.</summary>
    public const string PlaceholderMarker = "CHANGE_ME";

    /// <summary>The only environment this guard applies to.</summary>
    /// <remarks>
    /// A literal rather than <c>Environments.Production</c> so this project needs no hosting reference,
    /// the same trade <c>TestingEnvironmentGuard.TestServerTypeName</c> makes. A test asserts the two
    /// still agree.
    /// </remarks>
    public const string GuardedEnvironmentName = "Production";

    /// <summary>
    /// Security-relevant configuration keys inspected for <see cref="PlaceholderMarker"/>.
    /// </summary>
    /// <remarks>
    /// Substring, not prefix: the database password reaches the app embedded in a connection string
    /// (<c>…;password=CHANGE_ME_strong_app_password;</c>), never as a value of its own.
    /// <c>MARIADB_ROOT_PASSWORD</c> is deliberately absent — no host reads it, so it is guarded at the
    /// compose layer instead. The <c>Bootstrap:Admin:*</c> keys are only ever present on the migrations
    /// job; a key that is absent simply cannot be a placeholder, so one shared list serves both hosts.
    /// The migrated credentials are absent for the reason in the type remarks: nothing reads them.
    /// </remarks>
    public static IReadOnlyList<string> GuardedKeys { get; } =
    [
        "ConnectionStrings:OdysseyConnection",
        "Bootstrap:Admin:Email",
        "Bootstrap:Admin:Password"
    ];

    /// <summary>Returns the <see cref="GuardedKeys"/> whose configured value still carries the marker.</summary>
    public static IReadOnlyList<string> FindPlaceholderKeys(this IConfiguration configuration) =>
    [
        .. GuardedKeys.Where(key =>
            configuration[key]?.Contains(PlaceholderMarker, StringComparison.OrdinalIgnoreCase) == true)
    ];

    /// <summary>
    /// Throws if the host is running as <see cref="GuardedEnvironmentName"/> and any
    /// <see cref="GuardedKeys"/> value still carries the marker. Outside that environment it does
    /// nothing, so callers wire it unconditionally.
    /// </summary>
    /// <param name="configuration">The configuration to inspect.</param>
    /// <param name="environmentName">
    /// The host's environment name. Compared case-insensitively, because <c>IHostEnvironment</c> does —
    /// otherwise <c>production</c> would get none of the protection.
    /// </param>
    /// <exception cref="InvalidOperationException">A placeholder value is still configured.</exception>
    public static void ThrowIfPlaceholderValues(this IConfiguration configuration, string environmentName)
    {
        if (!string.Equals(environmentName, GuardedEnvironmentName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var offenders = configuration.FindPlaceholderKeys();
        if (offenders.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Refusing to start in Production: {offenders.Count} configuration value(s) still contain the "
            + $"'{PlaceholderMarker}' placeholder from .env.prod.example — "
            + string.Join(", ", offenders)
            + ". Replace every CHANGE_ME value in your .env.prod with a real one "
            + "(generate secrets with `openssl rand -base64 48`). These placeholders are published in "
            + "this repository, so they are equivalent to having no secret at all.");
    }
}
