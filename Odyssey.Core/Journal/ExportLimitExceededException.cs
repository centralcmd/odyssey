using Odyssey.Core;
using System.Net;
using Odyssey.Core.Finance;

namespace Odyssey.Core.Journal;

/// <summary>
/// The requested VJOURNAL export matched more entries than the per-request cap allows (issue #339 §12).
/// Maps to <c>400 Bad Request</c> and carries the stable <see cref="Code"/> discriminator so the client
/// can show a specific "narrow your filters" message instead of a generic failure (§11).
/// </summary>
public sealed class ExportLimitExceededException : DomainException
{
    /// <summary>Stable, machine-readable discriminator surfaced as the <c>code</c> problem extension.</summary>
    public const string DiscriminatorCode = "journal_entries_export_limit_exceeded";

    public ExportLimitExceededException(int limit)
        : base($"The export matched more than {limit} entries. Narrow your filters and try again.")
    {
    }

    public override int StatusCode => (int)HttpStatusCode.BadRequest;

    public override string? Code => DiscriminatorCode;
}
