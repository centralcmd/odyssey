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

namespace Odyssey.Api.Tests.Infrastructure;

/// <summary>
/// A host that authenticates through the <b>real</b> Identity cookie pipeline, for the must-change-password
/// gate (issue #406).
/// </summary>
/// <remarks>
/// <c>TestAuthHandler</c> cannot express this feature's central assertions: it materialises a principal from
/// <see cref="TestClaimsProvider"/> on every request and never inspects a <c>Set-Cookie</c> header, so the
/// client's cookie jar is inert and every request authenticates identically regardless of what
/// <c>RefreshSignInAsync</c> did — which is precisely the regression the gate's escape hatch has to absorb.
/// Logging in for real is what makes "the same cookie still works afterwards" a real question.
/// </remarks>
public sealed class PasswordGateFactory : WebApplicationFactory<Program>
{
    public const string Password = "Password123!Safe";

    private readonly string databaseName = $"password-gate-{Guid.NewGuid()}";
    private readonly IReadOnlyDictionary<string, string?>? configuration;
    private readonly TimeSpan? securityStampValidationInterval;

    /// <param name="configuration">Extra configuration entries, layered over this fixture's defaults.</param>
    /// <param name="securityStampValidationInterval">
    /// Overrides <c>Program.cs</c>'s one-minute <see cref="SecurityStampValidatorOptions.ValidationInterval"/>.
    /// Pass <see cref="TimeSpan.Zero"/> to make every request revalidate the cookie against the stored stamp,
    /// which is the only way a test can observe a rotation-driven revocation without waiting out the real
    /// interval. It changes *when* revalidation happens, never its outcome — the production interval is a
    /// deliberate latency trade-off, not part of the property under test (issue #442).
    /// </param>
    public PasswordGateFactory(
        IReadOnlyDictionary<string, string?>? configuration = null,
        TimeSpan? securityStampValidationInterval = null)
    {
        this.configuration = configuration;
        this.securityStampValidationInterval = securityStampValidationInterval;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["UseInMemoryDatabase"] = "true",
                ["Email:SmtpHost"] = string.Empty,
                ["RateLimiting:Identity:PermitLimit"] = "1000",
                // Effectively off by default, so the lockout tests below can make as many wrong-password
                // attempts as Identity's threshold requires without the limiter answering first — the two
                // controls overlap, and a 429 arriving mid-run would make those tests pass for the wrong
                // reason. The limiter's OWN behaviour is exercised by overriding this back down; see
                // PasswordChangeRateLimitTests.
                ["RateLimiting:PasswordChange:PermitLimit"] = "1000",
            };

            if (configuration is not null)
            {
                foreach (var entry in configuration)
                {
                    settings[entry.Key] = entry.Value;
                }
            }

            config.AddInMemoryCollection(settings);
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<OdysseyContext>>();
            services.AddDbContext<OdysseyContext>(options =>
                options.UseInMemoryDatabase(databaseName)
                    .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));

            // The seeded roles store NormalizedName lower-cased, which the stock upper-invariant normalizer
            // can't match on a case-sensitive store like EF InMemory — so role (and therefore permission)
            // claims would silently never reach the principal here.
            services.RemoveAll<ILookupNormalizer>();
            services.AddSingleton<ILookupNormalizer, LowerInvariantLookupNormalizer>();

            // After Program.cs's own Configure, so this one wins.
            if (securityStampValidationInterval is { } interval)
            {
                services.Configure<SecurityStampValidatorOptions>(
                    options => options.ValidationInterval = interval);
            }
        });
    }

    /// <summary>Create a confirmed, loginable, legally-compliant user, optionally in a role.</summary>
    public async Task<ApplicationUser> CreateUserAsync(string email, string? roleId = null)
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

        // By role id, not AddToRoleAsync — the seeded roles' NormalizedName casing doesn't round-trip
        // through the normalizer.
        if (roleId is not null)
        {
            context.UserRoles.Add(new IdentityUserRole<string> { UserId = user.Id, RoleId = roleId });
            await context.SaveChangesAsync();
        }

        // OdysseyContext locks every newly added user behind the admin-approval gate (issue #349,
        // and since issue #290 with no first-user exemption), so a test user would be created unable to
        // sign in — the same reason DemoDataSeeder creates its users confirmed AND unlocked. Cleared
        // here rather than per test, because a fixture whose users cannot log in is never what a test is
        // trying to express.
        user.LockoutEnd = null;
        await userManager.UpdateAsync(user);

        // Without an acceptance row the legal gate answers every authenticated endpoint with a 451, which
        // would mask what these tests are actually measuring.
        await LegalTestData.AcceptAllAsync(context, user.Id);
        return user;
    }

    /// <summary>Log in over the real cookie flow and return a client carrying the session.</summary>
    public async Task<HttpClient> LoginAsync(string email, string password = Password)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/login?useCookies=true", new { email, password });

        if (!response.IsSuccessStatusCode)
        {
            client.Dispose();
            throw new InvalidOperationException($"Login for '{email}' failed with {(int)response.StatusCode}.");
        }

        return client;
    }

    /// <summary>Put a user in the state an admin-initiated reset leaves them in, without the mail.</summary>
    public Task SetMustChangePasswordAsync(string userId, bool value = true) =>
        WithUserAsync(userId, user => user.MustChangePassword = value);

    /// <summary>
    /// Drop the acceptance rows <see cref="CreateUserAsync"/> writes, so the user owes the legal gate as
    /// well — the only way to construct the both-gates-pending case these tests need. Their principal
    /// already carries the pending-acceptance claim on the next request, because the claims factory
    /// recomputes it against the current rows.
    /// </summary>
    public async Task RevokeLegalAcceptanceAsync(string userId)
    {
        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        context.LicenseAcceptances.RemoveRange(
            context.LicenseAcceptances.Where(row => row.UserId == userId));
        context.TermsOfServiceAcceptances.RemoveRange(
            context.TermsOfServiceAcceptances.Where(row => row.UserId == userId));
        await context.SaveChangesAsync();
    }

    public async Task<bool> MustChangePasswordAsync(string userId)
    {
        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return await context.Users.AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.MustChangePassword)
            .SingleAsync();
    }

    public async Task<int> AccessFailedCountAsync(string userId)
    {
        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return await context.Users.AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.AccessFailedCount)
            .SingleAsync();
    }

    private async Task WithUserAsync(string userId, Action<ApplicationUser> mutate)
    {
        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var user = await context.Users.SingleAsync(candidate => candidate.Id == userId);
        mutate(user);
        await context.SaveChangesAsync();
    }

    private sealed class LowerInvariantLookupNormalizer : ILookupNormalizer
    {
        public string? NormalizeName(string? name) => name?.ToLowerInvariant();

        public string? NormalizeEmail(string? email) => email?.ToLowerInvariant();
    }
}
