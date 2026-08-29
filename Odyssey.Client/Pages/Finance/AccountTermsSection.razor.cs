using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class AccountTermsSection
{
    [Parameter, EditorRequired] public ExistingAccount Account { get; set; } = default!;

    /// <summary>Gates the New term / edit / delete affordances (accounts.terms.write).</summary>
    [Parameter] public bool CanWrite { get; set; }

    /// <summary>Raised after a term is created/edited/deleted so the host can refresh the account
    /// list (the header subtitle shows the in-force rate).</summary>
    [Parameter] public EventCallback OnChanged { get; set; }

    /// <summary>Formats a fee amount in its currency — supplied by the host (per-account currency).</summary>
    [Parameter, EditorRequired] public Func<decimal, string?, string> FormatMoney { get; set; } = (v, _) => v.ToString(CultureInfo.InvariantCulture);

    private List<ExistingAccountTerm> _terms = [];
    private List<ExistingAccountTerm> _current = [];
    private HashSet<Guid> _currentIds = [];
    private HeroModel? _hero;

    private bool _isLoading;
    private bool _isOpen;

    private Guid _dialogKey = Guid.Empty;
    private bool _dialogOpen;
    private ExistingAccountTerm? _editingTerm;

    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
            return;
        await LoadAsync();
    }

    private async Task ToggleOpen()
    {
        _isOpen = !_isOpen;
        if (_isOpen && _terms.Count == 0 && !_isLoading)
            await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _isLoading = true;
        _terms = (await Accounts.ListTermsAsync(Account.AccountId)).ItemsOrToast(Snackbar, "terms");
        Recompute();
        _isLoading = false;
        StateHasChanged();
    }

    private async Task OnTermChanged()
    {
        await LoadAsync();
        await OnChanged.InvokeAsync();
    }

    private void Recompute()
    {
        // Newest first for the history table.
        _terms = _terms.OrderByDescending(t => t.EffectiveFrom).ThenByDescending(t => t.CreatedAtUtc).ToList();

        var asOf = DateTime.UtcNow.Date;
        _current = TermKindVisuals.All
            .Select(kind => _terms
                .Where(t => t.TermKind == kind && t.EffectiveFrom.Date <= asOf)
                .OrderByDescending(t => t.EffectiveFrom).ThenByDescending(t => t.CreatedAtUtc)
                .FirstOrDefault())
            .Where(t => t is not null)
            .Select(t => t!)
            .ToList();
        _currentIds = _current.Select(t => t.AccountTermId).ToHashSet();

        _hero = BuildHero();
    }

    private void OpenNew()
    {
        _editingTerm = null;
        _dialogKey = Guid.NewGuid();
        _dialogOpen = true;
    }

    private void OpenEdit(ExistingAccountTerm term)
    {
        _editingTerm = term;
        _dialogKey = Guid.NewGuid();
        _dialogOpen = true;
    }

    private async Task DeleteAsync(ExistingAccountTerm term)
    {
        var info = TermKindVisuals.Info(term.TermKind);
        var confirmed = await DialogService.ShowMessageBoxAsync(
            "Delete term?",
            $"Remove the {info.Label} entry effective {term.EffectiveFrom:MMM dd, yyyy}? This can’t be undone.",
            yesText: "Delete", cancelText: "Cancel");

        if (confirmed != true)
            return;

        var ok = (await Accounts.DeleteTermAsync(Account.AccountId, term.AccountTermId))
            .Toast(Snackbar, "Unable to delete term", "Term deleted.");

        if (ok)
            await OnTermChanged();
    }

    private static string MonthYear(DateTime date) =>
        date.ToString("MMM", CultureInfo.InvariantCulture) + " ’" + (date.Year % 100).ToString("00");

    // ── Hero step-line chart ────────────────────────────────────────────────
    private sealed record HeroModel(
        TermKindInfo Info, string Color, string CurrentLabel, string SubLine,
        string? DeltaLabel, string DeltaIcon, string Svg);

    private HeroModel? BuildHero()
    {
        // Prefer the interest rate; fall back to expected return. Need ≥ 1 entry.
        var kind = _terms.Any(t => t.TermKind == TermKind.InterestRate) ? TermKind.InterestRate
            : _terms.Any(t => t.TermKind == TermKind.ExpectedReturn) ? TermKind.ExpectedReturn
            : (TermKind?)null;
        if (kind is null)
            return null;

        var info = TermKindVisuals.Info(kind.Value);
        var ascending = _terms
            .Where(t => t.TermKind == kind.Value)
            .OrderBy(t => t.EffectiveFrom).ThenBy(t => t.CreatedAtUtc)
            .ToList();
        if (ascending.Count == 0)
            return null;

        // Apply the cost sign for a liability's interest rate (negative + expense-colored).
        var cost = kind.Value == TermKind.InterestRate && TermKindVisuals.IsLiability(Account.AccountType);
        var color = cost ? "var(--finance-expense)" : info.Color;
        var points = ascending
            .Select(t => (Date: t.EffectiveFrom.Date, Value: (double)(cost ? -Math.Abs(t.Value) : t.Value)))
            .ToList();

        var current = points[^1];
        var prev = points.Count > 1 ? points[^2] : (ValueTuple<DateTime, double>?)null;

        string Fmt(double v) => (v < 0 ? "−" : "") + TermKindVisuals.PctStr((decimal)Math.Abs(v));

        string? deltaLabel = null;
        var deltaIcon = "remove";
        if (prev is { } p)
        {
            var diff = current.Value - p.Item2;
            deltaIcon = diff > 0 ? "arrow_upward" : diff < 0 ? "arrow_downward" : "remove";
            deltaLabel = $"{TermKindVisuals.PctStr((decimal)Math.Abs(diff))} vs {MonthYear(p.Item1)}";
        }

        var subLine = $"{points.Count} change{(points.Count == 1 ? "" : "s")} since {MonthYear(points[0].Date)} · in force since {current.Date:MMM dd, yyyy}";

        return new HeroModel(info, color, Fmt(current.Value), subLine, deltaLabel, deltaIcon, BuildChartSvg(points, color));
    }

    private static string F(double d) => d.ToString("0.0", CultureInfo.InvariantCulture);

    private static string BuildChartSvg(List<(DateTime Date, double Value)> series, string color)
    {
        const double W = 680, H = 210, padL = 48, padR = 18, padT = 16, padB = 28;
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

        var vals = series.Select(s => s.Value).ToList();
        double lo = vals.Min(), hi = vals.Max();
        if (lo == hi)
        { lo -= 0.005; hi += 0.005; }
        var padV = (hi - lo) * 0.35;
        lo -= padV;
        hi += padV;
        double Y(double v) => padT + plotH - (v - lo) / (hi - lo) * plotH;

        // Staircase: hold flat to each change, then jump.
        var step = new List<(double X, double Y)>();
        for (var i = 0; i < series.Count; i++)
        {
            var sx = X(Days(series[i].Date));
            var sy = Y(series[i].Value);
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

        // Gradient fill under the solid line.
        var fillId = $"trmfill-{Math.Abs(series[0].Date.GetHashCode())}";
        sb.Append($"<defs><linearGradient id=\"{fillId}\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\">");
        sb.Append($"<stop offset=\"0%\" stop-color=\"{color}\" stop-opacity=\"0.20\" />");
        sb.Append($"<stop offset=\"100%\" stop-color=\"{color}\" stop-opacity=\"0\" /></linearGradient></defs>");

        // Gridlines + y labels (3 lines).
        var yticks = new[] { hi, (hi + lo) / 2, lo };
        foreach (var v in yticks)
        {
            sb.Append($"<line class=\"grid\" x1=\"{F(padL)}\" y1=\"{F(Y(v))}\" x2=\"{F(W - padR)}\" y2=\"{F(Y(v))}\" />");
            var label = (v < 0 ? "−" : "") + TermKindVisuals.PctStr((decimal)Math.Abs(v));
            sb.Append($"<text class=\"axis\" x=\"{F(padL - 8)}\" y=\"{F(Y(v) + 3)}\" text-anchor=\"end\">{label}</text>");
        }

        // Area + step line (solid past, dashed future).
        if (solid.Count > 0)
        {
            var area = PtsToPath(solid) + $" L {F(solid[^1].X)} {F(baseY)} L {F(solid[0].X)} {F(baseY)} Z";
            sb.Append($"<path d=\"{area}\" fill=\"url(#{fillId})\" />");
        }
        sb.Append($"<path class=\"step\" d=\"{PtsToPath(solid)}\" stroke=\"{color}\" />");
        if (dashed.Count > 1)
            sb.Append($"<path class=\"step future\" d=\"{PtsToPath(dashed)}\" stroke=\"{color}\" />");

        // Today marker.
        sb.Append($"<line class=\"nowline\" x1=\"{F(nowX)}\" y1=\"{F(padT - 4)}\" x2=\"{F(nowX)}\" y2=\"{F(baseY)}\" />");
        sb.Append($"<text class=\"nowlabel\" x=\"{F(Math.Min(nowX, W - padR))}\" y=\"{F(padT - 7)}\" text-anchor=\"end\">Today</text>");

        // Change-point dots.
        foreach (var s in series)
        {
            var cx = X(Days(s.Date));
            var cy = Y(s.Value);
            sb.Append($"<circle class=\"dot-halo\" cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"5\" />");
            sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"3.4\" fill=\"{color}\" />");
        }

        // X labels — skip when two land within 44px.
        double? last = null;
        foreach (var s in series)
        {
            var px = X(Days(s.Date));
            if (last is null || px - last > 44)
            {
                sb.Append($"<text class=\"axis\" x=\"{F(px)}\" y=\"{F(H - 8)}\" text-anchor=\"middle\">{MonthYear(s.Date)}</text>");
                last = px;
            }
        }

        return sb.ToString();
    }

    // Clip an axis-aligned polyline to x ≤ bound (keepBelow) or x ≥ bound, interpolating
    // the crossing so the solid/dashed split lands exactly on the "today" marker.
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
