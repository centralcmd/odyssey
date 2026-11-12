using Odyssey.Core;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Mapster;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Linq.Expressions;
using Odyssey.Core.Finance.ExchangeRateSync;
using Odyssey.Dtos.Authorization;

namespace Odyssey.Core.Finance;

/// <summary>
/// Manages the exchange-rate history. A record's currency pair is its identity and is immutable
/// once created; <see cref="Update"/> only corrects the Rate/AsOf of an existing row in place.
/// <see cref="Create"/> remains available for recording a genuinely new entry, and the newest
/// <see cref="ExchangeRate.AsOf"/> for a pair wins in conversions either way.
/// </summary>
public class ExchangeRateService(OdysseyContext context, TimeProvider? injectedTimeProvider = null)
{
    private readonly TimeProvider timeProvider = injectedTimeProvider ?? TimeProvider.System;

    /// <summary>
    /// Server-side paged list (issue #277): search over currency codes, target-currency, status and source
    /// filters, and allowlisted sort. A row's status comes from <see cref="ExchangeRateOrdering"/>:
    /// Upcoming while its <c>AsOf</c> is in the future, Current when it is the newest row of its pair
    /// already in force, Historical otherwise (issue #349 §3).
    /// </summary>
    public async Task<PagedResult<ExistingExchangeRate>> ListAsync(
        ExchangeRatesQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var now = Now;
        var all = context.ExchangeRates.AsNoTracking();
        var q = all;

        var term = ListQuery.NormalizeSearch(query.Search);
        if (term is not null)
        {
            var pattern = ListQuery.ContainsPattern(term);
            q = q.Where(r =>
                EF.Functions.Like(r.FromCurrencyCode, pattern) ||
                EF.Functions.Like(r.ToCurrencyCode, pattern));
        }

        var targets = query.ToCurrencies?
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim().ToUpperInvariant())
            .ToList();
        if (targets is { Count: > 0 })
        {
            q = q.Where(r => targets.Contains(r.ToCurrencyCode));
        }

        // Duplicates are ignored and an out-of-range numeric value is dropped, so `source=99` matches
        // nothing rather than everything.
        if (query.Source is { Length: > 0 } sources)
        {
            var manual = sources.Contains(ExchangeRateSource.Manual);
            var fetched = sources.Contains(ExchangeRateSource.Fetched);
            q = q.Where(r => (manual && r.Provider == null) || (fetched && r.Provider != null));
        }

        var rank = ExchangeRateOrdering.StatusRank(all, now);
        if (query.Status is { Length: > 0 } statuses)
        {
            var ranks = statuses.Where(Enum.IsDefined).Select(status => (int)status).Distinct().ToList();
            q = WhereRankIn(q, rank, ranks);
        }

        var ascending = ListQuery.Ascending(query.SortDir, naturalDefaultAscending: query.SortBy is ExchangeRateSortBy.Pair or ExchangeRateSortBy.Status);
        var sorted = query.SortBy switch
        {
            ExchangeRateSortBy.Pair => ascending
                ? q.OrderBy(r => r.FromCurrencyCode).ThenBy(r => r.ToCurrencyCode)
                : q.OrderByDescending(r => r.FromCurrencyCode).ThenByDescending(r => r.ToCurrencyCode),
            ExchangeRateSortBy.Rate => ascending ? q.OrderBy(r => r.Rate) : q.OrderByDescending(r => r.Rate),
            // Inverse is 1 / Rate, and Rate is > 0 on every write path, so the reciprocal is strictly
            // decreasing in Rate — order by Rate reversed rather than dividing in SQL, which keeps the
            // sort exact (no decimal rounding) and lets it use the same index Rate does.
            ExchangeRateSortBy.Inverse => ascending ? q.OrderByDescending(r => r.Rate) : q.OrderBy(r => r.Rate),
            ExchangeRateSortBy.CreatedAt => ascending ? q.OrderBy(r => r.CreatedAt) : q.OrderByDescending(r => r.CreatedAt),
            // Current (0) sorts before Historical (1) before Upcoming (2), the ExchangeRateStatus ordinals.
            ExchangeRateSortBy.Status => ascending ? q.OrderBy(rank) : q.OrderByDescending(rank),
            _ => ascending
                ? q.OrderBy(r => r.AsOf).ThenBy(r => r.UpdatedAt ?? r.CreatedAt)
                : q.NewestFirst(),
        };
        q = sorted.ThenBy(r => r.ExchangeRateId);

        var page = await q.ToPagedResultAsync(query.Offset, query.Limit, r => r, cancellationToken);
        var current = await CurrentIdsAsync(page.Items, now, cancellationToken);

        return new PagedResult<ExistingExchangeRate>
        {
            Items = page.Items.Select(r => ToDto(r, now, current)).ToList(),
            Offset = page.Offset,
            Limit = page.Limit,
            TotalCount = page.TotalCount,
        };
    }

    /// <summary>
    /// The unfiltered aggregates for <c>GET /api/exchange-rates/summary</c> (issue #349 §5.10). Current is the
    /// number of stored pairs whose winner is one of their OWN rows (issue #472 §5.4): a pair beaten by an
    /// inverted fetched row of the swapped pair has no Current row. It is counted from the per-pair winners —
    /// the same lookup the row badges use — so the summary and the badges cannot disagree, its cost grows with
    /// the pairs rather than the rows, and the three status buckets still sum to the total.
    /// </summary>
    public async Task<ExchangeRateSummary> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        var now = Now;
        var rates = context.ExchangeRates.AsNoTracking();

        var total = await rates.CountAsync(cancellationToken);
        var fetched = await rates.CountAsync(r => r.Provider != null, cancellationToken);
        var upcoming = await rates.CountAsync(r => r.AsOf > now, cancellationToken);
        var currentPairs = (await CurrentWinnersAsync(rates, rates, now, cancellationToken)).Count;
        var pairs = await rates
            .Select(r => new { r.FromCurrencyCode, r.ToCurrencyCode })
            .Distinct()
            .ToListAsync(cancellationToken);
        var latest = await rates
            .Where(r => r.AsOf <= now)
            .MaxAsync(r => (DateTime?)r.AsOf, cancellationToken);

        return new ExchangeRateSummary
        {
            TotalCount = total,
            ByStatus = new ExchangeRateStatusCounts
            {
                Current = currentPairs,
                Upcoming = upcoming,
                Historical = total - currentPairs - upcoming,
            },
            BySource = new ExchangeRateSourceCounts { Manual = total - fetched, Fetched = fetched },
            PairCount = pairs.Count,
            FromCurrencyCodes = pairs.Select(p => p.FromCurrencyCode).Distinct().Order(StringComparer.Ordinal).ToList(),
            ToCurrencyCodes = pairs.Select(p => p.ToCurrencyCode).Distinct().Order(StringComparer.Ordinal).ToList(),
            Attribution = fetched > 0 ? ExchangeRateSyncConstants.Attribution : null,
            LatestAsOfUtc = latest,
            FetchedRateLocalTime = ExchangeRateSyncConstants.FetchedRateLocalTime.ToString("HH:mm", CultureInfo.InvariantCulture),
            FetchedRateTimeZone = ExchangeRateSyncConstants.FetchedRateTimeZone,
            FetchedRateTimeZoneLabel = ExchangeRateSyncConstants.FetchedRateTimeZoneLabel,
            ProviderDisplayName = ExchangeRateSyncConstants.ProviderDisplayName,
        };
    }

    public async Task<ExistingExchangeRate?> Get(Guid exchangeRateId, CancellationToken cancellationToken = default)
    {
        var rate = await context.ExchangeRates.AsNoTracking()
            .FirstOrDefaultAsync(value => value.ExchangeRateId == exchangeRateId, cancellationToken);
        return rate is null ? null : await ToDtoAsync(rate, cancellationToken);
    }

    /// <summary>
    /// Returns the row in force for <c>A → B</c> — the newest with <c>AsOf ≤ now</c>, so a future-dated row is
    /// ignored until its date arrives — or null. The candidates are the stored <c>A → B</c> rows and the FETCHED
    /// <c>B → A</c> rows, inverted (issue #472 §5.2), under the shared ordering, so a stored row wins a full tie.
    /// An inverted winner comes back with the codes swapped, <c>1 / Rate</c> unrounded, the source row's id and
    /// dates, and <see cref="ExistingExchangeRate.Inverted"/> set. No NOK hop: this returns a row, and a hop is
    /// not one; <see cref="ResolveAsync"/> covers that. Null too when either currency does not exist (an
    /// archived one does) or the two are the same.
    /// </summary>
    public async Task<ExistingExchangeRate?> GetLatest(string fromCurrencyCode, string toCurrencyCode, CancellationToken cancellationToken = default)
    {
        var from = CurrencyValidationService.Normalize(fromCurrencyCode);
        var to = CurrencyValidationService.Normalize(toCurrencyCode);
        var now = Now;

        if (!await CurrenciesExistAsync(from, to, cancellationToken))
        {
            return null;
        }

        var rates = context.ExchangeRates.AsNoTracking();
        var stored = await rates
            .Where(value => value.FromCurrencyCode == from && value.ToCurrencyCode == to && value.AsOf <= now)
            .NewestFirst()
            .FirstOrDefaultAsync(cancellationToken);
        var reversed = await rates
            .Where(value => value.FromCurrencyCode == to && value.ToCurrencyCode == from
                && value.Provider != null && value.AsOf <= now)
            .NewestFirst()
            .FirstOrDefaultAsync(cancellationToken);

        var candidates = new List<LatestCandidate>();
        if (stored is not null)
        {
            candidates.Add(new LatestCandidate(stored, Inverted: false));
        }

        if (reversed is not null)
        {
            candidates.Add(new LatestCandidate(reversed, Inverted: true));
        }

        return ExchangeRateOrdering.Newest(candidates) switch
        {
            null => null,
            { Inverted: false } winner => ToDto(winner.Rate, now, new HashSet<Guid> { winner.ExchangeRateId }),
            // A non-positive rate is never divided by: only a hand edit around the CHECK can leave one.
            { Rate.Rate: <= 0 } => null,
            var winner => ToInvertedDto(winner.Rate),
        };
    }

    /// <summary>
    /// The rate a conversion of <c>from</c> into <c>to</c> would use right now, with its method and legs
    /// (issue #472 §5.3). Null when either currency does not exist (an archived one does) or nothing resolves.
    /// </summary>
    public async Task<ResolvedExchangeRate?> ResolveAsync(
        string fromCurrencyCode, string toCurrencyCode, CancellationToken cancellationToken = default)
    {
        var from = CurrencyValidationService.Normalize(fromCurrencyCode);
        var to = CurrencyValidationService.Normalize(toCurrencyCode);

        if (!await CurrenciesExistAsync(from, to, cancellationToken))
        {
            return null;
        }

        var resolution = await new CurrencyConversionService(context, timeProvider)
            .ResolveAsync(from, to, cancellationToken);

        return resolution is null
            ? null
            : new ResolvedExchangeRate
            {
                FromCurrencyCode = from,
                ToCurrencyCode = to,
                Rate = resolution.Rate,
                AsOf = resolution.AsOf,
                Method = resolution.Method,
                Legs = resolution.Legs.Select(leg => new ResolvedExchangeRateLeg
                {
                    ExchangeRateId = leg.ExchangeRateId,
                    FromCurrencyCode = leg.FromCurrencyCode,
                    ToCurrencyCode = leg.ToCurrencyCode,
                    Rate = leg.Rate,
                    AsOf = leg.AsOf,
                    Source = leg.Fetched ? ExchangeRateSource.Fetched : ExchangeRateSource.Manual,
                    Inverted = leg.Inverted,
                }).ToList(),
            };
    }

    /// <summary>
    /// Whether both codes name a <c>Currencies</c> row, archived ones included — their rates stay readable and
    /// conversions still use them. Deliberately not <see cref="CurrencyValidationService.EnsureSupportedAndActive"/>,
    /// which refuses archived currencies with a <c>400</c>.
    /// </summary>
    private async Task<bool> CurrenciesExistAsync(string from, string to, CancellationToken cancellationToken)
    {
        var codes = new[] { from, to }.Distinct(StringComparer.Ordinal).ToList();
        var found = await context.Currencies.AsNoTracking()
            .CountAsync(currency => codes.Contains(currency.CurrencyCode), cancellationToken);
        return found == codes.Count;
    }

    public async Task<ExistingExchangeRate> Create(NewExchangeRate newExchangeRate, CancellationToken cancellationToken = default)
    {
        var from = CurrencyValidationService.Normalize(newExchangeRate.FromCurrencyCode);
        var to = CurrencyValidationService.Normalize(newExchangeRate.ToCurrencyCode);

        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            throw new DomainValidationException("From and To currencies must differ; a same-currency rate is implicitly 1.");
        }

        if (newExchangeRate.Rate <= 0)
        {
            throw new DomainValidationException("Rate must be greater than zero.");
        }

        // Both ends must reference an existing, non-archived currency.
        await CurrencyValidationService.EnsureSupportedAndActive(context, from, nameof(newExchangeRate.FromCurrencyCode), cancellationToken);
        await CurrencyValidationService.EnsureSupportedAndActive(context, to, nameof(newExchangeRate.ToCurrencyCode), cancellationToken);

        var exchangeRate = new ExchangeRate
        {
            FromCurrencyCode = from,
            ToCurrencyCode = to,
            Rate = newExchangeRate.Rate,
            AsOf = newExchangeRate.AsOf is { } asOf
                ? DateTimeNormalization.NormalizeToUtc(asOf)
                : timeProvider.GetUtcNow().UtcDateTime,
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
        };

        context.ExchangeRates.Add(exchangeRate);
        await context.SaveChangesAsync(cancellationToken);

        return await ToDtoAsync(exchangeRate, cancellationToken);
    }

    /// <summary>
    /// Corrects the Rate and AsOf of an existing rate record. The currency pair can't change —
    /// the update DTO doesn't carry From/To — so this never touches a different pair's identity.
    /// Returns null if the id doesn't exist. A fetched row is never corrected in place — a correction is
    /// a manual row with a later <c>AsOf</c> — so it is a <c>422</c> keyed <c>source</c> for everyone.
    /// </summary>
    public async Task<ExistingExchangeRate?> Update(
        Guid exchangeRateId, UpdateExchangeRate update, CancellationToken cancellationToken = default)
    {
        var rate = await context.ExchangeRates.FirstOrDefaultAsync(value => value.ExchangeRateId == exchangeRateId, cancellationToken);
        if (rate is null)
        {
            return null;
        }

        if (rate.Provider is not null)
        {
            throw new DomainUnprocessableException(
                $"Exchange rate {exchangeRateId} was fetched and cannot be corrected in place. Enter a manual rate with a later date instead.",
                "source");
        }

        if (update.Rate <= 0)
        {
            throw new DomainValidationException("Rate must be greater than zero.");
        }

        rate.Rate = update.Rate;
        rate.AsOf = DateTimeNormalization.NormalizeToUtc(update.AsOf);
        rate.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await context.SaveChangesAsync(cancellationToken);

        return await ToDtoAsync(rate, cancellationToken);
    }

    /// <summary>
    /// Deletes one MANUAL rate; a missing id is an idempotent no-op. A fetched row is refused, whoever asks:
    /// the sync fills only past its fetched extents, so a row removed from inside them is never fetched again
    /// and a conversion would silently fall back to an older leg. Fetched rates leave as a whole, through the
    /// per-currency purge.
    /// </summary>
    public async Task Delete(Guid exchangeRateId, CancellationToken cancellationToken = default)
    {
        var rate = await context.ExchangeRates.FirstOrDefaultAsync(value => value.ExchangeRateId == exchangeRateId, cancellationToken);
        if (rate is null)
        {
            return;
        }

        if (rate.Provider is not null)
        {
            throw new DomainUnprocessableException(
                $"Exchange rate {exchangeRateId} was fetched and cannot be deleted on its own. Turn the currency's sync off and delete its fetched rates instead.",
                "source");
        }

        context.ExchangeRates.Remove(rate);
        await context.SaveChangesAsync(cancellationToken);
    }

    private DateTime Now => timeProvider.GetUtcNow().UtcDateTime;

    private static IQueryable<ExchangeRate> WhereRankIn(
        IQueryable<ExchangeRate> query, Expression<Func<ExchangeRate, int>> rank, List<int> ranks)
    {
        // ranks.Contains(rank(r)), composed by hand so the status rule stays written once.
        var parameter = rank.Parameters[0];
        var contains = Expression.Call(
            Expression.Constant(ranks),
            typeof(List<int>).GetMethod(nameof(List<int>.Contains))!,
            rank.Body);
        return query.Where(Expression.Lambda<Func<ExchangeRate, bool>>(contains, parameter));
    }

    /// <summary>The ids of the rows in force for the pairs on a page — at most one per pair, by the shared ordering.</summary>
    private async Task<HashSet<Guid>> CurrentIdsAsync(
        IReadOnlyCollection<ExchangeRate> rows, DateTime now, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var froms = rows.Select(r => r.FromCurrencyCode).Distinct().ToList();
        var tos = rows.Select(r => r.ToCurrencyCode).Distinct().ToList();
        var rates = context.ExchangeRates.AsNoTracking();

        var winners = await CurrentWinnersAsync(
            rates.Where(r => froms.Contains(r.FromCurrencyCode) && tos.Contains(r.ToCurrencyCode)),
            rates.Where(r => tos.Contains(r.FromCurrencyCode) && froms.Contains(r.ToCurrencyCode)),
            now,
            cancellationToken);

        return winners.Select(winner => winner.ExchangeRateId).ToHashSet();
    }

    /// <summary>
    /// The Current row of every stored pair in <paramref name="storedScope"/> (issue #472 §8.12). Each pair's
    /// candidates are its own in-force rows and the in-force FETCHED rows of the swapped pair (from
    /// <paramref name="reversedScope"/>), inverted, each branch resolved by its own grouped <c>MAX(AsOf)</c> with
    /// its filters applied BEFORE the grouping. A pair whose winner is an inverted row has NO Current row: that
    /// row's id names a row of the other stored pair, and letting it in would mark that row Current inside its
    /// own pair. This is the in-memory form of <see cref="ExchangeRateOrdering.StatusRank"/>, which ranks only
    /// stored rows and counts a reversed competitor only when strictly newer.
    /// </summary>
    private static async Task<List<Candidate>> CurrentWinnersAsync(
        IQueryable<ExchangeRate> storedScope, IQueryable<ExchangeRate> reversedScope, DateTime now,
        CancellationToken cancellationToken)
    {
        // Index columns only, so each grouped MAX stays a loose index scan (see CurrencyConversionService).
        var stored = await LatestCandidatesAsync(
            storedScope.Where(r => r.AsOf <= now), cancellationToken);
        var reversed = await LatestCandidatesAsync(
            reversedScope.Where(r => r.Provider != null && r.AsOf <= now), cancellationToken);

        return stored
            .Concat(reversed.Select(c => c with { From = c.To, To = c.From, Inverted = true }))
            .GroupBy(c => (c.From, c.To))
            .Select(g => ExchangeRateOrdering.Newest(g)!)
            .Where(winner => !winner.Inverted)
            .ToList();
    }

    /// <summary>The rows at each pair's latest instant: one grouped <c>MAX(AsOf)</c> joined back.</summary>
    private static Task<List<Candidate>> LatestCandidatesAsync(
        IQueryable<ExchangeRate> inForce, CancellationToken cancellationToken)
    {
        var instants = inForce
            .GroupBy(r => new { r.FromCurrencyCode, r.ToCurrencyCode })
            .Select(g => new { g.Key.FromCurrencyCode, g.Key.ToCurrencyCode, AsOf = g.Max(r => r.AsOf) });

        return (
                from rate in inForce
                join instant in instants
                    on new { rate.FromCurrencyCode, rate.ToCurrencyCode, rate.AsOf }
                    equals new { instant.FromCurrencyCode, instant.ToCurrencyCode, instant.AsOf }
                select new Candidate(rate.FromCurrencyCode, rate.ToCurrencyCode, rate.AsOf,
                    rate.UpdatedAt ?? rate.CreatedAt, rate.ExchangeRateId, false))
            .ToListAsync(cancellationToken);
    }

    private async Task<ExistingExchangeRate> ToDtoAsync(ExchangeRate rate, CancellationToken cancellationToken)
    {
        var now = Now;
        return ToDto(rate, now, await CurrentIdsAsync([rate], now, cancellationToken));
    }

    private static ExistingExchangeRate ToDto(ExchangeRate rate, DateTime now, IReadOnlySet<Guid> current)
    {
        var dto = rate.Adapt<ExistingExchangeRate>();
        dto.Source = rate.Provider is null ? ExchangeRateSource.Manual : ExchangeRateSource.Fetched;
        dto.Status = rate.AsOf > now
            ? ExchangeRateStatus.Upcoming
            : current.Contains(rate.ExchangeRateId) ? ExchangeRateStatus.Current : ExchangeRateStatus.Historical;
        return dto;
    }

    /// <summary>
    /// A fetched <c>B → A</c> row served for <c>A → B</c> (issue #472 §5.2): codes swapped, <c>1 / Rate</c>
    /// unrounded, everything else the stored row's. It is the pair's winner, so it reads Current.
    /// </summary>
    private static ExistingExchangeRate ToInvertedDto(ExchangeRate rate) => new()
    {
        ExchangeRateId = rate.ExchangeRateId,
        FromCurrencyCode = rate.ToCurrencyCode,
        ToCurrencyCode = rate.FromCurrencyCode,
        Rate = 1m / rate.Rate,
        AsOf = rate.AsOf,
        CreatedAt = rate.CreatedAt,
        UpdatedAt = rate.UpdatedAt,
        Provider = rate.Provider,
        Source = ExchangeRateSource.Fetched,
        Status = ExchangeRateStatus.Current,
        Inverted = true,
    };

    private sealed record Candidate(
        string From, string To, DateTime AsOf, DateTime Tiebreak, Guid ExchangeRateId, bool Inverted)
        : IRateCandidate;

    private sealed record LatestCandidate(ExchangeRate Rate, bool Inverted) : IRateCandidate
    {
        public DateTime AsOf => Rate.AsOf;

        public DateTime Tiebreak => ExchangeRateOrdering.Tiebreak(Rate);

        public Guid ExchangeRateId => Rate.ExchangeRateId;
    }
}
