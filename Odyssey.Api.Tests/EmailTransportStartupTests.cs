using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Odyssey.Context;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Startup no longer has an opinion about mail (issue #8 §11.3, AC 14).
///
/// <para>
/// This suite replaces <c>EmailOptionsProductionValidationTests</c>, which asserted the opposite: that
/// Production refused to start without <c>Email:SmtpHost</c>. That gate could not survive the setting's
/// move into the database — a value entered through the settings UI cannot be a precondition for that
/// UI coming up — so the failure moved from startup to the first send, which logs and skips. It is the
/// identical trade issue #445 made when <c>Legal:PseudonymizationSecret</c> lost its own gate.
/// </para>
///
/// <para>
/// <strong>The inverted assertion is the point, so it is worth being explicit about what is no longer
/// covered.</strong> Nothing at startup now tells an operator that mail is unconfigured. Three things
/// carry that instead, none of them here: the settings page's own header signal while the host is
/// empty, <c>docs/deployment.md</c>, and <c>.env.prod.example</c>. A startup warning was considered and
/// declined; this test exists so that decision cannot be reversed by accident.
/// </para>
/// </summary>
public class EmailTransportStartupTests
{
    [Fact]
    public void Production_StartsWithNoEmailConfigurationAtAll()
    {
        // Not merely "with an empty SmtpHost": with no `Email` section present in configuration in any
        // form, which is what a deployment built from the current .env.prod.example actually has.
        using var factory = new ProductionFactory();

        using var client = factory.CreateClient();

        Assert.NotNull(client);
    }

    [Fact]
    public void Production_StartsEvenWhenAnEmailSectionIsPresentAndEmpty()
    {
        // A leftover section from an older .env or a hand-edited appsettings.json must not resurrect
        // the old binding by being noticed. Nothing reads `Email:` any more, so this is inert — and
        // "inert" is the assertion.
        using var factory = new ProductionFactory(new Dictionary<string, string?>
        {
            ["Email:SmtpHost"] = string.Empty,
            ["Email:SmtpPort"] = string.Empty,
            ["Email:ClientBaseUrl"] = "https://stale.example.test",
        });

        using var client = factory.CreateClient();

        Assert.NotNull(client);
    }

    private sealed class ProductionFactory(IDictionary<string, string?>? extraConfiguration = null)
        : WebApplicationFactory<Program>
    {
        private readonly string databaseName = $"EmailTransportStartupTests-{Guid.NewGuid()}";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");

            // The in-memory database is otherwise selected by the "Testing" environment check inside
            // AddDatabases, which runs before this factory's configuration is applied — so it has to
            // arrive as an environment variable, exactly as AntiforgeryEnforcementTests does it.
            Environment.SetEnvironmentVariable("UseInMemoryDatabase", "true");

            builder.ConfigureAppConfiguration((_, config) =>
            {
                var values = new Dictionary<string, string?> { ["UseInMemoryDatabase"] = "true" };
                foreach (var (key, value) in extraConfiguration ?? new Dictionary<string, string?>())
                {
                    values[key] = value;
                }

                config.AddInMemoryCollection(values);
            });

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<OdysseyContext>>();
                services.AddDbContext<OdysseyContext>(options =>
                    options.UseInMemoryDatabase(databaseName));
            });
        }
    }
}
