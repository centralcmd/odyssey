namespace Odyssey.Api.UserAdministration;

public sealed class UserAdministrationValidationException(string message) : Exception(message);

public sealed class UserAdministrationNotFoundException(string message) : Exception(message);

public sealed class UserAdministrationConflictException(string message) : Exception(message);

/// <summary>
/// A well-formed request against a real user that cannot be fulfilled because of the target's state —
/// mapped to <c>422</c>, keeping <see cref="UserAdministrationConflictException"/>'s <c>409</c> for
/// genuine conflicts (issue #406).
/// </summary>
public sealed class UserAdministrationUnprocessableException(string message) : Exception(message);

/// <summary>
/// A per-recipient send budget is exhausted, so the operation was refused before it mutated anything —
/// mapped to <c>429</c>. Surfacing throttle state here is safe: the endpoint is admin-only and the caller
/// already holds <c>users.read</c>, so it discloses nothing they could not already list (issue #406).
/// </summary>
public sealed class UserAdministrationThrottledException(string message) : Exception(message);
