namespace Odyssey.Core.Finance;

/// <summary>
/// The field rules every owner's event log applies on its HTTP write path — title, the two optional
/// free-text fields and the "not in the future" bound (issues #138 §8, #209 §8). Declared once so the
/// contract and property logs cannot drift: a divergence here would make one log accept what the
/// other refuses, silently.
/// </summary>
public static class EventFieldRules
{
    /// <summary>
    /// How far ahead of the server clock an <c>occurredAt</c> may sit before it is rejected. An
    /// ordinary "now" from a client whose clock runs slightly fast must not be spuriously refused, and
    /// a minute is far short of anything a user would mean by a future-dated event (issue #138 §8.3).
    /// </summary>
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromSeconds(60);

    public const string OccurredAtField = "occurredAt";
    public const string TitleField = "title";
    public const string TypeField = "type";

    /// <summary>
    /// The "not in the future" bound. It is the server clock — runtime state, not a compile-time
    /// constant — so it belongs in the service and not in a <c>[Range]</c> attribute.
    /// </summary>
    /// <param name="asUnprocessable">
    /// Which status the refusal carries. The contract log shipped it as a <c>400</c> (issue #138) and
    /// keeps that wire behaviour; the property log reports it as the <c>422</c> issue #209 §5.2
    /// specifies — the body is well-formed and only the server clock makes it unacceptable. The
    /// <b>bound</b> is the same either way, which is the part that must not drift.
    /// </param>
    /// <exception cref="DomainValidationException">More than <see cref="FutureTolerance"/> ahead (contract).</exception>
    /// <exception cref="DomainUnprocessableException">More than <see cref="FutureTolerance"/> ahead (property).</exception>
    public static DateTime ValidateOccurredAt(DateTime occurredAt, DateTime utcNow, bool asUnprocessable = false)
    {
        var utc = NormalizeToUtc(occurredAt);
        if (utc > utcNow + FutureTolerance)
        {
            const string message = "An event cannot have occurred in the future.";
            throw asUnprocessable
                ? new DomainUnprocessableException(message, OccurredAtField)
                : new DomainValidationException(message, code: null, field: OccurredAtField);
        }

        return utc;
    }

    /// <summary>
    /// <c>[StringLength(MinimumLength = 1)]</c> accepts a string of spaces, so the whitespace-only case
    /// is rejected here — as empty, which is what it is.
    /// </summary>
    /// <exception cref="DomainValidationException">The title is blank.</exception>
    public static string RequireTitle(string? title)
    {
        var trimmed = title?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new DomainValidationException("Title is required.", code: null, field: TitleField);
        }

        return trimmed;
    }

    /// <summary>A blank optional field is stored as absent, not as a string of spaces.</summary>
    public static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static DateTime NormalizeToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
