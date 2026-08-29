using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Odyssey.ApiClient;
using Odyssey.Dtos.Application;

namespace Odyssey.Client.Components;

/// <summary>
/// The behaviour half of <c>OdsSecretSettingField</c> (issue #444 §3, reshaped onto the design
/// system's SettingField vocabulary for issue #445).
/// </summary>
public partial class OdsSecretSettingField : IDisposable
{
    /// <summary>The registry key this row edits. Sent as the route segment by the caller's delegates.</summary>
    [Parameter, EditorRequired] public string SecretKey { get; set; } = string.Empty;

    /// <summary>Human name of the credential. Authored client-side — the status DTO carries no title.</summary>
    [Parameter, EditorRequired] public string Title { get; set; } = string.Empty;

    [Parameter] public string? Description { get; set; }

    /// <summary>The server-reported status of the stored row.</summary>
    [Parameter] public SecretSettingState State { get; set; } = SecretSettingState.NotSet;

    [Parameter] public DateTime? UpdatedAt { get; set; }

    [Parameter] public string? UpdatedByDisplayName { get; set; }

    /// <summary>
    /// Whether losing this credential is unrecoverable — the client-side half of the server
    /// descriptor's <c>Kind</c>. It is load-bearing in exactly one place: the Clear confirmation,
    /// which must say that a derivation key's prior data becomes permanently un-re-derivable.
    /// </summary>
    [Parameter] public bool IsDerivationKey { get; set; }

    /// <summary>
    /// What is not working while this credential is <see cref="SecretSettingState.NotSet"/> — rendered
    /// in the row's amber advisory band (issue #445).
    ///
    /// <para>
    /// Not decoration. Every one of these rows starts unset after the upgrade that introduces it,
    /// because a secret is deliberately never adopted from configuration — so the gap is a designed
    /// state, not an edge case, and its cost has to be legible on the row rather than discovered when
    /// mail stops. It goes in the ADVISORY channel and nowhere else: an absent row is healthy, and the
    /// error channel belongs to <see cref="SecretSettingState.Unreadable"/>.
    /// </para>
    /// </summary>
    [Parameter] public string? Consequence { get; set; }

    /// <summary>
    /// What is broken <em>right now</em> when the row is <see cref="SecretSettingState.Unreadable"/> —
    /// appended to the status line. The key's name does not say what fails when it cannot be read, and
    /// an administrator cannot infer it.
    /// </summary>
    [Parameter] public string? Affects { get; set; }

    /// <summary>
    /// Opts this row out of the client-side printable-ASCII check, for a descriptor that has taken the
    /// server-side relaxation. Default <see langword="false"/>, and no row sets it today: the check is
    /// a mirror of the store's rule, so relaxing it here without relaxing it there would replace a
    /// specific message with a bare <c>400</c>.
    /// </summary>
    [Parameter] public bool AllowNonAscii { get; set; }

    /// <summary>Read-only rendering — the caller lacks the write claim, or the page is busy.</summary>
    [Parameter] public bool Disabled { get; set; }

    /// <summary>Extra classes on the field wrapper, so a host page can restyle the block.</summary>
    [Parameter] public string? Class { get; set; }

    /// <summary>
    /// Commits a value. A delegate rather than an injected client so the row owns error <em>resolution</em>
    /// while the page owns the call — and so a test can drive every status code through it.
    /// </summary>
    [Parameter] public Func<string, Task<ApiResult>>? OnSave { get; set; }

    /// <summary>Clears the stored value.</summary>
    [Parameter] public Func<Task<ApiResult>>? OnClear { get; set; }

    /// <summary>Raised after a successful write, so the page can refresh the statuses.</summary>
    [Parameter] public EventCallback OnChanged { get; set; }

    /// <summary>
    /// Live-region text. An <see cref="EventCallback{T}"/> rather than a reach into the page: the
    /// settings page's <c>Announce()</c> is <c>private</c> and its <c>OdsLiveAnnouncer</c> is hosted
    /// once by the page's own markup, so a component in <c>Components/</c> can reach neither.
    /// </summary>
    [Parameter] public EventCallback<string> OnAnnounce { get; set; }

    /// <summary>
    /// A caller-supplied id for the field's LABEL, so the page's credential signal can move focus to
    /// the field it names. The label carries <c>tabindex="-1"</c> for exactly that reason — the same
    /// contract <c>OdsSettingField.LabelId</c> offers. Without it the id is generated per component
    /// instance and the page has nothing to aim at.
    /// </summary>
    [Parameter] public string? AnchorId { get; set; }

    /// <summary>Optional id on the field wrapper, forwarded so a host page can target the block.</summary>
    [Parameter] public string? Id { get; set; }

    /// <summary>Native maxlength, mirroring the server's compile-time cap.</summary>
    [Parameter] public int MaxLength { get; set; } = SecretSettingKeys.MaxPlaintextLength;

    /// <summary>Axis 2 — the row's local interaction mode, valid over ANY server status.</summary>
    private enum Mode { Display, Editing, Saving, Clearing }

    private Mode _mode = Mode.Display;

    // The in-progress credential. A component-local field and nothing else: it is never written to
    // page state, never round-tripped through the API, and cleared on save, cancel and dispose.
    private string _value = string.Empty;

    private string? _fieldError;
    private string? _rowError;
    private bool _confirmOpen;
    private bool _revealed;

    // Focus has to cross a re-render in BOTH directions: the input does not exist when the Set
    // handler runs, and the Set/Replace button is a NEW element when Cancel runs — so a FocusAsync in
    // the handler targets an absent or stale reference. The flag is set in the handler and consumed
    // in OnAfterRenderAsync, the deferred pattern this codebase already uses.
    private string? _pendingFocusId;

    private string _id = default!;

    private string LabelId => AnchorId ?? $"{_id}-lbl";
    private string HelpId => $"{_id}-help";
    private string InputId => $"{_id}-input";
    private string ActionId => $"{_id}-action";
    private string ErrorId => $"{_id}-err";
    private string RowErrorId => $"{_id}-rowerr";
    private string AdvisoryId => $"{_id}-adv";

    /// <summary>
    /// Whether the entry input is on screen. <c>NotSet</c> shows it inline and immediately — the one
    /// state with nothing to protect and something to do, so it costs no click (DS · SecretSettingField).
    /// A stored or unreadable value takes an explicit Replace first, so it cannot be overwritten by a
    /// stray keystroke.
    /// </summary>
    private bool Entering =>
        // An edit already under way survives a transient Disabled — the page raises it while its own
        // Save is in flight, and blanking a half-typed credential because an unrelated save was
        // running would lose it. Disabled gates only the INLINE entry a NotSet field offers, which is
        // the case where the caller genuinely holds no claim.
        _mode is Mode.Editing or Mode.Saving || (!Disabled && State == SecretSettingState.NotSet);

    private string WrapperClass =>
        string.IsNullOrEmpty(Class) ? "odc-sfield odc-secret wide" : $"odc-sfield odc-secret wide {Class}";

    /// <summary>
    /// The frame's state classes. <c>unreadable</c> is its own, NOT a flavour of <c>error</c>: an
    /// error is something the administrator just typed, and this is a fault in what is stored.
    /// <c>advised</c> tints the frame while the amber band is showing, so the two read as one block.
    /// </summary>
    private string FrameClass
    {
        get
        {
            var classes = new List<string>(4) { "odc-sfield-frame" };

            if (FieldError is not null || _rowError is not null || State == SecretSettingState.Unreadable)
            {
                classes.Add("error");
            }

            if (State == SecretSettingState.Unreadable)
            {
                classes.Add("unreadable");
            }

            if (ConsequenceText is not null)
            {
                classes.Add("advised");
            }

            return string.Join(' ', classes);
        }
    }

    /// <summary>
    /// A FIXED count, deliberately unrelated to the stored value's length — the length is itself a
    /// disclosure, and a mask that tracked it would leak one on every page load.
    /// </summary>
    private const string MaskGlyphs = "••••••••••••••••";

    protected override void OnInitialized() => _id = $"odc-secret-{Guid.NewGuid():N}";

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_pendingFocusId is not { } target)
        {
            return;
        }

        _pendingFocusId = null;
        await JS.InvokeVoidAsync("odsFocusById", target);
    }

    /// <summary>
    /// Provenance on the helper line — who last changed the value and when, the thing an administrator
    /// actually scans for. Never the value, never anything derived from it.
    /// </summary>
    private string MetaText => UpdatedAt is { } when_
        ? $"Set by {UpdatedByDisplayName ?? "Unknown user"}, {when_.ToLocalTime():d MMM yyyy, HH:mm}."
        : "Never set.";

    /// <summary>
    /// The <c>Unreadable</c> condition, in the ERROR channel — as TEXT (WCAG 1.4.1), never a coral
    /// outline alone. It names the likely cause, what is broken right now (<see cref="Affects"/>,
    /// because nothing about the key's name tells an administrator that) and the only remedy.
    /// </summary>
    private string? UnreadableMessage => State != SecretSettingState.Unreadable
        ? null
        : string.Join(" ", new[]
        {
            "Stored, but this server cannot decrypt it — the Data Protection key ring was replaced or lost.",
            Affects,
            "Clearing the row and entering the value again is the only fix.",
        }.Where(part => !string.IsNullOrWhiteSpace(part)));

    /// <summary>
    /// The advisory's text, or <see langword="null"/> when the band must not render. Only while the
    /// row is <see cref="SecretSettingState.NotSet"/>: the advisory channel's contract is strictly
    /// non-blocking text about a cost, and an undecryptable row is a fault.
    /// </summary>
    private string? ConsequenceText =>
        State == SecretSettingState.NotSet && !string.IsNullOrWhiteSpace(Consequence) ? Consequence : null;

    /// <summary>
    /// The store accepts <c>0x20</c>–<c>0x7E</c> only, and this says so as the value is typed rather
    /// than leaving it to the server's <c>400</c> (issue #445 AC 9). The constraint is arbitrary from
    /// the administrator's side, and <see cref="SecretSettingKeys.EmailPassword"/> is a human-chosen
    /// password at a third party — the one credential in the catalogue that could legitimately fall
    /// outside the range.
    ///
    /// <para>
    /// It echoes the offending character, which is safe and is the point: "somewhere in what you
    /// pasted" is not actionable, and this is the administrator's own input on their own screen, not a
    /// stored value read back. It is a mirror of a COMPILE-TIME server rule, not of an admin-editable
    /// cap — the client-side-copy prohibition is about limits an administrator can change underneath a
    /// hardcoded constant, which this is not.
    /// </para>
    /// </summary>
    private string? AsciiError
    {
        get
        {
            if (AllowNonAscii || _value.Length == 0)
            {
                return null;
            }

            var offender = _value.FirstOrDefault(c => c < 0x20 || c > 0x7E);
            if (offender == '\0')
            {
                return null;
            }

            var described = offender switch
            {
                '\t' => "a tab",
                < (char)0x20 => "a control character",
                _ => $"\u201c{offender}\u201d",
            };

            return $"Only printable ASCII — space to ~ — can be stored. This value contains {described}.";
        }
    }

    /// <summary>
    /// The server's rejection wins over the local check: it is the more specific answer, and a value
    /// the server has already refused should not be re-described by a rule it did not cite.
    /// </summary>
    private string? FieldError => _fieldError ?? AsciiError;

    private void ToggleReveal() => _revealed = !_revealed;

    /// <summary>
    /// Enter commits, Escape abandons — the design system's entry-input contract
    /// (DS · components/SecretSettingField).
    ///
    /// <para>
    /// It matters more here than on an ordinary field: this control does not participate in the page's
    /// Save, so there is no form submit for Enter to fall through to, and a credential typed into a row
    /// that then loses focus is simply gone. Escape only cancels a REPLACE — from
    /// <see cref="SecretSettingState.NotSet"/> the input is the row's resting state, so there is
    /// nothing to return to and Escape is left to the browser.
    /// </para>
    ///
    /// <para>
    /// No <c>preventDefault</c>: <c>@onkeydown:preventDefault</c> cannot be applied to a component
    /// (Razor generates a second <c>onkeydown</c> parameter and refuses to compile), and neither key
    /// has a native default worth suppressing on a bare input outside a form.
    /// </para>
    /// </summary>
    private async Task OnEntryKeyDown(KeyboardEventArgs args)
    {
        if (_mode == Mode.Saving)
        {
            return;
        }

        if (args.Key == "Enter")
        {
            await SaveAsync();
        }
        else if (args.Key == "Escape" && State != SecretSettingState.NotSet)
        {
            await CancelAsync();
        }
    }


    /// <summary>
    /// The advisory is described-by text, not a live region, so it has to be reachable from the
    /// control — otherwise a screen-reader user editing the field never hears why the row matters.
    /// </summary>
    private string DescribedBy
    {
        get
        {
            var parts = new List<string>(3) { HelpId };

            if (ConsequenceText is not null)
            {
                parts.Add(AdvisoryId);
            }

            // Two ids, not one: the field error and the row error are separate elements and can
            // render together (AsciiError is a live getter over _value, so a keystroke can raise it
            // while a row-level rejection is still on screen). Sharing one id would leave whichever
            // block aria-describedby did not resolve visible but programmatically unreachable.
            if (FieldError is not null)
            {
                parts.Add(ErrorId);
            }

            if (_rowError is not null)
            {
                parts.Add(RowErrorId);
            }

            return string.Join(' ', parts);
        }
    }

    private void OnValueInput(ChangeEventArgs args)
    {
        _value = args.Value?.ToString() ?? string.Empty;

        // A keystroke supersedes the last rejection: leaving a stale 400 on the field would keep
        // aria-invalid set against a value the server has never seen. The row-level rejection goes
        // with it for the same reason — a 403, 429 or 503 describes the submission, not the value
        // now being typed — and clearing it here is also what keeps the two error blocks from ever
        // rendering at once.
        _fieldError = null;
        _rowError = null;
    }

    private async Task BeginEdit()
    {
        _mode = Mode.Editing;
        _value = string.Empty;
        _fieldError = null;
        _rowError = null;
        _revealed = false;
        _pendingFocusId = InputId;
        await AnnounceAsync($"Editing {Title}. The value is hidden as you type.");
    }

    private async Task CancelAsync()
    {
        _mode = Mode.Display;
        _value = string.Empty;
        _fieldError = null;
        _rowError = null;
        _revealed = false;
        _pendingFocusId = ActionId;
        await AnnounceAsync($"{Title} edit cancelled.");
    }

    private async Task SaveAsync()
    {
        if (OnSave is null || _mode == Mode.Saving)
        {
            return;
        }

        if (AsciiError is not null)
        {
            // Refused locally rather than sent: the store would answer 400 with a message that does not
            // name the character, and this row can.
            _rowError = null;
            _pendingFocusId = InputId;
            return;
        }

        var value = _value.Trim();
        if (value.Length == 0)
        {
            // Locally, because an empty PUT is a 400 the server would answer with the same sentence —
            // and clearing is DELETE, so "set it to nothing" must never be an accident of a blank field.
            _fieldError = "Enter the credential, or use Clear to remove the stored value.";
            _rowError = null;
            _pendingFocusId = InputId;
            return;
        }

        _mode = Mode.Saving;
        _fieldError = null;
        _rowError = null;

        var result = await OnSave(value);
        if (result.IsSuccess)
        {
            _value = string.Empty;
            _revealed = false;
            _mode = Mode.Display;
            _pendingFocusId = ActionId;

            // The announcement NAMES the credential: a bare "Credential saved." is identical for every
            // row, so a screen-reader user with several credentials cannot tell which one committed.
            await AnnounceAsync($"{Title} saved.");
            await OnChanged.InvokeAsync();
            return;
        }

        // Back to Editing, with the typed value intact so a 429 or a transient 503 can simply be retried.
        _mode = Mode.Editing;
        ApplyFailure(result);
        _pendingFocusId = InputId;
        await AnnounceAsync($"{Title} could not be saved. {(_fieldError ?? _rowError)}");
    }

    private void OpenConfirm()
    {
        _confirmOpen = true;
        _rowError = null;
    }

    /// <summary>
    /// Closing the dialog moves NO focus of its own (PR #450 accessibility review, WCAG 2.4.3).
    /// <c>OdsModal</c> already captures whatever opened it and restores that on the close edge, so a
    /// redirect here raced it — and aimed at the wrong element besides: the button the user pressed
    /// was <b>Clear</b>, not the primary action.
    ///
    /// <para>
    /// The one case the row must still handle is a <em>successful</em> clear, where the Clear button
    /// no longer exists to be restored to; <see cref="ConfirmClearAsync"/> sets the pending focus for
    /// that path alone.
    /// </para>
    /// </summary>
    private void OnConfirmOpenChanged(bool open) => _confirmOpen = open;

    /// <summary>
    /// The destructive action's commit. Built on <c>OdsFormDialog</c> because there is no
    /// confirmation dialog in this client to reuse — <c>Components/</c> contains no
    /// <c>ConfirmDialog</c>/<c>OdsConfirm</c> — and reusing the form shell keeps the focus-trap and
    /// dismissal behaviour rather than introducing a second dialog pattern.
    /// </summary>
    private async Task<bool> ConfirmClearAsync()
    {
        if (OnClear is null)
        {
            return true;
        }

        _mode = Mode.Clearing;
        _rowError = null;
        _fieldError = null;
        StateHasChanged();

        var result = await OnClear();
        _mode = Mode.Display;

        if (!result.IsSuccess)
        {
            // The dialog stays open (returning false), so focus stays inside it and the failure
            // renders there. Moving focus out to the row would take the user away from the message.
            ApplyFailure(result);
            await AnnounceAsync($"{Title} could not be cleared. {(_fieldError ?? _rowError)}");
            return false;
        }

        // The only path that moves focus itself: the Clear button OdsModal captured is gone once the
        // row falls back to NotSet, so restoring to it would drop focus to <body>.
        _pendingFocusId = ActionId;
        _value = string.Empty;
        _rowError = null;
        await AnnounceAsync($"{Title} cleared.");
        await OnChanged.InvokeAsync();
        return true;
    }

    /// <summary>
    /// One resolution path covering <c>400</c>, <c>403</c>, <c>429</c> and <c>503</c> alike.
    ///
    /// <para>
    /// <c>ApiProblem.ErrorFor</c> keys on the REQUEST DTO PROPERTY name, not the setting key: these
    /// are per-key endpoints whose body has a single property, so a validation failure keys on
    /// <c>"Value"</c> and never on the key. The settings page's key-based join works only because
    /// that page PUTs one body containing every field. And the <c>503</c> ephemeral-key-ring refusal
    /// carries no <c>errors</c> entry at all, so the fallback to <c>ApiProblem.Message</c> — which
    /// itself degrades to <c>Detail</c>, then the reason phrase — is what keeps it from rendering blank.
    /// </para>
    /// </summary>
    private void ApplyFailure(ApiResult result)
    {
        var problem = result.Problem;
        var fieldMessage = problem?.ErrorFor(nameof(SecretSettingUpdate.Value));

        if (fieldMessage is not null)
        {
            _fieldError = fieldMessage;
            _rowError = null;
            return;
        }

        _fieldError = null;
        _rowError = problem?.Message ?? "The request failed.";
    }

    private Task AnnounceAsync(string message) =>
        OnAnnounce.HasDelegate ? OnAnnounce.InvokeAsync(message) : Task.CompletedTask;

    /// <summary>
    /// Clears the in-progress value when the row is torn down — a search-filter change re-renders the
    /// row away, and this is the last point at which the plaintext is still in a field this component
    /// owns.
    /// </summary>
    public void Dispose()
    {
        _value = string.Empty;
        _revealed = false;
    }
}
