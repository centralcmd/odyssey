using Odyssey.Core;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Every exception type <c>Odyssey.Api</c> and <c>Odyssey.Core</c> declare derives from
/// <see cref="DomainException"/>, so <c>GlobalExceptionHandler</c> maps it for every caller (issue #287
/// M11). A parallel <c>: Exception</c> type needs a controller try/catch to become anything but a
/// <c>500</c>, and a caller that does not know about that catch gets the <c>500</c>.
/// </summary>
public class DomainExceptionHierarchyGuardTests
{
    /// <summary>
    /// Deliberately outside the hierarchy: none of these is a client-correctable condition, so the
    /// <c>500</c> the global handler gives a non-domain exception is the intended response.
    /// </summary>
    private static readonly HashSet<string> AllowedNonDomain =
    [
        // An export that cannot be produced as a complete snapshot; logged and surfaced as a generic 500.
        "Odyssey.Core.FileExport.FileExportException",
        // A third-party provider failure, recorded on the analysis job rather than shaped for a client.
        "Odyssey.Core.Finance.FileAnalysisProviderException",
        "Odyssey.Core.Finance.FileAnalysisCredentialException",
        // The shared Claude transport's refusals (issue #602 §3.1): a provider failure and a missing key. The statement
        // flow wraps the first in FileAnalysisProviderException; the recipe-import worker records both on the import.
        "Odyssey.Core.Ai.ClaudeTransportException",
        "Odyssey.Core.Ai.AiCredentialUnavailableException",
        // Internal to the geocoding clients and never escapes them: each client turns it into a
        // GeocodingUnavailableException (a 503) or a partial result (issue #350).
        "Odyssey.Core.Journal.Geocoding.GeocodingProviderException",
        // A Norges Bank request failure (issue #349): recorded on the sync run as its ErrorKind, never
        // shaped for a client. The one path that surfaces it — enabling a currency while the provider
        // list is down — raises ExchangeRateSupportedListUnavailableException (a 503) instead.
        "Odyssey.Core.Finance.ExchangeRateSync.Providers.ExchangeRateProviderException",
        // A failed file-storage startup check (issue #346 §3.6): it stops the host before it listens, so it
        // never reaches a request and has no status to map to.
        "Odyssey.Core.FileStorage.FileStorageMisconfiguredException",
        // The instrument-price source transport's refusals (issue #446 §3.2). They run only inside the worker's
        // sync, which records each on the binding and the run as a failure category; no request reaches them.
        "Odyssey.Core.Finance.Instruments.Sources.SourceCredentialException",
        "Odyssey.Core.Finance.Instruments.Sources.SourceDestinationException",
        "Odyssey.Core.Finance.Instruments.Sources.SourceQuotaExhaustedException",
        "Odyssey.Core.Finance.Instruments.Sources.SourceResponseMalformedException",
        // The news egress policy's refusal (issue #470 §7.9). Thrown inside the worker's connect callback and
        // recorded on the feed as BlockedAddress; the API makes no outbound request, so no request reaches it.
        "Odyssey.Core.Internet.News.Fetching.NewsEgressRefusedException",
        // The news job's own control flow (issue #470 §3.5, §6.3): the headroom refusal and an interrupted transaction
        // that is not replayed. Both are caught by the worker's ingestor as a transient skip; the API writes no news
        // file and runs no ingest, so no request reaches either. Since issue #508 an API news write that is interrupted
        // is mapped to 503 news-busy by NewsQueryGuard, so it never reaches the global handler either.
        "Odyssey.Core.Internet.News.Files.NewsHeadroomException",
        "Odyssey.Core.Internet.News.NewsTransactionRetryException",
        // The podcast audio download's own control flow (issue #556 §3.5, §3.6): a streamed body stopped by the stall,
        // floor or deadline rule, a download stopped by its lease renewal, and a refused byte reservation. All three are
        // caught inside PodcastAudioDownloadJob, which only the worker runs.
        "Odyssey.Core.Internet.News.Fetching.NewsStreamAbortedException",
        "Odyssey.Core.Internet.News.Podcasts.PodcastAudioDownloadJob+DownloadStoppedException",
        "Odyssey.Core.Internet.News.Podcasts.PodcastAudioDownloadJob+RefusedException",
        "Odyssey.Core.Internet.News.Podcasts.PodcastAudioDownloadJob+SourceReadException",
        // The OPML reader's internal signals (issue #508 §7.5, §9 item 3): the import service turns both into
        // 400 news-opml-unreadable, so neither escapes it.
        "Odyssey.Core.Internet.News.Opml.OpmlTokenLimitException",
        "Odyssey.Core.Internet.News.Opml.OpmlUnreadableException",
        // A task, journal or calendar export that grew past its byte cap after the response had started: no status can be sent
        // any more, so the controller catches it and aborts the connection — the handler never sees it.
        "Odyssey.Core.Journal.Interop.IcsExportSizeExceededException",
    ];

    [Fact]
    public void Every_declared_exception_type_is_a_DomainException_or_explicitly_allowed()
    {
        var offenders = new[] { typeof(Program).Assembly, typeof(DomainException).Assembly }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => typeof(Exception).IsAssignableFrom(type))
            .Where(type => !typeof(DomainException).IsAssignableFrom(type))
            .Select(type => type.FullName!)
            .Where(name => !AllowedNonDomain.Contains(name))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Derive these from DomainException (or allow-list them with a reason): " + string.Join(", ", offenders));
    }

    [Fact]
    public void Every_allow_listed_type_still_exists_outside_the_hierarchy()
    {
        var declared = new[] { typeof(Program).Assembly, typeof(DomainException).Assembly }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => typeof(Exception).IsAssignableFrom(type) && !typeof(DomainException).IsAssignableFrom(type))
            .Select(type => type.FullName!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(AllowedNonDomain, name => Assert.Contains(name, declared));
    }
}
