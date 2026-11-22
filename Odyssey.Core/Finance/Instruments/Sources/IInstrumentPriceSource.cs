using Odyssey.Dtos.Application;
using Odyssey.Dtos.Finance;

namespace Odyssey.Core.Finance.Instruments.Sources;

/// <summary>Where a source puts its credential (issue #446 §3.1); the framework's credential handler attaches it.</summary>
public abstract record CredentialPlacement
{
    private CredentialPlacement()
    {
    }

    public sealed record None : CredentialPlacement;

    /// <summary>
    /// The credential travels in header <paramref name="Name"/> as <paramref name="ValuePrefix"/> + the stored secret
    /// (issue #447 A4). The prefix is a compiled constant — an authentication scheme such as <c>"Token "</c> — so the
    /// administrator pastes the bare secret and the scheme is never stored inside it.
    /// </summary>
    public sealed record Header(string Name, string? ValuePrefix = null) : CredentialPlacement;

    public sealed record Query(string Name) : CredentialPlacement;
}

/// <summary>
/// A source's own limits, enforced by the job (issue #446 §3.1). <see cref="MaxRequestsPerDay"/> counts per
/// Europe/Oslo day; a source whose provider resets its daily limit elsewhere relies on
/// <see cref="MaxRequestsPerHour"/> to bound any provider day (issue #447 A5). <see cref="MinRequestInterval"/> is
/// enforced per source across a whole run, retries included, in each process (A7). The hourly cap counts per UTC
/// clock hour, across replicas (A1); when set it may not be below <see cref="MaxRequestsPerRun"/>.
/// </summary>
public sealed record InstrumentPriceSourceLimits(
    int MaxDaysPerRequest,
    TimeSpan MinRequestInterval,
    int MaxRequestsPerRun,
    int MaxRequestsPerDay,
    int? MaxRequestsPerHour = null);

/// <summary>What the job asks a source for: one binding's symbol and a date window.</summary>
public sealed record SourceFetchRequest(string SourceSymbol, DateOnly From, DateOnly To, string ExpectedCurrencyCode);

/// <summary>
/// One close as the provider stated it. <see cref="CurrencyCode"/> is the provider's: either stated on the row, or
/// derived from a provider-stated fact such as the listing venue through a <strong>compiled</strong> map with no
/// default (issue #447 A2). It is never derived from the instrument — a source never reads
/// <see cref="SourceFetchRequest.ExpectedCurrencyCode"/> — so <see cref="FetchedBatchCheck"/> still catches a symbol
/// bound to an instrument in another currency.
/// </summary>
public sealed record FetchedClose(DateOnly TradingDate, decimal Close, string? CurrencyCode);

/// <summary>The outcome of one fetch: a (possibly empty) batch, or a failure in the run log's vocabulary.</summary>
public sealed record SourceFetchResult
{
    public IReadOnlyList<FetchedClose>? Closes { get; private init; }

    public JobFailureCategory? Failure { get; private init; }

    public TimeSpan? RetryAfter { get; private init; }

    public bool Succeeded => Failure is null;

    public static SourceFetchResult Success(IReadOnlyList<FetchedClose> closes) => new() { Closes = closes };

    public static SourceFetchResult Failed(JobFailureCategory category, TimeSpan? retryAfter = null) =>
        new() { Failure = category, RetryAfter = retryAfter };
}

/// <summary>
/// A code-declared market-data source (issue #446 §3.1; Tiingo, issue #447, is the first production one). Each source is registered with <c>AddInstrumentPriceSource&lt;T&gt;()</c>, and every request it makes goes
/// through the framework's <see cref="ISourceHttp"/>. A source never throws for a provider failure and never returns
/// provider text; only <see cref="FetchClosesAsync"/> makes a request, and only the worker calls it.
/// </summary>
public interface IInstrumentPriceSource
{
    /// <summary>Stable and persisted; matches <see cref="InstrumentIdentifierRules.SourceKeyPattern"/>. Never renamed.</summary>
    string Key { get; }

    string DisplayName { get; }

    /// <summary>Plain text shown wherever this source's prices are shown.</summary>
    string Attribution { get; }

    /// <summary>The single https host this source may call — a compiled constant.</summary>
    string Host { get; }

    CredentialPlacement CredentialPlacement { get; }

    IReadOnlySet<InstrumentType> SupportedTypes { get; }

    InstrumentPriceSourceLimits Limits { get; }

    /// <summary>The secret keys the source needs; may be empty.</summary>
    IReadOnlyList<string> SecretKeys { get; }

    /// <summary>A symbol as this provider writes it, for the source-symbol help line; plain text, a compiled constant.</summary>
    string? SymbolExample => null;

    /// <summary>
    /// The provider's own data-processing note (issue #447), a compiled plain-text constant the enable disclosure
    /// shows beside what is sent. <c>null</c> when the source declares none.
    /// </summary>
    string? DataProcessingNote => null;

    /// <summary>
    /// Fetches closes for one window through <paramref name="http"/> — the framework-built transport, which pins the
    /// host, attaches the credential, reserves the daily quota and bounds the response. A source never builds its
    /// own <see cref="System.Net.Http.HttpClient"/>.
    /// </summary>
    /// <remarks>
    /// A per-symbol refusal — an unknown symbol, a plan that does not cover it, a provider's unique-symbol quota — is
    /// <see cref="JobFailureCategory.UnsupportedInput"/> for that binding (issue #447 A3); it is never
    /// <see cref="JobFailureCategory.Configuration"/> (source unusable) nor <see cref="JobFailureCategory.ProviderRateLimited"/>
    /// (source backed off). The framework's own exceptions (<see cref="SourceQuotaExhaustedException"/>,
    /// <see cref="SourceCredentialException"/>, <see cref="SourceDestinationException"/>,
    /// <see cref="SourceResponseMalformedException"/>, cancellation) pass through unchanged.
    /// </remarks>
    Task<SourceFetchResult> FetchClosesAsync(SourceFetchRequest request, ISourceHttp http, CancellationToken cancellationToken);

    /// <summary>
    /// Told when the job's structural check rejected a batch this source returned (the <paramref name="rule"/> of
    /// <see cref="FetchedBatchCheck"/>), so a source that caches what it derived — a venue, a currency — can drop it.
    /// </summary>
    void OnBatchRejected(SourceFetchRequest request, int rule)
    {
    }
}

/// <summary>
/// A source's key and host as compile-time statics, so <c>AddInstrumentPriceSource&lt;T&gt;()</c> can name and
/// address its client at registration. They must equal the instance's <see cref="IInstrumentPriceSource.Key"/> and
/// <see cref="IInstrumentPriceSource.Host"/>; the contract suite asserts it.
/// </summary>
public interface IInstrumentPriceSourceDeclaration
{
    static abstract string DeclaredKey { get; }

    static abstract string DeclaredHost { get; }
}

/// <summary>
/// The registered sources (issue #446 §3, the <c>StorageFeatureCatalogue</c> shape). Holds Tiingo in production
/// (issue #447). Registered in both hosts: the API reads the metadata, the worker fetches.
/// </summary>
public sealed class InstrumentPriceSourceCatalogue
{
    private readonly Dictionary<string, IInstrumentPriceSource> byKey;

    public InstrumentPriceSourceCatalogue(IEnumerable<IInstrumentPriceSource> sources)
    {
        byKey = new Dictionary<string, IInstrumentPriceSource>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(source.Key, InstrumentIdentifierRules.SourceKeyPattern))
            {
                throw new InvalidOperationException($"Price source key '{source.Key}' does not match {InstrumentIdentifierRules.SourceKeyPattern}.");
            }

            if (source.Limits.MaxRequestsPerDay < source.Limits.MaxRequestsPerRun)
            {
                throw new InvalidOperationException(
                    $"Price source '{source.Key}' declares MaxRequestsPerDay below MaxRequestsPerRun.");
            }

            if (source.Limits.MaxRequestsPerHour is { } perHour
                && (perHour < source.Limits.MaxRequestsPerRun || perHour > source.Limits.MaxRequestsPerDay))
            {
                throw new InvalidOperationException(
                    $"Price source '{source.Key}' declares MaxRequestsPerHour outside MaxRequestsPerRun..MaxRequestsPerDay.");
            }

            if (!byKey.TryAdd(source.Key, source))
            {
                throw new InvalidOperationException($"Price source key '{source.Key}' is registered twice.");
            }
        }
    }

    public IReadOnlyCollection<IInstrumentPriceSource> All => byKey.Values;

    public bool IsEmpty => byKey.Count == 0;

    public IInstrumentPriceSource? Find(string? key) =>
        key is not null && byKey.TryGetValue(key, out var source) ? source : null;

    /// <summary>The name shown for a stored row's key: the display name, or the bare key once unregistered.</summary>
    public string DisplayNameOf(string key) => Find(key)?.DisplayName ?? key;
}
