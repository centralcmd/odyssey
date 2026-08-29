using Xunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Core.Configuration;

namespace Odyssey.Api.Tests;

// Issue #451 §1.4. The compose `${VAR:?...}` guards only catch unset/empty, and every placeholder in
// .env.prod.example was a valid, publicly-known value — so this guard is what stands between an
// operator who edited only ODYSSEY_DOMAIN and a Production instance running on a published database
// password and a published one-time administrator credential. It covers configuration only; the five
// application credentials moved into the encrypted secret store (issue #445) and are not read from
// configuration at all, so a placeholder left in those variables is inert.
public class PlaceholderConfigurationGuardTests
{
    [Fact]
    public void ThrowIfPlaceholderValues_Passes_WhenNothingIsAPlaceholder()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["ConnectionStrings:OdysseyConnection"] = "server=mariadb;database=odyssey;password=s3cret;",
            ["Bootstrap:Admin:Email"] = "admin@example.com"
        });

        configuration.ThrowIfPlaceholderValues("Production");
    }

    // An absent key cannot be a placeholder — which is what lets one shared list serve both the API
    // and the migrations job, only one of which ever sees Bootstrap:Admin:*.
    [Fact]
    public void ThrowIfPlaceholderValues_Passes_WhenEveryGuardedKeyIsAbsent()
    {
        BuildConfiguration([]).ThrowIfPlaceholderValues("Production");
    }

    // The database password reaches the app embedded in a connection string, never as a value of its
    // own, so the match has to be a substring rather than a prefix.
    [Fact]
    public void ThrowIfPlaceholderValues_Throws_WhenThePlaceholderIsEmbeddedInAConnectionString()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["ConnectionStrings:OdysseyConnection"] =
                "server=mariadb;database=odyssey;user=odyssey;password=CHANGE_ME_strong_app_password;"
        });

        var exception = Assert.Throws<InvalidOperationException>(() => configuration.ThrowIfPlaceholderValues("Production"));

        Assert.Contains("ConnectionStrings:OdysseyConnection", exception.Message);
    }

    [Theory]
    [InlineData("ConnectionStrings:OdysseyConnection")]
    [InlineData("Bootstrap:Admin:Email")]
    [InlineData("Bootstrap:Admin:Password")]
    public void ThrowIfPlaceholderValues_Throws_ForEveryGuardedSecret(string key)
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [key] = "CHANGE_ME_long_random_value"
        });

        var exception = Assert.Throws<InvalidOperationException>(() => configuration.ThrowIfPlaceholderValues("Production"));

        Assert.Contains(key, exception.Message);
    }

    [Fact]
    public void ThrowIfPlaceholderValues_NamesEveryOffender()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Bootstrap:Admin:Email"] = "CHANGE_ME_admin@example.com",
            ["Bootstrap:Admin:Password"] = "CHANGE_ME_one_time_password",
            ["ConnectionStrings:OdysseyConnection"] = "server=mariadb;database=odyssey;password=s3cret;"
        });

        var exception = Assert.Throws<InvalidOperationException>(() => configuration.ThrowIfPlaceholderValues("Production"));

        Assert.Contains("Bootstrap:Admin:Email", exception.Message);
        Assert.Contains("Bootstrap:Admin:Password", exception.Message);
        Assert.DoesNotContain("ConnectionStrings:OdysseyConnection", exception.Message);
    }

    // The message reaches container logs, and most guarded keys are secrets. It names keys only —
    // the same rule the encrypted secret settings surface follows.
    [Fact]
    public void ThrowIfPlaceholderValues_NeverEchoesTheValue()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Bootstrap:Admin:Password"] = "CHANGE_ME_but_this_tail_is_secret"
        });

        var exception = Assert.Throws<InvalidOperationException>(() => configuration.ThrowIfPlaceholderValues("Production"));

        Assert.DoesNotContain("this_tail_is_secret", exception.Message);
    }

    // An operator retyping the marker from the docs is as exposed as one who never edited the file.
    [Fact]
    public void FindPlaceholderKeys_MatchesCaseInsensitively()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Bootstrap:Admin:Email"] = "change_me"
        });

        Assert.Equal(["Bootstrap:Admin:Email"], configuration.FindPlaceholderKeys());
    }

    // The shipped example file must fail the deploy rather than boot on published secrets. The four
    // deploy-time secrets it still carries have to be empty; compose's `:?` guards refuse the required
    // ones before an image is even pulled. The rest are entered at System Settings -> Credentials.
    [Fact]
    public void ShippedProductionExample_LeavesEveryGuardedSecretEmpty()
    {
        var values = ParseEnvFile(RepositoryRoot.ReadAllText(".env.prod.example"));

        Assert.NotEmpty(values);
        Assert.All(
            new[]
            {
                "MARIADB_ROOT_PASSWORD",
                "MARIADB_PASSWORD",
                "BOOTSTRAP_ADMIN_EMAIL",
                "BOOTSTRAP_ADMIN_PASSWORD"
            },
            key =>
            {
                Assert.True(values.ContainsKey(key), $"{key} is missing from .env.prod.example");
                Assert.True(
                    string.IsNullOrEmpty(values[key]),
                    $"{key} must ship empty so the deploy fails closed, not with a placeholder value.");
            });
    }

    // The gate the guard exists behind. It used to be an `if (builder.Environment.IsProduction())` at
    // each call site, where inverting or deleting it would fail nothing; moving it into the guard is
    // what makes these three cases reachable at all.
    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    [InlineData("")]
    public void ThrowIfPlaceholderValues_DoesNothing_OutsideProduction(string environmentName)
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Bootstrap:Admin:Password"] = "CHANGE_ME_one_time_password"
        });

        configuration.ThrowIfPlaceholderValues(environmentName);
    }

    // IHostEnvironment.IsEnvironment compares case-insensitively, so this must too — otherwise
    // DOTNET_ENVIRONMENT=production would get none of the protection.
    [Theory]
    [InlineData("Production")]
    [InlineData("production")]
    [InlineData("PRODUCTION")]
    public void ThrowIfPlaceholderValues_Throws_InProductionWhateverTheCasing(string environmentName)
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Bootstrap:Admin:Password"] = "CHANGE_ME_one_time_password"
        });

        Assert.Throws<InvalidOperationException>(
            () => configuration.ThrowIfPlaceholderValues(environmentName));
    }

    // The literal exists so Odyssey.Core needs no hosting reference; this is what keeps it honest,
    // mirroring TestServerTypeName_MatchesTheTypeWebApplicationFactorySubstitutes.
    [Fact]
    public void GuardedEnvironmentName_MatchesTheHostingConstant()
    {
        Assert.Equal(PlaceholderConfigurationGuard.GuardedEnvironmentName, Environments.Production);
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static Dictionary<string, string> ParseEnvFile(string contents) =>
        contents.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#') && line.Contains('='))
            .ToDictionary(
                line => line[..line.IndexOf('=')].Trim(),
                line => line[(line.IndexOf('=') + 1)..].Trim());
}
