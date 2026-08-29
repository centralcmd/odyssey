using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos;

namespace Odyssey.Api.SystemSettings;

/// <summary>
/// Backs <see cref="IFileAnalysisSettingsLookup"/> (issue #421 Wave 1): a 30s
/// <see cref="IMemoryCache"/> TTL over the six file-analysis rows, evicted synchronously by
/// <see cref="SystemSettingsService"/> the moment any of them actually changes, so the writing
/// instance never serves its own stale read back to the admin who just changed it.
///
/// <para>
/// <strong>Absent and invalid are not the same thing.</strong> A row that is absent while the query
/// itself succeeded resolves to its compiled default as a <em>healthy</em> value — "should not happen
/// post-migration", the same defensive posture <see cref="SystemSettingsService"/> takes on reads, and
/// exactly what <c>ImportExportLimitsLookup</c> documents. Only a failed query, or a row that is
/// present carrying a value this setting cannot use, is degraded. Conflating the two would return
/// <c>503</c> from the consent-gate endpoint on any database whose settings rows have not been seeded
/// — every fresh in-memory and development environment included.
/// </para>
///
/// <para>
/// <strong>One value's safe direction is upward.</strong> The auto-link threshold resolves to the more
/// conservative of last-known-good and default, and for this setting conservative means
/// <strong>higher</strong>, because the matcher applies <c>confidence &gt;= threshold</c>. An admin who
/// tightens to 0.95 and then hits a bad read must not silently get 0.60 auto-linking back. It is the
/// only bound-like setting in the feature whose direction is <c>max</c>; the rest are tabulated in
/// issue #421 §5, and the spec's first draft had this one backwards.
/// </para>
///
/// <para>
/// The full cold-floor apparatus of <c>ImportExportLimitsLookup</c> is not copied: that exists because
/// a failed read there could loosen a real DoS bound across sixteen caps. Here exactly one value needs
/// monotonicity and the rest need completeness. A degraded resolution is not cached, so recovery is
/// immediate on the next good read.
/// </para>
/// </summary>
public sealed class FileAnalysisSettingsLookup(
    OdysseyContext context,
    IMemoryCache cache,
    ILogger<FileAnalysisSettingsLookup> logger) : IFileAnalysisSettingsLookup
{
    internal const string CacheKey = "system-settings:file-analysis-settings";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Cache slot holding the last threshold read cleanly, so a later bad read can resolve to it.
    ///
    /// <para>
    /// In <see cref="IMemoryCache"/> rather than a <c>static</c> field, matching
    /// <c>ImportExportLimitsLookup</c>'s last-known-good slots. Same lifetime in production — the cache
    /// is a singleton — but scoped to the container rather than the process, so it cannot leak between
    /// tests running in parallel. A static field here made this lookup's behaviour depend on which other
    /// test had run first.
    /// </para>
    /// </summary>
    private const string LastKnownGoodThresholdKey = "system-settings:file-analysis-threshold-lkg";

    public async Task<FileAnalysisSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(CacheKey, out FileAnalysisSettings? cached) && cached is not null)
        {
            return cached;
        }

        var keys = new[]
        {
            SystemSettingsKeys.FileAnalysisProcessor,
            SystemSettingsKeys.FileAnalysisProcessorRegion,
            SystemSettingsKeys.FileAnalysisLawfulBasis,
            SystemSettingsKeys.FileAnalysisPrivacyNoticeUrl,
            SystemSettingsKeys.FileAnalysisMaxFutureTransactionDays,
            SystemSettingsKeys.FileAnalysisMatchAutoLinkThreshold,
            SystemSettingsKeys.FileAnalysisMaxTokens,
            SystemSettingsKeys.FileAnalysisMatchMaxVocabulary,
            SystemSettingsKeys.FileAnalysisMatchTimeoutSeconds,
            SystemSettingsKeys.FileAnalysisModel,
            SystemSettingsKeys.FileAnalysisBaseUrl,
        };

        Dictionary<string, string>? values = null;
        try
        {
            values = await context.SystemSettings.AsNoTracking()
                .Where(row => keys.Contains(row.Key))
                .ToDictionaryAsync(row => row.Key, row => row.Value, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A read fault must degrade, never throw: the analyze path and the consent gate both sit
            // downstream of this, and an exception here would 500 a user action over a settings read.
            logger.LogError(ex, "Reading the file-analysis settings failed; degrading to defaults.");
        }

        // Degradation follows the ImportExportLimitsLookup precedent exactly, and the distinction is
        // the whole point: a row that is ABSENT while the query succeeded is "should not happen
        // post-migration" and resolves to the compiled default as a HEALTHY value — the same defensive
        // posture SystemSettingsService takes for reads. Only a failed query, or a row that is present
        // with an unusable value, is degraded.
        //
        // Getting this wrong is not a nuance: treating absent-as-degraded 503s the consent gate on any
        // database whose settings rows have not been seeded, which includes every fresh in-memory and
        // development environment.
        var readFailed = values is null;
        var rows = values ?? [];
        var degraded = readFailed;

        var settings = new FileAnalysisSettings(
            Processor: Text(rows, SystemSettingsKeys.FileAnalysisProcessor,
                SystemSettingsDefaults.FileAnalysisProcessor, ref degraded),
            ProcessorRegion: Text(rows, SystemSettingsKeys.FileAnalysisProcessorRegion,
                SystemSettingsDefaults.FileAnalysisProcessorRegion, ref degraded),
            LawfulBasis: Text(rows, SystemSettingsKeys.FileAnalysisLawfulBasis,
                SystemSettingsDefaults.FileAnalysisLawfulBasis, ref degraded),
            PrivacyNoticeUrl: Url(rows, ref degraded),
            MaxFutureTransactionDays: Integer(rows, SystemSettingsKeys.FileAnalysisMaxFutureTransactionDays,
                SystemSettingsDefaults.FileAnalysisMaxFutureTransactionDays, ref degraded),
            AutoLinkThreshold: Threshold(rows, ref degraded),
            // The three issue #434 tuning values. All three are caps, so a present-but-unusable value
            // resolves to the compiled default, which is the more conservative of the two.
            MaxTokens: Integer(rows, SystemSettingsKeys.FileAnalysisMaxTokens,
                SystemSettingsDefaults.FileAnalysisMaxTokens, ref degraded),
            MatchMaxVocabulary: Integer(rows, SystemSettingsKeys.FileAnalysisMatchMaxVocabulary,
                SystemSettingsDefaults.FileAnalysisMatchMaxVocabulary, ref degraded),
            MatchTimeoutSeconds: Integer(rows, SystemSettingsKeys.FileAnalysisMatchTimeoutSeconds,
                SystemSettingsDefaults.FileAnalysisMatchTimeoutSeconds, ref degraded),
            // The two issue #439 values, and the only two members of this record that resolve to NULL
            // rather than to a default. See Refusable() — a degradation here refuses the analysis, it
            // does not substitute, so the model stamped on a job is always the model that ran and the
            // host a document reached is always the host an administrator set.
            Model: Refusable(rows, SystemSettingsKeys.FileAnalysisModel,
                SystemSettingsDefaults.FileAnalysisModel, ModelUsable, ref degraded),
            BaseUrl: Refusable(rows, SystemSettingsKeys.FileAnalysisBaseUrl,
                SystemSettingsDefaults.FileAnalysisBaseUrl, FileAnalysisBaseUrlRule.Canonicalize, ref degraded),
            IsDegraded: degraded);

        // A degraded resolution is deliberately not cached, matching the precedent: recomputing it is
        // cheap once the read is already failing, and recovery is immediate on the next good read
        // rather than lingering for up to the TTL.
        if (!degraded)
        {
            cache.Set(CacheKey, settings, CacheTtl);
        }

        return settings;
    }

    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        // Deliberately NOT cached and deliberately not part of GetAsync's snapshot (issue #439 §5.1).
        // A disable has to bind on the very next request, on every instance — the snapshot's 30s TTL is
        // a real window and its eviction is instance-local, and this is the switch that stops personal
        // data leaving the deployment for a third party.
        try
        {
            var value = await context.SystemSettings.AsNoTracking()
                .Where(row => row.Key == SystemSettingsKeys.FileAnalysisEnabled)
                .Select(row => row.Value)
                .FirstOrDefaultAsync(cancellationToken);

            // Absent is HEALTHY and resolves to the compiled default, matching every other read here:
            // treating it as degraded would 503 file analysis on any database whose rows have not been
            // seeded, including every fresh in-memory and development environment.
            if (value is null)
            {
                return SystemSettingsDefaults.FileAnalysisEnabled;
            }

            if (bool.TryParse(value, out var parsed))
            {
                return parsed;
            }

            // Present but unparseable: fail closed. The value is logged nowhere near a user response.
            logger.LogError(
                "The stored file-analysis enabled flag is not a boolean; treating analysis as disabled.");
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail closed, and loudly. Every other read in this class degrades to a default because a
            // fallback there is merely imprecise; here a fallback of `true` would let a failed settings
            // read be the reason a document was transferred to a third party.
            logger.LogError(ex, "Reading the file-analysis enabled flag failed; treating analysis as disabled.");
            return false;
        }
    }

    /// <summary>
    /// Absent → the compiled default, healthy. Present but blank → degraded: the row exists, so
    /// somebody stored a value this setting cannot use.
    /// </summary>
    private static string Text(
        IReadOnlyDictionary<string, string> rows, string key, string fallback, ref bool degraded)
    {
        if (!rows.TryGetValue(key, out var value))
        {
            return fallback;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            degraded = true;
            return fallback;
        }

        return value;
    }

    /// <summary>
    /// The privacy URL is validated here as well as on write, so a value planted by a restore or a hand
    /// edit never reaches the <c>href</c> the consent panel renders. A present-but-invalid one is
    /// degraded, so the endpoint 503s rather than passing the compiled default off as configured.
    /// </summary>
    private string Url(IReadOnlyDictionary<string, string> rows, ref bool degraded)
    {
        var fallback = SystemSettingsDefaults.FileAnalysisPrivacyNoticeUrl;

        if (!rows.TryGetValue(SystemSettingsKeys.FileAnalysisPrivacyNoticeUrl, out var value))
        {
            return fallback;
        }

        var projected = PrivacyNoticeUrl.Project(value, fallback);
        if (!string.Equals(projected, value, StringComparison.Ordinal)
            && PrivacyNoticeUrl.Validate(value) is not null)
        {
            logger.LogError(
                "The stored file-analysis privacy notice URL is not a usable https URL; serving the default.");
            degraded = true;
        }

        return projected;
    }

    /// <summary>
    /// A value the analysis <strong>refuses</strong> on rather than substituting a default for (issue
    /// #439 §11): absent resolves to the compiled default and stays healthy, but a row that is present
    /// carrying something <paramref name="usable"/> rejects resolves to <see langword="null"/> and
    /// degrades.
    ///
    /// <para>
    /// Null rather than the default is the whole mechanism. <c>FileAnalysisTarget</c> cannot be
    /// constructed from a null, so the refusal is structural — <c>FileAnalysisService</c> never tests
    /// <c>IsDegraded</c>, which could not tell it <em>which</em> field degraded and would either block
    /// all analysis on an unrelated bad row or need an invented heuristic. The other seven fields keep
    /// their resolve-to-default-and-flag behaviour, so a degradation in any of them leaves analysis
    /// working while still 503-ing the disclosure endpoint.
    /// </para>
    ///
    /// <para>
    /// The stored value never reaches the log line. A base-URL row planted by a restore can carry
    /// <c>userinfo</c>, and this is one of the paths that exists to catch exactly that.
    /// </para>
    /// </summary>
    private string? Refusable(
        IReadOnlyDictionary<string, string> rows, string key, string fallback, Func<string, string?> usable,
        ref bool degraded)
    {
        if (!rows.TryGetValue(key, out var value))
        {
            // Absent is healthy: it resolves to the compiled default, which is also what the seed
            // writes. There is nothing to refuse — nobody stored an unusable value.
            return fallback;
        }

        if (usable(value) is { } resolved)
        {
            return resolved;
        }

        logger.LogError(
            "The stored file-analysis setting '{Key}' holds a value this setting cannot use; refusing analysis "
            + "rather than substituting the shipped default.", key);
        degraded = true;
        return null;
    }

    /// <summary>
    /// A model name is usable when it is non-blank and within the bound the write DTO enforces. There is
    /// no ground truth to validate it against — naming a model the provider does not know is an admin
    /// error the provider reports, not something this can catch.
    /// </summary>
    private static string? ModelUsable(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length is > 0 and <= 128 ? trimmed : null;
    }

    private static int Integer(
        IReadOnlyDictionary<string, string> rows, string key, int fallback, ref bool degraded)
    {
        if (!rows.TryGetValue(key, out var value))
        {
            return fallback;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            degraded = true;
            return fallback;
        }

        return parsed;
    }

    /// <summary>
    /// The one setting here whose degraded direction is upward. A clean read tracks the last-known-good
    /// value; a bad one resolves to the more conservative of that and the compiled default, where
    /// conservative means HIGHER, because the matcher applies <c>confidence &gt;= threshold</c>.
    /// </summary>
    private decimal Threshold(IReadOnlyDictionary<string, string> rows, ref bool degraded)
    {
        var key = SystemSettingsKeys.FileAnalysisMatchAutoLinkThreshold;

        if (!rows.TryGetValue(key, out var value))
        {
            return SystemSettingsDefaults.FileAnalysisMatchAutoLinkThreshold;
        }

        if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && parsed is >= 0m and <= 1m)
        {
            // An admin LOWERING the threshold is a legitimate write, so the watermark tracks the
            // current clean value rather than ratcheting up forever — the guarantee is "a fault never
            // loosens", not "a threshold never decreases".
            cache.Set(LastKnownGoodThresholdKey, parsed, new MemoryCacheEntryOptions());
            return parsed;
        }

        logger.LogError(
            "The stored auto-link threshold '{Value}' is not a usable confidence; falling back conservatively.", value);

        degraded = true;

        var lastKnownGood = cache.TryGetValue(LastKnownGoodThresholdKey, out decimal lkg)
            ? lkg
            : SystemSettingsDefaults.FileAnalysisMatchAutoLinkThreshold;

        // max, not min: the matcher applies confidence >= threshold, so HIGHER auto-links less. This is
        // the one bound-like setting in the feature whose safe direction is upward.
        return Math.Max(lastKnownGood, SystemSettingsDefaults.FileAnalysisMatchAutoLinkThreshold);
    }
}
