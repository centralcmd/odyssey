using System.Net;

namespace Odyssey.Core.FileStorage;

// The runtime failure classes of issue #346 §9. Every message is curated: an error body carries no root path,
// relative path, file name or object id, and names at most the volume.

/// <summary>
/// The store is not set up (§3.6 no-op rule), the feature is unbound, or its volume is <c>Degraded</c>.
/// <c>503</c> with <c>Retry-After: 60</c>.
/// </summary>
public sealed class FileStorageUnavailableException(string message) : DomainException(message)
{
    public const int RetryAfterSeconds = 60;

    public override int StatusCode => (int)HttpStatusCode.ServiceUnavailable;

    public override string? Code => "file-storage-unavailable";
}

/// <summary>Free space below the floor, or ENOSPC mid-write. <c>507</c>.</summary>
public sealed class FileStorageFullException(string message) : DomainException(message)
{
    public override int StatusCode => (int)HttpStatusCode.InsufficientStorage;

    public override string? Code => "file-storage-full";
}

/// <summary>The stream passed the caller's <c>maxBytes</c>. <c>413</c>.</summary>
public sealed class StoredObjectTooLargeException(string message) : DomainException(message)
{
    public override int StatusCode => (int)HttpStatusCode.RequestEntityTooLarge;

    public override string? Code => "file-storage-too-large";
}

/// <summary>
/// A committed object whose file is missing, the wrong length or flagged by the scrub — or a path that failed
/// the containment or symlink checks. <c>500</c>; the store has already logged the cause.
/// </summary>
public sealed class FileStorageIntegrityException(string message) : DomainException(message)
{
    public override int StatusCode => (int)HttpStatusCode.InternalServerError;

    public override string? Code => "file-storage-integrity";
}

/// <summary>A code defect in a consumer or a feature declaration: a bad layout, an unmapped content type, an invalid suffix. <c>500</c>.</summary>
public sealed class StoreContractViolationException(string message) : DomainException(message)
{
    public override int StatusCode => (int)HttpStatusCode.InternalServerError;

    public override string? Code => "file-storage-contract-violation";
}

/// <summary>
/// A startup verification check failed (§3.6). Not a <see cref="DomainException"/>: it never reaches a request —
/// it stops the host before it listens. The message names the check, the volume or feature, the configuration
/// key and the remedy, and never a file's contents.
/// </summary>
public sealed class FileStorageMisconfiguredException(string check, string message) : Exception($"File storage check {check} failed: {message}")
{
    /// <summary>The check's name, e.g. <c>VolumeLost</c>.</summary>
    public string Check { get; } = check;
}
