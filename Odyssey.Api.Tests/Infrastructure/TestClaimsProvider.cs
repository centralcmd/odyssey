namespace Odyssey.Api.Tests.Infrastructure;

/// <summary>
/// Carries the test principal the shared <see cref="TestAuthHandler"/> issues.
/// <see cref="Permissions"/> is <see langword="null"/> for an anonymous request (so
/// <c>[Authorize]</c> challenges produce 401, not 403); an empty collection models an
/// authenticated user with no permissions.
///
/// <para>
/// <see cref="Permissions"/> is settable so a host shared across tests can give each test its own default caller
/// (<see cref="OdysseyApiFactory.UsePermissions"/>); a request carrying
/// <see cref="TestAuthHandler.PermissionsOverrideHeader"/> overrides it for that request alone.
/// </para>
/// </summary>
public sealed class TestClaimsProvider(IReadOnlyCollection<string>? permissions, string actorUserId)
{
    public IReadOnlyCollection<string>? Permissions { get; set; } = permissions;

    public string ActorUserId { get; } = actorUserId;
}
