using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Odyssey.Api.SystemSettings;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Xunit;
using System.Xml.Linq;

namespace Odyssey.Api.Tests;

/// <summary>
/// The key-ring durability refusal (issue #444 §16 ACs 23, 23a, 24).
///
/// <para>
/// This is the v1-fatal path: without it an administrator on a stack with no configured key path —
/// which was <em>every</em> dev stack before this issue — would paste a credential, receive a
/// <c>204</c>, and lose it at the next restart with no signal at any point. A startup warning cannot
/// cover it, because by construction it does not fire for the write that creates the problem.
/// </para>
///
/// <para>
/// <strong>The ephemeral case stands up its own host.</strong> It must not depend on the shared
/// factory's default, which deliberately provisions a per-factory temporary keys directory so every
/// OTHER write assertion runs against a durable ring.
/// </para>
/// </summary>
public class SecretSettingsKeyRingTests
{
    private const string Path = "/api/system-settings/secrets/" + SecretSettingKeys.DiagnosticsSelfTest;
    private const string ActorUserId = "66666666-6666-6666-6666-666666666666";

    private static readonly string[] ReadAndSecurityUpdate =
    [
        PermissionClaims.SystemSettingsRead,
        PermissionClaims.SystemSettingsSecurityUpdate,
    ];

    /// <summary>
    /// AC 23. A write against an ephemeral key ring is refused with <c>503</c>, the message names the
    /// configuration to set, and NO ROW IS WRITTEN — the point being that a <c>204</c> followed by
    /// permanent loss at restart is the worst outcome available.
    /// </summary>
    [Fact]
    public async Task Put_AgainstAnEphemeralKeyRing_Is503_AndWritesNoRow()
    {
        await using var factory = new EphemeralKeyRingFactory();
        var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path, new SecretSettingUpdate { Value = "sk-should-not-persist" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("DataProtection", body, StringComparison.Ordinal);
        Assert.Contains("KeysPath", body, StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.False(await context.SystemSettingSecrets.AnyAsync());
    }

    /// <summary>
    /// The <c>503</c> deliberately carries no <c>Retry-After</c>: the condition is not retryable until
    /// an operator acts, so advertising a retry delay would be a lie the client would act on.
    /// </summary>
    [Fact]
    public async Task TheEphemeralRefusal_CarriesNoRetryAfter()
    {
        await using var factory = new EphemeralKeyRingFactory();

        var response = await factory.CreateClient()
            .PutAsJsonAsync(Path, new SecretSettingUpdate { Value = "sk-should-not-persist" });

        Assert.Null(response.Headers.RetryAfter);
    }

    /// <summary>
    /// A READ is unaffected by an ephemeral ring: refusing to report status would take away the
    /// administrator's ability to see what is stored, which is the same "don't remove the operator's
    /// ability to act" reasoning the plaintext store's degraded reads already follow.
    /// </summary>
    [Fact]
    public async Task Get_AgainstAnEphemeralKeyRing_StillReturns200()
    {
        await using var factory = new EphemeralKeyRingFactory();

        var response = await factory.CreateClient().GetAsync("/api/system-settings/secrets");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// AC 23a — the check is POSITIVE, not negative, and this is the case that proves why. The
    /// negative form ("is it the ephemeral implementation?") cannot be written at all:
    /// <c>EphemeralXmlRepository</c> is <c>internal</c> to <c>Microsoft.AspNetCore.DataProtection</c>,
    /// so there is no type to test against — and depending on version the fallback is selected inside
    /// <c>XmlKeyManager</c> without being written back to the options, so <c>XmlRepository</c> reads
    /// <see langword="null"/> and a negative check never fires.
    /// </summary>
    [Fact]
    public void ANullRepository_IsClassifiedEphemeral()
    {
        Assert.False(KeyRingDurability.IsDurableRepository(null));
    }

    /// <summary>
    /// The allow-list is an EXTENSION POINT, and this pins the consequence: any repository outside it —
    /// a blob, Redis or custom <see cref="IXmlRepository"/> — is classified ephemeral and has every
    /// write refused. That fails closed, which is the right direction, but on a correctly configured
    /// durable deployment, so a future provider must extend the list rather than assume "no API change".
    /// </summary>
    [Fact]
    public void ARepositoryOutsideTheAllowList_IsClassifiedEphemeral()
    {
        Assert.False(KeyRingDurability.IsDurableRepository(new CustomRepository()));
    }

    /// <summary>The one type the dev and production stacks actually use.</summary>
    [Fact]
    public void AFileSystemRepository_IsClassifiedDurable()
    {
        var directory = Directory.CreateTempSubdirectory("odyssey-durability-");
        try
        {
            Assert.True(KeyRingDurability.IsDurableRepository(new FileSystemXmlRepository(directory, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance)));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>The shared factory's own host must be durable, or every other write AC is meaningless.</summary>
    [Fact]
    public async Task TheSharedTestFactory_RunsOnADurableKeyRing()
    {
        await using var factory = new OdysseyApiFactory(ReadAndSecurityUpdate, ActorUserId);
        _ = factory.CreateClient();

        var durability = factory.Services.GetRequiredService<IKeyRingDurability>();

        Assert.True(durability.IsDurable, "Detected repository: " + durability.RepositoryTypeName);
        Assert.Contains("FileSystemXmlRepository", durability.RepositoryTypeName, StringComparison.Ordinal);
    }

    // ── AC 24 — the startup assertion ───────────────────────────────────────────────────────────

    /// <summary>
    /// AC 24. A CONFIGURED but unwritable keys directory fails loudly. Data Protection's own behaviour
    /// there is to fall back to an in-memory ring with a log warning, which is the silent-ephemeral
    /// failure by another route — cheap when it only costs a forced re-login, unacceptable once
    /// credentials depend on it.
    ///
    /// <para>
    /// Asserted against the assertion helper directly rather than by booting a host: making the
    /// directory unwritable is a filesystem-permission manoeuvre, and a <c>WebApplicationFactory</c>
    /// that fails to start reports as an opaque aggregate rather than as this message.
    /// </para>
    /// </summary>
    [Fact]
    public void EnsureWritable_OnAnUnwritableDirectory_Throws()
    {
        // Self-skipping rather than SkippableFact: this project references no skip package, and the two
        // conditions are environmental — POSIX mode bits are how a directory is made unwritable here,
        // and a root process can write a 0500 directory regardless.
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("odyssey-unwritable-");
        try
        {
            File.SetUnixFileMode(directory.FullName, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            var exception = Assert.Throws<InvalidOperationException>(
                () => DataProtectionKeyDirectory.EnsureWritable(directory.FullName));

            // The remediation is in the message, because a pre-existing keys volume does NOT inherit
            // the image's build-time ownership — Docker copies it only into an empty volume — so an
            // installation that is silently ephemeral today will fail to start on upgrade.
            Assert.Contains("chown", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(
                directory.FullName,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            directory.Delete(recursive: true);
        }
    }

    /// <summary>A writable directory is created on demand and passes.</summary>
    [Fact]
    public void EnsureWritable_CreatesAMissingDirectory_AndLeavesNoProbeBehind()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"odyssey-ensure-{Guid.NewGuid():N}");
        try
        {
            DataProtectionKeyDirectory.EnsureWritable(path);

            Assert.True(Directory.Exists(path));
            Assert.Empty(Directory.GetFileSystemEntries(path));
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    /// <summary>
    /// AC 24's negative half, and it is the important one. An <strong>unset</strong>
    /// <c>DataProtection:KeysPath</c> must NOT fail startup — a bare <c>dotnet run</c>, every CI host
    /// and any stack that has not adopted the keys volume all sit there, and the write-path refusal is
    /// what covers them. Failing startup instead would turn a recoverable misconfiguration into an
    /// outage for every deployment, including ones storing no secrets at all.
    /// </summary>
    [Fact]
    public async Task AnUnsetKeysPath_DoesNotFailStartup()
    {
        await using var factory = new EphemeralKeyRingFactory();

        var response = await factory.CreateClient().GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class CustomRepository : IXmlRepository
    {
        public IReadOnlyCollection<XElement> GetAllElements() => [];

        public void StoreElement(XElement element, string? friendlyName)
        {
        }
    }

    /// <summary>
    /// A host with NO configured keys path and its key repository explicitly cleared — a
    /// deliberately-ephemeral ring, stood up here rather than relying on any default. The base factory
    /// provisions a durable directory, so clearing the repository in test services is what actually
    /// reproduces the condition.
    /// </summary>
    private sealed class EphemeralKeyRingFactory() : OdysseyApiFactory(ReadAndSecurityUpdate, ActorUserId)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IConfigureOptions<KeyManagementOptions>>();
                services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(
                    new ConfigureOptions<KeyManagementOptions>(options => options.XmlRepository = null));
            });
        }
    }
}
