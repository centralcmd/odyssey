using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Components;
using Odyssey.Client.Components;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance.Instruments;

/// <summary>What the Watchlist card is showing (issue #452 §3.2).</summary>
public enum WatchlistState
{
    Loading,
    Ready,
    Error,
    CurrencyUnavailable,
}

public partial class WatchlistCard
{
    /// <summary>The trend window the card asks for (§5: <c>sparklineDays=30</c>).</summary>
    internal const int SparklineDays = 30;

    /// <summary>
    /// The display currency (<c>UserPreferences.MainCurrency ?? "NOK"</c>), as Home resolved it. Null until Home has
    /// resolved it: the card shows its skeleton and makes no request before then (§3.5, AC 24).
    /// </summary>
    [Parameter] public string? DisplayCurrencyCode { get; set; }

    /// <summary>The display currency's decimals.</summary>
    [Parameter] public int MinorUnits { get; set; } = OdsMoney.DefaultMinorUnits;

    /// <summary>Every currency's decimals, for the native close shown beneath a converted one.</summary>
    [Parameter] public IReadOnlyDictionary<string, int>? MinorUnitsByCode { get; set; }

    private WatchlistState _state = WatchlistState.Loading;
    private InstrumentWatchlist? _watchlist;
    private string? _requested;
    private DateOnly? _today;

    private IReadOnlyList<InstrumentWatchlistItem> Items => _watchlist?.Items ?? [];

    private string DisplayCode => _watchlist?.DisplayCurrencyCode ?? DisplayCurrencyCode ?? string.Empty;

    /// <summary>The hidden caption, which names the currency every figure is in.</summary>
    internal string Caption => $"Watchlist, prices in {DisplayCode}";

    internal static string TruncatedText(int count) => $"Showing the first {count} followed instruments.";

    internal static string InstrumentHref(Guid instrumentId) => $"instruments?instrument={instrumentId}";

    private IReadOnlyList<OdsTableColumn<InstrumentWatchlistItem>> Columns =>
    [
        new() { Key = "symbol", HeaderText = "Symbol", Cell = r => SymbolCell(r) },
        new() { Key = "name", HeaderText = "Name", CellClass = "wl-col-name", Cell = r => b => b.AddContent(0, r.Name) },
        new() { Key = "price", HeaderText = "Price", Align = OdsAlign.End, Cell = r => PriceCell(r) },
        new() { Key = "change", HeaderText = "Change", Align = OdsAlign.End, Cell = r => ChangeCell(r) },
        new() { Key = "trend", HeaderText = "30-day trend", CellClass = "wl-col-trend", Cell = r => TrendContent(r) },
        new() { Key = "asOf", HeaderText = "As of", Align = OdsAlign.End, Cell = r => AsOfCell(r) },
    ];

    protected override void OnInitialized() => _today = InstrumentVisuals.TryOsloToday(Time);

    protected override async Task OnParametersSetAsync()
    {
        if (string.IsNullOrWhiteSpace(DisplayCurrencyCode) || DisplayCurrencyCode == _requested)
            return;

        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (string.IsNullOrWhiteSpace(DisplayCurrencyCode))
            return;

        _requested = DisplayCurrencyCode;
        _state = WatchlistState.Loading;
        StateHasChanged();

        var result = await Instruments.GetWatchlistAsync(DisplayCurrencyCode, SparklineDays);
        if (result is { IsSuccess: true, Value: { } watchlist })
        {
            _watchlist = watchlist;
            _state = WatchlistState.Ready;
        }
        else if (result.Status == HttpStatusCode.UnprocessableEntity && result.Problem?.ErrorFor("mainCurrency") is not null)
        {
            _state = WatchlistState.CurrencyUnavailable;
        }
        else
        {
            _state = WatchlistState.Error;
        }
    }

    private static bool NoPrice(InstrumentWatchlistItem r) => r.NativeClose is null || r.LatestTradingDate is null;

    private bool SameCurrency(InstrumentWatchlistItem r) =>
        string.Equals(r.NativeCurrencyCode, DisplayCode, StringComparison.OrdinalIgnoreCase);

    private string NativeClose(InstrumentWatchlistItem r) =>
        InstrumentVisuals.FormatClose(r.NativeClose, r.NativeCurrencyCode,
            MinorUnitsByCode is not null && MinorUnitsByCode.TryGetValue(r.NativeCurrencyCode, out var minor) ? minor : OdsMoney.DefaultMinorUnits);

    private string ChangeText(InstrumentWatchlistItem r) =>
        NoPrice(r) ? "No price yet" : InstrumentVisuals.ChangeAnnouncement(r, DisplayCode, MinorUnits);

    internal static string FormatDate(DateOnly date) => date.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>The sparkline's points: the closes the server sent, oldest first, gaps skipped.</summary>
    internal static IReadOnlyList<double> TrendValues(InstrumentWatchlistItem r) =>
        [.. r.Sparkline.OrderBy(p => p.Date).Where(p => p.Close is not null).Select(p => (double)p.Close!.Value)];

    /// <summary>
    /// The trend as text, since the sparkline is hidden from assistive technology: "30-day trend: up 4.2 %",
    /// "… down …", "… unchanged", or "30-day trend: not enough prices" with fewer than two points.
    /// </summary>
    internal static string TrendAnnouncement(InstrumentWatchlistItem r)
    {
        var closes = r.Sparkline.OrderBy(p => p.Date).Where(p => p.Close is not null).Select(p => p.Close!.Value).ToList();
        if (closes.Count < 2 || closes[0] == 0m)
            return $"{SparklineDays}-day trend: not enough prices";

        var percent = Math.Round((closes[^1] - closes[0]) / closes[0] * 100m, 1, MidpointRounding.AwayFromZero);
        if (percent == 0m)
            return $"{SparklineDays}-day trend: unchanged";

        var magnitude = Math.Abs(percent).ToString("0.0", CultureInfo.CurrentCulture);
        return $"{SparklineDays}-day trend: {(percent > 0 ? "up" : "down")} {magnitude} %";
    }
}
