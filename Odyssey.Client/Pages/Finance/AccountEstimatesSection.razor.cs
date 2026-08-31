using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class AccountEstimatesSection
{
    [Parameter, EditorRequired] public ExistingAccount Account { get; set; } = default!;

    /// <summary>Gates the New estimate / edit / delete affordances (accounts.estimates.write).</summary>
    [Parameter] public bool CanWrite { get; set; }

    /// <summary>
    /// The disclosure shell. False renders the section bare — no OdsCollapsible, no header — for a host
    /// that introduces it with its own OdsSectionDivider (an OdsRecordCard body).
    /// </summary>
    [Parameter] public bool Chrome { get; set; } = true;

    /// <summary>
    /// Render the "Current value" block. False when the host lifted those values into the record
    /// card's own Current band, so they are not stated twice in one body.
    /// </summary>
    [Parameter] public bool ShowCurrent { get; set; } = true;

    /// <summary>
    /// Render the inner "History" sub-divider. False when the host's own section divider already
    /// labels this content and carries its count.
    /// </summary>
    [Parameter] public bool BareAction { get; set; } = true;

    /// <summary>Raised after an estimate is created/edited/deleted so the host can refresh the account
    /// list (the header shows the in-force estimate as the headline value).</summary>
    [Parameter] public EventCallback OnChanged { get; set; }

    /// <summary>Formats a money amount in its currency — supplied by the host (per-account currency).</summary>
    [Parameter, EditorRequired] public Func<decimal, string?, string> FormatMoney { get; set; } = (v, _) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>The account currency's symbol, for the compact chart axis — supplied by the host.</summary>
    [Parameter] public string CurrencySymbol { get; set; } = "$";

    private List<ExistingAccountEstimate> _estimates = [];
    private ExistingAccountEstimate? _current;
    private Dictionary<Guid, decimal?> _changes = [];
    private HeroModel? _hero;
    private bool _recommended;

    private bool _isLoading;
    private bool _isOpen;

    private Guid _dialogKey = Guid.Empty;
    private bool _dialogOpen;
    private ExistingAccountEstimate? _editingEstimate;

    private string TypeIcon => AccountTypeVisuals.MaterialIcon(Account.AccountType);
    private string TypeFg => AccountTypeVisuals.FgColor(Account.AccountType);
    private string TypeBg => AccountTypeVisuals.BgColor(Account.AccountType);

    protected override void OnInitialized() =>
        _recommended = EstimateVisuals.IsRecommended(Account.AccountType);

    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
            return;
        await LoadAsync();
    }

    private async Task ToggleOpen()
    {
        _isOpen = !_isOpen;
        if (_isOpen && _estimates.Count == 0 && !_isLoading)
            await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _isLoading = true;
        _estimates = (await Accounts.ListEstimatesAsync(Account.AccountId)).ItemsOrToast(Snackbar, "estimates");
        Recompute();
        _isLoading = false;
        StateHasChanged();
    }

    private async Task OnEstimateChanged()
    {
        await LoadAsync();
        await OnChanged.InvokeAsync();
    }

    private void Recompute()
    {
        // Newest first for the history table.
        _estimates = _estimates
            .OrderByDescending(e => e.EffectiveFrom).ThenByDescending(e => e.CreatedAtUtc)
            .ToList();

        var asOf = DateTime.UtcNow.Date;
        _current = _estimates
            .Where(e => e.EffectiveFrom.Date <= asOf)
            .OrderByDescending(e => e.EffectiveFrom).ThenByDescending(e => e.CreatedAtUtc)
            .FirstOrDefault();

        // Change vs the chronologically prior estimate (null on the first).
        var ascending = _estimates
            .OrderBy(e => e.EffectiveFrom).ThenBy(e => e.CreatedAtUtc)
            .ToList();
        _changes = [];
        for (var i = 0; i < ascending.Count; i++)
            _changes[ascending[i].AccountEstimateId] = i == 0 ? null : ascending[i].Value - ascending[i - 1].Value;

        _hero = BuildHero(ascending);
    }

    private void OpenNew()
    {
        _editingEstimate = null;
        _dialogKey = Guid.NewGuid();
        _dialogOpen = true;
    }

    private void OpenEdit(ExistingAccountEstimate estimate)
    {
        _editingEstimate = estimate;
        _dialogKey = Guid.NewGuid();
        _dialogOpen = true;
    }

    private async Task DeleteAsync(ExistingAccountEstimate estimate)
    {
        var confirmed = await DialogService.ShowMessageBoxAsync(
            "Delete estimate?",
            $"Remove the estimate effective {estimate.EffectiveFrom:MMM dd, yyyy}? This can’t be undone.",
            yesText: "Delete", cancelText: "Cancel");

        if (confirmed != true)
            return;

        var ok = (await Accounts.DeleteEstimateAsync(Account.AccountId, estimate.AccountEstimateId))
            .Toast(Snackbar, "Unable to delete estimate", "Estimate deleted.");

        if (ok)
            await OnEstimateChanged();
    }

    private static string MonthYear(DateTime date) =>
        date.ToString("MMM", CultureInfo.InvariantCulture) + " ’" + (date.Year % 100).ToString("00");

    // ── Hero step-line value chart ────────────────────────────────────────────
    private sealed record HeroModel(
        decimal CurrentValue, string SubLine, string? DeltaLabel, string DeltaDir, string DeltaIcon, string Svg, bool Scheduled);

    private HeroModel? BuildHero(List<ExistingAccountEstimate> ascending)
    {
        if (ascending.Count == 0)
            return null;

        var points = ascending
            .Select(e => (Date: e.EffectiveFrom.Date, Value: e.Value))
            .ToList();

        // The hero headline mirrors _current's as-of rule: the latest estimate
        // whose effective date is today or earlier. A scheduled future estimate
        // still renders on the chart (projected step) but must not be shown as
        // the current/in-force value. When nothing is effective yet we preview
        // the earliest scheduled estimate but flag it as scheduled, so it is not
        // presented as the current/net-worth value (which the backend, account
        // header and _current all still derive from the balance).
        var asOf = DateTime.UtcNow.Date;
        var currentIndex = points.FindLastIndex(p => p.Date <= asOf);
        var scheduledOnly = currentIndex < 0;
        if (scheduledOnly)
            currentIndex = 0;

        var current = points[currentIndex];
        var prev = !scheduledOnly && currentIndex > 0 ? points[currentIndex - 1] : ((DateTime Date, decimal Value)?)null;

        string? deltaLabel = null;
        var deltaDir = "flat";
        var deltaIcon = "remove";
        if (prev is { } p)
        {
            var diff = current.Value - p.Value;
            deltaDir = diff > 0 ? "up" : diff < 0 ? "down" : "flat";
            deltaIcon = diff > 0 ? "arrow_upward" : diff < 0 ? "arrow_downward" : "remove";
            deltaLabel = $"{FormatMoney(Math.Abs(diff), Account.CurrencyCode)} vs {MonthYear(p.Date)}";
        }

        var subLine = scheduledOnly
            ? $"{points.Count} scheduled estimate{(points.Count == 1 ? "" : "s")} · effective {current.Date:MMM dd, yyyy}"
            : $"{points.Count} estimate{(points.Count == 1 ? "" : "s")} since {MonthYear(points[0].Date)} · in force since {current.Date:MMM dd, yyyy}";

        return new HeroModel(current.Value, subLine, deltaLabel, deltaDir, deltaIcon, BuildChartSvg(points), scheduledOnly);
    }

    private static string F(double d) => d.ToString("0.0", CultureInfo.InvariantCulture);

    private string BuildChartSvg(List<(DateTime Date, decimal Value)> series)
    {
        // One accent per record: inside an open card this resolves to the account's own type colour,
        // which OdsRecordCard publishes as --rec. The mint stays the fallback for the section rendered
        // outside a card, where there is no record accent to inherit.
        const string color = "var(--rec, var(--finance-income))";
        const double W = 680, H = 210, padL = 54, padR = 18, padT = 16, padB = 28;
        var plotW = W - padL - padR;
        var plotH = H - padT - padB;
        var baseY = padT + plotH;

        var epoch = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        double Days(DateTime d) => (DateTime.SpecifyKind(d, DateTimeKind.Utc) - epoch).TotalDays;

        var now = (DateTime.UtcNow - epoch).TotalDays;
        var t0 = Days(series[0].Date);
        var tLast = Days(series[^1].Date);
        var tMax = Math.Max(now, tLast);
        var span = Math.Max(tMax - t0, 1);
        double X(double t) => padL + (t - t0) / span * plotW;

        var vals = series.Select(s => (double)s.Value).ToList();
        double lo = vals.Min(), hi = vals.Max();
        if (lo == hi)
        { lo *= 0.97; hi *= 1.03; }
        var padV = (hi - lo) * 0.28;
        if (padV == 0)
            padV = hi * 0.05;
        if (padV == 0)
            padV = 1;
        lo = Math.Max(0, lo - padV);
        hi += padV;
        double Y(double v) => padT + plotH - (v - lo) / (hi - lo) * plotH;

        // Staircase: hold flat to each appraisal, then jump.
        var step = new List<(double X, double Y)>();
        for (var i = 0; i < series.Count; i++)
        {
            var sx = X(Days(series[i].Date));
            var sy = Y((double)series[i].Value);
            if (i == 0)
                step.Add((sx, sy));
            else
            { step.Add((sx, step[^1].Y)); step.Add((sx, sy)); }
        }
        var nowX = X(now);
        var endX = X(tMax);
        step.Add((endX, step[^1].Y));

        var solid = ClipPts(step, nowX, keepBelow: true);
        var dashed = ClipPts(step, nowX, keepBelow: false);

        var sb = new StringBuilder();

        var fillId = $"estfill-{Math.Abs(series[0].Date.GetHashCode())}";
        sb.Append($"<defs><linearGradient id=\"{fillId}\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\">");
        sb.Append($"<stop offset=\"0%\" stop-color=\"{color}\" stop-opacity=\"0.20\" />");
        sb.Append($"<stop offset=\"100%\" stop-color=\"{color}\" stop-opacity=\"0\" /></linearGradient></defs>");

        // Gridlines + y labels (3 lines, compact money).
        var yticks = new[] { hi, (hi + lo) / 2, lo };
        foreach (var v in yticks)
        {
            sb.Append($"<line class=\"grid\" x1=\"{F(padL)}\" y1=\"{F(Y(v))}\" x2=\"{F(W - padR)}\" y2=\"{F(Y(v))}\" />");
            var label = EstimateVisuals.MoneyCompact((decimal)v, CurrencySymbol);
            sb.Append($"<text class=\"axis\" x=\"{F(padL - 8)}\" y=\"{F(Y(v) + 3)}\" text-anchor=\"end\">{label}</text>");
        }

        // Area + step line (solid past, dashed future).
        if (solid.Count > 0)
        {
            var area = PtsToPath(solid) + $" L {F(solid[^1].X)} {F(baseY)} L {F(solid[0].X)} {F(baseY)} Z";
            sb.Append($"<path d=\"{area}\" fill=\"url(#{fillId})\" />");
        }
        sb.Append($"<path class=\"line\" d=\"{PtsToPath(solid)}\" stroke=\"{color}\" />");
        if (dashed.Count > 1)
            sb.Append($"<path class=\"line future\" d=\"{PtsToPath(dashed)}\" stroke=\"{color}\" />");

        // Today marker.
        sb.Append($"<line class=\"nowline\" x1=\"{F(nowX)}\" y1=\"{F(padT - 4)}\" x2=\"{F(nowX)}\" y2=\"{F(baseY)}\" />");
        sb.Append($"<text class=\"nowlabel\" x=\"{F(Math.Min(nowX, W - padR))}\" y=\"{F(padT - 7)}\" text-anchor=\"end\">Today</text>");

        // Appraisal dots.
        foreach (var s in series)
        {
            var cx = X(Days(s.Date));
            var cy = Y((double)s.Value);
            sb.Append($"<circle class=\"dot-halo\" cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"5\" />");
            sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"3.4\" fill=\"{color}\" />");
        }

        // X labels — skip when two land within 46px.
        double? last = null;
        foreach (var s in series)
        {
            var px = X(Days(s.Date));
            if (last is null || px - last > 46)
            {
                sb.Append($"<text class=\"axis\" x=\"{F(px)}\" y=\"{F(H - 8)}\" text-anchor=\"middle\">{MonthYear(s.Date)}</text>");
                last = px;
            }
        }

        return sb.ToString();
    }

    // Clip an axis-aligned polyline to x ≤ bound (keepBelow) or x ≥ bound, interpolating the
    // crossing so the solid/dashed split lands exactly on the "today" marker.
    private static List<(double X, double Y)> ClipPts(List<(double X, double Y)> pts, double bound, bool keepBelow)
    {
        var outPts = new List<(double X, double Y)>();
        for (var i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            var inside = keepBelow ? p.X <= bound : p.X >= bound;
            if (i > 0)
            {
                var prev = pts[i - 1];
                var prevInside = keepBelow ? prev.X <= bound : prev.X >= bound;
                if (inside != prevInside && prev.X != p.X)
                {
                    var t = (bound - prev.X) / (p.X - prev.X);
                    outPts.Add((bound, prev.Y + (p.Y - prev.Y) * t));
                }
                else if (inside != prevInside)
                {
                    outPts.Add((bound, inside ? prev.Y : p.Y));
                }
            }
            if (inside)
                outPts.Add(p);
        }
        return outPts;
    }

    private static string PtsToPath(List<(double X, double Y)> pts) =>
        pts.Count == 0 ? "" : "M " + string.Join(" L ", pts.Select(p => $"{F(p.X)} {F(p.Y)}"));
}
