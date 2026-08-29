using Odyssey.Api.Tests.Infrastructure;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The default security posture of the checked-in infrastructure files (issue #451 §1.2, §1.3, §1.5).
///
/// <para>
/// These are contracts that live in text no assembly compiles, and each one regressed silently once
/// already: a port mapping that binds every interface, an override key the app does not read, and a
/// GRANT for a user nothing creates. Same reasoning as
/// <see cref="SecretSettingsInfrastructureTests"/> — the only way to keep them in step is to read them.
/// </para>
/// </summary>
public class PublicDefaultsInfrastructureTests
{
    /// <summary>
    /// The dev stack publishes MariaDB, the API and the client, and it runs in Development with demo
    /// data seeded and a password published in the README. Compose short syntax binds <c>0.0.0.0</c>,
    /// so every one of those mappings has to name the loopback address explicitly.
    /// </summary>
    [Fact]
    public void TheDevComposeStack_PublishesEveryPortOnLoopbackOnly()
    {
        var compose = RepositoryRoot.ReadAllText("docker-compose.yml");

        Assert.Contains("- \"127.0.0.1:3307:3306\"", compose, StringComparison.Ordinal);
        Assert.Contains("- \"127.0.0.1:5188:8080\"", compose, StringComparison.Ordinal);
        Assert.Contains("- \"127.0.0.1:5199:8080\"", compose, StringComparison.Ordinal);

        Assert.DoesNotContain("- \"3307:3306\"", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("- \"5188:8080\"", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("- \"5199:8080\"", compose, StringComparison.Ordinal);
    }

    /// <summary>
    /// The overlay's <c>environment:</c> map is the container's environment, so it must use the key the
    /// app binds. <c>SWAGGER_ENABLED</c> there was inert: the base file's
    /// <c>Swagger__Enabled: ${SWAGGER_ENABLED:-true}</c> is compose-level interpolation from the env
    /// file, and it resolved to false only because <c>.env.prod.example</c> happens to set the variable.
    /// </summary>
    [Fact]
    public void TheProductionOverlay_DisablesSwaggerWithTheKeyTheAppReads()
    {
        var overlay = RepositoryRoot.ReadAllText("docker-compose.prod.yml");

        Assert.Contains("Swagger__Enabled: \"false\"", overlay, StringComparison.Ordinal);

        // As a mapping key, not as prose: the comment above it names the variable it replaced.
        Assert.DoesNotContain(
            overlay.Split('\n').Select(line => line.Trim()),
            line => line.StartsWith("SWAGGER_ENABLED:", StringComparison.Ordinal));
    }

    /// <summary>
    /// The base file falls back to <c>root_password</c> / <c>odyssey_password</c> so a laptop needs no
    /// env file; the overlay has to refuse those fallbacks rather than inherit them. <c>:?</c> covers
    /// unset and empty — a placeholder value is covered by
    /// <see cref="Odyssey.Core.Configuration.PlaceholderConfigurationGuard"/> instead, since compose
    /// interpolation cannot inspect a value.
    /// </summary>
    [Fact]
    public void TheProductionOverlay_RequiresBothDatabasePasswords()
    {
        var overlay = RepositoryRoot.ReadAllText("docker-compose.prod.yml");

        Assert.Contains("MARIADB_ROOT_PASSWORD: ${MARIADB_ROOT_PASSWORD:?", overlay, StringComparison.Ordinal);
        Assert.Contains("MARIADB_PASSWORD: ${MARIADB_PASSWORD:?", overlay, StringComparison.Ordinal);
    }

    /// <summary>
    /// The migrations job runs on <c>Host.CreateApplicationBuilder</c>, which resolves its environment
    /// from <c>DOTNET_ENVIRONMENT</c> and ignores <c>ASPNETCORE_ENVIRONMENT</c> completely, defaulting
    /// to Production. Setting only the ASPNETCORE name left that job running as Production in the dev
    /// stack, which silently disabled demo seeding — <c>DemoDataSeeder</c> seeds only in
    /// Development/Testing, whatever <c>Seed__DemoData</c> says.
    /// </summary>
    /// <remarks>
    /// The overlay has to pin its own value rather than inherit the base file's, for the same reason
    /// the Swagger key does: the base interpolates from the <em>env file</em>, while the overlay map is
    /// the container's environment. An overlay used without a matching <c>.env.prod</c> would otherwise
    /// run the migrations job as Development in production.
    /// </remarks>
    [Fact]
    public void TheMigrationsJob_SetsTheEnvironmentNameItsHostActuallyReads()
    {
        var compose = RepositoryRoot.ReadAllText("docker-compose.yml");
        var overlay = RepositoryRoot.ReadAllText("docker-compose.prod.yml");

        Assert.Contains(
            "DOTNET_ENVIRONMENT: ${ASPNETCORE_ENVIRONMENT:-Development}", compose, StringComparison.Ordinal);
        Assert.Contains("DOTNET_ENVIRONMENT: Production", overlay, StringComparison.Ordinal);
    }

    /// <summary>
    /// This init script is bind-mounted in <em>both</em> stacks — the mount is declared in the base
    /// file and the overlay inherits it — so anything it grants is granted in production. It carried
    /// <c>GRANT ALL PRIVILEGES ON `odyssey`.* TO 'admin'@'%'</c> for a user this repository never
    /// creates.
    /// </summary>
    [Fact]
    public void TheMariaDbInitScript_GrantsNothing()
    {
        var initScript = RepositoryRoot.ReadAllText("docker/mariadb/init/01-init.sql");

        var statements = initScript
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("--", StringComparison.Ordinal));

        Assert.DoesNotContain(statements, statement =>
            statement.StartsWith("GRANT", StringComparison.OrdinalIgnoreCase)
            || statement.StartsWith("CREATE USER", StringComparison.OrdinalIgnoreCase));
    }
}
