namespace Odyssey.Core.Legal;

/// <summary>
/// Derives the value that replaces a deleted user's <c>UserId</c> on their acceptance rows (issue #354
/// §6, §10.7).
/// </summary>
public interface ILegalPseudonymizer
{
    /// <summary>
    /// <c>HMAC-SHA256(secret, subject)</c> as lowercase hex, where <paramref name="subject"/> is the
    /// user's email upper-cased invariantly (matching Identity's own normalisation, so re-derivation
    /// works from a differently-cased claim of the same address).
    ///
    /// <para>
    /// <strong>Asynchronous since issue #445 Wave 4</strong>, because the key now lives in the encrypted
    /// secret store and is read live on each call. A value captured once at construction could not
    /// follow a rotation, and this type is a singleton resolved from the root provider — so it can hold
    /// neither the key nor the scoped context behind it.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No usable key: the stored row cannot be decrypted, or no row exists and this is Production.
    /// Deliberately a throw rather than a substituted value — the caller's deletion runs in a
    /// transaction, so failing rolls the deletion back and leaves the acceptance rows intact and
    /// attributable, which is the recoverable outcome. Writing a pseudonym derived from the wrong key
    /// is not recoverable.
    /// </exception>
    Task<string> PseudonymizeAsync(string? subject, CancellationToken cancellationToken = default);
}
