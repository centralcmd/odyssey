using System.Net;

namespace Odyssey.Core;

/// <summary>
/// Base type for domain errors that map deterministically to an HTTP status code. Controllers catch
/// the base type (or one of the three concrete kinds below) and translate it via
/// <see cref="StatusCode"/>, so the exception-to-HTTP mapping lives in one place instead of being
/// re-derived in every catch block. Replaces the previous family of ~one-off exception classes.
/// </summary>
public abstract class DomainException : Exception
{
    /// <summary>The HTTP status code this error maps to.</summary>
    public abstract int StatusCode { get; }

    /// <summary>
    /// Optional stable, machine-readable discriminator surfaced as the <c>code</c> problem-details
    /// extension, letting a client distinguish this error from other responses with the same status
    /// (e.g. an export cap being exceeded vs. any other <c>400</c>). Null when no discriminator applies.
    /// </summary>
    public virtual string? Code => null;

    /// <summary>
    /// Per-field messages for the problem-details <c>errors</c> extension, or <see langword="null"/>
    /// when this rejection is not attributable to a specific field. On the base type because two of the
    /// concrete kinds carry it: a <c>400</c> for an unresolvable link and a <c>422</c> for a collection
    /// over its cap both belong on the same form control (issue #27 §11).
    /// </summary>
    public IReadOnlyDictionary<string, string[]>? Errors { get; protected init; }

    protected DomainException(string message) : base(message)
    {
    }

    protected DomainException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Invalid input or a business-rule violation. Maps to <c>400 Bad Request</c>.
/// </summary>
public sealed class DomainValidationException : DomainException
{
    public override int StatusCode => (int)HttpStatusCode.BadRequest;

    public override string? Code { get; }

    public DomainValidationException(string message) : base(message)
    {
    }

    /// <summary>
    /// Carries a stable, machine-readable <paramref name="code"/> alongside the message — e.g. a
    /// per-field code a client maps back to the responsible form field (issue #343 §11 UX, fe D2).
    /// </summary>
    public DomainValidationException(string message, string? code) : base(message)
    {
        Code = code;
    }

    /// <summary>
    /// As above, plus the <paramref name="field"/> this rejection belongs to, surfaced in the
    /// problem-details <c>errors</c> dictionary so a form can render the message on the offending
    /// control rather than only in a toast (issue #421 Wave 0b).
    ///
    /// <para>
    /// <see cref="Code"/> stays the machine-readable discriminator; <see cref="Errors"/> is the
    /// human-readable, per-field one. A single throw names a single field — the multi-field case is
    /// served by <c>[ApiController]</c> model validation, which aggregates every annotation failure
    /// into the same <c>errors</c> shape before any of this code runs.
    /// </para>
    /// </summary>
    public DomainValidationException(string message, string? code, string field) : base(message)
    {
        Code = code;
        Errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { [field] = [message] };
    }
}

/// <summary>
/// A referenced entity does not exist. Maps to <c>404 Not Found</c>.
/// </summary>
public sealed class DomainNotFoundException : DomainException
{
    public override int StatusCode => (int)HttpStatusCode.NotFound;

    public DomainNotFoundException(string message) : base(message)
    {
    }
}

/// <summary>
/// A conflict with existing state — typically a duplicate of something that already exists. Maps to
/// <c>409 Conflict</c>.
/// </summary>
public sealed class DomainConflictException : DomainException
{
    public override int StatusCode => (int)HttpStatusCode.Conflict;

    public DomainConflictException(string message) : base(message)
    {
    }
}

/// <summary>
/// The request is well-formed but cannot be processed — e.g. a cap/quota that would be exceeded, or
/// an operation against an archived resource. Maps to <c>422 Unprocessable Entity</c>.
/// </summary>
public sealed class DomainUnprocessableException : DomainException
{
    public override int StatusCode => (int)HttpStatusCode.UnprocessableEntity;

    public DomainUnprocessableException(string message) : base(message)
    {
    }

    /// <summary>
    /// As above, plus the <paramref name="field"/> this rejection belongs to, surfaced in the
    /// problem-details <c>errors</c> dictionary so a form can render the message on the offending
    /// control. A cap that is exceeded is attributable to exactly one field, and the client needs to
    /// mark and focus it (issue #27 §3 State 5, §11).
    /// </summary>
    public DomainUnprocessableException(string message, string field) : base(message)
    {
        Errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { [field] = [message] };
    }
}
