using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Odyssey.Context;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// An unset <c>Email:SmtpHost</c> degrades to "send nothing, log the action link" — a fine local-dev
/// affordance, and an unacceptable Production posture once password-reset tokens travel that path
/// (issue #405). Production therefore refuses to start rather than silently running a deployment in
/// which nobody can confirm an address or reset a password.
/// </summary>
public class EmailOptionsProductionValidationTests
{
    [Fact]
    public void Production_WithoutAnSmtpHost_FailsToStart()
    {
        using var factory = new ProductionFactory(smtpHost: null);

        var error = Assert.ThrowsAny<OptionsValidationException>(() => factory.CreateClient());

        Assert.Contains(
            "Email:SmtpHost must be configured in Production.",
            string.Join(" ", error.Failures),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Production_WithAnSmtpHost_Starts()
    {
        // The control: the failure above is the missing host, not something else about a Production
        // host that this suite has never booted before.
        using var factory = new ProductionFactory(smtpHost: "smtp.example.test");

        using var client = factory.CreateClient();

        Assert.NotNull(client);
    }

    [Fact]
    public void OutsideProduction_AnUnsetSmtpHostIsStillAllowed()
    {
        // The dev/Compose stack runs with no relay on purpose; the validator must not reach it.
        using var factory = new ProductionFactory(smtpHost: null, environmentName: "Staging");

        using var client = factory.CreateClient();

        Assert.NotNull(client);
    }

    private sealed class ProductionFactory : WebApplicationFactory<Program>
    {
        private readonly string databaseName = $"EmailOptionsProductionValidationTests-{Guid.NewGuid()}";
        private readonly string? smtpHost;
        private readonly string environmentName;

        public ProductionFactory(string? smtpHost, string environmentName = "Production")
        {
            this.smtpHost = smtpHost;
            this.environmentName = environmentName;

            // The in-memory database is otherwise selected by the "Testing" environment check inside
            // AddDatabases, which runs before this factory's configuration is applied — so it has to
            // arrive as an environment variable, exactly as AntiforgeryEnforcementTests does it.
            Environment.SetEnvironmentVariable("UseInMemoryDatabase", "true");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environmentName);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["UseInMemoryDatabase"] = "true",
                    ["Email:SmtpHost"] = smtpHost,
                    ["Email:FromAddress"] = "no-reply@odyssey.test",
                    // Legal:PseudonymizationSecret used to be Production's other hard startup
                    // requirement and had to be supplied here to keep it from masking what this test
                    // measures. It moved to the encrypted secret store in issue #445 and is no longer
                    // read from configuration or checked at startup, so nothing is set for it — the
                    // key is left named here because its absence is now the deliberate part.
                }));

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<OdysseyContext>>();
                services.AddDbContext<OdysseyContext>(options =>
                    options.UseInMemoryDatabase(databaseName));
            });
        }
    }
}
