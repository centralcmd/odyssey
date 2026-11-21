using System.Net;

namespace Odyssey.Core.Journal;

/// <summary>
/// The calendar attachment refusals that carry a code (issue #521 §9): an export or import that needs a read or create
/// claim the caller lacks (<c>403</c>), and a link write another writer held (<c>409</c>). Messages and extensions name
/// the missing claim, never an id, a name or a count of anything the caller cannot read.
/// </summary>
public sealed class CalendarAttachmentsException : DomainException
{
    public const string ExportForbiddenCode = "calendar_ics_export_attachments_forbidden";

    public const string ImportForbiddenCode = "calendar_ics_import_attachments_forbidden";

    public const string LinksBusyCode = "calendar-links-busy";

    private readonly int status;
    private readonly string code;
    private readonly Dictionary<string, object?>? extensions;

    private CalendarAttachmentsException(int status, string code, string message, Dictionary<string, object?>? extensions = null)
        : base(message)
    {
        this.status = status;
        this.code = code;
        this.extensions = extensions;
    }

    public override int StatusCode => status;

    public override string? Code => code;

    public override IReadOnlyDictionary<string, object?>? Extensions => extensions;

    /// <summary>The claims an export or import needs and the caller lacks, in a fixed order.</summary>
    public IReadOnlyList<string> MissingClaims =>
        extensions?.GetValueOrDefault("missingClaims") as IReadOnlyList<string> ?? [];

    public static CalendarAttachmentsException ExportForbidden(IReadOnlyList<string> missingClaims) =>
        new((int)HttpStatusCode.Forbidden, ExportForbiddenCode,
            $"This export carries attached files or photos, which needs the {string.Join(" and ", missingClaims)} "
            + "permission. Export without attachments instead, or ask an administrator.",
            new Dictionary<string, object?> { ["missingClaims"] = missingClaims });

    public static CalendarAttachmentsException ImportForbidden(IReadOnlyList<string> missingClaims) =>
        new((int)HttpStatusCode.Forbidden, ImportForbiddenCode,
            $"This file carries attached files or photos, and importing them needs the {string.Join(" and ", missingClaims)} "
            + "permission. Nothing was imported.",
            new Dictionary<string, object?> { ["missingClaims"] = missingClaims });

    /// <summary>A concurrent identical add won a link's unique index: retried once by the writer before it is busy.</summary>
    public bool IsLinkRace { get; private init; }

    public static CalendarAttachmentsException LinkRace() =>
        new((int)HttpStatusCode.Conflict, LinksBusyCode,
            "This event's attachments are being changed by another request. Try again in a moment.")
        { IsLinkRace = true };

    public static CalendarAttachmentsException LinksBusy() =>
        new((int)HttpStatusCode.Conflict, LinksBusyCode,
            "This event's attachments are being changed by another request. Try again in a moment.");
}
