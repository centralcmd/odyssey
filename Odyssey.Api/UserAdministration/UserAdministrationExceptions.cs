using Odyssey.Core;

namespace Odyssey.Api.UserAdministration;

/// <summary>
/// A per-recipient send budget is exhausted, so the operation was refused before it mutated anything —
/// mapped to <c>429</c>. Surfacing throttle state here is safe: the endpoint is admin-only and the caller
/// already holds <c>users.read</c>, so it discloses nothing they could not already list (issue #406).
/// The other user-administration failures use the shared <c>Domain*</c> kinds; this one has no shared
/// <c>429</c> kind to use (issue #287 M11).
/// </summary>
public sealed class UserAdministrationThrottledException(string message) : DomainException(message)
{
    public override int StatusCode => StatusCodes.Status429TooManyRequests;
}
