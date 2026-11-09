using System.Net;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;

namespace Odyssey.Core;

/// <summary>A <c>409</c> that names a stable code, optionally a field, and carries extra problem values (issue #446 §9).</summary>
public sealed class DomainCodedConflictException : DomainException
{
    public override int StatusCode => (int)HttpStatusCode.Conflict;

    public override string? Code { get; }

    /// <summary>Named problem values beyond <c>code</c> and <c>errors</c>, e.g. the id a duplicate collided with.</summary>
    public override IReadOnlyDictionary<string, object?>? Extensions { get; }

    public DomainCodedConflictException(
        string message, string code, string? field = null, IReadOnlyDictionary<string, object?>? values = null)
        : base(message)
    {
        Code = code;
        Extensions = values;
        if (field is not null)
        {
            Errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { [field] = [message] };
        }
    }
}

/// <summary>The instrument-feature problem codes (issue #446 §9). Stable wire values.</summary>
public static class InstrumentProblemCodes
{
    public const string InstrumentDuplicate = "instrument-duplicate";
    public const string PriceDuplicate = "price-duplicate";
    public const string InstrumentHasPrices = "instrument-has-prices";
    public const string PurgeCountMismatch = "purge-count-mismatch";
}

/// <summary>
/// MariaDB's duplicate-key error (1062) for the instrument module. A local copy of the one predicate it needs, because
/// the shared helper sits in <c>Odyssey.Core.Journal</c> and Finance takes no new dependency on Journal.
/// </summary>
internal static class InstrumentDbErrors
{
    public static bool IsDuplicateKey(DbUpdateException exception) =>
        exception.InnerException is MySqlException { Number: 1062 };
}
