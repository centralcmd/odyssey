using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Legal;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Authorization;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// AC 12 — deleting a user must not delete their compliance history, must not leave it pointing at an
/// id a future account could reuse, and must not make it untraceable either. The three assertions below
/// are that triple: the rows survive, a recreated account with the same id inherits nothing, and the
/// record still re-verifies against the original person's email.
/// </summary>
public class LegalAcceptancePseudonymizationTests
{
    private const string ActorUserId = TestAuthHandler.DefaultActorUserId;
    private const string TargetUserId = "deleted-user-id";
    private const string TargetEmail = "deleted@example.com";

    [Fact]
    public async Task DeletingAUser_PseudonymizesTheirAcceptanceRowsInsteadOfRemovingThem()
    {
        await using var factory = NewFactory();
        await SeedAsync(factory);

        var response = await factory.CreateClient().DeleteAsync($"/api/users/{TargetUserId}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await WithContextAsync(factory, async context =>
        {
            Assert.Null(await context.Users.FindAsync(TargetUserId));

            var licenseRows = await context.LicenseAcceptances.ToListAsync();
            var termsRows = await context.TermsOfServiceAcceptances.ToListAsync();

            // The rows survive the account, with the id overwritten rather than removed.
            Assert.Single(licenseRows);
            Assert.Single(termsRows);
            Assert.NotEqual(TargetUserId, licenseRows[0].UserId);
            Assert.NotEqual(TargetUserId, termsRows[0].UserId);
        });
    }

    /// <summary>
    /// (a) A future account reusing the deleted id inherits nothing — the reuse bug the pseudonymization
    /// exists to close, which <c>DemoDataSeeder</c>'s deterministic ids make more than theoretical.
    /// </summary>
    [Fact]
    public async Task AnAccountReusingTheDeletedId_InheritsNoComplianceHistory()
    {
        await using var factory = NewFactory();
        await SeedAsync(factory);
        await factory.CreateClient().DeleteAsync($"/api/users/{TargetUserId}");

        await WithContextAsync(factory, async context =>
        {
            var inherited = await context.LicenseAcceptances.AnyAsync(row => row.UserId == TargetUserId)
                || await context.TermsOfServiceAcceptances.AnyAsync(row => row.UserId == TargetUserId);

            Assert.False(inherited);
        });
    }

    /// <summary>
    /// (b) The record remains individually re-verifiable: recomputing the HMAC from the same email still
    /// matches the stored value, which is the Art. 7(1) attribution a random pseudonym would have destroyed.
    /// </summary>
    [Fact]
    public async Task ThePseudonym_IsStillReDerivableFromTheOriginalEmail()
    {
        await using var factory = NewFactory();
        await SeedAsync(factory);
        await factory.CreateClient().DeleteAsync($"/api/users/{TargetUserId}");

        var expected = await factory.Services.GetRequiredService<ILegalPseudonymizer>().PseudonymizeAsync(TargetEmail);

        await WithContextAsync(factory, async context =>
        {
            Assert.Equal(expected, (await context.LicenseAcceptances.SingleAsync()).UserId);
            Assert.Equal(expected, (await context.TermsOfServiceAcceptances.SingleAsync()).UserId);
        });

        // Case-insensitively, too — the email is normalised the way Identity normalises it.
        Assert.Equal(
            expected,
            await factory.Services.GetRequiredService<ILegalPseudonymizer>()
                .PseudonymizeAsync(TargetEmail.ToUpperInvariant()));
    }

    /// <summary>
    /// A delete refused by the service's own self-delete guard leaves the acceptance rows untouched.
    /// </summary>
    /// <remarks>
    /// This is a guard-clause test, <b>not</b> a transaction test: the refusal happens before the
    /// transaction is ever opened, so it would pass just as happily with no transaction at all. The
    /// atomicity guarantee — a failure <em>inside</em> the transaction unwinding the pseudonymization
    /// with it — needs a real engine and is proven in
    /// <c>Odyssey.IntegrationTests.UserDeletionRollbackTests</c>. Said explicitly because the earlier
    /// wording here claimed the stronger guarantee (PR #407 test review).
    /// </remarks>
    [Fact]
    public async Task ARefusedDeletion_LeavesTheAcceptanceRowsUntouched()
    {
        await using var factory = NewFactory();
        await SeedAsync(factory);
        await SeedAcceptancesAsync(factory, ActorUserId);

        // Deleting yourself is refused — nothing about the actor's own records may change.
        var response = await factory.CreateClient().DeleteAsync($"/api/users/{ActorUserId}");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        await WithContextAsync(factory, async context =>
        {
            Assert.True(await context.LicenseAcceptances.AnyAsync(row => row.UserId == ActorUserId));
            Assert.True(await context.TermsOfServiceAcceptances.AnyAsync(row => row.UserId == ActorUserId));
        });
    }

    /// <summary>
    /// Deleting the publishing admin never takes the published version with it. That the FK also nulls
    /// the publisher is a database-level <c>SetNull</c> rule, which EF InMemory does not apply to
    /// untracked dependents — it is asserted against a real engine in
    /// <c>Odyssey.IntegrationTests.LegalAcceptanceRelationalTests</c>.
    /// </summary>
    [Fact]
    public async Task DeletingThePublishingAdmin_KeepsThePublishedVersion()
    {
        await using var factory = NewFactory();
        await SeedAsync(factory);

        await factory.CreateClient().DeleteAsync($"/api/users/{TargetUserId}");

        await WithContextAsync(factory, async context =>
            Assert.Equal("Terms v1", (await context.TermsOfServiceVersions.SingleAsync()).Content));
    }

    private static OdysseyApiFactory NewFactory() =>
        new([PermissionClaims.UsersDelete, PermissionClaims.UsersRead], ActorUserId);

    /// <summary>Seed the acting admin, the target user, a ToS version they published, and their acceptances.</summary>
    private static async Task SeedAsync(OdysseyApiFactory factory)
    {
        await factory.SeedActorUserAsync();

        await WithContextAsync(factory, async context =>
        {
            context.Users.Add(new ApplicationUser
            {
                Id = TargetUserId,
                UserName = TargetEmail,
                NormalizedUserName = TargetEmail.ToUpperInvariant(),
                Email = TargetEmail,
                NormalizedEmail = TargetEmail.ToUpperInvariant(),
            });
            await context.SaveChangesAsync();

            context.TermsOfServiceVersions.Add(new TermsOfServiceVersion
            {
                Content = "Terms v1",
                PublishedAt = DateTime.UtcNow,
                PublishedByUserId = TargetUserId,
            });
            await context.SaveChangesAsync();
        });

        await SeedAcceptancesAsync(factory, TargetUserId);
    }

    private static Task SeedAcceptancesAsync(OdysseyApiFactory factory, string userId) =>
        WithContextAsync(factory, context => LegalTestData.AcceptAllAsync(context, userId));

    private static async Task WithContextAsync(OdysseyApiFactory factory, Func<OdysseyContext, Task> work)
    {
        using var scope = factory.Services.CreateScope();
        await work(scope.ServiceProvider.GetRequiredService<OdysseyContext>());
    }
}
