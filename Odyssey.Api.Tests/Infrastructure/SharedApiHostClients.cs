using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Odyssey.Api.Tests.Infrastructure;

/// <summary>
/// How a test class runs every test on ONE host, with each test (and each client) authenticated as its own caller.
///
/// <para>
/// <b>Why.</b> Booting a host (DI, EF InMemory <c>EnsureCreated</c>, file-storage verification) costs far more than
/// a test body, and the caller's claims used to be fixed per factory, so nearly every test built its own. Now the
/// factory's default caller can be changed per test (<see cref="ForCaller{T}"/>), and a client from
/// <see cref="ClientFor"/> sends <see cref="TestAuthHandler.PermissionsOverrideHeader"/> (and optionally
/// <see cref="TestAuthHandler.ActorOverrideHeader"/>) on every request. Either way <see cref="TestAuthHandler"/>
/// builds the same principal a factory constructed with those permissions would. A second caller over the first
/// one's data — what <c>sharingStoreWith</c> exists for — is just a second client on the same host.
/// </para>
///
/// <para>
/// <b>The pattern.</b> Derive the test class from <see cref="SharedApiHostTests{THost}"/> (an
/// <c>IClassFixture</c> whose host is reset before every test, <see cref="OdysseyApiFactory.ResetStoreAsync"/>), then:
/// <code>
/// public class WidgetApiTests(WidgetApiTests.Host host) : SharedApiHostTests&lt;WidgetApiTests.Host&gt;(host)
/// {
///     [Fact]
///     public async Task Update_WithoutUpdateClaim_IsForbidden()
///     {
///         var factory = Factory.ForCaller([PermissionClaims.WidgetsRead]); // this test's default caller
///         using var client = factory.CreateClient();                       // ...and every helper's client
///         using var writer = factory.ClientFor([PermissionClaims.WidgetsUpdate]);   // a second caller
///         using var someoneElse = factory.ClientFor([PermissionClaims.WidgetsRead], actorUserId: "other-user");
///         using var anonymous = factory.AnonymousClient();                 // the 401 path
///         ...
///     }
///
///     public sealed class Host() : OdysseyApiFactory([], actorUserId: "widget-actor");
/// }
/// </code>
/// The host may derive from the class's own factory (stub services, a fixed clock); a factory whose stubs hold
/// state (a call counter, captured logs) overrides <see cref="OdysseyApiFactory.ResetStoreAsync"/> to clear it.
/// </para>
///
/// <para>
/// <b>When NOT to share.</b> Keep a fresh host (<c>await using var factory = new …Factory(…)</c>) when the test
/// needs configuration or services of its own (<c>UseSetting</c>, a <c>configuration</c> dictionary, a replaced
/// service, a production environment, an ephemeral key ring), when it needs two stores that really are separate
/// (an export from one instance imported into another), or when it changes host-wide state the reset does not
/// restore: a rate-limit window (a shared host raises every limit out of reach, so a test of one must not share),
/// a lockout partition, a fake <see cref="TimeProvider"/> it advances, a singleton cache outside
/// <c>IMemoryCache</c>. A class may mix both: share the host for the tests that can, build one for those that cannot.
/// </para>
/// </summary>
public static class SharedApiHostClients
{
    /// <summary>
    /// Makes <paramref name="permissions"/> the default caller of this test (<see langword="null"/> is anonymous): every
    /// <c>CreateClient()</c> client — a helper's included — is authenticated with it until the next reset. The shared-host
    /// stand-in for <c>await using var factory = new Factory(permissions)</c>; returns the factory.
    /// </summary>
    public static T ForCaller<T>(this T factory, IReadOnlyCollection<string>? permissions) where T : OdysseyApiFactory
    {
        factory.UsePermissions(permissions);
        return factory;
    }

    /// <summary>An authenticated client holding exactly <paramref name="permissions"/> (none when empty).</summary>
    public static HttpClient ClientWith(this WebApplicationFactory<Program> factory, params string[] permissions) =>
        factory.ClientFor(permissions);

    /// <summary>An unauthenticated client: <c>[Authorize]</c> challenges with <c>401</c>.</summary>
    public static HttpClient AnonymousClient(this WebApplicationFactory<Program> factory, WebApplicationFactoryClientOptions? options = null) =>
        factory.ClientFor(null, options: options);

    /// <summary>
    /// A client for <paramref name="permissions"/> (<see langword="null"/> is anonymous, empty is authenticated with no
    /// claim), optionally as another user and with client options such as <c>AllowAutoRedirect</c>.
    /// </summary>
    public static HttpClient ClientFor(
        this WebApplicationFactory<Program> factory,
        IReadOnlyCollection<string>? permissions,
        string? actorUserId = null,
        WebApplicationFactoryClientOptions? options = null)
    {
        var client = options is null ? factory.CreateClient() : factory.CreateClient(options);
        client.DefaultRequestHeaders.Add(TestAuthHandler.PermissionsOverrideHeader, TestAuthHandler.EncodePermissions(permissions));
        if (actorUserId is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.ActorOverrideHeader, actorUserId);
        }

        return client;
    }
}

/// <summary>
/// Base for a test class whose tests share one host (<typeparamref name="THost"/>, an xUnit class fixture). The store,
/// the file volume and the memory cache are reset before every test (<see cref="OdysseyApiFactory.ResetStoreAsync"/>),
/// so no test can see another's rows and test order does not matter. See <see cref="SharedApiHostClients"/> for the
/// pattern and for when a test must keep a host of its own.
/// </summary>
public abstract class SharedApiHostTests<THost>(THost host) : IClassFixture<THost>, IAsyncLifetime
    where THost : OdysseyApiFactory
{
    /// <summary>The class's shared host, already reset for this test.</summary>
    protected THost Factory { get; } = MarkShared(host);

    private static THost MarkShared(THost host)
    {
        host.MarkSharedAcrossTests();
        return host;
    }

    public Task InitializeAsync() => Factory.ResetStoreAsync();

    public Task DisposeAsync() => Task.CompletedTask;
}
