namespace Odyssey.Api.Identity;

/// <summary>
/// The marker <see cref="PasswordChangeRequiredMiddleware"/> looks for to let an authenticated endpoint
/// through while the caller's <c>MustChangePassword</c> flag is set (issue #406 §5.6). The middleware
/// matches on the interface rather than the attribute so a route with no method to decorate — a
/// framework-mapped minimal API, say — can carry an equivalent metadata object instead. Every exemption
/// today is an attribute on an action of this app's own.
/// </summary>
public interface IPasswordChangeExemptMetadata;

/// <summary>
/// Exempts one controller action from the must-change-password block. Controller action attributes are
/// automatically part of the endpoint's <c>Endpoint.Metadata</c>, so no separate
/// <c>.WithMetadata(...)</c> call is needed.
/// </summary>
/// <remarks>
/// <b><see cref="AttributeTargets.Method"/> deliberately, not <c>Class</c>.</b> <c>AuthController</c>
/// carries a single class-level <c>[Authorize]</c> over three actions, of which all three happen to be
/// exempt today — but a class-level exemption would be one attribute covering them all, and would
/// silently exempt any <em>fourth</em> action added later. The exempt set is asserted per route + HTTP
/// method by <c>PasswordChangeExemptEndpointsTests</c>, so a new exemption cannot land without a reviewer
/// seeing that test change.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PasswordChangeExemptAttribute : Attribute, IPasswordChangeExemptMetadata;
