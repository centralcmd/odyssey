namespace Odyssey.Core.Finance;

/// <summary>
/// The single definition of "a document's validity dates are normalised to UTC, individually storable,
/// and <c>ValidTo</c> is not before <c>ValidFrom</c>" (issue #146 §8.2). Both document surfaces —
/// account files and contract files — route their write paths through here, so the rule cannot drift
/// between them the way four hand-written copies of it would.
/// </summary>
/// <remarks>
/// It lives in the service layer rather than on the DTOs as <c>IValidatableObject</c> for three
/// reasons: a cross-field rule expressed per DTO would be four copies of one rule; model validation
/// runs on the HTTP path only, while the domain services have non-HTTP callers; and normalisation
/// belongs with the comparison, since splitting them would let a <c>Local</c>-kind pair compare in one
/// timezone and store in another.
///
/// <para>
/// The three field names are the property names all four request DTOs share, so the problem-details
/// <c>errors</c> key a client joins on is the same on every surface.
/// </para>
/// </remarks>
public static class DocumentValidity
{
    public const string ValidFromField = "ValidFrom";
    public const string ValidToField = "ValidTo";
    public const string IssuedAtField = "IssuedAt";

    /// <summary>
    /// The earliest instant MariaDB's <c>datetime</c> can store. Stated as a property of the storage
    /// column, not as a judgement about plausible document dates: a deed issued in 1650 is refused by
    /// the column, and no narrower "sensible year" window is imposed on top of it.
    /// </summary>
    public static readonly DateTime MinStorable = new(1000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The latest instant MariaDB's <c>datetime</c> can store.</summary>
    public static readonly DateTime MaxStorable = new(9999, 12, 31, 23, 59, 59, DateTimeKind.Utc);

    /// <summary>
    /// Normalises the three client-supplied dates to UTC, range-checks each against the storage column
    /// independently, then rejects an inverted <c>ValidFrom</c>/<c>ValidTo</c> pair.
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// A date outside the storable range (named on its own field — any of the three can be the
    /// offender), or <c>ValidTo</c> before <c>ValidFrom</c> (named on <c>ValidTo</c>, the later of the
    /// pair and the control a user most likely just typed). Both are <c>400</c>: an inverted range is
    /// malformed input, not a well-formed request that cannot be processed.
    /// </exception>
    public static (DateTime? ValidFrom, DateTime? ValidTo, DateTime? IssuedAt) Normalize(
        DateTime? validFrom, DateTime? validTo, DateTime? issuedAt)
    {
        var from = NormalizeOne(validFrom, ValidFromField);
        var to = NormalizeOne(validTo, ValidToField);
        var issued = NormalizeOne(issuedAt, IssuedAtField);

        // By date, not by instant, matching ContractService.NormalizePartyTerm: these are calendar
        // facts on a document, and ValidFrom == ValidTo is a legitimate single-day validity.
        if (from is { } start && to is { } end && end.Date < start.Date)
        {
            throw new DomainValidationException(
                $"{ValidToField} must be on or after {ValidFromField}.",
                code: null,
                field: ValidToField);
        }

        return (from, to, issued);
    }

    private static DateTime? NormalizeOne(DateTime? value, string field)
    {
        if (value is not { } date)
        {
            return null;
        }

        var normalized = DateTimeNormalization.NormalizeToUtc(date);
        if (normalized < MinStorable || normalized > MaxStorable)
        {
            // Without this the value passes every other rule and then fails at SaveChangesAsync, which
            // GlobalExceptionHandler turns into an opaque 500 on a well-formed-looking request.
            throw new DomainValidationException(
                $"{field} must be between {MinStorable:yyyy-MM-dd} and {MaxStorable:yyyy-MM-dd}.",
                code: null,
                field: field);
        }

        return normalized;
    }
}
