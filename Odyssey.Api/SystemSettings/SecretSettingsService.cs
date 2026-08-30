using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Odyssey.Api.Identity;
using Odyssey.Context;
using Odyssey.Context.Secrets;
using Odyssey.Dtos.Application;
using Odyssey.Core;
using Odyssey.Dtos.Authorization;

namespace Odyssey.Api.SystemSettings;

/// <summary>
/// Read-status, set and clear for the encrypted secret store (issue #444 §5).
///
/// <para>
/// <strong>No code path here returns a secret value to a caller.</strong> The status projection
/// carries the key, a three-valued state and the same attribution triple the plaintext settings
/// already expose — no length, no hash, no prefix, no last-four. The audit line records key, actor,
/// action and timestamp, and nothing else.
/// </para>
///
/// <para>
/// <strong>There is no change detection.</strong> Every successful write is audited unconditionally
/// and stamps <c>UpdatedAt</c>/<c>UpdatedBy</c>. Decrypt-and-compare was dropped for three converging
/// reasons: it materialises the old plaintext on every write purely to suppress a line that contains
/// no plaintext; it leaves the stamp undefined for a no-op; and the presence or absence of the line is
/// itself a plaintext equality oracle for anyone who can read the log but not the store.
/// </para>
/// </summary>
public sealed class SecretSettingsService(
    OdysseyContext context,
    ISecretProtector protector,
    SecretSettingsRegistry registry,
    IKeyRingDurability keyRing,
    TimeProvider timeProvider,
    IUserDisplayNameResolver displayNames,
    ILogger<SecretSettingsService> logger)
{
    /// <summary>
    /// One entry per registry key, whether or not a row exists.
    ///
    /// <para>
    /// <c>Unreadable</c> is computed by attempting an unprotect and discarding the result. That is a
    /// real trade-off: it touches every stored credential on each admin page load. It is kept because
    /// the alternative is worse — Data Protection offers no integrity check short of decryption, so
    /// the only other option is reporting <c>Set</c> for a row nobody can decrypt, which actively
    /// misleads an administrator into believing a credential is live until the consuming feature fails
    /// in production. The probe never materialises the plaintext as a managed string (see
    /// <see cref="ISecretProtector.CanUnprotect"/>).
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<SecretSettingStatusDto>> GetStatusesAsync(
        ClaimsPrincipal caller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var keys = registry.All.Select(descriptor => descriptor.Key).ToList();
        var rows = await context.SystemSettingSecrets.AsNoTracking()
            .Where(row => keys.Contains(row.Key))
            .ToDictionaryAsync(row => row.Key, cancellationToken);

        var names = await displayNames.ResolveAsync(
            caller, rows.Values.Select(row => row.UpdatedBy), cancellationToken);

        var statuses = new List<SecretSettingStatusDto>(registry.All.Count);
        foreach (var descriptor in registry.All)
        {
            if (!rows.TryGetValue(descriptor.Key, out var row))
            {
                statuses.Add(new SecretSettingStatusDto
                {
                    Key = descriptor.Key,
                    State = SecretSettingState.NotSet,
                });
                continue;
            }

            statuses.Add(new SecretSettingStatusDto
            {
                Key = descriptor.Key,
                State = Readable(row) ? SecretSettingState.Set : SecretSettingState.Unreadable,
                UpdatedAt = row.UpdatedAt,
                UpdatedBy = row.UpdatedBy,
                UpdatedByDisplayName = row.UpdatedBy is null
                    ? null
                    : names.TryGetValue(row.UpdatedBy, out var name) ? name : null,
            });
        }

        return statuses;
    }

    /// <summary>
    /// Stores one credential. Authorization has already run at the action; the descriptor's own claim
    /// is re-checked here as defence in depth for a non-HTTP caller.
    /// </summary>
    public async Task SetAsync(
        ClaimsPrincipal caller,
        string actorUserId,
        string key,
        string plaintext,
        CancellationToken cancellationToken = default)
    {
        var descriptor = Authorize(caller, key);

        // Before anything is protected: a value accepted onto an ephemeral key ring would be
        // unrecoverable at the next restart, and returning 204 for it is the worst outcome available.
        if (!keyRing.IsDurable)
        {
            throw new KeyRingNotDurableException(KeyRingDurability.RefusalMessage);
        }

        var value = Validate(descriptor, plaintext);
        var ciphertext = protector.Protect(descriptor.Key, value);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var row = await context.SystemSettingSecrets
            .FirstOrDefaultAsync(existing => existing.Key == descriptor.Key, cancellationToken);

        if (row is null)
        {
            row = new SystemSettingSecret { Key = descriptor.Key };
            context.SystemSettingSecrets.Add(row);
        }

        row.Ciphertext = ciphertext;
        row.ProtectionScheme = SystemSettingSecret.CurrentProtectionScheme;
        row.UpdatedAt = now;
        row.UpdatedBy = actorUserId;

        await context.SaveChangesAsync(cancellationToken);

        Audit(actorUserId, descriptor.Key, "set");
    }

    /// <summary>
    /// Removes one credential. Idempotent: clearing an absent key also succeeds, since the caller's
    /// intent ("this must not be set") is satisfied either way and distinguishing them is a needless
    /// oracle.
    ///
    /// <para>
    /// This is the CONTROLLER path — a clear the administrator asked for directly — so it keeps its
    /// self-contained shape: its own <c>SaveChanges</c>, its own audit line, immediately. The removal
    /// itself is delegated to <see cref="StageClearAsync"/> so there is one implementation rather than
    /// two (issue #8 §5.8).
    /// </para>
    /// </summary>
    public async Task ClearAsync(
        ClaimsPrincipal caller, string actorUserId, string key, CancellationToken cancellationToken = default)
    {
        var descriptor = Authorize(caller, key);

        var staged = await StageClearAsync(context, descriptor.Key, ClearedDirectly, cancellationToken);

        await context.SaveChangesAsync(cancellationToken);

        AuditStagedClear(actorUserId, staged);
    }

    /// <summary>
    /// The composable half of a clear (issue #8 §5.8): queue the row's removal onto
    /// <paramref name="target"/> and hand back the audit record, WITHOUT saving and WITHOUT logging.
    ///
    /// <para>
    /// <strong>Why <see cref="ClearAsync"/> could not be reused as-is.</strong> It calls
    /// <c>SaveChangesAsync</c> itself and writes its own audit line immediately, so calling it from
    /// inside <c>SystemSettingsService.UpdateAsync</c>'s write loop would commit the credential
    /// removal in its own earlier transaction. The two writes could then interleave — and the
    /// dangerous direction is the one that is easy to miss: the settings write fails or is interrupted
    /// AFTER the clear, or the clear fails after the settings write commits, leaving the attacker's
    /// host live with the old credential still stored and readable. That is precisely the exploit G4
    /// exists to close, reached without the attacker ever needing the credential themselves.
    /// </para>
    ///
    /// <para>
    /// <strong>It deliberately performs no authorization check</strong>, unlike every other entry
    /// point here. A G4/G7 clear is a safety CONSEQUENCE of a change the caller was already authorized
    /// to make, not an action they requested — so refusing it for a caller who somehow held the
    /// setting's claim but not the secret's would leave the credential live on a host it was never
    /// entered for, which is the opposite of the intent. Both claims are
    /// <c>system-settings.security.update</c> today, so the branch is unreachable; it is stated
    /// because a future split must resolve it this way and not the other.
    /// </para>
    ///
    /// <para>
    /// <strong>Idempotent, and safe to run twice.</strong> The caller runs inside a retrying execution
    /// strategy whose delegate can be re-executed, so this must not depend on state built before it:
    /// it reads the row it needs itself, and removing an already-absent row is a no-op.
    /// </para>
    /// </summary>
    public async Task<StagedSecretClear> StageClearAsync(
        OdysseyContext target, string key, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        var descriptor = registry.Find(key)
            ?? throw new DomainNotFoundException($"Secret setting '{key}' is not registered.");

        var row = await target.SystemSettingSecrets
            .FirstOrDefaultAsync(existing => existing.Key == descriptor.Key, cancellationToken);

        if (row is not null)
        {
            target.SystemSettingSecrets.Remove(row);
        }

        return new StagedSecretClear(descriptor.Key, reason);
    }

    /// <summary>
    /// Writes the audit line for a staged clear. Called by the caller AFTER its transaction commits,
    /// so a rolled-back clear cannot leave a line claiming it happened.
    ///
    /// <para>
    /// Written unconditionally, including when no row existed — matching <see cref="ClearAsync"/> and
    /// for the reason stated on this class: the presence or absence of a line must never be a signal
    /// about the value. Row presence is not the value, but it is reported by the status endpoint to
    /// exactly the callers who can read this log, so suppressing the line would buy nothing and cost
    /// the record of an attempted clear.
    /// </para>
    /// </summary>
    public void AuditStagedClear(string actorUserId, StagedSecretClear staged)
    {
        ArgumentNullException.ThrowIfNull(staged);
        Audit(actorUserId, staged.Key, staged.Reason);
    }

    /// <summary>The audit verb for a clear the administrator asked for directly.</summary>
    public const string ClearedDirectly = "cleared";

    /// <summary>The audit verb for the G4 clear — the relay changed under a stored credential.</summary>
    public const string ClearedByHostChange = "cleared (SMTP host changed)";

    /// <summary>The audit verb for the G7 clear — the transport stopped being encrypted.</summary>
    public const string ClearedByStartTlsOff = "cleared (STARTTLS turned off)";

    private SecretSettingDescriptor Authorize(ClaimsPrincipal caller, string key)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var descriptor = registry.Find(key)
            ?? throw new DomainNotFoundException($"Secret setting '{key}' is not registered.");

        if (!caller.HasClaim(PermissionClaims.Type, descriptor.RequiredClaim))
        {
            throw new SystemSettingsForbiddenException(
                $"Secret setting '{descriptor.Key}' requires the '{descriptor.RequiredClaim}' claim.");
        }

        return descriptor;
    }

    /// <summary>
    /// Trim → reject empty → reject non-printable-ASCII → length bound → the descriptor's own
    /// validator, the same order and the same three shared rules <c>StringSetting.Validate</c> applies.
    ///
    /// <para>
    /// The character rule is <em>tighter</em> than the plaintext settings': printable ASCII
    /// <c>0x20</c>–<c>0x7E</c>, not merely "no control characters". Rejecting only <c>&lt; 0x20</c> and
    /// <c>0x7F</c> admits every multi-byte character, and a 1,024-character value of 3-byte BMP
    /// characters protects to roughly 4,176 base64url characters — which MariaDB outside strict mode
    /// would silently truncate, returning <c>204</c> for a credential that is permanently unreadable.
    /// Every credential shape in scope is printable ASCII by construction, and CR/LF reaching an SMTP
    /// handshake or an HTTP header is injection either way.
    /// </para>
    ///
    /// <para>
    /// <strong>No message names the value or its length</strong> (§9 rule 3) — stricter than the
    /// plaintext settings service, which does interpolate offending values.
    /// </para>
    /// </summary>
    private static string Validate(SecretSettingDescriptor descriptor, string plaintext)
    {
        var value = (plaintext ?? string.Empty).Trim();

        if (value.Length == 0)
        {
            // Clearing is DELETE, not an empty PUT, so "set it to nothing" can never be an accident of
            // a blank form field.
            throw Invalid(descriptor, "must not be empty.");
        }

        if (value.Any(c => c < 0x20 || c > 0x7E))
        {
            throw Invalid(descriptor, "must contain printable ASCII characters only.");
        }

        if (value.Length > descriptor.MaxLength)
        {
            throw Invalid(descriptor, $"must be {descriptor.MaxLength} characters or fewer.");
        }

        if (descriptor.Validator?.Invoke(value) is { } problem)
        {
            throw Invalid(descriptor, problem);
        }

        return value;
    }

    /// <summary>
    /// Keyed on the request DTO's property name, not the setting key: <c>ApiProblem.ErrorFor</c> joins
    /// on the DTO property, and these are per-key endpoints whose body has exactly one property. The
    /// settings page's key-based join works only because that page PUTs one body containing every
    /// field, which this design deliberately does not.
    /// </summary>
    private static DomainValidationException Invalid(SecretSettingDescriptor descriptor, string problem) =>
        new($"Credential '{descriptor.Key}' {problem}",
            $"system-settings.secret.invalid.{descriptor.Key}",
            nameof(SecretSettingUpdate.Value));

    private bool Readable(SystemSettingSecret row) =>
        // The forward-compatibility tag doing its job: a row written by a future format is REPORTED,
        // not misparsed as a decryption failure of the current one.
        string.Equals(row.ProtectionScheme, SystemSettingSecret.CurrentProtectionScheme, StringComparison.Ordinal)
        && protector.CanUnprotect(row.Key, row.Ciphertext);

    /// <summary>
    /// Key, actor, action, timestamp — enough for ISO 27001 / GDPR Art. 32 accountability (who changed
    /// which credential when) without the log becoming a secondary, unencrypted, long-retention copy
    /// of every credential. Written unconditionally on success, so its presence never signals whether
    /// the value changed.
    /// </summary>
    private void Audit(string actorUserId, string key, string action) =>
        logger.LogInformation(
            "Secret setting {Key} {Action} by {ActorUserId}.", key, action, actorUserId);
}

/// <summary>
/// One queued-but-uncommitted secret removal and the audit verb it will be recorded under
/// (issue #8 §5.8). The record carries no value and never could: <see cref="StagedSecretClear"/>
/// names the key it removed, not what was in it.
/// </summary>
public sealed record StagedSecretClear(string Key, string Reason);

/// <summary>
/// The key ring would not survive a restart, so a write is refused (issue #444 §11). Mapped to
/// <c>503</c> by <c>SecretSettingsController</c> rather than by the shared <c>DomainException</c>
/// hierarchy: it is an infrastructure condition of this one surface, not a domain rule, and it
/// deliberately carries no <c>Retry-After</c> — it is not retryable until an operator acts.
/// </summary>
public sealed class KeyRingNotDurableException(string message) : Exception(message);
