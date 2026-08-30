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
    /// The overlay serves both supported deployments, and <c>CADDY_BIND</c> is the single line that
    /// separates them: unset it renders <c>80:80</c> (compose short syntax binds <c>0.0.0.0</c>), and
    /// <c>127.0.0.1:</c> renders <c>127.0.0.1:80:80</c>. Caddy is the only service in the overlay that
    /// publishes a host port at all — every other one carries <c>ports: !reset []</c> — so hardcoding
    /// these two mappings back would put a deployment documented as private on every interface, with
    /// no error anywhere saying so. That is the same silent-exposure regression
    /// <see cref="TheDevComposeStack_PublishesEveryPortOnLoopbackOnly"/> exists to catch, one file over.
    /// </summary>
    /// <remarks>
    /// The trailing colon lives in the <em>value</em>, not the mapping: compose's short syntax is
    /// <c>[HOST_IP:][HOST_PORT:]CONTAINER_PORT</c>, so <c>"${CADDY_BIND}:80:80"</c> would leave a
    /// leading colon and fail to parse when the variable is empty. Asserting the two env templates
    /// here keeps the separator with the only values that supply it — and pins the other half of the
    /// contract, that <c>.env.prod.example</c> leaves the variable unset on purpose.
    /// </remarks>
    [Fact]
    public void TheProductionOverlay_BindsCaddyThroughTheVariableThatMakesADeploymentPrivate()
    {
        var overlay = RepositoryRoot.ReadAllText("docker-compose.prod.yml");

        Assert.Contains("- \"${CADDY_BIND:-}80:80\"", overlay, StringComparison.Ordinal);
        Assert.Contains("- \"${CADDY_BIND:-}443:443\"", overlay, StringComparison.Ordinal);

        Assert.DoesNotContain("- \"80:80\"", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("- \"443:443\"", overlay, StringComparison.Ordinal);

        // The private template supplies the host IP *and* the separator; the public one supplies
        // neither, which is what leaves Caddy on 0.0.0.0.
        var privateEnv = RepositoryRoot.ReadAllText(".env.localhost.example");
        var publicEnv = RepositoryRoot.ReadAllText(".env.prod.example");

        Assert.Contains("CADDY_BIND=127.0.0.1:", privateEnv, StringComparison.Ordinal);
        Assert.DoesNotContain(
            publicEnv.Split('\n').Select(line => line.Trim()),
            line => line.StartsWith("CADDY_BIND=", StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>.gitignore</c> ignores <c>.env.*</c> wholesale so a real env file cannot be committed, and
    /// each template is re-admitted by name. The negation is load-bearing in two ways that both fail
    /// silently: without it the new template is simply never committed, and because git applies the
    /// last matching pattern, moving it <em>above</em> the blanket rule ignores it again while still
    /// looking present in a diff.
    /// </summary>
    [Fact]
    public void TheLocalhostEnvTemplate_IsReadmittedAfterTheBlanketDotenvRule()
    {
        Assert.True(
            File.Exists(Path.Combine(RepositoryRoot.Path, ".env.localhost.example")),
            "The .env.localhost.example template is missing, so the .gitignore negation admits nothing.");

        var lines = RepositoryRoot.ReadAllText(".gitignore")
            .Split('\n')
            .Select(line => line.Trim())
            .ToList();

        var blanketRule = lines.IndexOf(".env.*");
        var negation = lines.IndexOf("!.env.localhost.example");

        Assert.True(blanketRule >= 0, "The blanket `.env.*` ignore rule is gone.");
        Assert.True(negation >= 0, "`!.env.localhost.example` is missing — the template will not be committed.");
        Assert.True(
            negation > blanketRule,
            "`!.env.localhost.example` must come after `.env.*`; git applies the last matching pattern, "
            + "so a negation above the blanket rule is inert.");
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
