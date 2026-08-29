namespace Odyssey.Client.Auth;

/// <summary>
/// The seam between <see cref="PasswordChangeRequiredHandler"/> and the shell (issue #406 §7).
/// </summary>
/// <remarks>
/// <para>
/// The handler cannot navigate for itself in any way a component can observe: handler instances are
/// constructed as part of building the <see cref="HttpClient"/> pipeline in <c>Program.cs</c>, so
/// <c>MainLayout</c> resolving "the handler" from DI would get a different object. A separate singleton
/// carrying a plain event is the same structural answer <c>AntiforgeryTokenStore</c> already gives for a
/// handler that needs shared state — the handler raises, the shell subscribes and navigates.
/// </para>
/// <para>
/// <c>LegalComplianceHandler</c> takes the simpler route of injecting <c>NavigationManager</c> directly,
/// which works but puts the destination inside the pipeline; keeping it out here means the redirect stays
/// a decision the shell makes, and a test can observe the signal without a router.
/// </para>
/// </remarks>
public sealed class PasswordChangeRequiredNotifier
{
    /// <summary>
    /// Raised when any API call comes back refused because the account owes a password change. May fire
    /// repeatedly (several calls can fail in one render pass), so subscribers must be idempotent.
    /// </summary>
    public event Action? PasswordChangeRequired;

    public void NotifyPasswordChangeRequired() => PasswordChangeRequired?.Invoke();
}
