using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Context;
using Odyssey.Context.Secrets;
using Odyssey.Dtos.Application;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Real-engine coverage for the encrypted secret store (issue #444 §15, §16 ACs 6 and 25).
///
/// <para>
/// Two things InMemory cannot see, and both are silent failures rather than loud ones:
/// </para>
///
/// <list type="bullet">
/// <item>
/// <strong>Column truncation.</strong> <c>[StringLength]</c> counts UTF-16 code units, so a
/// maximum-length plaintext protects to a ciphertext materially longer than the plaintext cap —
/// and MariaDB outside strict mode truncates silently. That would return <c>204</c> and leave a
/// permanently unreadable credential with no error at write time, the worst shape available. Only
/// the real column charset and collation can prove it fits, which is the same reason this tier
/// exists for decimal and datetime fidelity.
/// </item>
/// <item>
/// <strong>Cross-host key derivation.</strong> The migrations job and the API must derive the SAME
/// keys from the same directory, or a future adoption step would write rows the API can never
/// decrypt — a silent, delayed failure. This exercises two independently-built Data Protection
/// stacks over one directory, which is exactly what the two containers do.
/// </item>
/// </list>
/// </summary>
[Collection(MariaDbCollection.Name)]
public class SecretSettingsRelationalTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_secret_settings_relational";
    private const string Key = SecretSettingKeys.DiagnosticsSelfTest;

    /// <summary>
    /// AC 6 (the real-engine half). A maximum-length printable-ASCII value round-trips through the
    /// actual column WITHOUT truncation — asserted by reading the stored string back and comparing its
    /// length, then decrypting it, because a truncated ciphertext still <em>looks</em> stored.
    /// </summary>
    [SkippableFact]
    public async Task A_maximum_length_secret_round_trips_through_the_real_column()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateDatabaseAsync();

        await using (var context = new OdysseyContext(ApplicationOptions()))
        {
            await context.Database.MigrateAsync();
        }

        var keysDirectory = Directory.CreateTempSubdirectory("odyssey-secrets-relational-");
        try
        {
            var protector = Protector(keysDirectory.FullName);

            // The cap, in printable ASCII — the shape every credential in scope actually has.
            var plaintext = new string('X', SecretSettingKeys.MaxPlaintextLength);
            var ciphertext = protector.Protect(Key, plaintext);

            await using (var context = new OdysseyContext(ApplicationOptions()))
            {
                context.SystemSettingSecrets.Add(new SystemSettingSecret
                {
                    Key = Key,
                    Ciphertext = ciphertext,
                    ProtectionScheme = SystemSettingSecret.CurrentProtectionScheme,
                    UpdatedAt = DateTime.UtcNow,
                });
                await context.SaveChangesAsync();
            }

            await using (var context = new OdysseyContext(ApplicationOptions()))
            {
                var stored = await context.SystemSettingSecrets.AsNoTracking().SingleAsync(row => row.Key == Key);

                Assert.Equal(ciphertext.Length, stored.Ciphertext.Length);
                Assert.Equal(ciphertext, stored.Ciphertext);
                Assert.Equal(plaintext, protector.Unprotect(Key, stored.Ciphertext));
            }
        }
        finally
        {
            keysDirectory.Delete(recursive: true);
        }

        await DropDatabaseAsync();
    }

    /// <summary>
    /// AC 25 — the test that would have caught the silent adoption failure. A value protected in one
    /// host unprotects in another when both are given the same keys path and the same application
    /// name.
    ///
    /// <para>
    /// It holds in the containers only because both Dockerfiles share <c>aspnet:10.0-alpine</c> and
    /// <c>USER app</c>, so the key files one writes are readable by the other — a dependency
    /// <c>docs/deployment.md</c> states explicitly, since the migrations job now creates the key ring
    /// first.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_secret_protected_in_the_migrations_host_unprotects_in_the_api_host()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateDatabaseAsync();

        await using (var context = new OdysseyContext(ApplicationOptions()))
        {
            await context.Database.MigrateAsync();
        }

        var keysDirectory = Directory.CreateTempSubdirectory("odyssey-secrets-crosshost-");
        try
        {
            const string plaintext = "sk-adopted-from-configuration";

            // Host A — the migrations job, writing a row the way a future adoption step would.
            await using (var context = new OdysseyContext(ApplicationOptions()))
            {
                context.SystemSettingSecrets.Add(new SystemSettingSecret
                {
                    Key = Key,
                    Ciphertext = Protector(keysDirectory.FullName).Protect(Key, plaintext),
                    ProtectionScheme = SystemSettingSecret.CurrentProtectionScheme,
                    UpdatedAt = DateTime.UtcNow,
                });
                await context.SaveChangesAsync();
            }

            // Host B — the API, an independently-built Data Protection stack over the same directory.
            await using (var context = new OdysseyContext(ApplicationOptions()))
            {
                var stored = await context.SystemSettingSecrets.AsNoTracking().SingleAsync(row => row.Key == Key);

                Assert.Equal(plaintext, Protector(keysDirectory.FullName).Unprotect(Key, stored.Ciphertext));
            }

            // The negative control: a DIFFERENT key directory cannot read it — which is what makes the
            // shared volume load-bearing rather than incidental.
            var otherDirectory = Directory.CreateTempSubdirectory("odyssey-secrets-otherring-");
            try
            {
                await using var context = new OdysseyContext(ApplicationOptions());
                var stored = await context.SystemSettingSecrets.AsNoTracking().SingleAsync(row => row.Key == Key);

                Assert.Null(Protector(otherDirectory.FullName).Unprotect(Key, stored.Ciphertext));
            }
            finally
            {
                otherDirectory.Delete(recursive: true);
            }
        }
        finally
        {
            keysDirectory.Delete(recursive: true);
        }

        await DropDatabaseAsync();
    }

    /// <summary>
    /// The migration creates the table and — unlike every plaintext settings key — seeds NO rows. An
    /// absent row is a secret's correct initial state, so a seeded empty-string row would create a
    /// fourth state ("present but empty") every consumer would have to handle.
    /// </summary>
    [SkippableFact]
    public async Task The_migration_creates_an_empty_table_and_seeds_no_rows()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateDatabaseAsync();

        var options = ApplicationOptions();

        await using (var context = new OdysseyContext(options))
        {
            await context.Database.MigrateAsync();
        }

        await using (var context = new OdysseyContext(options))
        {
            Assert.Empty(await context.SystemSettingSecrets.AsNoTracking().ToListAsync());
        }

        await using (var context = new OdysseyContext(options))
        {
            // The plaintext settings rows are seeded alongside it, so an empty secrets table is a
            // deliberate initial state rather than a migration that failed to run.
            Assert.NotEmpty(await context.SystemSettings.AsNoTracking().ToListAsync());
        }

        await DropDatabaseAsync();
    }

    /// <summary>
    /// A standalone Data Protection stack over <paramref name="keysPath"/>, built the way both hosts
    /// build theirs — the same application name is what makes the derived keys interchangeable.
    /// </summary>
    private static ISecretProtector Protector(string keysPath)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection()
            .SetApplicationName("Odyssey")
            .PersistKeysToFileSystem(new DirectoryInfo(keysPath));

        var provider = services.BuildServiceProvider();
        return new SecretProtector(provider.GetRequiredService<IDataProtectionProvider>());
    }

    private async Task RecreateDatabaseAsync()
    {
        await using var admin = AdminContext();

        await admin.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
        await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
    }

    private async Task DropDatabaseAsync()
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

    private DbContextOptions<OdysseyContext> ApplicationOptions()
    {
        var connection = fixture.ConnectionStringFor(Database);
        return new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(connection, ServerVersion.AutoDetect(connection)).Options;
    }
}
