using Odyssey.Core;
using System.Net;
using Odyssey.Core.Finance;

namespace Odyssey.Core.Journal;

/// <summary>
/// The requested export matched more records than the per-request cap allows (issue #339 §12). Maps to
/// <c>400 Bad Request</c> and carries a stable <see cref="Code"/> discriminator so the client can show a specific
/// "narrow your filters" message instead of a generic failure (§11). The journal keeps
/// <see cref="DiscriminatorCode"/> as the default; another surface passes its own code and wording (issue #516 R4),
/// so a client branches on the code rather than on which endpoint it called.
/// </summary>
public sealed class ExportLimitExceededException : DomainException
{
    /// <summary>The journal's discriminator, and the default.</summary>
    public const string DiscriminatorCode = "journal_entries_export_limit_exceeded";

    /// <summary>The calendar's discriminator (issue #516).</summary>
    public const string CalendarEventsCode = "calendar_events_export_limit_exceeded";

    private readonly string code;

    public ExportLimitExceededException(int limit)
        : this(limit, DiscriminatorCode, "entries")
    {
    }

    /// <param name="limit">The cap the export exceeded.</param>
    /// <param name="code">The surface's stable discriminator.</param>
    /// <param name="noun">What the surface exports, plural, for the message ("events").</param>
    public ExportLimitExceededException(int limit, string code, string noun)
        : base($"The export matched more than {limit} {noun}. Narrow your filters and try again.")
    {
        this.code = code;
    }

    public override int StatusCode => (int)HttpStatusCode.BadRequest;

    public override string? Code => code;
}
