namespace Odyssey.Core.Finance;

/// <summary>
/// Serializes the two application-level operations that, together, must preserve the "a policy's
/// required insurer always exists" invariant now that the DB foreign key is gone (Contact moved to
/// OdysseyContext, issue #325 follow-up): creating/updating an <c>InsurancePolicy</c> that references a
/// contact as its insurer, and deleting that contact. Without the old <c>ON DELETE RESTRICT</c> FK, a
/// concurrent interleave of "validate insurer exists" (policy write) and "no policy references this
/// contact" (delete) could persist a policy pointing at a just-deleted contact — a TOCTOU race.
///
/// Both paths acquire this lock keyed on the contact id before their check-and-write, so they run
/// mutually exclusively for a given contact. Backed by a MariaDB session advisory lock
/// (<c>GET_LOCK</c>), which is server-scoped and therefore shared across the Finance and Journal
/// contexts (same physical database). A no-op on non-relational providers (the in-memory test store has
/// no real concurrency).
/// </summary>
public interface IContactMutationLock
{
    /// <summary>
    /// Acquires the per-contact lock, returning a handle that releases it on dispose. Wrap the whole
    /// check-and-write critical section in an <c>await using</c> over the result.
    /// </summary>
    Task<IAsyncDisposable> AcquireAsync(Guid contactId, CancellationToken cancellationToken = default);
}
