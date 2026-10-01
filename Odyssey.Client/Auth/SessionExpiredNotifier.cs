namespace Odyssey.Client.Auth;

/// <summary>
/// The seam between <see cref="UnauthorizedHandler"/> and the app shell (issue #250) — the same shape
/// as <see cref="PasswordChangeRequiredNotifier"/>, for the same reason: handler instances are built into
/// the <see cref="HttpClient"/> pipeline, so no component can resolve the one that saw the status.
/// </summary>
public sealed class SessionExpiredNotifier
{
    /// <summary>
    /// Raised when a domain call comes back <c>401</c>. May fire repeatedly (several calls can fail in one
    /// render pass), so subscribers must be idempotent.
    /// </summary>
    public event Action? SessionExpired;

    public void NotifySessionExpired() => SessionExpired?.Invoke();
}
