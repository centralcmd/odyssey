namespace Odyssey.Dtos.Application;

/// <summary>
/// Body of a successful <c>POST /api/users/{id}/password-reset</c> (issue #406 §7).
/// </summary>
/// <remarks>
/// <see cref="EmailDelivered"/> is <see langword="false"/> when the reset <em>was</em> applied — stamp
/// rotated, flag set, sessions revoked — and the relay refused the message, so the admin should tell the
/// user to use <b>Forgot password</b> instead. It is deliberately not an error status: the state change
/// is committed and retrying the whole call would be wrong. The body carries no email, id, or token.
/// </remarks>
public sealed record PasswordResetDispatch
{
    public bool EmailDelivered { get; set; }
}
