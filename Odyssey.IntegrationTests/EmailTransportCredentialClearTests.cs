using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Odyssey.Api.Identity;
using Odyssey.Api.SystemSettings;
using Odyssey.Context;
using Odyssey.Context.Secrets;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The G4/G7 credential clear and the transaction it shares with the settings write (issue #8 §5.8 —
/// ACs 3, 3b, 4, 4b, 4c, 4d).
///
/// <para>
/// <strong>This tier, and not the fast ones, because the fast ones cannot see any of it.</strong> The
/// EF InMemory provider honours neither transactions nor the retrying execution strategy, so an
/// InMemory test would pass a <c>PUT</c> that throws <c>InvalidOperationException</c> on its first real
/// exercise — a retrying strategy refuses a user-initiated transaction, and
/// <c>EnableRetryOnFailure</c> is configured on the API's context. Retry-on-failure is therefore
/// enabled here too: the execution-strategy wrapping is part of what is under test, not scaffolding
/// around it.
/// </para>
///
/// <para>
/// <strong>Why ACs 4 and 4b are one test rather than two.</strong> They were written against the v1
/// design, where the clear committed in its own earlier transaction, and they name the two directions
/// an interleaving could go: the settings write failing after the clear, and the clear failing after
/// the settings write. Under the shipped design the two writes share one <c>SaveChanges</c> inside one
/// transaction, so there is no ordering between them left to fail in — which is the property both ACs
/// were asking for. A single rollback assertion covering both rows is the strongest available form,
/// and it is the one that fails if the transaction is ever removed.
/// </para>
/// </summary>
[Collection(MariaDbCollection.Name)]
public class EmailTransportCredentialClearTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_email_transport_clear";
    private const string ActorId = "clear-actor";
    private const string OriginalHost = "smtp.original.test";

    // ── AC 3 / AC 3b: the clear fires, in the right direction ────────────────────────────────────

    [SkippableFact]
    public async Task ChangingTheSmtpHost_ClearsTheStoredRelayCredential()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var connectionString = await MigratedSchemaAsync();
        await SeedAsync(connectionString, host: OriginalHost, startTls: true, withCredential: true);

        await UpdateAsync(connectionString, new SystemSettingsUpdate { EmailSmtpHost = "smtp.newprovider.test" });

        await using var verify = new OdysseyContext(OptionsFor(connectionString));
        Assert.Equal("smtp.newprovider.test", await ValueAsync(verify, SystemSettingsKeys.EmailSmtpHost));
        Assert.Empty(await verify.SystemSettingSecrets.AsNoTracking().ToListAsync());

        await DropAsync();
    }

    [SkippableFact]
    public async Task TurningStartTlsOff_ClearsTheStoredRelayCredential()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var connectionString = await MigratedSchemaAsync();
        await SeedAsync(connectionString, host: OriginalHost, startTls: true, withCredential: true);

        await UpdateAsync(connectionString, new SystemSettingsUpdate { EmailUseStartTls = false });

        await using var verify = new OdysseyContext(OptionsFor(connectionString));
        Assert.Equal("false", await ValueAsync(verify, SystemSettingsKeys.EmailUseStartTls));
        Assert.Empty(await verify.SystemSettingSecrets.AsNoTracking().ToListAsync());

        await DropAsync();
    }

    [SkippableTheory]
    // false → false is not a change at all, and false → true is a strengthening. Neither may cost an
    // administrator their credential — a control that fires on the safe direction teaches people to
    // work around it.
    [InlineData(false, false)]
    [InlineData(false, true)]
    // …and the strengthening direction from the other starting point, for completeness.
    [InlineData(true, true)]
    public async Task StartTlsClearsOnlyInTheTrueToFalseDirection(bool stored, bool requested)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var connectionString = await MigratedSchemaAsync();
        await SeedAsync(connectionString, host: OriginalHost, startTls: stored, withCredential: true);

        await UpdateAsync(connectionString, new SystemSettingsUpdate { EmailUseStartTls = requested });

        await using var verify = new OdysseyContext(OptionsFor(connectionString));
        Assert.Equal(2, await verify.SystemSettingSecrets.AsNoTracking().CountAsync());

        await DropAsync();
    }

    /// <summary>
    /// AC 5. Re-sending the SAME host is not a change, so nothing is cleared — this is the routine
    /// whole-resource resave the client performs on every Save, and it must be free.
    /// </summary>
    [SkippableFact]
    public async Task ResavingTheSameHost_ClearsNothing()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var connectionString = await MigratedSchemaAsync();
        await SeedAsync(connectionString, host: OriginalHost, startTls: true, withCredential: true);

        await UpdateAsync(connectionString, new SystemSettingsUpdate { EmailSmtpHost = OriginalHost });

        await using var verify = new OdysseyContext(OptionsFor(connectionString));
        Assert.Equal(2, await verify.SystemSettingSecrets.AsNoTracking().CountAsync());

        await DropAsync();
    }

    /// <summary>
    /// Clearing the host back to empty turns mail OFF, so there is no new relay for the credential to
    /// reach and nothing is cleared. Asserted because the opposite reading — "any host change clears" —
    /// is the obvious simplification, and it would silently destroy a credential every time an
    /// administrator paused mail.
    /// </summary>
    [SkippableFact]
    public async Task ClearingTheHostToEmpty_ClearsNothing()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var connectionString = await MigratedSchemaAsync();
        await SeedAsync(connectionString, host: OriginalHost, startTls: true, withCredential: true);

        await UpdateAsync(connectionString, new SystemSettingsUpdate { EmailSmtpHost = string.Empty });

        await using var verify = new OdysseyContext(OptionsFor(connectionString));
        Assert.Equal(string.Empty, await ValueAsync(verify, SystemSettingsKeys.EmailSmtpHost));
        Assert.Equal(2, await verify.SystemSettingSecrets.AsNoTracking().CountAsync());

        await DropAsync();
    }

    // ── ACs 4, 4b, 4c: the rollback ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The exploit G4 exists to close, reached without the attacker ever needing the credential
    /// themselves: if the two writes can interleave, an interruption leaves the NEW host live with the
    /// OLD credential still stored and readable.
    ///
    /// <para>
    /// The failure is injected <em>after</em> <c>SaveChanges</c> has reached the database and
    /// <em>before</em> <c>CommitAsync</c> — the one window in which a non-transactional implementation
    /// would already have persisted both writes, and a transactional one has persisted neither. An
    /// interceptor that threw on the way IN would prove nothing: nothing would have been written yet.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task WhenTheWriteFailsInsideTheTransaction_NeitherTheHostNorTheCredentialChanges()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var connectionString = await MigratedSchemaAsync();
        await SeedAsync(connectionString, host: OriginalHost, startTls: true, withCredential: true);

        var audit = new CapturingLoggerProvider();

        await Assert.ThrowsAsync<InjectedWriteFailure>(() => UpdateAsync(
            connectionString,
            new SystemSettingsUpdate { EmailSmtpHost = "smtp.attacker.test" },
            failAfterSave: true,
            auditLogs: audit));

        await using var verify = new OdysseyContext(OptionsFor(connectionString));

        // The host did not move…
        Assert.Equal(OriginalHost, await ValueAsync(verify, SystemSettingsKeys.EmailSmtpHost));

        // …and the credential is still there. Both halves matter: a rollback that unwound only the
        // settings row would leave the credential cleared for a change that never happened, and one
        // that unwound only the clear is the exploit itself.
        Assert.Equal(2, await verify.SystemSettingSecrets.AsNoTracking().CountAsync());

        // AC 4c. No audit line claims a credential was cleared, and none claims the host changed —
        // both are written after the commit precisely so a rolled-back write leaves no record
        // asserting it happened.
        Assert.DoesNotContain(audit.Messages, message =>
            message.Contains("cleared", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(audit.Messages, message =>
            message.Contains(SystemSettingsKeys.EmailSmtpHost, StringComparison.Ordinal));

        await DropAsync();
    }

    /// <summary>
    /// AC 6, and the positive control for the test above: without it, the rollback assertion would
    /// still pass if the service simply never audited or never cleared anything.
    /// </summary>
    [SkippableFact]
    public async Task ASuccessfulHostChange_AuditsOldAndNewHost_AndTheClear()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var connectionString = await MigratedSchemaAsync();
        await SeedAsync(connectionString, host: OriginalHost, startTls: true, withCredential: true);

        var audit = new CapturingLoggerProvider();
        await UpdateAsync(
            connectionString,
            new SystemSettingsUpdate { EmailSmtpHost = "smtp.newprovider.test" },
            auditLogs: audit);

        var line = Assert.Single(
            audit.Messages, message => message.Contains(SystemSettingsKeys.EmailSmtpHost, StringComparison.Ordinal));
        Assert.Contains(OriginalHost, line, StringComparison.Ordinal);
        Assert.Contains("smtp.newprovider.test", line, StringComparison.Ordinal);

        // Two clears, one per secret, each naming why. No credential MATERIAL appears anywhere.
        var clears = audit.Messages
            .Where(message => message.Contains("cleared (SMTP host changed)", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, clears.Count);
        Assert.DoesNotContain(audit.Messages, message =>
            message.Contains(CredentialPlaintext, StringComparison.Ordinal));

        await DropAsync();
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────────────

    private const string CredentialPlaintext = "relay-secret-value";

    /// <summary>
    /// Drives the real <see cref="SystemSettingsService.UpdateAsync"/> against the real engine, with
    /// retry-on-failure enabled so the execution-strategy wrapping is genuinely exercised.
    /// </summary>
    private static async Task UpdateAsync(
        string connectionString,
        SystemSettingsUpdate request,
        bool failAfterSave = false,
        CapturingLoggerProvider? auditLogs = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            if (auditLogs is not null)
            {
                logging.AddProvider(auditLogs);
            }
        });
        services.AddMemoryCache();
        services.AddDbContext<OdysseyContext>(options =>
        {
            options.UseMySql(
                connectionString,
                ServerVersion.AutoDetect(connectionString),
                mySql => mySql.EnableRetryOnFailure());

            if (failAfterSave)
            {
                options.AddInterceptors(new ThrowAfterSaveInterceptor());
            }
        });

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        var secrets = new SecretSettingsService(
            context,
            new PassThroughProtector(),
            new SecretSettingsRegistry(new StubHostEnvironment("Testing")),
            new AlwaysDurableKeyRing(),
            TimeProvider.System,
            new StubDisplayNameResolver(),
            LoggerFor<SecretSettingsService>(auditLogs));

        var service = new SystemSettingsService(
            context,
            scope.ServiceProvider.GetRequiredService<IMemoryCache>(),
            TimeProvider.System,
            new StubDisplayNameResolver(),
            new RequestCapCeilings(Options.Create(new FileStorageOptions())),
            secrets,
            LoggerFor<SystemSettingsService>(auditLogs));

        await service.UpdateAsync(Caller(), ActorId, request);
    }

    private static ILogger<T> LoggerFor<T>(CapturingLoggerProvider? provider) =>
        provider is null ? NullLogger<T>.Instance : new LoggerFactory([provider]).CreateLogger<T>();

    /// <summary>The full-permission administrator; the claim gates are covered at the API tier.</summary>
    private static ClaimsPrincipal Caller() =>
        new(new ClaimsIdentity(
        [
            new Claim(PermissionClaims.Type, PermissionClaims.SystemSettingsRead),
            new Claim(PermissionClaims.Type, PermissionClaims.SystemSettingsUpdate),
            new Claim(PermissionClaims.Type, PermissionClaims.SystemSettingsSecurityUpdate),
        ], "test"));

    private static async Task<string?> ValueAsync(OdysseyContext context, string key) =>
        await context.SystemSettings.AsNoTracking()
            .Where(setting => setting.Key == key)
            .Select(setting => setting.Value)
            .SingleOrDefaultAsync();

    private async Task SeedAsync(string connectionString, string host, bool startTls, bool withCredential)
    {
        await using var context = new OdysseyContext(OptionsFor(connectionString));

        await SetAsync(context, SystemSettingsKeys.EmailSmtpHost, host);
        await SetAsync(context, SystemSettingsKeys.EmailUseStartTls, startTls ? "true" : "false");

        if (withCredential)
        {
            foreach (var key in new[] { SecretSettingKeys.EmailUsername, SecretSettingKeys.EmailPassword })
            {
                context.SystemSettingSecrets.Add(new SystemSettingSecret
                {
                    Key = key,
                    Ciphertext = CredentialPlaintext,
                    ProtectionScheme = SystemSettingSecret.CurrentProtectionScheme,
                    UpdatedAt = DateTime.UtcNow,
                });
            }
        }

        await context.SaveChangesAsync();

        static async Task SetAsync(OdysseyContext context, string key, string value)
        {
            var row = await context.SystemSettings.FirstOrDefaultAsync(setting => setting.Key == key);
            if (row is null)
            {
                context.SystemSettings.Add(new SystemSetting { Key = key, Value = value, UpdatedAt = DateTime.UtcNow });
            }
            else
            {
                row.Value = value;
            }
        }
    }

    private async Task<string> MigratedSchemaAsync()
    {
        await using (var admin = AdminContext())
        {
            await admin.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
            await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
        }

        var connectionString = fixture.ConnectionStringFor(Database);
        await using (var context = new OdysseyContext(OptionsFor(connectionString)))
        {
            await context.Database.MigrateAsync();
        }

        return connectionString;
    }

    private async Task DropAsync()
    {
        await using var admin = AdminContext();
        await admin.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
    }

    private OdysseyContext AdminContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(
                fixture.OdysseyConnectionString,
                ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);

    private static DbContextOptions<OdysseyContext> OptionsFor(string connectionString) =>
        new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)).Options;

    /// <summary>
    /// Fails the write in the one window that distinguishes a transactional implementation from a
    /// non-transactional one: after <c>SaveChanges</c> has reached the database, before the commit.
    /// </summary>
    private sealed class ThrowAfterSaveInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default) =>
            throw new InjectedWriteFailure();
    }

    private sealed class PassThroughProtector : ISecretProtector
    {
        public string Protect(string key, string plaintext) => plaintext;

        public string? Unprotect(string key, string ciphertext) => ciphertext;

        public bool CanUnprotect(string key, string ciphertext) => true;
    }

    private sealed class AlwaysDurableKeyRing : IKeyRingDurability
    {
        public bool IsDurable => true;

        public string RepositoryTypeName => "TestRepository";
    }

    private sealed class StubDisplayNameResolver : IUserDisplayNameResolver
    {
        public Task<IReadOnlyDictionary<string, string>> ResolveAsync(
            ClaimsPrincipal caller, IEnumerable<string?> userIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task<string> ResolveAsync(ClaimsPrincipal caller, string? userId, CancellationToken cancellationToken) =>
            Task.FromResult("Unknown user");
    }

    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Odyssey.IntegrationTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}

/// <summary>The injected failure, so a real one cannot be mistaken for it.</summary>
public sealed class InjectedWriteFailure() : Exception("Injected failure inside the settings transaction.");

/// <summary>Collects formatted log lines so a test can assert on what was, and was not, audited.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public List<string> Messages { get; } = [];

    public ILogger CreateLogger(string categoryName) => new Collector(Messages);

    public void Dispose()
    {
    }

    private sealed class Collector(List<string> messages) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            messages.Add(formatter(state, exception));
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
