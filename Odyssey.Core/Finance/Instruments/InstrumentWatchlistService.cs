using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.Finance.Instruments.Sources;
using Odyssey.Dtos.Finance;

namespace Odyssey.Core.Finance.Instruments;

/// <summary>
/// The dashboard watchlist (issue #446 §5 endpoint 15): each followed instrument's latest and previous close and
/// its change, converted to the caller's display currency, with a fixed number of queries whatever the instrument
/// count or sparkline length.
/// </summary>
/// <remarks>
/// A close for trading date <c>D</c> converts at the rate newest <b>strictly before</b> <c>D + 1 day at 00:00
/// Europe/Oslo</c> — matching <see cref="CurrencyConversionService"/>'s exclusive bound — so the change reflects
/// both the price and the currency move, as a NOK investor sees it. A missing rate makes the row
/// <c>Unconvertible</c>: the display fields are null and the native ones filled; never 1:1.
/// </remarks>
public sealed class InstrumentWatchlistService(
    OdysseyContext context,
    CurrencyConversionService conversion,
    IInstrumentPriceSettingsLookup settings,
    InstrumentPriceSourceCatalogue sources,
    TimeProvider? injectedTimeProvider = null)
{
    /// <summary>The most followed instruments one response carries; beyond it the response says <c>Truncated</c>.</summary>
    public const int MaxItems = 200;

    public const int MaxSparklineDays = 90;

    private readonly TimeProvider timeProvider = injectedTimeProvider ?? TimeProvider.System;

    public async Task<InstrumentWatchlist> GetAsync(string mainCurrency, int sparklineDays, CancellationToken cancellationToken = default)
    {
        var display = mainCurrency.Trim().ToUpperInvariant();
        if (!await context.Currencies.AnyAsync(c => c.CurrencyCode == display && c.Archived == null, cancellationToken))
        {
            throw new DomainUnprocessableException($"Currency '{display}' is not available.", "mainCurrency");
        }

        var resolved = await settings.GetAsync(cancellationToken);
        var today = OsloDate.Today(timeProvider);

        var followed = await context.Instruments.AsNoTracking()
            .Where(i => i.Archived == null && i.ShowOnDashboard)
            .OrderBy(i => i.Symbol).ThenBy(i => i.InstrumentId)
            .Take(MaxItems + 1)
            .ToListAsync(cancellationToken);
        var truncated = followed.Count > MaxItems;
        if (truncated)
        {
            followed.RemoveAt(followed.Count - 1);
        }

        var watchlist = new InstrumentWatchlist
        {
            DisplayCurrencyCode = display,
            StaleAfterDays = resolved.StaleAfterDays,
            Truncated = truncated,
        };
        if (followed.Count == 0)
        {
            return watchlist;
        }

        var ids = followed.Select(i => i.InstrumentId).ToList();
        var priorities = await InstrumentPriceReader.PrioritiesAsync(context, ids, cancellationToken);
        var latest = await InstrumentPriceReader.LatestAsync(context, ids, priorities, cancellationToken);
        var previous = await InstrumentPriceReader.PreviousAsync(context, latest, priorities, cancellationToken);

        var days = Math.Clamp(sparklineDays, 0, MaxSparklineDays);
        var sparkFrom = today.AddDays(-days);
        Dictionary<Guid, List<InstrumentPrice>> sparklines = [];
        if (days > 0)
        {
            var rows = await context.InstrumentPrices.AsNoTracking()
                .Where(p => ids.Contains(p.InstrumentId) && p.TradingDate >= sparkFrom && p.TradingDate <= today)
                .ToListAsync(cancellationToken);
            sparklines = InstrumentPriceOrdering.WinnersPerDate(rows, priorities)
                .GroupBy(p => p.InstrumentId)
                .ToDictionary(g => g.Key, g => g.OrderBy(p => p.TradingDate).ToList());
        }

        var dates = latest.Values.Select(p => p.TradingDate)
            .Concat(previous.Values.Select(p => p.TradingDate))
            .Concat(sparklines.Values.SelectMany(list => list.Select(p => p.TradingDate)))
            .ToList();
        var rates = new RateLookup(display);
        if (dates.Count > 0)
        {
            var foreign = followed.Select(i => i.CurrencyCode).Where(code => code != display).Distinct().ToList();
            if (foreign.Count > 0)
            {
                var timeline = await conversion.GetRateTimelineToAsync(
                    display, foreign,
                    OsloDate.StartOfDayUtc(dates.Min().AddDays(1)),
                    OsloDate.StartOfDayUtc(dates.Max().AddDays(1)),
                    cancellationToken);
                rates = new RateLookup(display, timeline);
            }
        }

        var shownSources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var instrument in followed)
        {
            latest.TryGetValue(instrument.InstrumentId, out var newest);
            previous.TryGetValue(instrument.InstrumentId, out var before);
            var item = new InstrumentWatchlistItem
            {
                InstrumentId = instrument.InstrumentId,
                Symbol = instrument.Symbol,
                Name = instrument.Name,
                Type = instrument.Type,
                NativeCurrencyCode = instrument.CurrencyCode,
            };

            if (newest is not null)
            {
                item.NativeClose = newest.Close;
                item.LatestTradingDate = newest.TradingDate;
                item.Source = newest.SourceKey is null ? InstrumentPriceSource.Manual : InstrumentPriceSource.Fetched;
                item.IsStale = newest.TradingDate < today.AddDays(-resolved.StaleAfterDays);
                if (newest.SourceKey is not null)
                {
                    shownSources.Add(newest.SourceKey);
                }

                item.Close = rates.Convert(newest.Close, instrument.CurrencyCode, newest.TradingDate);
                item.Unconvertible = item.Close is null;
                if (before is not null)
                {
                    item.PreviousTradingDate = before.TradingDate;
                    item.PreviousClose = rates.Convert(before.Close, instrument.CurrencyCode, before.TradingDate);
                    if (item.Close is { } close && item.PreviousClose is { } prior)
                    {
                        item.Change = close - prior;
                        item.ChangePercent = prior == 0 ? null : decimal.Round((close - prior) / prior * 100m, 4);
                    }
                    else if (item.PreviousClose is null)
                    {
                        item.Unconvertible = true;
                    }
                }

                if (item.Unconvertible)
                {
                    item.Close = null;
                    item.PreviousClose = null;
                    item.Change = null;
                    item.ChangePercent = null;
                }
            }

            if (sparklines.TryGetValue(instrument.InstrumentId, out var points))
            {
                item.Sparkline = points
                    .Select(p => new InstrumentWatchlistPoint
                    {
                        Date = p.TradingDate,
                        Close = rates.Convert(p.Close, instrument.CurrencyCode, p.TradingDate),
                    })
                    .ToList();
            }

            watchlist.Items.Add(item);
        }

        watchlist.Attributions = shownSources.Select(sources.Find)
            .Where(source => source is not null && !string.IsNullOrWhiteSpace(source.Attribution))
            .Select(source => source!.Attribution)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToList();
        return watchlist;
    }

    /// <summary>Steps a rate timeline: the rate for a close on <c>D</c> is the newest point before <c>D+1 00:00 Oslo</c>.</summary>
    private sealed class RateLookup(
        string display,
        IReadOnlyDictionary<string, IReadOnlyList<CurrencyConversionService.RatePoint>>? timeline = null)
    {
        public decimal? Convert(decimal amount, string currency, DateOnly date)
        {
            if (string.Equals(currency, display, StringComparison.Ordinal))
            {
                return amount;
            }

            if (timeline is null || !timeline.TryGetValue(currency, out var points) || points.Count == 0)
            {
                return null;
            }

            var bound = OsloDate.StartOfDayUtc(date.AddDays(1));
            decimal? rate = null;
            foreach (var point in points)
            {
                if (point.AsOf >= bound)
                {
                    break;
                }

                rate = point.Rate;
            }

            return rate is { } r ? amount * r : null;
        }
    }
}
