namespace Odyssey.Api.SystemSettings;

/// <summary>
/// Thrown by <see cref="SystemSettingsService.UpdateAsync"/> when the caller sets a field it lacks
/// the matching write claim for. The controller maps this to <c>403 Forbidden</c> naming the field
/// (issue #349 §7/§10) — mirrors the local exception-then-catch convention <c>UserAdministrationService</c>
/// uses rather than the cross-cutting <c>DomainException</c> hierarchy, since this is a claim-authorization
/// failure, not a domain-validation one.
/// </summary>
public sealed class SystemSettingsForbiddenException(string message) : Exception(message);
