extern alias migrations;

using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RoleClaimSeeder = migrations::Odyssey.MigrationService.RoleClaimSeeder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Odyssey.Context;
using Odyssey.Context.Authorization;
using Odyssey.Context.Legal;
using Odyssey.Dtos.Application;

namespace Odyssey.Api.Tests.Infrastructure;

/// <summary>
/// A host that authenticates with the <b>real</b> Identity cookie pipeline rather than
/// <c>TestAuthHandler</c> — required for anything that exercises issue #354's claims factory or gate,
/// because a <c>TestAuthHandler</c> principal never runs through the factory and so never carries the
/// pending-acceptance claim (which is exactly why the gate needs its own tests: it would otherwise pass
/// vacuously — AC 8).
/// </summary>
public sealed class LegalLoginFactory : WebApplicationFactory<Program>
{
    public const string Password = "Password123!Safe";

    private readonly string databaseName = $"legal-{Guid.NewGuid()}";

    /// <summary>Swapped in for the real provider so a test can make compliance computation fail on demand.</summary>
    public ControllableLicenseDocumentProvider License { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["UseInMemoryDatabase"] = "true" }));

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<OdysseyContext>>();
            services.AddDbContext<OdysseyContext>(options =>
                options.UseInMemoryDatabase(databaseName)
                    .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));

            services.RemoveAll<ILicenseDocumentProvider>();
            services.AddSingleton<ILicenseDocumentProvider>(License);

            // The seeded roles store NormalizedName lower-cased, which the stock upper-invariant
            // normalizer can't match on a case-sensitive store like EF InMemory — so role (and therefore
            // permission) claims would silently never reach the principal here. MariaDB's collation
            // hides this in the real stack; the test store has to be told.
            services.RemoveAll<ILookupNormalizer>();
            services.AddSingleton<ILookupNormalizer, LowerInvariantLookupNormalizer>();

            // Revalidate the security stamp on every request instead of every 30 minutes, so a test can
            // observe what a background revalidation does without waiting for the real interval.
            services.Configure<SecurityStampValidatorOptions>(
                options => options.ValidationInterval = TimeSpan.Zero);
        });
    }

    /// <summary>Create a confirmed, loginable user, optionally in a role, and optionally already compliant.</summary>
    public async Task<ApplicationUser> CreateUserAsync(
        string email, string? roleId = null, bool acceptLegalDocuments = false)
    {
        using var scope = Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        // EF InMemory does not apply HasData seeds until the database is explicitly created, and the
        // roles that seed carries are what a principal's permission claims hang off.
        await context.Database.EnsureCreatedAsync();

        // Claims themselves are no longer part of the model seed — they are reconciled at runtime by the
        // migrations job, because a positional HasData seed made every claim addition a hand-written
        // migration. These fixtures sign in for real rather than through TestAuthHandler, so without
        // this the principal carries a role and no permissions, and every gated endpoint answers 403.
        await new RoleClaimSeeder(Services, NullLogger<RoleClaimSeeder>.Instance)
            .ExecuteAsync(CancellationToken.None);

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            LockoutEnabled = true,
        };

        var result = await userManager.CreateAsync(user, Password);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join(", ", result.Errors.Select(error => error.Description)));
        }

        // The require-admin-approval gate disables every newly added account, with no first-user
        // exemption since issue #290 — and a fixture whose users cannot log in is never what a test is
        // trying to express. Cleared here rather than per test, mirroring PasswordGateFactory.
        user.LockoutEnd = null;
        await userManager.UpdateAsync(user);

        // By role id, not AddToRoleAsync — same reason the demo seeder does: the seeded roles'
        // NormalizedName casing doesn't round-trip through the normalizer.
        if (roleId is not null)
        {
            context.UserRoles.Add(new IdentityUserRole<string> { UserId = user.Id, RoleId = roleId });
            await context.SaveChangesAsync();
        }

        if (acceptLegalDocuments)
        {
            await LegalTestData.AcceptAllAsync(context, user.Id);
        }

        return user;
    }

    /// <summary>Log in over the real cookie flow and return a client carrying the session.</summary>
    public async Task<HttpClient> LoginAsync(string email)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync(
            "/login?useCookies=true", new { email, password = Password });

        if (!response.IsSuccessStatusCode)
        {
            client.Dispose();
            throw new InvalidOperationException($"Login for '{email}' failed with {(int)response.StatusCode}.");
        }

        return client;
    }

    public async Task<TermsOfServiceVersion> PublishTermsOfServiceAsync(string content, string? publishedByUserId = null)
    {
        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        var version = new TermsOfServiceVersion
        {
            Content = content,
            PublishedAt = DateTime.UtcNow,
            PublishedByUserId = publishedByUserId,
        };

        context.TermsOfServiceVersions.Add(version);
        await context.SaveChangesAsync();
        return version;
    }

    public async Task WithContextAsync(Func<OdysseyContext, Task> work)
    {
        using var scope = Services.CreateScope();
        await work(scope.ServiceProvider.GetRequiredService<OdysseyContext>());
    }

    private sealed class LowerInvariantLookupNormalizer : ILookupNormalizer
    {
        public string? NormalizeName(string? name) => name?.ToLowerInvariant();

        public string? NormalizeEmail(string? email) => email?.ToLowerInvariant();
    }
}

/// <summary>
/// Serves the real license document until <see cref="ShouldThrow"/> is set, then fails every read. The
/// provider is the narrowest seam that makes the whole compliance computation fail, which is how the
/// §10.11 failure-handling contract is tested without reaching into the service itself.
/// </summary>
public sealed class ControllableLicenseDocumentProvider : ILicenseDocumentProvider
{
    private readonly ILicenseDocumentProvider inner = new LicenseDocumentProvider(AppContext.BaseDirectory);

    public bool ShouldThrow { get; set; }

    public LicenseDocument Get() =>
        ShouldThrow ? throw new InvalidOperationException("Simulated compliance-computation failure.") : inner.Get();
}
