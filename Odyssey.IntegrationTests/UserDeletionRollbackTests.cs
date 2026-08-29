using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Odyssey.Api.Email;
using Odyssey.Api.Identity;
using Odyssey.Api.Legal;
using Odyssey.Api.UserAdministration;
using Odyssey.Context;
using Odyssey.Context.Secrets;
using Odyssey.Dtos.Application;
using Microsoft.Extensions.Hosting;
using System.Security.Claims;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// AC 12's rollback half, proven by driving the real
/// <see cref="UserAdministrationService.DeleteAsync"/> through a failure that happens <em>inside</em>
/// its transaction, against a real engine.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the guarantee was previously only asserted indirectly. The API-tier test that
/// looked like it covered this is rejected by an earlier guard clause before the transaction is ever
/// opened, and an integration test that hand-rolls the same EF calls proves the database can roll back
/// — not that <em>this service</em> does. Neither would notice if the transaction were removed from
/// the service tomorrow.
/// </para>
/// <para>
/// The failure is injected by overriding <c>UserManager.DeleteAsync</c> to return a failed
/// <see cref="IdentityResult"/> — the same shape a concurrency conflict or a store error produces. The
/// service turns that into an exception, which must unwind the already-written pseudonymization with
/// it: a deletion that half-succeeded would leave a live user whose compliance history no longer
/// points at them.
/// </para>
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class UserDeletionRollbackTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_delete_rollback";
    private const string ActorId = "rollback-actor";
    private const string TargetId = "rollback-target";
    private const string TargetEmail = "target@rollback.test";
    private static readonly Guid EntryId = Guid.NewGuid();

    [SkippableFact]
    public async Task WhenTheDeletionFailsInsideTheTransaction_thePseudonymizationRollsBackWithIt()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var connectionString = await MigratedSchemaAsync();
        await SeedAsync(connectionString);

        using var provider = BuildProvider(connectionString, failDelete: true);
        using var scope = provider.CreateScope();
        var service = ServiceFor(scope);

        await Assert.ThrowsAsync<UserAdministrationValidationException>(
            () => service.DeleteAsync(ActorId, TargetId));

        await using var verify = new OdysseyContext(OptionsFor(connectionString));

        // The user survived — the deletion genuinely failed, so this is the case under test...
        Assert.True(await verify.Users.AnyAsync(user => user.Id == TargetId));

        // ...and their compliance history still points at them, rather than at a pseudonym for an
        // account that was never actually deleted.
        Assert.Equal(TargetId, (await verify.LicenseAcceptances.AsNoTracking().SingleAsync()).UserId);
        Assert.Equal(TargetId, (await verify.TermsOfServiceAcceptances.AsNoTracking().SingleAsync()).UserId);

        // The attribution key did not fire either: the account still exists, so its journal entry must
        // still name it. A rollback that unwound the pseudonymization but left the attribution nulled
        // would be exactly the half-completed state this test exists to rule out.
        Assert.Equal(
            TargetId,
            (await verify.JournalEntries.AsNoTracking().SingleAsync(entry => entry.JournalEntryId == EntryId))
                .CreatedByUserId);

        await DropAsync();
    }

    /// <summary>
    /// The positive control. Without it, the test above would still pass if the service simply never
    /// pseudonymized anything — so this pins that the same path, when it succeeds, does commit both
    /// halves.
    /// </summary>
    [SkippableFact]
    public async Task WhenTheDeletionSucceeds_bothHalvesCommit()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var connectionString = await MigratedSchemaAsync();
        await SeedAsync(connectionString);

        using var provider = BuildProvider(connectionString, failDelete: false);
        using var scope = provider.CreateScope();
        var service = ServiceFor(scope);
        var expected = await scope.ServiceProvider.GetRequiredService<ILegalPseudonymizer>()
            .PseudonymizeAsync(TargetEmail);

        await service.DeleteAsync(ActorId, TargetId);

        await using var verify = new OdysseyContext(OptionsFor(connectionString));

        Assert.False(await verify.Users.AnyAsync(user => user.Id == TargetId));
        Assert.Equal(expected, (await verify.LicenseAcceptances.AsNoTracking().SingleAsync()).UserId);
        Assert.Equal(expected, (await verify.TermsOfServiceAcceptances.AsNoTracking().SingleAsync()).UserId);

        // Three outcomes, one transaction: the account is gone, the compliance logs are pseudonymized
        // and kept, and the shared journal entry is kept with its attribution dropped.
        var entry = await verify.JournalEntries.AsNoTracking().SingleAsync(e => e.JournalEntryId == EntryId);
        Assert.Null(entry.CreatedByUserId);

        await DropAsync();
    }

    private static UserAdministrationService ServiceFor(IServiceScope scope) =>
        new(scope.ServiceProvider.GetRequiredService<OdysseyContext>(),
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
            scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>(),
            new StubDisplayNameResolver(),
            scope.ServiceProvider.GetRequiredService<ILegalPseudonymizer>(),
            // The password-reset collaborators (issue #406) are irrelevant to deletion; stubs keep this
            // fixture from having to stand up an SMTP sender it never exercises.
            new StubEmailSendThrottle(),
            new StubEmailRecipientHashKey(),
            new StubPasswordResetLinkSender(),
            NullLogger<UserAdministrationService>.Instance);

    /// <summary>
    /// Identity over the real database, with the deletion optionally forced to fail. Retry-on-failure is
    /// enabled to match production, since the service's execution-strategy wrapping is part of what is
    /// being exercised.
    /// </summary>
    private static ServiceProvider BuildProvider(string connectionString, bool failDelete)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<OdysseyContext>(options =>
            options.UseMySql(
                connectionString,
                ServerVersion.AutoDetect(connectionString),
                mySql => mySql.EnableRetryOnFailure()));

        var identity = services.AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<OdysseyContext>();

        if (failDelete)
        {
            identity.AddUserManager<FailingDeleteUserManager>();
        }

        // The pseudonymization secret is database-backed since issue #445 Wave 4, so the pseudonymizer
        // resolves it through a scope. A stub reader supplies a fixed one here: this fixture is about
        // the deletion's TRANSACTION, and a real store round-trip would only add a way for it to fail
        // for reasons that are not the subject.
        services.AddSingleton<ISecretSettingsReader>(
            new StubSecretSettingsReader(SecretSettingKeys.LegalPseudonymizationSecret, "rollback-test-secret"));
        services.AddSingleton<ILegalPseudonymizer>(provider => new LegalPseudonymizer(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new StubHostEnvironment("Testing")));

        return services.BuildServiceProvider();
    }

    /// <summary>A store/concurrency failure at the deletion step, without needing to provoke a real one.</summary>
    private sealed class FailingDeleteUserManager(
        IUserStore<ApplicationUser> store,
        IOptions<IdentityOptions> optionsAccessor,
        IPasswordHasher<ApplicationUser> passwordHasher,
        IEnumerable<IUserValidator<ApplicationUser>> userValidators,
        IEnumerable<IPasswordValidator<ApplicationUser>> passwordValidators,
        ILookupNormalizer keyNormalizer,
        IdentityErrorDescriber errors,
        IServiceProvider services,
        ILogger<UserManager<ApplicationUser>> logger)
        : UserManager<ApplicationUser>(
            store, optionsAccessor, passwordHasher, userValidators, passwordValidators, keyNormalizer, errors, services, logger)
    {
        public override Task<IdentityResult> DeleteAsync(ApplicationUser user) =>
            Task.FromResult(IdentityResult.Failed(new IdentityError
            {
                Code = "ConcurrencyFailure",
                Description = "Optimistic concurrency failure, object has been modified.",
            }));
    }

    private sealed class StubDisplayNameResolver : IUserDisplayNameResolver
    {
        public Task<IReadOnlyDictionary<string, string>> ResolveAsync(
            ClaimsPrincipal caller, IEnumerable<string?> userIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task<string> ResolveAsync(ClaimsPrincipal caller, string? userId, CancellationToken cancellationToken) =>
            Task.FromResult("Unknown user");
    }

    private sealed class StubEmailRecipientHashKey : IEmailRecipientHashKey
    {
        public Task<ReadOnlyMemory<byte>> ResolveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ReadOnlyMemory<byte>>(System.Text.Encoding.UTF8.GetBytes("rollback-test-hash-key"));
    }

    /// <summary>One stored secret, every other key NotSet.</summary>
    private sealed class StubSecretSettingsReader(string key, string value) : ISecretSettingsReader
    {
        public Task<SecretResult> GetAsync(string requested, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(requested, key, StringComparison.Ordinal)
                ? SecretResult.Found(value)
                : SecretResult.NotSet);
    }

    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Odyssey.Api";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class StubEmailSendThrottle : IEmailSendThrottle
    {
        public bool TryAcquire(
            string emailAddress,
            int limit,
            int windowMinutes,
            int maxTrackedRecipients,
            ReadOnlyMemory<byte> recipientHashKey) => true;
    }

    private sealed class StubPasswordResetLinkSender : IPasswordResetLinkSender
    {
        public Task<PasswordResetLinkDelivery> SendResetLinkAsync(
            string email, string base64UrlCode, CancellationToken cancellationToken = default) =>
            Task.FromResult(PasswordResetLinkDelivery.Delivered);
    }

    private async Task<string> MigratedSchemaAsync()
    {
        await DropAsync();

        await using (var admin = new OdysseyContext(OptionsFor(fixture.OdysseyConnectionString)))
        {
            await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
        }

        var connectionString = fixture.ConnectionStringFor(Database);
        await using (var context = new OdysseyContext(OptionsFor(connectionString)))
        {
            await context.Database.MigrateAsync();
        }

        return connectionString;
    }

    private static async Task SeedAsync(string connectionString)
    {
        await using var context = new OdysseyContext(OptionsFor(connectionString));

        foreach (var (id, email) in new[] { (ActorId, "actor@rollback.test"), (TargetId, TargetEmail) })
        {
            await context.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO `AspNetUsers`
                     (`Id`, `UserName`, `NormalizedUserName`, `Email`, `NormalizedEmail`, `EmailConfirmed`,
                      `PasswordHash`, `SecurityStamp`, `ConcurrencyStamp`, `PhoneNumberConfirmed`,
                      `TwoFactorEnabled`, `LockoutEnabled`, `AccessFailedCount`, `MustChangePassword`)
                 VALUES
                     ({id}, {email}, {email.ToUpperInvariant()}, {email}, {email.ToUpperInvariant()}, 1,
                      'hash', 'stamp', 'concurrency', 0, 0, 1, 0, 0)
                 """);
        }

        var version = new TermsOfServiceVersion { Content = "Terms v1", PublishedAt = DateTime.UtcNow };
        context.TermsOfServiceVersions.Add(version);
        await context.SaveChangesAsync();

        // A domain row attributed to the target. Before the contexts were merged this lived in another
        // model entirely, so no transaction could have covered it and the service's own comment said as
        // much; it is here so both halves of that claim are now actually exercised.
        context.JournalEntries.Add(new JournalEntry
        {
            JournalEntryId = EntryId,
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            Title = "A shared entry",
            Content = "Written by the account under test.",
            EntryDate = DateTime.UtcNow.Date,
            CreatedByUserId = TargetId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        context.LicenseAcceptances.Add(new LicenseAcceptance
        {
            UserId = TargetId,
            LicenseHash = new string('c', 64),
            Accepted = true,
            RespondedAt = DateTime.UtcNow,
        });
        context.TermsOfServiceAcceptances.Add(new TermsOfServiceAcceptance
        {
            UserId = TargetId,
            TermsOfServiceVersionId = version.Id,
            Accepted = true,
            RespondedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    private async Task DropAsync()
    {
        await using var admin = new OdysseyContext(OptionsFor(fixture.OdysseyConnectionString));
        await admin.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
    }

    private static DbContextOptions<OdysseyContext> OptionsFor(string connectionString) =>
        new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(connectionString, ServerVersion.AutoDetect(connectionString))
            .Options;
}
