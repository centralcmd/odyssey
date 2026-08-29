namespace Odyssey.Api.Tests.Infrastructure;

/// <summary>
/// Carries the test principal the shared <see cref="TestAuthHandler"/> issues.
/// <see cref="Permissions"/> is <see langword="null"/> for an anonymous request (so
/// <c>[Authorize]</c> challenges produce 401, not 403); an empty collection models an
/// authenticated user with no permissions.
/// </summary>
public sealed record TestClaimsProvider(IReadOnlyCollection<string>? Permissions, string ActorUserId);
