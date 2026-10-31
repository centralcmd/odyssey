using System.Net;

namespace Odyssey.Core.Finance;

/// <summary>
/// Told when a file is about to take a new name (issue #362 §3.1 item 5), in the same unit of work as the rename:
/// whatever it stages on the shared context lands in the rename's own save. Finance knows only this interface; the
/// Journal module implements it, so no Finance → Journal dependency exists.
/// </summary>
public interface IFileRenameObserver
{
    /// <summary>Stages its consequences of renaming <paramref name="fileId"/> on the caller's context, unsaved.</summary>
    Task OnFileRenamingAsync(Guid fileId, CancellationToken cancellationToken);

    /// <summary>Whether a concurrency failure on this entity type is one this observer can recover from by re-running.</summary>
    bool Owns(Type entityType);
}

/// <summary>
/// A rename that kept colliding with a concurrent write it depends on (issue #362 §3.1): <c>409</c>,
/// <c>rename-busy</c>, retry in a second. Nothing was committed.
/// </summary>
public sealed class FileRenameBusyException()
    : DomainException("The file is being processed right now and could not be renamed. Try again in a moment.")
{
    public override int StatusCode => (int)HttpStatusCode.Conflict;

    public override string? Code => "rename-busy";

    public override int? RetryAfterSeconds => 1;
}
