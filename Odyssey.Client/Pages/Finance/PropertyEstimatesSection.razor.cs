using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class PropertyEstimatesSection
{
    [Parameter, EditorRequired] public ExistingProperty Property { get; set; } = default!;

    /// <summary>Gates the row edit/delete buttons (<c>properties.estimates.write</c>).</summary>
    [Parameter] public bool CanWrite { get; set; }

    [Parameter, EditorRequired] public Func<decimal, string?, string> FormatMoney { get; set; } =
        (v, _) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>Raised after a create, edit or delete, so the host can refresh the row's figure and counts.</summary>
    [Parameter] public EventCallback OnChanged { get; set; }

    /// <summary>
    /// A fresh token opens the New estimate dialog once — the row menu's entry point. A token rather
    /// than a method on a component reference, because the section only exists once its card is open,
    /// so the host sets the token and the section acts on it when it arrives (the Terms section's
    /// <c>NewTermRequestToken</c> shape).
    /// </summary>
    [Parameter] public Guid? NewEstimateRequestToken { get; set; }

    private Guid? _handledNewEstimateToken;

    private List<ExistingPropertyEstimate> _estimates = [];
    private ExistingPropertyEstimate? _current;
    private ExistingPropertyEstimate? _previous;
    private ExistingPropertyEstimate? _first;
    private ExistingPropertyEstimate? _scheduled;
    private int _pastCount;
    private IReadOnlyList<OdsTermHistorySeries> _chartSeries = [];

    private bool _isLoading = true;
    private bool _loadFailed;

    private Guid _dialogKey = Guid.Empty;
    private bool _dialogOpen;
    private ExistingPropertyEstimate? _editing;

    private static DateTime Today => DateTime.UtcNow.Date;

    private OdsTypeOption TypeInfo => OdsTypeRegistries.PropertyTypeOf(Property.Type);

    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
            return;

        await LoadAsync();
    }

    protected override void OnParametersSet()
    {
        if (NewEstimateRequestToken is not { } token || token == Guid.Empty || token == _handledNewEstimateToken)
            return;

        _handledNewEstimateToken = token;
        OpenNew();
    }

    /// <summary>
    /// Re-reads the history. Public because <c>OnInitializedAsync</c> early-returns outside the
    /// browser, so a render test has no other way in — the seam the smart-tag section exposes too.
    /// </summary>
    public async Task LoadAsync()
    {
        _isLoading = true;
        var result = await Properties.ListEstimatesAsync(Property.PropertyId);
        _loadFailed = !result.IsSuccess;
        _estimates = result.ValueOr([]);
        Recompute();
        _isLoading = false;
        StateHasChanged();
    }

    private void OpenNew()
    {
        _editing = null;
        _dialogKey = Guid.NewGuid();
        _dialogOpen = true;
    }

    private void OpenEdit(ExistingPropertyEstimate estimate)
    {
        _editing = estimate;
        _dialogKey = Guid.NewGuid();
        _dialogOpen = true;
    }

    private async Task DeleteAsync(ExistingPropertyEstimate estimate)
    {
        var confirmed = await DialogService.ShowMessageBoxAsync(
            "Delete estimate?",
            $"Delete the {Money(estimate.Value)} estimate effective {LongDate(estimate.EffectiveFrom)}? The history keeps every other entry.",
            yesText: "Delete", cancelText: "Cancel");
        if (confirmed != true)
            return;

        var ok = (await Properties.DeleteEstimateAsync(Property.PropertyId, estimate.PropertyEstimateId))
            .Toast(Snackbar, "Unable to delete estimate", "Estimate deleted.");
        if (ok)
            await OnEstimateChanged();
    }

    private async Task OnEstimateChanged()
    {
        await LoadAsync();
        await OnChanged.InvokeAsync();
    }

    private IReadOnlyList<OdsRowAction> RowActions(ExistingPropertyEstimate estimate) =>
    [
        new()
        {
            Icon = Icons.Material.Filled.Edit,
            Label = "Edit estimate",
            OnClick = EventCallback.Factory.Create<MouseEventArgs>(this, () => OpenEdit(estimate)),
        },
        new()
        {
            Icon = Icons.Material.Filled.Delete,
            Label = "Delete estimate",
            Danger = true,
            OnClick = EventCallback.Factory.Create<MouseEventArgs>(this, () => DeleteAsync(estimate)),
        },
    ];

    private void Recompute()
    {
        // Newest first for the ledger, with the in-force tie-break (newest created) applied.
        _estimates = [.. _estimates.OrderByDescending(e => e.EffectiveFrom).ThenByDescending(e => e.CreatedAtUtc)];

        var past = _estimates.Where(e => e.EffectiveFrom.Date <= Today).ToList();
        _pastCount = past.Count;
        _current = past.FirstOrDefault();
        _previous = past.Skip(1).FirstOrDefault();
        _first = past.LastOrDefault() is { } f && f.PropertyEstimateId != _current?.PropertyEstimateId ? f : null;
        _scheduled = _estimates.Where(e => e.EffectiveFrom.Date > Today).MinBy(e => e.EffectiveFrom);

        _chartSeries = _estimates.Count == 0 ? [] :
        [
            new OdsTermHistorySeries
            {
                Key = "value",
                Label = "Estimated value",
                Value = _current is { } c ? Money(c.Value) : "—",
                // A chart token rather than the type's oklch hue: the type hue is a glyph-on-soft
                // colour that reads about 2:1 on the light theme, too faint for a line.
                Color = Property.Type == PropertyType.Vehicle ? "var(--chart-1)" : "var(--chart-2)",
                Group = $"amt:{Property.CurrencyCode}",
                Points =
                [
                    .. _estimates
                        .OrderBy(e => e.EffectiveFrom).ThenBy(e => e.CreatedAtUtc)
                        .Select(e => new OdsStepPoint(DateOnly.FromDateTime(e.EffectiveFrom), e.Value)
                        {
                            Id = e.PropertyEstimateId.ToString(),
                            Note = e.Note,
                        }),
                ],
                Format = v => Money(v),
                AxisFormat = CompactTick,
            },
        ];
    }

    private string Money(decimal value) => FormatMoney(value, Property.CurrencyCode);

    private string Signed(decimal diff) =>
        diff == 0 ? Money(0) : (diff > 0 ? "+" : "−") + Money(Math.Abs(diff));

    private static string Percent(decimal diff, decimal from) =>
        from == 0 ? "" : $" · {(diff >= 0 ? "+" : "−")}{Math.Abs(diff / from * 100):0.0}%";

    private static string TrendIcon(decimal diff) =>
        diff > 0 ? "trending_up" : diff < 0 ? "trending_down" : "trending_flat";

    // A worth going up reads as income, going down as expense — the finance hues, never the brand's.
    private static OdsInfoTileTone TrendTone(decimal diff) =>
        diff > 0 ? OdsInfoTileTone.Income : diff < 0 ? OdsInfoTileTone.Expense : OdsInfoTileTone.Default;

    private static string CurrentFoot(ExistingPropertyEstimate current) =>
        $"since {LongDate(current.EffectiveFrom)}{(string.IsNullOrWhiteSpace(current.Note) ? "" : $" · {current.Note}")}";

    private static string LongDate(DateTime date) => date.ToString("MMM dd, yyyy", CultureInfo.InvariantCulture);

    private static string MonthYear(DateTime date) =>
        date.ToString("MMM", CultureInfo.InvariantCulture) + " ’" + (date.Year % 100).ToString("00", CultureInfo.InvariantCulture);

    // No currency code on the tick (it is stated once, on the series): compact so 5.14M fits.
    private static string CompactTick(decimal v)
    {
        var a = Math.Abs(v);
        var sign = v < 0 ? "−" : "";
        return sign + (a >= 1_000_000m
            ? (a / 1_000_000m).ToString("0.00", CultureInfo.InvariantCulture) + "M"
            : a >= 10_000m
                ? Math.Round(a / 1_000m).ToString("0", CultureInfo.InvariantCulture) + "K"
                : a.ToString("#,##0", CultureInfo.InvariantCulture));
    }
}
