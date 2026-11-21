using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Client.Components;
using Odyssey.Dtos;

namespace Odyssey.Client.Pages;

public partial class Settings
{
    // ── Draft state — a dictionary of setting-key → draft record per control shape, so adding a
    //    setting is a catalog entry (above) plus one ApplyLoaded line, rather than five new
    //    per-key switch arms (issue #343 — five key-switch methods growing to sixteen arms is the
    //    wrong shape one commit after #399 split the god components). The original Security
    //    settings moved onto the same structure. ──────────────────────────────────────────
    private sealed class BoolState
    {
        public bool Draft;
        public bool Saved;
    }

    private sealed class IntState
    {
        public int? Draft;
        public int? Saved;
    }

    private sealed class CapacityState
    {
        public bool Unlimited;
        // Retained even while Unlimited is true — never cleared by a toggle (issue #343 fe R6), so
        // toggling to "No limit" and back restores the previously entered number.
        public int? Value;
        public bool SavedUnlimited;
        public int? SavedValue;
    }

    private sealed class TextState
    {
        public string Draft = string.Empty;
        public string Saved = string.Empty;
    }

    private sealed class DecimalState
    {
        public decimal? Draft;
        public decimal? Saved;
    }

    private readonly Dictionary<string, BoolState> _bools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IntState> _ints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CapacityState> _capacities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextState> _texts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DecimalState> _decimals = new(StringComparer.Ordinal);

    // Per-field messages the SERVER rejected, keyed by catalogue key — merged into ErrorFor so a 400
    // renders on the offending row rather than only in a toast (issue #421 Wave 0b, frontend B2).
    //
    // Both clearing rules matter: HasErrors disables Save AND Save() early-returns on it, so without
    // them the first server 400 would disable the button permanently, with no way back.
    private readonly Dictionary<string, string> _serverErrors = new(StringComparer.Ordinal);

    private void ClearServerError(string key) => _serverErrors.Remove(key);

    private void SetBoolLoaded(string key, bool value) =>
        _bools[key] = new BoolState { Draft = value, Saved = value };

    private void SetIntLoaded(string key, int value) =>
        _ints[key] = new IntState { Draft = value, Saved = value };

    private void SetCapacityLoaded(string key, int? value) =>
        _capacities[key] = new CapacityState
        {
            Unlimited = value is null,
            Value = value,
            SavedUnlimited = value is null,
            SavedValue = value,
        };

    private bool GetBool(string key) => _bools.TryGetValue(key, out var state) && state.Draft;

    private void SetBool(string key, bool value)
    {
        if (_bools.TryGetValue(key, out var state))
        {
            state.Draft = value;
        }

        ClearServerError(key);
        Touch();
    }

    internal void SetTextLoaded(string key, string value) =>
        _texts[key] = new TextState { Draft = value ?? string.Empty, Saved = value ?? string.Empty };

    internal void SetDecimalLoaded(string key, decimal value) =>
        _decimals[key] = new DecimalState { Draft = value, Saved = value };

    private string GetText(string key) => _texts.TryGetValue(key, out var state) ? state.Draft : string.Empty;

    private void SetText(string key, string? value)
    {
        if (_texts.TryGetValue(key, out var state))
        {
            state.Draft = value ?? string.Empty;
        }

        ClearServerError(key);
        Touch();
    }

    // ── Guidance rows: edge-triggered announcements (issue #434 §6) ─────────────────────────────
    //
    // Debounced 750 ms after the last keystroke, so typing never chatters. The counter speaks when the
    // length RISES past 90 % (the actual count, so a paste reads correctly) and on reaching the limit
    // exactly; above the limit it is silent, because the length error owns that announcement. It re-arms
    // below 85 %. An error is announced by its KIND (length, character, delimiter) when the kind first
    // appears and again only after it has cleared, so a moving count or line number never repeats it.
    private readonly Dictionary<string, LongTextAnnouncerState> _longTextAnnouncers = new(StringComparer.Ordinal);

    private sealed class LongTextAnnouncerState
    {
        public CancellationTokenSource? Pending { get; set; }
        public bool NearArmed { get; set; } = true;
        public bool LimitArmed { get; set; } = true;
        public string? LastErrorKind { get; set; }
    }

    private void SetLongText(SettingItem item, string? value)
    {
        SetText(item.Key, value);

        if (!_longTextAnnouncers.TryGetValue(item.Key, out var state))
        {
            state = new LongTextAnnouncerState();
            _longTextAnnouncers[item.Key] = state;
        }

        state.Pending?.Cancel();
        state.Pending?.Dispose();
        var pending = state.Pending = new CancellationTokenSource();
        _ = AnnounceLongTextAsync(item, state, pending.Token);
    }

    private async Task AnnounceLongTextAsync(SettingItem item, LongTextAnnouncerState state, CancellationToken token)
    {
        try
        {
            await Task.Delay(LongTextAnnounceDelay, token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        var message = LongTextAnnouncement(item, state, GetText(item.Key));
        if (message is not null)
        {
            Announce(message);
            await InvokeAsync(StateHasChanged);
        }
    }

    internal static readonly TimeSpan LongTextAnnounceDelay = TimeSpan.FromMilliseconds(750);

    /// <summary>The announcement a settled guidance value owes, or null. Pure apart from the edge state.</summary>
    private static string? LongTextAnnouncement(SettingItem item, LongTextAnnouncerState state, string text)
    {
        var max = item.Max;
        var length = PromptAdditionRule.NormalisedLength(text);
        var error = item.Rule?.Invoke(text);
        var kind = ErrorKind(error);

        string? message = null;
        if (kind is not null && kind != state.LastErrorKind)
        {
            message = $"{item.Title}: {error}";
        }

        state.LastErrorKind = kind;
        if (message is not null)
        {
            return message;
        }

        if (length < max * 85 / 100)
        {
            state.NearArmed = true;
        }

        if (length < max)
        {
            state.LimitArmed = true;
        }

        if (length == max && state.LimitArmed)
        {
            state.LimitArmed = false;
            state.NearArmed = false;
            return $"{item.Title}: Character limit reached";
        }

        if (length >= max * 90 / 100 && length < max && state.NearArmed)
        {
            state.NearArmed = false;
            return $"{item.Title}: {length} of {max} characters";
        }

        return null;
    }

    // Classified by the shared rule's own message openings; GuidanceAndMatchingSurfaceTests pins them,
    // so a reworded rule fails a test instead of silently re-announcing on every keystroke.
    internal static string? ErrorKind(string? error) => error switch
    {
        null => null,
        _ when error.StartsWith("Guidance can be at most", StringComparison.Ordinal) => "length",
        _ when error.StartsWith("Line ", StringComparison.Ordinal) => "character",
        _ => "delimiter",
    };

    private decimal? GetDecimal(string key) => _decimals.TryGetValue(key, out var state) ? state.Draft : null;

    private void SetDecimal(string key, decimal? value)
    {
        if (_decimals.TryGetValue(key, out var state))
        {
            state.Draft = value;
        }

        ClearServerError(key);
        Touch();
    }

    private int? GetInt(string key) => _ints.TryGetValue(key, out var state) ? state.Draft : null;

    // decimal → int is a CHECKED cast in C# regardless of context, so a bare `(int)value.Value`
    // throws OverflowException the instant a typed number exceeds int range — synchronously, from
    // inside the oninput handler, with no ErrorBoundary anywhere in the client to catch it (issue
    // #403 fe finding). None of this page's Max values exceed 1,000,000, so clamping to the int
    // bounds rather than truncating is safe: an out-of-range value still lands on the wrong side of
    // ErrorFor's Min/Max check and surfaces as the normal "Must be between …" message instead of a
    // crashed page.
    private static int? ClampToInt(decimal? value) => value switch
    {
        null => null,
        > int.MaxValue => int.MaxValue,
        < int.MinValue => int.MinValue,
        { } v => (int)v,
    };

    private void SetInt(string key, decimal? value)
    {
        if (_ints.TryGetValue(key, out var state))
        {
            state.Draft = ClampToInt(value);
        }

        ClearServerError(key);
        Touch();
    }

    // TryGetValue, never an indexer. ErrorFor -> Capacity(key) runs on the RENDER path, and there is
    // no ErrorBoundary anywhere in the client, so a catalogue key whose Load never ran used to blank
    // the whole page with a KeyNotFoundException. The catalogue registry makes that unreachable and
    // a test asserts it; this is the belt to that braces, and it costs nothing.
    private CapacityState Capacity(string key) =>
        _capacities.TryGetValue(key, out var state) ? state : new CapacityState();

    private void SetCapacityValue(string key, decimal? value)
    {
        if (_capacities.TryGetValue(key, out var state))
        {
            state.Value = ClampToInt(value);
        }

        ClearServerError(key);
        Touch();
    }

    private void SetCapacityUnlimited(string key, bool unlimited, string title)
    {
        if (_capacities.TryGetValue(key, out var state))
        {
            // The number is NEVER cleared here (fe R6) — toggling back off restores it.
            state.Unlimited = unlimited;
        }

        Touch();
        Announce($"{title} — {(unlimited ? "no limit" : "limited")}");
    }

    private bool IsDirty(SettingItem item) => item.Control switch
    {
        SettingControl.Action => false,
        SettingControl.Date => _texts.TryGetValue(item.Key, out var date)
            && !string.Equals(date.Draft.Trim(), date.Saved, StringComparison.Ordinal),
        SettingControl.Capacity => _capacities.TryGetValue(item.Key, out var cap) &&
            (cap.Unlimited != cap.SavedUnlimited || (!cap.Unlimited && cap.Value != cap.SavedValue)),
        SettingControl.Toggle => _bools.TryGetValue(item.Key, out var b) && b.Draft != b.Saved,
        SettingControl.Text => _texts.TryGetValue(item.Key, out var s)
            && !string.Equals(s.Draft.Trim(), s.Saved, StringComparison.Ordinal),
        // Compared NORMALISED against the server-returned value: a CRLF or a trailing newline is not a
        // change, and after Save the row re-binds to the normalised text the server stored.
        SettingControl.LongText => _texts.TryGetValue(item.Key, out var lt)
            && !string.Equals(PromptAdditionRule.Normalize(lt.Draft), PromptAdditionRule.Normalize(lt.Saved), StringComparison.Ordinal),
        SettingControl.Decimal or SettingControl.Percent =>
            _decimals.TryGetValue(item.Key, out var d) && d.Draft != d.Saved,
        _ => _ints.TryGetValue(item.Key, out var i) && i.Draft != i.Saved,
    };

    // Client-side validation — only meaningful while the row is actually editable; a
    // permission-disabled row always sends null on save, never a stand-in for "cleared". A
    // capacity row is valid whenever Unlimited is true, regardless of Value (issue #343 fe 3-2):
    // the retained draft number is never itself an error.
    /// <summary>
    /// The effective upper bound for a row: a server-supplied ceiling when the catalogue declares one
    /// (issue #421 Waves 3/4 put the photo caps and the upload cap behind a DTO-carried ceiling),
    /// otherwise the static <see cref="SettingItem.Max"/>.
    ///
    /// <para>
    /// Both the rendered control's bound and this page's own range check resolve through here, so they
    /// cannot disagree. Binding the number control to the static <c>Max</c> instead would go unnoticed
    /// today — the catalogue's literal happens to equal the current compile-time ceiling — and would
    /// silently start refusing valid values the moment that ceiling was raised.
    /// </para>
    ///
    /// <para>
    /// The <c>_dto is null</c> fallback is load-bearing, not defensive: the Save button evaluates
    /// <c>HasErrors</c> inside its own <c>Disabled</c> expression, which renders during the Loading
    /// phase before any DTO exists. Without the fallback a ceiling-bounded row would resolve
    /// <c>Max = 0</c> and report "Must be between 1 and 0" on every page load.
    /// </para>
    /// </summary>
    private int MaxFor(SettingItem item) =>
        item.MaxFrom is { } resolve && _dto is { } dto ? resolve(dto) : item.Max;

    /// <summary>
    /// The effective lower bound for a row — the mirror of <see cref="MaxFor"/>, added for issue #434's
    /// one raise-only key (<c>EmailMaxTrackedRecipients</c>, whose floor is the shipped default because
    /// the mail throttle fails open once its table is full).
    ///
    /// <para>
    /// The <c>_dto is null</c> fallback is load-bearing for the same reason <see cref="MaxFor"/>'s is:
    /// the Save button evaluates <c>HasErrors</c> during the Loading phase, before any DTO exists.
    /// Unlike the ceiling case, the static <c>Min</c> in the catalogue is not a hopeful literal — it
    /// names the same <c>SystemSettingsDefaults</c> constant the server's <c>[Range]</c> does, so the
    /// fallback resolves to exactly the same number.
    /// </para>
    /// </summary>
    private int MinFor(SettingItem item) =>
        item.MinFrom is { } resolve && _dto is { } dto ? resolve(dto) : item.Min;

    private string? ErrorFor(SettingItem item)
    {
        if (!Editable(item.Claim))
        {
            return null;
        }

        // A server rejection outranks the client's own view: it is newer, and it is the one the user
        // just failed to save against.
        if (_serverErrors.TryGetValue(item.Key, out var fromServer))
        {
            return fromServer;
        }

        if (IsTileOnly(item.Control))
        {
            return null;
        }

        // The shared rule owns length, characters and the reserved delimiter, measured on the normalised
        // value — the same pipeline and the same messages as the server's 400.
        if (item.Control == SettingControl.LongText)
        {
            return item.Rule?.Invoke(GetText(item.Key));
        }

        if (item.Control is SettingControl.Text or SettingControl.Date)
        {
            var text = GetText(item.Key).Trim();
            if (text.Length == 0)
            {
                return item.AllowEmpty ? null : "Enter a value";
            }

            if (item.Max > 0 && text.Length > item.Max)
            {
                return $"Must be {item.Max:N0} characters or fewer";
            }
            // Length first, then shape: a 300-character URL should be told it is too long rather than
            // handed the rule's own sentence about schemes and fragments.
            if (item.Rule?.Invoke(text) is { } ruleError)
            {
                return ruleError;
            }
            // A Date row is also bounded above by today (UTC); the server enforces it with its own clock.
            if (item.Control == SettingControl.Date && GetDate(item.Key) is { } date && date.Date > Time.GetUtcNow().UtcDateTime.Date)
            {
                return "Enter a date on or before today.";
            }

            return null;
        }

        if (item.Control is SettingControl.Decimal or SettingControl.Percent)
        {
            var value = GetDecimal(item.Key);
            if (value is null)
            {
                return "Enter a value";
            }

            if (value < item.DecimalMin || value > item.DecimalMax)
            {
                // Phrased in the unit the field actually shows. A percent row displays 1-100, so
                // reporting its stored 0.01-1 bounds would name numbers the control cannot even
                // accept.
                return item.Control == SettingControl.Percent
                    ? $"Must be between {PercentOf(item.DecimalMin)} and {PercentOf(item.DecimalMax)}%"
                    : $"Must be between {item.DecimalMin:0.##} and {item.DecimalMax:0.##}";
            }

            return null;
        }

        if (item.Control == SettingControl.Capacity)
        {
            var cap = Capacity(item.Key);
            if (cap.Unlimited)
            {
                return null;
            }

            if (cap.Value is null)
            {
                return "Enter a value";
            }

            var capMin = MinFor(item);
            var capMax = MaxFor(item);
            if (cap.Value < capMin || cap.Value > capMax)
            {
                return $"Must be between {capMin:N0} and {capMax:N0}";
            }

            return null;
        }

        if (item.Control is SettingControl.Number or SettingControl.Size)
        {
            var value = GetInt(item.Key);
            if (value is null)
            {
                return "Enter a value";
            }

            var numMin = MinFor(item);
            var numMax = MaxFor(item);
            if (value < numMin || value > numMax)
            {
                return $"Must be between {numMin:N0} and {numMax:N0}";
            }

            return null;
        }

        return null;
    }

    // Effective count for a capacity draft — null represents "no limit" (+∞), matching the
    // server-side convention (issue #343 §9).
    private int? EffectiveCount(string key) => Capacity(key) is { Unlimited: true } ? null : Capacity(key).Value;

    // Round-trip: the export cap must not exceed the import cap (unlimited = +∞ on both sides).
    // Rendered as a GROUP-level alert, never on the export row (fe A5) — in the documented flow
    // the export row is disabled (unlimited is on), so an error placed there would be unfocusable.
    private string? RoundTripError(SettingSection section)
    {
        if (section.RoundTrip is not { } rt)
        {
            return null;
        }

        var export = EffectiveCount(rt.ExportKey);
        var import = EffectiveCount(rt.ImportKey);
        if (import is null) // unlimited import never violates, regardless of export
        {
            return null;
        }

        if (export is not null && export <= import)
        {
            return null;
        }

        var exportLabel = export is null ? "no limit" : export.Value.ToString("N0");
        var importLabel = import.Value.ToString("N0");
        return $"Export limit ({exportLabel}) must not exceed the import limit ({importLabel}), or an exported file could not be imported back.";
    }

    // Whether a rendered group mixes editable rows with claim-locked rows — the
    // partially-privileged admin who'd otherwise see greyed controls with no explanation (the
    // page-level read-only note below only covers the holds-NEITHER case) (issue #343 fe R8).
    private bool GroupPartial(SettingSection section)
    {
        if (!CanSave)
        {
            return false;
        }

        var claims = section.Items.Where(i => i.Claim != SettingClaim.None).ToList();
        return claims.Any(i => Editable(i.Claim)) && claims.Any(i => !Editable(i.Claim));
    }

    // Save is disabled while any CLIENT-DETECTABLE error is present, evaluated over rendered
    // (visible/search-filtered) sections only — field-level errors AND group round-trip conflicts.
    // Today's original page spanned all sections while only VisibleSections render, so an active
    // search could hide the row responsible for a disabled Save button (issue #343 fe B3).
    private bool HasErrors =>
        VisibleSections.Any(s => s.Items.Any(i => ErrorFor(i) is not null) || RoundTripError(s) is not null);

    private RenderFragment RenderControl(SettingItem item) => item.Control switch
    {
        SettingControl.Number => NumberControl(item),
        SettingControl.Size => SizeControl(item),
        SettingControl.Capacity => CapacityControl(item),
        SettingControl.Text => item.OptionsFrom is null ? TextControl(item) : SelectControl(item),
        SettingControl.Decimal => DecimalControl(item),
        SettingControl.Percent => PercentControl(item),
        SettingControl.Date => DateControl(item),
        SettingControl.Action => ActionControl(item),
        SettingControl.LongText => LongTextControl(item),
        _ => ToggleControl(item),
    };

    // A Text row chosen from a served list (issue #348). The frame owns the label and help, as for every
    // other control, so the select is handed neither. Built here rather than in the markup's @code block,
    // which is at its size limit.
    private RenderFragment SelectControl(SettingItem item) => builder =>
    {
        builder.OpenComponent<OdsSelect>(0);
        builder.AddComponentParameter(1, nameof(OdsSelect.Value), GetText(item.Key));
        builder.AddComponentParameter(2, nameof(OdsSelect.ValueChanged),
            EventCallback.Factory.Create<string>(this, v => SetText(item.Key, v)));
        builder.AddComponentParameter(3, nameof(OdsSelect.Options), item.OptionsFrom!(this));
        builder.AddComponentParameter(4, nameof(OdsSelect.Disabled), IsRowDisabled(item));
        builder.AddComponentParameter(5, nameof(OdsSelect.Error), FieldStateFor(item));
        // Named and described through aria-*, which MudSelect forwards to its visible combobox. An `id`
        // would land on its hidden input, leaving the frame's <label for> pointing at nothing focusable —
        // so a select row takes no `for` (HasFocusableControl).
        builder.AddAttribute(6, "aria-labelledby", TitleId(item.Key));
        builder.AddAttribute(7, "aria-describedby", DescribedBy(item));
        builder.CloseComponent();
    };

    // Administrator guidance (issue #434). Built here rather than in the markup's @code block, which is
    // at its size limit. READ ONLY, never disabled, for a viewer without the claim: the guidance in force
    // stays focusable, scrollable and copyable. The frame owns the label (its <label for>), help, error and
    // advisory; OdsNoteField owns the counter header and appends its sr-only twin after the row's ids.
    private RenderFragment LongTextControl(SettingItem item) => builder =>
    {
        builder.OpenComponent<OdsNoteField>(0);
        builder.AddComponentParameter(1, nameof(OdsNoteField.Id), ControlId(item.Key));
        builder.AddComponentParameter(2, nameof(OdsNoteField.Value), GetText(item.Key));
        builder.AddComponentParameter(3, nameof(OdsNoteField.ValueChanged),
            EventCallback.Factory.Create<string>(this, v => SetLongText(item, v)));
        builder.AddComponentParameter(4, nameof(OdsNoteField.Rows), 6);
        builder.AddComponentParameter(5, nameof(OdsNoteField.CounterMax), item.Max);
        builder.AddComponentParameter(6, nameof(OdsNoteField.CountLength), (Func<string, int>)PromptAdditionRule.NormalisedLength);
        builder.AddComponentParameter(7, nameof(OdsNoteField.Placeholder), item.Placeholder);
        builder.AddComponentParameter(8, nameof(OdsNoteField.ReadOnly), IsRowDisabled(item));
        builder.AddComponentParameter(9, nameof(OdsNoteField.AriaDescribedBy), DescribedBy(item));
        builder.AddComponentParameter(10, nameof(OdsNoteField.Spellcheck), "false");
        builder.AddComponentParameter(11, nameof(OdsNoteField.AutoComplete), "off");
        builder.AddComponentParameter(12, nameof(OdsNoteField.AutoCapitalize), "off");
        // aria-invalid without a message on the control: the frame renders the message once, and the
        // control reaches it through ErrorId in DescribedBy.
        builder.AddAttribute(13, "aria-invalid", ErrorFor(item) is null ? null : "true");
        builder.CloseComponent();
    };

    // ── The ids an OdsSettingField and its control share ──────────────────────────────────────────
    //
    // Four elements per row, each addressable: the label (also the jump target for both the problems
    // rollup and the fault summary), the control, the helper line and the error. Stated here once so
    // the field and the control cannot disagree about what describes what.
    private static string TitleId(string key) => $"ss-ttl-{key}";

    private static string ControlId(string key) => $"ss-in-{key}";

    private static string DescId(string key) => $"ss-desc-{key}";

    private static string ErrorId(string key) => $"ss-err-{key}";

    /// <summary>
    /// Whether the field's legend can be a real <c>&lt;label for&gt;</c>. A capacity row flips between
    /// a number input and static "No limit" text, so there is no id that is reliably focusable — it
    /// names itself through <c>aria-labelledby</c> on the title instead.
    /// </summary>
    private static bool HasFocusableControl(SettingItem item) =>
        item.Control is not (SettingControl.Capacity or SettingControl.Toggle or SettingControl.Action)
        && item.OptionsFrom is null;

    /// <summary>
    /// The always-visible helper line: what the setting does, then the bound that qualifies it, then
    /// the permission that is missing. Never behind a disclosure — at a full page of settings a "?"
    /// per row is a page of buttons nobody presses.
    /// </summary>
    private string HelpLine(SettingItem item)
    {
        // The currencies row says how many are synced — a server count, read from the sync status.
        var description = item.Key == ExchangeRateCurrenciesKey && _syncStatus is { } sync
            ? $"{sync.EnabledCount} currencies synced"
            : item.Description;
        // The guidance rows name the processor the text travels to, read from the saved setting so the
        // help line and the consent gate cannot name two different recipients.
        description = description.Replace(ProcessorToken,
            string.IsNullOrWhiteSpace(_dto?.FileAnalysisProcessor) ? "the analysis provider" : _dto!.FileAnalysisProcessor,
            StringComparison.Ordinal);
        // A row bounded by a server-published ceiling names the EFFECTIVE maximum, never a literal that
        // would go stale the moment the deployment's file-size limit changes (issue #473 §5.4).
        description = description.Replace(MaxToken, MaxFor(item).ToString("N0", System.Globalization.CultureInfo.CurrentCulture), StringComparison.Ordinal);
        var parts = new List<string>(3) { description };

        // The marker in the outline says WHICH WAY; the range belongs here, where there is room for
        // it. Resolved through MinFor/MaxFor so a server-published bound is the one quoted.
        if (item.LowerOnly)
        {
            parts.Add($"Can be lowered but not raised: {MinFor(item):N0}–{MaxFor(item):N0}.");
        }
        else if (item.MinFrom is not null)
        {
            parts.Add($"Can be raised but not lowered: {MinFor(item):N0}–{MaxFor(item):N0}.");
        }

        if (!Editable(item.Claim))
        {
            parts.Add(item.Control == SettingControl.LongText
                ? "Read only."
                : "Changing this needs an additional permission.");
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// The direction marker in the field's outline. A tighten-only row may only be lowered; a published
    /// floor means it may only be raised. Both are refusals, not discouragements, which is what earns
    /// the marker rather than a sentence.
    /// </summary>
    private static OdsSettingBound BoundFor(SettingItem item) =>
        item.LowerOnly ? OdsSettingBound.LowerOnly
        : item.MinFrom is not null ? OdsSettingBound.RaiseOnly
        : OdsSettingBound.None;

    /// <summary>
    /// What is handed to the CONTROL as its error, as opposed to what the field renders.
    ///
    /// <para>
    /// Inside an <c>OdsSettingField</c> frame the sheet hides the control's own help/error node — the
    /// field owns the message, so it renders once, below the frame. The control still needs to know it
    /// is invalid, for <c>aria-invalid</c> and the error tint on its unit adornment, so it is handed a
    /// non-empty placeholder rather than the text (which would render twice or not at all).
    /// </para>
    /// </summary>
    private string? FieldStateFor(SettingItem item) => ErrorFor(item) is null ? null : " ";

    // ── Percent rows ──────────────────────────────────────────────────────────────────────────────
    //
    // Stored as a 0.0-1.0 fraction, entered as a whole percent. decimal division is exact for these
    // values (62m / 100m == 0.62m), so the round trip is lossless and a percent row is dirty on the
    // same terms as any other decimal row.
    private static int PercentOf(decimal fraction) => (int)Math.Round(fraction * 100m);

    private decimal? GetPercent(string key) =>
        GetDecimal(key) is { } fraction ? Math.Round(fraction * 100m) : null;

    private void SetPercent(string key, decimal? percent) =>
        SetDecimal(key, percent is null ? null : percent.Value / 100m);
}
