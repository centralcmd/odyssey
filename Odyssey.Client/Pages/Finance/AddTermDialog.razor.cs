using System.Globalization;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// New / Edit dialog for a term, on either of the two owners the Term table serves — an account or a
/// contract (issue #135).
/// </summary>
/// <remarks>
/// <b>One dialog, two owners</b>, mirroring the server: <c>TermService</c> runs both owners through a
/// single <c>ApplyAndValidate</c>, with the owner-specific facts supplied as data. A second dialog
/// would be a second copy of the value bounds, the label rule, the cadence pair and the duplicate
/// guard — and two copies of a validator diverge, with the copy that has fewer eyes on it being the
/// one that will. What the owner decides, and all it decides, is: which kinds are eligible, whether
/// an amount may fall back to an owner currency, and which typed client the write goes to.
/// </remarks>
public partial class AddTermDialog
{
    /// <summary>
    /// The owning account. Exactly one of this and <see cref="Contract"/> is supplied — the same
    /// exactly-one-owner invariant the row itself carries.
    /// </summary>
    [Parameter] public ExistingAccount? Account { get; set; }

    /// <summary>The owning contract (issue #135).</summary>
    [Parameter] public ExistingContract? Contract { get; set; }

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

    /// <summary>Whether this dialog is writing against a contract rather than an account.</summary>
    private bool IsContractOwner => Contract is not null;

    /// <summary>The owner in prose, for the wording that names it.</summary>
    private string OwnerNoun => IsContractOwner ? "contract" : "account";

    /// <summary>The owner's display name, for the subtitle.</summary>
    private string OwnerName => Contract?.Name ?? Account?.Name ?? "";

    /// <summary>
    /// The currency an amount term falls back to when the field is left blank, or <c>null</c> when
    /// the owner has none of its own and an explicit answer is therefore required. A contract has no
    /// currency: the two defaulting alternatives — its first account party's currency, or an
    /// instance-wide base currency — both silently assign a meaning nobody chose, and the first
    /// changes retroactively when parties are detached or re-ordered.
    /// </summary>
    private string? OwnerCurrency => Account?.CurrencyCode;

    /// <summary>
    /// Whether the currency is a question rather than a default. True for a contract, and the reason
    /// the money field opens unset there and refuses to submit without an answer.
    /// </summary>
    private bool CurrencyRequired => OwnerCurrency is null;

    /// <summary>Whether the Name field is rendered: a fee takes a label, a rate is refused one.</summary>
    private bool TakesLabel => TermLabel.RuleFor(_kind) == TermLabelRule.Required;

    // ---- direction (issue #159) ---------------------------------------------------------------

    /// <summary>
    /// Whether this dialog offers a direction at all — any term on a CONTRACT. Read from the one shared
    /// predicate, so the control, the read surfaces and the refusal copy cannot disagree.
    /// </summary>
    private bool DirectionApplies => TermKindVisuals.DirectionApplies(_kind, IsContractOwner);

    /// <summary>Why it is refused here, in the words the server's <c>400</c> uses; null when allowed.</summary>
    private string? DirectionRefusal => TermKindVisuals.DirectionRefusal(_kind, IsContractOwner);

    private TermDirectionInfo DirectionInfo => TermDirectionVisuals.Info(_direction);

    /// <summary>The value control's lead — the registry mapped to the field's two-state shape.</summary>
    private static IReadOnlyList<OdsDirectionOption> DirectionLead => TermDirectionVisuals.LeadOptions;

    /// <summary>
    /// The lead's current value, or <c>null</c> where direction does not apply — which is what turns
    /// the lead OFF in the field. The handler is wired unconditionally: with no value the control
    /// never enters direction mode, so a conditional callback would be a second switch for one fact.
    /// </summary>
    private string? DirectionValue => DirectionApplies ? _direction.ToString() : null;

    private void OnDirectionChanged(string value)
    {
        _direction = TermDirectionVisuals.Parse(value);
        // A direction is never the reason a value is invalid, so nothing is re-validated here; the
        // refusals it could trip are decided by the KIND, which has its own handler.
    }

    private const int TermLabelMaxLength = TermLabel.MaxLength;

    private TermKind _kind;
    private string _label = "";
    private TermValueUnit _unit;
    private string _valueStr = "";
    private string _currency = "";
    private string _interval = "";
    private decimal? _intervalCount;
    private DateTime? _anchorDate;
    private DateTime? _effectiveFrom = DateTime.UtcNow.Date;
    private string? _note = "";
    private bool _isSaving;

    /// <summary>
    /// Which way the money moves (issue #159). <see cref="TermDirection.Outgoing"/> is the default
    /// because it is what every term meant before the field existed — an omitted direction and a
    /// chosen <c>Outgoing</c> are the same fact.
    /// </summary>
    private TermDirection _direction = TermDirection.Outgoing;

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
        _eligibleKinds = IsContractOwner
            ? TermKindVisuals.ContractEligibleKinds
            : TermKindVisuals.EligibleKinds(Account!.AccountType);

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
            _currency = Term.CurrencyCode ?? OwnerCurrency ?? "";
            _interval = Term.Interval?.ToString() ?? "";
            _intervalCount = Term.IntervalCount;
            _anchorDate = Term.AnchorDate?.Date;
            _effectiveFrom = Term.EffectiveFrom.Date;
            _direction = Term.Direction;
            _note = Term.Note ?? "";
        }
        else
        {
            // A contract opens on Fee — the overwhelmingly common case, and the one whose extra
            // required field (the name) is worth showing first. An account keeps its registry-order
            // default, where the eligible set is what narrows the choice.
            _kind = IsContractOwner
                ? TermKind.Fee
                : _eligibleKinds.Count > 0 ? _eligibleKinds[0] : TermKind.Fee;
            _unit = TermKindVisuals.Info(_kind).DefaultUnit;
            // Empty on a contract: an unset currency is the honest starting state when nothing can
            // supply one, and it is what makes the answer required rather than silently assigned.
            _currency = OwnerCurrency ?? "";
            _interval = DefaultIntervalFor(_kind);
            _effectiveFrom = DateTime.UtcNow.Date;
        }

        RefreshNameSuggestions();
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

        // Guarantee the owner's own currency stays selectable even if the list failed to load. An
        // unset currency (a contract's starting state) adds no such entry — there is nothing to
        // preserve, and an empty option would read as a currency.
        if (!string.IsNullOrEmpty(_currency) && !_currencyOptions.Any(o => o.Value == _currency))
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
        // Direction survives a kind change on a contract: an arrears rate and a late-payment fee are
        // both money out, so the answer already given still holds. Only an owner that refuses one
        // (an account) drops it, rather than carrying it invisibly into a field the server rejects.
        if (!DirectionApplies)
            _direction = TermDirection.Outgoing;
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
        RefreshNameSuggestions();
    }

    private void OnLabelChanged(string? value)
    {
        _label = value ?? "";
        _errors.Remove("label");
    }

    /// <summary>
    /// The contract name field's suggestions: one row per distinct series of the kind being written,
    /// DERIVED from this contract's own history. There is no term-name table — a name is a free string
    /// and a series exists only because entries share it — and a name from another contract is not a
    /// name this one has used, so nothing else is offered.
    /// </summary>
    /// <remarks>
    /// Each row carries what a reader needs to recognise the series rather than the string: its value
    /// in force and its cadence ("2,250.00 USD · monthly"), read off the latest entry on or before
    /// today. A series whose entries are all future-dated says when it starts and carries a
    /// <c>schedule</c> glyph. The option's value IS its display label, so picking a row writes the
    /// same name the series already carries.
    /// </remarks>
    internal static List<NameSuggestion> NameSuggestions(
        IReadOnlyList<ExistingTerm> existing, TermKind kind, Guid? currentId, DateTime today)
    {
        return existing
            .Where(t => t.TermKind == kind && t.TermId != currentId)
            .Select(t => (Term: t, Key: TermLabel.Key(t.Label)))
            .Where(x => x.Key is not null)
            .GroupBy(x => x.Key!, StringComparer.Ordinal)
            .Select(group =>
            {
                var sorted = group.Select(x => x.Term).OrderBy(t => t.EffectiveFrom).ThenBy(t => t.CreatedAtUtc).ToList();
                // The display form follows the newest entry — a later spelling is the one the user
                // last chose to write.
                var label = TermLabel.Normalize(sorted[^1].Label)!;
                var inForce = sorted.LastOrDefault(t => t.EffectiveFrom.Date <= today.Date);
                var t = inForce ?? sorted[^1];
                var parts = new List<string>(3)
                {
                    t.ValueUnit == TermValueUnit.Percentage
                        ? TermKindVisuals.PctStr(t.Value)
                        : OdsMoney.Format(t.Value, t.CurrencyCode),
                };
                if (TermKindVisuals.CadenceText(t) is { } cadence)
                    parts.Add(cadence);
                if (inForce is null)
                    parts.Add($"from {t.EffectiveFrom:yyyy-MM-dd}");
                return new NameSuggestion(group.Key, label, string.Join(" · ", parts), Scheduled: inForce is null);
            })
            .OrderBy(s => s.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal sealed record NameSuggestion(string Key, string Label, string Note, bool Scheduled);

    private List<NameSuggestion> _nameSuggestions = [];
    private List<OdsOption> _nameOptions = [];

    private IReadOnlyList<OdsOption> NameOptions => _nameOptions;

    private void RefreshNameSuggestions()
    {
        _nameSuggestions = TakesLabel && IsContractOwner
            ? NameSuggestions(Existing, _kind, Term?.TermId, DateTime.UtcNow)
            : [];
        _nameOptions = _nameSuggestions
            .Select(s => new OdsOption(s.Label, s.Label) { Note = s.Note, Icon = s.Scheduled ? "schedule" : null })
            .ToList();
    }

    /// <summary>
    /// The series the typed name would JOIN, resolved on the same normalized, case-folded key the
    /// duplicate guard and the server use — so what the help line promises is what gets written.
    /// </summary>
    private NameSuggestion? MatchedSeries =>
        TermLabel.Key(_label) is { } key ? _nameSuggestions.FirstOrDefault(s => s.Key == key) : null;

    /// <summary>The name field's help line: which of the two writes — join or start — is about to happen.</summary>
    private RenderFragment NameHelp => builder =>
    {
        if (MatchedSeries is { } matched)
        {
            builder.AddMarkupContent(0, "Joins the price history of ");
            builder.OpenElement(1, "b");
            builder.AddContent(2, matched.Label);
            builder.CloseElement();
            builder.AddContent(3, $" — currently {matched.Note}. This entry supersedes it from the effective date.");
        }
        else if (!string.IsNullOrWhiteSpace(_label))
        {
            builder.AddMarkupContent(4, "Starts a <b>new charge</b> on this contract, with its own history separate from the others.");
        }
        else
        {
            builder.AddContent(5, "Pick a charge this updates, or type a new name to start one.");
        }
    };

    // The combobox's "New charge" row writes the typed text as the name — a new series, not a record.
    internal static OdsOption? CreateNameOption(string text, string? _) =>
        string.IsNullOrWhiteSpace(text) ? null : OdsOption.From(text.Trim());

    private void OnUnitChanged(string value)
    {
        if (Enum.TryParse<TermValueUnit>(value, out var unit))
            _unit = unit;
        _errors.Remove("value");
        ClearCurrencyErrorOnUnitSwitch();
    }

    private void OnValueChanged(string value)
    {
        _valueStr = value;
        _errors.Remove("value");
    }

    /// <summary>
    /// A unit switch drops a currency complaint: a percentage carries no currency, so the refusal
    /// that was true a moment ago is no longer about anything on the form.
    /// </summary>
    private void ClearCurrencyErrorOnUnitSwitch() => _errors.Remove("currency");

    private void OnCurrencyChanged(string value)
    {
        _currency = value;
        _errors.Remove("currency");
    }

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

    /// <summary>
    /// What the money field's currency slot reads before an answer is given. "Pick" where the answer
    /// is required and nothing can supply it; otherwise the component's own em-dash placeholder,
    /// which a populated owner currency immediately replaces anyway.
    /// </summary>
    private string CurrencyPlaceholder => CurrencyRequired ? "Pick" : "\u2014";

    /// <summary>
    /// The money field renders ONE error line for the amount and its currency, because they are one
    /// control. Both are joined rather than one winning, so a submit that is wrong in both ways does
    /// not fix half and then re-fail.
    /// </summary>
    private string? MoneyError
    {
        get
        {
            _errors.TryGetValue("value", out var value);
            _errors.TryGetValue("currency", out var currency);
            var joined = string.Join(" ", new[] { value, currency }.Where(m => !string.IsNullOrEmpty(m)));
            return string.IsNullOrEmpty(joined) ? null : joined;
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

        var eligible = IsContractOwner
            ? TermKindVisuals.IsEligibleOnContract(_kind)
            : TermKindVisuals.IsEligible(_kind, Account!.AccountType);
        if (!eligible)
        {
            _errors["kind"] = IsContractOwner
                ? "Not available on a contract."
                : "Not available for this account type.";
        }

        // The contract rule: an amount needs a currency, and nothing supplies one.
        if (!IsPercentage && CurrencyRequired && string.IsNullOrWhiteSpace(_currency))
        {
            _errors["currency"] =
                $"Pick the currency this amount is in — a {OwnerNoun} has no currency of its own.";
        }

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
        {
            _errors["label"] = IsContractOwner
                ? "Name this charge so it keeps its own history."
                : "Name this fee so it keeps its own history.";
        }
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
            // An account term with the same kind, label and date is a DIFFERENT series and never
            // collides with a contract's — which is why the guard runs over `Existing`, the owner's
            // own rows, rather than over every term the client has seen.
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
            // Sent only where it means something. Everywhere else the request carries the default,
            // which is exactly what omitting it would have meant — and what the server stores.
            Direction = DirectionApplies ? _direction : TermDirection.Outgoing,
            EffectiveFrom = DateTime.SpecifyKind(_effectiveFrom!.Value.Date, DateTimeKind.Utc),
            Note = string.IsNullOrWhiteSpace(_note) ? null : _note!.Trim(),
        };

        _isSaving = true;
        try
        {
            // The owner is named by the ROUTE and by nothing else: NewTerm carries no owner id, so a
            // contracts.update holder cannot write a term onto an account through this dialog, and
            // re-parenting a term is a delete plus a create.
            var result = (IsContractOwner, IsEdit) switch
            {
                (true, true) => await Contracts.UpdateTermAsync(Contract!.ContractId, Term!.TermId, dto),
                (true, false) => await Contracts.AddTermAsync(Contract!.ContractId, dto),
                (false, true) => await Accounts.UpdateTermAsync(Account!.AccountId, Term!.TermId, dto),
                (false, false) => await Accounts.AddTermAsync(Account!.AccountId, dto),
            };

            var ok = IsEdit
                ? result.Toast(Snackbar, "Unable to update term", "Term updated.")
                : result.Toast(Snackbar, "Unable to create term", "Term created.");

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
