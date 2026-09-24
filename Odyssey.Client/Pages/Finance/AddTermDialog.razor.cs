using System.Globalization;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// New / Edit dialog for a term on a contract — the only owner a term has since issue #190. The value
/// bounds, label rule, cadence pair and duplicate guard mirror <c>TermService.ApplyAndValidate</c>.
/// </summary>
public partial class AddTermDialog
{
    /// <summary>The owning contract.</summary>
    [Parameter] public ExistingContract? Contract { get; set; }

    /// <summary>The term being edited, or <c>null</c> to create a new one.</summary>
    [Parameter] public ExistingTerm? Term { get; set; }

    /// <summary>The owner's existing terms — for the client-side (label, effectiveFrom) duplicate
    /// guard.</summary>
    [Parameter] public IReadOnlyList<ExistingTerm> Existing { get; set; } = [];

    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>Raised after a successful create/update so the host can reload.</summary>
    [Parameter] public EventCallback OnSaved { get; set; }

    private bool IsEdit => Term is not null;
    private bool IsPercentage => _unit == TermValueUnit.Percentage;
    private bool IsText => _unit == TermValueUnit.Text;
    private bool IsDateTime => _unit == TermValueUnit.DateTime;

    /// <summary>Whether the selected kind carries a number — and so a direction, currency and cadence.</summary>
    private bool IsNumeric => TermVisuals.IsNumeric(_unit);

    /// <summary>The owner's display name, for the subtitle.</summary>
    private string OwnerName => Contract?.Name ?? "";

    // ---- direction (issue #159) ---------------------------------------------------------------

    private TermDirectionInfo DirectionInfo => TermDirectionVisuals.Info(_direction);

    /// <summary>The value control's lead — the registry mapped to the field's two-state shape.</summary>
    private static IReadOnlyList<OdsDirectionOption> DirectionLead => TermDirectionVisuals.LeadOptions;

    /// <summary>
    /// The lead's current value — every term states a direction.
    /// </summary>
    private string DirectionValue => _direction.ToString();

    private void OnDirectionChanged(string value)
    {
        _direction = TermDirectionVisuals.Parse(value);
        // A direction is never the reason a value is invalid, so nothing is re-validated here.
    }

    private const int TermLabelMaxLength = TermLabel.MaxLength;

    private string _label = "";
    private TermValueUnit _unit = TermValueUnit.Amount;
    private string _valueStr = "";
    private string _currency = "";
    private string _interval = "";
    private decimal? _intervalCount;
    private DateTime? _anchorDate;
    private DateTime? _effectiveFrom = DateTime.UtcNow.Date;
    private string? _note = "";
    private bool _isSaving;

    // The two non-numeric kinds (issue #192). The date-time is edited as a LOCAL date and time of day
    // in the viewer's zone and converted to a UTC instant only on submit.
    private string _textValue = "";
    private DateTime? _dtDate;
    private TimeSpan? _dtTime;

    /// <summary>The zone the date-time pickers read in. The browser's, in WASM.</summary>
    private static TimeZoneInfo LocalZone => TimeZoneInfo.Local;

    /// <summary>
    /// Which way the money moves (issue #159). <see cref="TermDirection.Outgoing"/> is the default
    /// because it is what every term meant before the field existed — an omitted direction and a
    /// chosen <c>Outgoing</c> are the same fact.
    /// </summary>
    private TermDirection _direction = TermDirection.Outgoing;

    /// <summary>The kind picker — the four units, each with its registry glyph and label.</summary>
    private static readonly IReadOnlyList<OdsSegmentedOption> _unitOptions =
        [.. TermVisuals.AllUnits.Select(u => new OdsSegmentedOption
        {
            Value = u.ToString(),
            Label = TermVisuals.UnitInfo(u).Label,
            Icon = TermVisuals.UnitInfo(u).Icon,
        })];

    private List<OdsOption> _currencyOptions = [];
    private List<OdsOption> _intervalOptions = [];
    private readonly Dictionary<string, string> _errors = new();

    protected override void OnInitialized()
    {
        // Reading order, not ordinal order: the ordinals are deliberately out of sequence
        // (PerUnit is 6, Weekly is 7, and 4 stays retired), so the registry decides the order.
        _intervalOptions =
        [
            new OdsOption("", "Not specified"),
            .. TermVisuals.AllIntervals.Select(i => new OdsOption(i.ToString(), TermVisuals.InfoFor(i)!.Label)),
        ];

        if (Term is not null)
        {
            _label = Term.Label ?? "";
            _unit = Term.ValueUnit;
            _valueStr = Term.Value is not { } number
                ? ""
                : Term.ValueUnit == TermValueUnit.Percentage
                    ? FractionToPercentString(number)
                    : number.ToString(CultureInfo.InvariantCulture);
            _textValue = Term.TextValue ?? "";
            if (Term.DateTimeValue is { } instant)
            {
                var local = TermVisuals.ToLocal(instant, LocalZone);
                _dtDate = local.Date;
                _dtTime = new TimeSpan(local.Hour, local.Minute, 0);
            }
            _currency = Term.CurrencyCode ?? "";
            // A fact kind has no cadence; reopening one as a price starts from the default one.
            _interval = Term.Interval?.ToString() ?? (TermVisuals.IsNumeric(Term.ValueUnit) ? "" : TermVisuals.DefaultInterval.ToString());
            _intervalCount = Term.IntervalCount;
            _anchorDate = Term.AnchorDate?.Date;
            _effectiveFrom = Term.EffectiveFrom.Date;
            _direction = Term.Direction;
            _note = Term.Note ?? "";
        }
        else
        {
            _unit = TermValueUnit.Amount;
            // Empty on a contract: an unset currency is the honest starting state when nothing can
            // supply one, and it is what makes the answer required rather than silently assigned.
            _currency = "";
            _interval = TermVisuals.DefaultInterval.ToString();
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

    /// <summary>The selected cadence unit, or <c>null</c> when it is left unspecified.</summary>
    private Interval? SelectedInterval =>
        Enum.TryParse<Interval>(_interval, out var interval) ? interval : null;

    /// <summary>
    /// The one condition the cadence fields hang off. A count is offered — and written — only for a
    /// periodic unit, so for anything else the field is ABSENT rather than disabled: the answer is
    /// not merely unknown there, it has no meaning, and the request carries null.
    /// </summary>
    private bool IsPeriodicInterval => TermVisuals.IsPeriodic(SelectedInterval);

    /// <summary>The plural unit noun the count reads in ("every 3 <b>months</b>").</summary>
    private string IntervalUnitNoun => TermVisuals.InfoFor(SelectedInterval)?.Many ?? "";

    /// <summary>The cadence in words, as the request would store it — blank counts as the identity
    /// cadence, which is exactly what the service writes.</summary>
    private string? CadenceEcho =>
        TermVisuals.CadenceText(SelectedInterval, EffectiveIntervalCount);

    private string? IntervalCountHelp =>
        TermVisuals.InfoFor(SelectedInterval) is { } info ? $"Leave blank for {info.Adverb}" : null;

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

            var applies = $"Applies {TermVisuals.InfoFor(interval)!.Adverb}";

            return interval == Interval.PerUnit
                ? $"{applies} — name the unit in the term\u2019s name, e.g. \u201cCustody \u00b7 per share\u201d"
                : applies;
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

    private void OnLabelChanged(string? value)
    {
        _label = value ?? "";
        _errors.Remove("label");
    }

    /// <summary>
    /// The contract name field's suggestions: one row per distinct series,
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
        IReadOnlyList<ExistingTerm> existing, Guid? currentId, DateTime today)
    {
        return existing
            .Where(t => t.TermId != currentId)
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
                var parts = new List<string>(3) { SuggestionValue(t) };
                if (TermVisuals.CadenceText(t) is { } cadence)
                    parts.Add(cadence);
                if (inForce is null)
                    parts.Add($"from {t.EffectiveFrom:yyyy-MM-dd}");
                return new NameSuggestion(group.Key, label, string.Join(" · ", parts), Scheduled: inForce is null);
            })
            .OrderBy(s => s.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal sealed record NameSuggestion(string Key, string Label, string Note, bool Scheduled);

    /// <summary>The longest stretch of a Text value a name suggestion quotes before it ellipsizes.</summary>
    private const int SuggestionTextLength = 36;

    /// <summary>
    /// A series' in-force value as its suggestion row states it: the figure, a date-time in the
    /// viewer's zone, or a Text value quoted — and cut short, since a row is one line.
    /// </summary>
    private static string SuggestionValue(ExistingTerm t)
    {
        if (t.ValueUnit != TermValueUnit.Text)
            return TermVisuals.FormatValue(t, (v, c) => OdsMoney.Format(v, c));

        var text = t.TextValue ?? "";
        return text.Length > SuggestionTextLength
            ? $"\u201C{text[..(SuggestionTextLength - 1)]}\u2026\u201D"
            : $"\u201C{text}\u201D";
    }

    private List<NameSuggestion> _nameSuggestions = [];
    private List<OdsOption> _nameOptions = [];

    private IReadOnlyList<OdsOption> NameOptions => _nameOptions;

    private void RefreshNameSuggestions()
    {
        _nameSuggestions = NameSuggestions(Existing, Term?.TermId, DateTime.UtcNow);
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
            builder.AddMarkupContent(0, "Joins the history of ");
            builder.OpenElement(1, "b");
            builder.AddContent(2, matched.Label);
            builder.CloseElement();
            builder.AddContent(3, $" — currently {matched.Note}. This entry supersedes it from the effective date.");
        }
        else if (!string.IsNullOrWhiteSpace(_label))
        {
            builder.AddMarkupContent(4, "Starts a <b>new term</b> on this contract, with its own history separate from the others.");
        }
        else
        {
            builder.AddContent(5, "Pick a term this updates, or type a new name to start one.");
        }
    };

    // The combobox's "New term" row writes the typed text as the name — a new series, not a record.
    internal static OdsOption? CreateNameOption(string text, string? _) =>
        string.IsNullOrWhiteSpace(text) ? null : OdsOption.From(text.Trim());

    private void OnUnitChanged(string value)
    {
        if (Enum.TryParse<TermValueUnit>(value, out var unit) && Enum.IsDefined(unit))
            _unit = unit;
        _errors.Remove("value");
        _errors.Remove("textValue");
        _errors.Remove("dateTimeValue");
        _errors.Remove("intervalCount");
        ClearCurrencyErrorOnUnitSwitch();
    }

    /// <summary>
    /// On edit, what a kind change does to the entry — stated because the fields that do not apply to
    /// the new kind are dropped from the request rather than kept: the server refuses them.
    /// </summary>
    private string? KindSwitchNote
    {
        get
        {
            if (Term is null || Term.ValueUnit == _unit)
                return null;

            var from = TermVisuals.UnitInfo(Term.ValueUnit).Label.ToLowerInvariant();
            var to = TermVisuals.UnitInfo(_unit).Label.ToLowerInvariant();
            var removed = TermVisuals.IsNumeric(Term.ValueUnit) && !IsNumeric
                ? " Its value, direction, currency and cadence are removed."
                : "";
            return $"Changes this entry from {from} to {to}.{removed} The name keeps its history.";
        }
    }

    private void OnTextChanged(ChangeEventArgs e)
    {
        _textValue = e.Value?.ToString() ?? "";
        _errors.Remove("textValue");
    }

    /// <summary>The Text value's length as the server counts it — after the trim.</summary>
    private int TrimmedTextLength => _textValue.Trim().Length;

    /// <summary>
    /// The Text field's error: a submit-time refusal, or — live, as the value is typed — a forbidden
    /// character, since that one is invisible and would otherwise surface only on submit. The message
    /// names the rule and never repeats the text.
    /// </summary>
    private string? TextError =>
        _errors.TryGetValue("textValue", out var error)
            ? error
            : _textValue.Length > 0 && TermTextValue.HasForbiddenCharacter(_textValue.Trim())
                ? TextControlMessage
                : null;

    private const string TextControlMessage = "Remove tabs, line breaks and hidden direction marks.";

    private void OnDateTimeDateChanged(DateTime? date)
    {
        _dtDate = date;
        _errors.Remove("dateTimeValue");
    }

    private void OnDateTimeTimeChanged(TimeSpan? time)
    {
        _dtTime = time;
        _errors.Remove("dateTimeValue");
    }

    /// <summary>The picked local date and time as the UTC instant the request carries, or null.</summary>
    private DateTime? DateTimeUtc => TermVisuals.LocalToUtc(_dtDate, _dtTime, LocalZone);

    /// <summary>The viewer's zone, as the date-time help line names it.</summary>
    private static string LocalZoneName =>
        string.IsNullOrWhiteSpace(LocalZone.Id) ? "Local time" : LocalZone.Id;

    /// <summary>The offset in force at the picked instant (or now), so a summer date reads its summer offset.</summary>
    private string LocalOffsetLabel =>
        TermVisuals.OffsetLabel(LocalZone.GetUtcOffset(DateTimeUtc ?? DateTime.UtcNow));

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
    /// What the money field's currency slot reads before an answer is given: the answer is required
    /// and nothing can supply it, since a contract has no currency of its own.
    /// </summary>
    private const string CurrencyPlaceholder = "Pick";

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

        var raw = ParseValue();
        string? textValue = null;
        DateTime? dateTimeValue = null;
        if (IsNumeric)
        {
            // The contract rule: an amount needs a currency, and nothing supplies one.
            if (!IsPercentage && string.IsNullOrWhiteSpace(_currency))
            {
                _errors["currency"] =
                    "Pick the currency this amount is in — a contract has no currency of its own.";
            }

            if (raw is null)
            {
                _errors["value"] = "Enter a value.";
            }
            else if (IsPercentage)
            {
                if (raw < -100m || raw > 100m)
                    _errors["value"] = "Must be between −100% and 100%.";
            }
            else if (raw < 0m)
            {
                _errors["value"] = "An amount can’t be negative.";
            }
        }
        else if (IsText)
        {
            // The SAME rule, in the same order, the server applies — the shared TermTextValue delegate.
            textValue = TermTextValue.Normalize(_textValue);
            if (textValue is null)
                _errors["textValue"] = "Enter the text this term records.";
            else if (textValue.Length > TermTextValue.MaxLength)
                _errors["textValue"] = $"Keep it to {TermTextValue.MaxLength} characters.";
            else if (TermTextValue.HasForbiddenCharacter(textValue))
                _errors["textValue"] = TextControlMessage;
        }
        else if (IsDateTime)
        {
            dateTimeValue = DateTimeUtc;
            if (dateTimeValue is null)
                _errors["dateTimeValue"] = "Pick both a date and a time.";
            else if (!TermDateTimeValue.IsInRange(dateTimeValue.Value))
                _errors["dateTimeValue"] = "Pick a date between 1900 and 2200.";
        }

        if (_effectiveFrom is null)
            _errors["effectiveFrom"] = "Pick the date this takes effect.";

        // The same bound the DTO's [Range] carries and the service re-checks — named from the one
        // constant pair, so the message cannot quote a number the server would not enforce.
        if (IsNumeric && IsPeriodicInterval && _intervalCount is { } count
            && (count != Math.Truncate(count) || count < TermIntervalCount.Min || count > TermIntervalCount.Max))
        {
            _errors["intervalCount"] =
                $"Enter a whole number between {TermIntervalCount.Min} and {TermIntervalCount.Max}.";
        }

        // No ordering is imposed between the anchor and the effective date, in either direction:
        // billed in arrears and prepaid are both legitimate records, and the server rejects neither.

        if ((_note?.Length ?? 0) > 512)
            _errors["note"] = "Keep the note under 512 characters.";

        // Label — required on every term.
        var label = TermLabel.Normalize(_label);
        if (label is null)
        {
            _errors["label"] = "Name this term so it keeps its own history.";
        }
        else if (label is { Length: > TermLabel.MaxLength })
            _errors["label"] = $"Keep the name under {TermLabel.MaxLength} characters.";

        // Duplicate (label, effectiveFrom) → the server's 409, excluding the row being edited.
        // Compared on the SAME normalized, case-folded key the server writes, so "ATM abroad" and
        // "  atm   Abroad " collide here exactly as they would there.
        var labelKey = TermLabel.Key(label);
        if (label is not null && _effectiveFrom is { } date && Existing.Any(t =>
                t.TermId != (Term?.TermId ?? Guid.Empty)
                && TermLabel.Key(t.Label) == labelKey
                && t.EffectiveFrom.Date == date.Date))
        {
            _errors["effectiveFrom"] = $"“{label}” already has an entry on that date.";
            // Another contract's term with the same label and date is a DIFFERENT series and never
            // collides — which is why the guard runs over `Existing`, this contract's own rows,
            // rather than over every term the client has seen.
        }

        if (_errors.Count > 0)
            return;

        decimal? value = !IsNumeric
            ? null
            : IsPercentage
                ? Math.Round(raw!.Value / 100m, 6)
                : Math.Round(raw!.Value, 2);

        // Exactly one value field is set, and every field that does not apply to the kind goes out as
        // null (direction as Outgoing) — never left over from a kind the entry used to be. The server
        // refuses each of them on a Text or DateTime term rather than clearing it.
        var dto = new NewTerm
        {
            // LabelKey is derived server-side and is on no request DTO — only Label is sent.
            Label = label,
            ValueUnit = _unit,
            Value = value,
            TextValue = textValue,
            // Always a UTC instant — the server refuses a date-time with no offset.
            DateTimeValue = dateTimeValue,
            CurrencyCode = IsNumeric && !IsPercentage ? _currency : null,
            Interval = IsNumeric ? SelectedInterval : null,
            // The identity cadence when a periodic unit is left blank, and null — never a
            // meaningless 1 — in every other case, matching what the service persists.
            IntervalCount = IsNumeric && IsPeriodicInterval ? EffectiveIntervalCount : null,
            AnchorDate = !IsNumeric || _anchorDate is null
                ? null
                : DateTime.SpecifyKind(_anchorDate.Value.Date, DateTimeKind.Utc),
            Direction = IsNumeric ? _direction : TermDirection.Outgoing,
            EffectiveFrom = DateTime.SpecifyKind(_effectiveFrom!.Value.Date, DateTimeKind.Utc),
            Note = string.IsNullOrWhiteSpace(_note) ? null : _note!.Trim(),
        };

        _isSaving = true;
        try
        {
            // The owner is named by the ROUTE and by nothing else: NewTerm carries no owner id, so
            // re-parenting a term is a delete plus a create.
            var result = IsEdit
                ? await Contracts.UpdateTermAsync(Contract!.ContractId, Term!.TermId, dto)
                : await Contracts.AddTermAsync(Contract!.ContractId, dto);

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
