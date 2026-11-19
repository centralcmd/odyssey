using Odyssey.Core;

namespace Odyssey.Core.Journal;

/// <summary>
/// A calendar <c>.ics</c> import failed after at least one batch had committed (issue #516 §5, FB2/FN2). A streaming
/// import commits in batches, so the earlier batches stay applied; this says so, with counts only, and keeps the
/// original failure's status (<c>400</c> for a cap, <c>500</c>/<c>503</c> otherwise) so the client can tell the
/// user both what went wrong and that re-running the same file is safe and completes it.
/// </summary>
/// <remarks>
/// A cancellation is never wrapped: there is no client left to receive the body.
/// </remarks>
public sealed class CalendarIcsImportPartiallyAppliedException : DomainException
{
    public const string DiscriminatorCode = "calendar_ics_import_partially_applied";

    private readonly int statusCode;
    private readonly Dictionary<string, object?> extensions;

    public CalendarIcsImportPartiallyAppliedException(
        string message, int statusCode, int committedBatches, int committedEvents, Exception innerException)
        : base(message, innerException)
    {
        this.statusCode = statusCode;
        CommittedBatches = committedBatches;
        CommittedEvents = committedEvents;
        extensions = new Dictionary<string, object?>
        {
            ["committedBatches"] = committedBatches,
            ["committedEvents"] = committedEvents,
        };
    }

    public int CommittedBatches { get; }

    public int CommittedEvents { get; }

    public override int StatusCode => statusCode;

    public override string? Code => DiscriminatorCode;

    public override IReadOnlyDictionary<string, object?>? Extensions => extensions;
}
