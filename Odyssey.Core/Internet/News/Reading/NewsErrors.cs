using System.Net;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;

namespace Odyssey.Core.Internet.News.Reading;

/// <summary>
/// A news API error (issue #470 §9.1): a status, a stable <see cref="NewsErrorCodes"/> code, optionally the field it
/// belongs to and safe extensions (route ids, <c>existingFeedId</c>, counts). Every message is curated: no URL, no
/// cursor, no search text and no publisher text is ever echoed.
/// </summary>
public sealed class NewsException : DomainException
{
    private readonly int statusCode;
    private readonly string code;
    private readonly int? retryAfter;
    private readonly IReadOnlyDictionary<string, object?>? extensions;

    public NewsException(
        HttpStatusCode status,
        string code,
        string message,
        string? field = null,
        IReadOnlyDictionary<string, object?>? extensions = null,
        int? retryAfterSeconds = null) : base(message)
    {
        statusCode = (int)status;
        this.code = code;
        retryAfter = retryAfterSeconds;
        this.extensions = extensions;
        if (field is not null)
        {
            Errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { [field] = [message] };
        }
    }

    public override int StatusCode => statusCode;

    public override string? Code => code;

    public override int? RetryAfterSeconds => retryAfter;

    public override IReadOnlyDictionary<string, object?>? Extensions => extensions;
}

/// <summary>The news failure classes of §9.1, built in one place so every call site raises the same shape.</summary>
public static class NewsErrors
{
    public const int StorageRetryAfterSeconds = 30;

    public const int BusyRetryAfterSeconds = 2;

    public static NewsException StorageUnavailable() => new(
        HttpStatusCode.ServiceUnavailable, NewsErrorCodes.StorageUnavailable,
        "News storage is unavailable right now; try again shortly.", retryAfterSeconds: StorageRetryAfterSeconds);

    public static NewsException Busy() => new(
        HttpStatusCode.ServiceUnavailable, NewsErrorCodes.Busy,
        "The news reader is busy right now; try again in a moment.", retryAfterSeconds: BusyRetryAfterSeconds);

    public static NewsException FeedDuplicate(Guid? existingFeedId) => new(
        HttpStatusCode.Conflict, NewsErrorCodes.FeedDuplicate,
        existingFeedId is null
            ? "A feed with this address is being deleted; try again once it is gone."
            : "A feed with this address already exists.",
        "url",
        existingFeedId is { } id ? new Dictionary<string, object?> { ["existingFeedId"] = id } : null);

    public static NewsException FeedChanged(Guid feedId) => new(
        HttpStatusCode.Conflict, NewsErrorCodes.FeedChanged,
        $"News feed {feedId} was changed by someone else; reload it and try again.", "version");

    public static NewsException FeedLimit(int limit) => new(
        HttpStatusCode.UnprocessableEntity, NewsErrorCodes.FeedLimit,
        $"The news reader holds at most {limit} feeds, archived feeds included.");

    public static NewsException FeedInactive(Guid feedId) => new(
        HttpStatusCode.UnprocessableEntity, NewsErrorCodes.FeedInactive,
        $"News feed {feedId} is paused, archived or quarantined, so it cannot be refreshed.");

    public static NewsException FeedNotQuarantined(Guid feedId) => new(
        HttpStatusCode.UnprocessableEntity, NewsErrorCodes.FeedNotQuarantined,
        $"News feed {feedId} is not quarantined.");

    public static NewsException UrlRejected(string message) => new(
        HttpStatusCode.UnprocessableEntity, NewsErrorCodes.FeedUrlRejected, message, "url");

    public static NewsException TagNotUsable() => new(
        HttpStatusCode.UnprocessableEntity, NewsErrorCodes.TagNotUsable,
        "Every tag must exist, and an archived tag cannot be newly added.", "tagIds");

    public static NewsException TagDuplicate() => new(
        HttpStatusCode.Conflict, NewsErrorCodes.TagDuplicate, "A news tag with this name already exists.", "name");

    public static NewsException TagLimit() => new(
        HttpStatusCode.UnprocessableEntity, NewsErrorCodes.TagLimit, $"The news reader holds at most {NewsLimits.MaxTags} tags.");

    public static NewsException TagInUse(int feedCount, int articleCount) => new(
        HttpStatusCode.Conflict, NewsErrorCodes.TagInUse,
        "Tag is in use by one or more feeds, articles or episodes; archive it instead of deleting.",
        extensions: new Dictionary<string, object?> { ["feedCount"] = feedCount, ["articleCount"] = articleCount });

    public static NewsException TagArchived(Guid tagId) => new(
        HttpStatusCode.UnprocessableEntity, NewsErrorCodes.TagArchived, $"News tag {tagId} is archived.", "tagId");

    public static NewsException MarkReadScope(string field, string message) => new(
        HttpStatusCode.UnprocessableEntity, NewsErrorCodes.MarkReadScopeInvalid, message, field);

    public static NewsException FilterUnknown(string field) => new(
        HttpStatusCode.UnprocessableEntity, NewsErrorCodes.FilterUnknown,
        "Every id must name an existing feed or tag.", field);

    // ── OPML import (issue #508 §5.2, §9) ──────────────────────────────────────────────────────────────────────────

    public static NewsException OpmlFileRequired() => new(
        HttpStatusCode.BadRequest, NewsErrorCodes.OpmlFileRequired, "Choose an OPML file to import.", "file");

    public static NewsException OpmlWrongType() => new(
        HttpStatusCode.BadRequest, NewsErrorCodes.OpmlWrongType, "The file must be an OPML file (.opml or .xml).", "file");

    /// <summary>The generic detail of §9 item 3: never the parser's text, never the file's content.</summary>
    public static NewsException OpmlUnreadable() => new(
        HttpStatusCode.BadRequest, NewsErrorCodes.OpmlUnreadable, "This file is not an OPML document.", "file");

    public static NewsException OpmlTooManyEntries(int limit) => new(
        HttpStatusCode.UnprocessableEntity, NewsErrorCodes.OpmlTooManyEntries,
        $"An OPML import may name at most {limit} feeds.", "file",
        new Dictionary<string, object?> { ["limit"] = limit });

    public static NewsException OpmlConflict() => new(
        HttpStatusCode.Conflict, NewsErrorCodes.OpmlConflict,
        "Another change added one of these feeds while the import ran; nothing was imported. Try again.");

    // ── Podcasts (issue #556 §9.1) ────────────────────────────────────────────────────────────────────────────

    public static NewsException AutomationNotPodcast(string field) => new(
        HttpStatusCode.UnprocessableEntity, NewsErrorCodes.FeedAutomationNotPodcast,
        "Automatic downloads and automatic removal are only for podcast feeds.", field);

    public static DomainValidationException FeedKindUnknown() => new(
        "Unknown feed type.", NewsErrorCodes.FeedKindUnknown, "kind");

    public static NewsException PodcastAudioBusy(Guid episodeId) => new(
        HttpStatusCode.Conflict, NewsErrorCodes.PodcastAudioBusy,
        $"The audio of episode {episodeId} is downloading right now; try again when it has finished.");

    public static NewsException PodcastAudioNotStored(Guid episodeId) => new(
        HttpStatusCode.Conflict, NewsErrorCodes.PodcastAudioNotStored,
        $"The audio of episode {episodeId} is not on the server, so the listen cannot be marked finished.");

    public static NewsException PodcastAudioCooldown(Guid episodeId, int retryAfterSeconds) => new(
        HttpStatusCode.TooManyRequests, NewsErrorCodes.PodcastAudioCooldown,
        $"The audio of episode {episodeId} was requested or removed a moment ago; try again later.",
        retryAfterSeconds: retryAfterSeconds);

    public static NewsException PodcastAudioQuotaReached() => new(
        HttpStatusCode.InsufficientStorage, NewsErrorCodes.PodcastAudioQuotaReached,
        "The audio storage limit is reached. Remove audio from the server, or ask an administrator to raise the limit.");

    public static DomainValidationException CursorInvalid() => new(
        "The list position is no longer valid; start again from the first page.", NewsErrorCodes.CursorInvalid, "cursor");

    public static DomainValidationException SearchUnusable() => new(
        $"Use words of at least {NewsLimits.MinSearchTermLength} letters.", NewsErrorCodes.SearchUnusable, "search");
}
