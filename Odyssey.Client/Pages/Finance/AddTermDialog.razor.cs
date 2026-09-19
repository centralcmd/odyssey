using System.Globalization;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class AddTermDialog
{
    [Parameter, EditorRequired] public ExistingAccount Account { get; set; } = default!;

    /// <summary>The term being edited, or <c>null</c> to create a new one.</summary>
    [Parameter] public ExistingTerm? Term { get; set; }

    /// <summary>The account's existing terms — for the client-side (kind, label, effectiveFrom)
    /// duplicate guard.</summary>
    [Parameter] public IReadOnlyList<ExistingTerm> Existing { get; set; } = [];

    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>Raised after a successful create/update so the host can reload.</summary>
    [Parameter] public EventCallback OnSaved { get; set; }

    private bool IsEdit => Term is not null;
    private bool IsRate => TermKindVisuals.Info(_kind).Group == TermGroup.Rate;
    private bool IsPercentage => _unit == TermValueUnit.Percentage;

    /// <summary>Whether the Name field is rendered: a fee takes a label, a rate is refused one.</summary>
    private bool TakesLabel => TermLabel.RuleFor(_kind) == TermLabelRule.Required;

    private const int TermLabelMaxLength = TermLabel.MaxLength;

    private TermKind _kind;
    private string _label = "";
    private TermValueUnit _unit;
    private string _valueStr = "";
    private string _currency = "USD";
    private string _interval = "";
    private decimal? _intervalCount;
    private DateTime? _anchorDate;
    private DateTime? _effectiveFrom = DateTime.UtcNow.Date;
    private string? _note = "";
    private bool _isSaving;

    private static readonly IReadOnlyList<OdsSegmentedOption> _unitOptions =
    [
        new() { Value = nameof(TermValueUnit.Percentage), Label = "Percentage", Icon = "percent" },
        new() { Value = nameof(TermValueUnit.Amount), Label = "Amount", Icon = "payments" },
    ];

    private IReadOnlyList<TermKind> _eligibleKinds = [];
    private List<OdsOption> _currencyOptions = [];
    private List<OdsOption> _intervalOptions = [];
    private readonly Dictionary<string, string> _errors = new();

    protected override void OnInitialized()
    {
        _eligibleKinds = TermKindVisuals.EligibleKinds(Account.AccountType);

        // Reading order, not ordinal order: the ordinals are deliberately out of sequence
        // (PerUnit is 6, Weekly is 7, and 4 stays retired), so the registry decides the order.
        _intervalOptions =
        [
            new OdsOption("", "Not specified"),
            .. TermKindVisuals.AllIntervals.Select(i => new OdsOption(i.ToString(), TermKindVisuals.InfoFor(i)!.Label)),
        ];

        if (Term is not null)
        {
            _kind = Term.TermKind;
            _label = Term.Label ?? "";
            _unit = Term.ValueUnit;
            _valueStr = Term.ValueUnit == TermValueUnit.Percentage ? FractionToPercentString(Term.Value) : Term.Value.ToString(CultureInfo.InvariantCulture);
            _currency = Term.CurrencyCode ?? Account.CurrencyCode;
            _interval = Term.Interval?.ToString() ?? "";
            _intervalCount = Term.IntervalCount;
            _anchorDate = Term.AnchorDate?.Date;
            _effectiveFrom = Term.EffectiveFrom.Date;
            _note = Term.Note ?? "";
        }
        else
        {
            _kind = _eligibleKinds.Count > 0 ? _eligibleKinds[0] : TermKind.Fee;
            _unit = TermKindVisuals.Info(_kind).DefaultUnit;
            _currency = Account.CurrencyCode;
            _interval = DefaultIntervalFor(_kind);
            _effectiveFrom = DateTime.UtcNow.Date;
        }
    }

    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
            return;

        var currencies = await ReferenceData.CurrenciesAsync();
        if (currencies.Count > 0)
        {
            _currencyOptions = currencies
                .Where(c => c.Archived is null)
                .OrderBy(c => c.CurrencyCode)
                // The label is the currency NAME alone — OdsMoneyField renders the ISO code itself,
                // in mono, so a "USD · US Dollar" label would print the code twice.
                .Select(c => new OdsOption(c.CurrencyCode, c.Name))
                .ToList();
        }

        // Guarantee the account's own currency is selectable even if the list failed to load.
        if (!_currencyOptions.Any(o => o.Value == _currency))
            _currencyOptions.Insert(0, new OdsOption(_currency, _currency));

        StateHasChanged();
    }

    // One default for a fee — there is no longer a fee kind to guess from.
    private static string DefaultIntervalFor(TermKind kind) =>
        TermKindVisuals.Info(kind).Group == TermGroup.Fee
            ? TermKindVisuals.DefaultFeeInterval.ToString()
            : "";

    /// <summary>The selected cadence unit, or <c>null</c> when it is left unspecified.</summary>
    private Interval? SelectedInterval =>
        Enum.TryParse<Interval>(_interval, out var interval) ? interval : null;

    /// <summary>
    /// The one condition the cadence fields hang off. A count is offered — and written — only for a
    /// periodic unit, so for anything else the field is ABSENT rather than disabled: the answer is
    /// not merely unknown there, it has no meaning, and the request carries null.
    /// </summary>
    private bool IsPeriodicInterval => TermKindVisuals.IsPeriodic(SelectedInterval);

    /// <summary>The plural unit noun the count reads in ("every 3 <b>months</b>").</summary>
    private string IntervalUnitNoun => TermKindVisuals.InfoFor(SelectedInterval)?.Many ?? "";

    /// <summary>The cadence in words, as the request would store it — blank counts as the identity
    /// cadence, which is exactly what the service writes.</summary>
    private string? CadenceEcho =>
        TermKindVisuals.CadenceText(SelectedInterval, EffectiveIntervalCount);

    private string? IntervalCountHelp =>
        TermKindVisuals.InfoFor(SelectedInterval) is { } info ? $"Leave blank for {info.Adverb}" : null;

    /// <summary>
    /// The interval picker's own description. The non-periodic units say what they mean here, since
    /// they have no count field to explain them, and <c>PerUnit</c> adds where the unit itself is
    /// named — both through the Help slot rather than a loose sibling element, so MudSelect wires
    /// them into the control's <c>aria-describedby</c>. One-time needs no gloss.
    /// </summary>
    private string? IntervalHelp
    {
        get
        {
            if (IsPeriodicInterval || SelectedInterval is not { } interval || interval == Interval.OneTime)
                return null;

            var charged = $"Charged {TermKindVisuals.InfoFor(interval)!.Adverb}";

            return interval == Interval.PerUnit
                ? $"{charged} — name the unit in the fee\u2019s name, e.g. \u201cCustody \u00b7 per share\u201d"
                : charged;
        }
    }

    /// <summary>The id the count field points its <c>aria-describedby</c> at.</summary>
    private const string CadenceEchoId = "trm-cadence-echo";

    /// <summary>Whether the echo renders — and therefore whether the count field may name it.
    /// A field describing an element that is not in the DOM is a dangling reference.</summary>
    private bool ShowsCadenceEcho =>
        IsPeriodicInterval && !_errors.ContainsKey("intervalCount") && CadenceEcho is not null;

    /// <summary>What a blank count resolves to on a periodic unit: the identity cadence, 1.</summary>
    private int EffectiveIntervalCount =>
        _intervalCount is { } count ? (int)count : TermIntervalCount.Min;

    // Each kind keeps its registry hue on the card, as it does on every tile and history row.
    private IReadOnlyList<OdsCardSelectOption> KindOptions =>
        [.. _eligibleKinds.Select(KindOption)];

    private static OdsCardSelectOption KindOption(TermKind kind)
    {
        var info = TermKindVisuals.Info(kind);
        return new OdsCardSelectOption { Value = kind.ToString(), Label = info.Label, Icon = info.Icon, Color = info.Color, Soft = info.Soft };
    }

    private void OnKindPicked(string value) => PickKind(Enum.Parse<TermKind>(value));

    private void PickKind(TermKind kind)
    {
        _kind = kind;
        var info = TermKindVisuals.Info(kind);
        _unit = info.DefaultUnit;
        // A rate kind refuses a label, so a typed one is discarded on the switch rather than carried
        // invisibly into a request the server would reject.
        if (TermLabel.RuleFor(kind) != TermLabelRule.Required)
            _label = "";
        // A rate is not billed, so it carries NEITHER half of a billing description, nor an anchor.
        if (info.Group == TermGroup.Fee)
        {
            if (string.IsNullOrEmpty(_interval))
                _interval = DefaultIntervalFor(kind);
        }
        else
        {
            _interval = "";
            _intervalCount = null;
            _anchorDate = null;
        }
        _errors.Clear();
    }

    private void OnLabelChanged(string value)
    {
        _label = value;
        _errors.Remove("label");
    }

    private void OnUnitChanged(string value)
    {
        if (Enum.TryParse<TermValueUnit>(value, out var unit))
            _unit = unit;
        _errors.Remove("value");
    }

    private void OnValueChanged(string value)
    {
        _valueStr = value;
        _errors.Remove("value");
    }

    private void OnCurrencyChanged(string value) => _currency = value;

    private void OnIntervalChanged(string value)
    {
        _interval = value;
        // A count that is no longer meaningful is dropped rather than carried invisibly into a
        // request the server would refuse — the field it belonged to has just gone away.
        if (!IsPeriodicInterval)
            _intervalCount = null;
        _errors.Remove("intervalCount");
    }

    private void OnIntervalCountChanged(decimal? value)
    {
        _intervalCount = value;
        _errors.Remove("intervalCount");
    }

    private void OnAnchorDateChanged(DateTime? date) => _anchorDate = date;
    private void OnNoteChanged(string value) => _note = value;

    private void OnEffectiveFromChanged(DateTime? date)
    {
        _effectiveFrom = date;
        _errors.Remove("effectiveFrom");
    }

    private string PreviewFraction
    {
        get
        {
            var raw = ParseValue();
            return raw is null ? "—" : (raw.Value / 100m).ToString("0.0000", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>The cadence beside the money field's "Flat amount in USD" helper — the same words
    /// the echo below the picker uses, since both come from the one helper.</summary>
    private string CadenceValueHint =>
        CadenceEcho is { } cadence ? $" · {cadence}" : "";

    private decimal? ParseValue() =>
        OdsMoneyText.Parse(_valueStr);

    private async Task CloseAsync() => await OpenChanged.InvokeAsync(false);

    private async Task SubmitAsync()
    {
        if (_isSaving)
            return;

        _errors.Clear();

        if (!TermKindVisuals.IsEligible(_kind, Account.AccountType))
            _errors["kind"] = "Not available for this account type.";

        var raw = ParseValue();
        if (raw is null)
        {
            _errors["value"] = "Enter a value.";
        }
        else if (IsPercentage)
        {
            if (raw < -100m || raw > 100m)
                _errors["value"] = "Rate must be between −100% and 100%.";
        }
        else if (raw < 0m)
        {
            _errors["value"] = "A fee amount can’t be negative.";
        }

        if (_effectiveFrom is null)
            _errors["effectiveFrom"] = "Pick the date this takes effect.";

        // The same bound the DTO's [Range] carries and the service re-checks — named from the one
        // constant pair, so the message cannot quote a number the server would not enforce.
        if (!IsRate && IsPeriodicInterval && _intervalCount is { } count
            && (count != Math.Truncate(count) || count < TermIntervalCount.Min || count > TermIntervalCount.Max))
        {
            _errors["intervalCount"] =
                $"Enter a whole number between {TermIntervalCount.Min} and {TermIntervalCount.Max}.";
        }

        // No ordering is imposed between the anchor and the effective date, in either direction:
        // billed in arrears and prepaid are both legitimate records, and the server rejects neither.

        if ((_note?.Length ?? 0) > 512)
            _errors["note"] = "Keep the note under 512 characters.";

        // Label — refused on a rate kind (the field isn't rendered), required on every fee.
        var label = TakesLabel ? TermLabel.Normalize(_label) : null;
        if (TakesLabel && label is null)
            _errors["label"] = "Name this fee so it keeps its own history.";
        else if (label is { Length: > TermLabel.MaxLength })
            _errors["label"] = $"Keep the name under {TermLabel.MaxLength} characters.";

        // Duplicate (kind, label, effectiveFrom) → the server's 409, excluding the row being edited.
        // Compared on the SAME normalized, case-folded key the server writes, so "ATM abroad" and
        // "  atm   Abroad " collide here exactly as they would there.
        var labelKey = TermLabel.Key(label);
        if (_effectiveFrom is { } date && Existing.Any(t =>
                t.TermId != (Term?.TermId ?? Guid.Empty)
                && t.TermKind == _kind
                && TermLabel.Key(t.Label) == labelKey
                && t.EffectiveFrom.Date == date.Date))
        {
            _errors["effectiveFrom"] = label is null
                ? "This kind already has an entry on that date."
                : $"“{label}” already has an entry on that date.";
        }

        if (_errors.Count > 0)
            return;

        var value = IsPercentage
            ? Math.Round(raw!.Value / 100m, 6)
            : Math.Round(raw!.Value, 2);

        var dto = new NewTerm
        {
            TermKind = _kind,
            // LabelKey is derived server-side and is on no request DTO — only Label is sent.
            Label = label,
            ValueUnit = _unit,
            Value = value,
            CurrencyCode = IsPercentage ? null : _currency,
            Interval = IsRate ? null : SelectedInterval,
            // The identity cadence when a periodic unit is left blank, and null — never a
            // meaningless 1 — in every other case, matching what the service persists.
            IntervalCount = !IsRate && IsPeriodicInterval ? EffectiveIntervalCount : null,
            AnchorDate = IsRate || _anchorDate is null
                ? null
                : DateTime.SpecifyKind(_anchorDate.Value.Date, DateTimeKind.Utc),
            EffectiveFrom = DateTime.SpecifyKind(_effectiveFrom!.Value.Date, DateTimeKind.Utc),
            Note = string.IsNullOrWhiteSpace(_note) ? null : _note!.Trim(),
        };

        _isSaving = true;
        try
        {
            var ok = IsEdit
                ? (await Accounts.UpdateTermAsync(Account.AccountId, Term!.TermId, dto))
                    .Toast(Snackbar, "Unable to update term", "Term updated.")
                : (await Accounts.AddTermAsync(Account.AccountId, dto))
                    .Toast(Snackbar, "Unable to create term", "Term created.");

            if (!ok)
                return;

            await OnSaved.InvokeAsync();
            await OpenChanged.InvokeAsync(false);
        }
        finally
        {
            _isSaving = false;
        }
    }

    private static string FractionToPercentString(decimal fraction)
    {
        var p = fraction * 100m;
        return p.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
