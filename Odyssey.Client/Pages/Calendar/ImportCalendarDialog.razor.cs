using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Calendar;

public partial class ImportCalendarDialog
{
    /// <summary>The dialog's phases (issue #517 §3 Import flow).</summary>
    internal enum Phase { Compose, Uploading, Server, Result }

    // The picker's container: the browser keeps the picked File for it, so a large file uploads straight from disk.
    internal const string UploadId = "cal-import-upload";
    internal const string FootStatusId = "cal-imp-foot-status";
    internal const string CancelUploadId = "cal-imp-cancel-upload";
    internal const string StopUploadId = "cal-imp-stop-upload";
    internal const string ImportButtonId = "cal-imp-import";
    internal const string AlertId = "cal-imp-alert";
    internal const string RemoveId = "cal-imp-allow-removals";
    internal const string RemoveHelpId = "cal-imp-allow-removals-help";
    internal const string RemoveWarningId = "cal-imp-allow-removals-warning";
    internal const string AttachWarningId = "cal-imp-attach-warn";
    internal const string AttachClaimId = "cal-imp-attach-claim";

    /// <summary>The modal's class: the Escape watch is scoped to the dialog that carries it.</summary>
    internal const string DialogClass = "cal-imp-dialog";
    private const string RemovedGroupKey = "\u0000removed";

    // The most the typed-client fallback reads into the WASM heap (its arrays top out near 2 GB, and the heap well
    // before that once copied). Only reached when the browser holds no handle to the picked file. A named
    // per-surface constant, not a server cap: it narrows nothing the server enforces (issue #517 §8).
    internal const int FallbackMaxMegabytes = 512;
    internal const long FallbackMaxBytes = FallbackMaxMegabytes * 1024L * 1024;

    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }
    [Parameter] public IReadOnlyList<ExistingCalendar> Calendars { get; set; } = [];

    /// <summary>Pre-selected target calendar. Optional; falls back to the first calendar.</summary>
    [Parameter] public Guid? DefaultCalendarId { get; set; }

    /// <summary>
    /// Fires, while the dialog is open, after an outcome that may have changed rows — an import that created,
    /// updated or removed any, and any failure after the upload had finished — so the page reloads its events.
    /// </summary>
    [Parameter] public EventCallback OnChanged { get; set; }

    /// <summary>The caller holds <c>calendar.delete</c>: the opt-in removal checkbox is offered. A UX gate only.</summary>
    [Parameter] public bool CanDelete { get; set; }

    /// <summary>
    /// <c>files.read</c>, <c>files.create</c>, <c>photos.read</c> and <c>photos.create</c> together: without all four the
    /// compose step says a file carrying attachments will be refused (issue #522 §3.5, v3 §D).
    /// </summary>
    [Parameter] public bool CanAddAttachments { get; set; }

    /// <summary>What the picker is described by: the attachment Warning, and the claim line when it shows (AX6).</summary>
    private string PickerDescribedBy => CanAddAttachments ? AttachWarningId : $"{AttachWarningId} {AttachClaimId}";

    /// <summary>An earlier import this page handed off is still running: an Info note shows and Import is unavailable.</summary>
    [Parameter] public bool ImportPending { get; set; }

    /// <summary>
    /// Closing while the server imports hands the pending request to the page, which then owns its toast and its
    /// refresh; the dialog applies nothing of it.
    /// </summary>
    [Parameter] public EventCallback<Task<ApiResult<IcsImportResult>>> OnBackgrounded { get; set; }

    [Inject] private IServiceProvider Services { get; set; } = default!;

    /// <summary>
    /// The zone a floating time in the file is read in (issue #520 §3 "Import dialog"): the browser's, sent as
    /// <c>defaultTimeZoneId</c> on both paths; null when the browser reports none, and then the field is omitted and the
    /// compose note says floating times stay floating (AC 11). <c>UTC</c> once the server has refused the browser's zone
    /// this session (PR #528).
    /// </summary>
    private string? DefaultZone => Viewer.ImportZoneId;

    private ViewerTimeZone Viewer => ViewerTimeZone.From(Services);

    /// <summary>The form field the default zone is validated as: a 400 keyed on it is the server refusing the zone.</summary>
    internal const string DefaultZoneField = "defaultTimeZoneId";

    private string? _calendarValue;
    private IReadOnlyList<OdsUploadFile> _files = [];
    private string? _calendarError;
    private string? _fileError;
    private string? _error;
    private string? _errorExtra;
    private bool _allowRemovals;

    private Phase _phase = Phase.Compose;
    private bool _native;
    private int _generation;
    private Task<Attempt>? _pending;
    private bool _confirmOpen;
    private string _confirmReturnId = CancelUploadId;
    private bool _wasOpen;
    private bool _disposed;
    private readonly CancellationTokenSource _lifetime = new();

    private IcsImportResult? _result;
    private OdsTagChips.Tag? _target;
    private OdsLiveAnnouncer? _announcer;
    private bool _focusResultPending;
    private bool _focusAlertPending;
    private bool _focusPickerPending;
    private bool _focusStatusPending;
    private DotNetObjectReference<ImportCalendarDialog>? _selfRef;
    private int? _escapeWatch;

    // Set while a watch call is in flight: two overlapping renders must not register two listeners.
    private bool _escapeWatchStarting;
    private ElementReference _resultHeading;
    private ElementReference _alertRef;

    private readonly HashSet<string> _openReasons = new(StringComparer.Ordinal);

    /// <summary>One attempt's outcome, with what the browser saw of the upload.</summary>
    private sealed record Attempt(ApiResult<IcsImportResult> Result, bool Aborted, bool UploadComplete, bool ConnectionLost);

    internal Phase CurrentPhase => _phase;

    private IReadOnlyList<OdsOption> _calendarOptions =>
        [.. Calendars.Select(CalendarVisuals.Option)];

    private int SkippedTotal => _result is null ? 0 : CalendarImportCopy.Skipped(_result);

    private string FooterStatus => _phase switch
    {
        Phase.Uploading => CalendarImportCopy.UploadingStatus,
        Phase.Server => CalendarImportCopy.ServerStatus,
        _ => string.Empty,
    };

    // Effective limit (issue #343 §3/§11) — read from the live IImportLimitsCache, which itself
    // falls back to the shipped default when the load fails, so the dialog still opens and accepts
    // a file at that limit rather than erroring out (fe C4 — the fallback lives in the cache, not
    // here; see ImportLimitsCache.Fallback).
    private long _maxImportBytes = ImportLimitsCache.Fallback.CalendarIcsMaxImportMegabytes * 1024L * 1024;

    protected override void OnParametersSet()
    {
        if (Open && !_wasOpen)
        {
            // Every open starts clean: the removal option is off, and no earlier attempt can land here.
            Reset();
            _calendarValue = null;
        }

        _wasOpen = Open;

        // Seed the target selection once the dialog opens (default calendar, or the only/first one).
        if (Open && _calendarValue is null && _phase == Phase.Compose)
        {
            _calendarValue = (DefaultCalendarId ?? Calendars.FirstOrDefault()?.CalendarId)?.ToString();
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (!Open)
        {
            return;
        }

        var limits = await ImportLimits.GetAsync();
        _maxImportBytes = limits.CalendarIcsMaxImportMegabytes * 1024L * 1024;
    }

    /// <summary>
    /// The calendar the result line names, as its chip (icon and colour, through <see cref="CalendarVisuals"/>),
    /// or <c>null</c> when the list no longer holds it (it changed while the import ran); the line then
    /// reads "calendar".
    /// </summary>
    internal static OdsTagChips.Tag? TargetChip(IReadOnlyList<ExistingCalendar> calendars, Guid calendarId) =>
        calendars.FirstOrDefault(c => c.CalendarId == calendarId) is { } calendar ? CalendarVisuals.Chip(calendar) : null;

    private void OnCalendarSelected(string value)
    {
        _calendarValue = value;
        _calendarError = null;
    }

    private void OnFilesChanged(IReadOnlyList<OdsUploadFile> files)
    {
        _files = files;
        _fileError = null;
        _error = null;
        _errorExtra = null;
        _allowRemovals = false;
    }

    private void OnAllowRemovalsChanged(bool value) => _allowRemovals = value && CanDelete;

    private void Announce(string message) => _ = _announcer?.Announce(message);

    private async Task SubmitAsync()
    {
        if (ImportPending || _phase != Phase.Compose)
        {
            return;
        }

        _calendarError = null;
        _fileError = null;
        _error = null;
        _errorExtra = null;

        var calendarId = Guid.TryParse(_calendarValue, out var id) ? id : (Guid?)null;
        var file = _files.FirstOrDefault()?.Source;

        if (calendarId is null)
        {
            _calendarError = "Choose which calendar to import into.";
        }

        if (file is null)
        {
            _fileError = "Choose a .ics file to import.";
        }
        else if (!file.Name.EndsWith(".ics", StringComparison.OrdinalIgnoreCase))
        {
            _fileError = "That isn't a .ics file. iCalendar files use the .ics extension.";
        }
        else if (file.Size > _maxImportBytes)
        {
            _fileError = $"That file is larger than the {ByteSize.Format(_maxImportBytes)} limit.";
        }

        if (_calendarError is not null || _fileError is not null)
        {
            _focusPickerPending = _fileError is not null;
            return;
        }

        var generation = ++_generation;
        var allowRemovals = _allowRemovals && CanDelete;
        _target = TargetChip(Calendars, calendarId!.Value);

        bool native;
        try
        {
            native = await NativeTransfer.HoldsAsync(UploadId, file!);
        }
        catch (JSException)
        {
            native = false;
        }

        if (generation != _generation || _disposed)
        {
            return;
        }

        if (!native && file!.Size > Math.Min(_maxImportBytes, FallbackMaxBytes))
        {
            // The typed client reads the whole file into the WASM heap, which cannot hold a very large one.
            ShowAlert($"This browser could not stream the file, and files over {ByteSize.Format(FallbackMaxBytes)} "
                + "cannot be read into the page. Choose the file again with Browse, then import.");
            return;
        }

        _native = native;
        Announce(CalendarImportCopy.Importing);

        // The typed fallback has no upload-complete signal, so it counts as the server phase from the start: Close
        // and Escape hand it off, and Cancel upload is never offered (an HttpClient call cannot be stopped mid-body).
        _phase = native ? Phase.Uploading : Phase.Server;
        // Import, which held focus, is gone: focus moves to the phase line rather than to <body> (WCAG 2.4.3).
        _focusStatusPending = true;
        _pending = native
            ? UploadNativeAsync(generation, calendarId.Value, file!, allowRemovals)
            : UploadTypedAsync(calendarId.Value, file!, allowRemovals);
        StateHasChanged();

        var attempt = await _pending;
        await ApplyAsync(generation, attempt);
    }

    private async Task<Attempt> UploadNativeAsync(int generation, Guid calendarId, IBrowserFile file, bool allowRemovals)
    {
        // The zone sent is the session's: once anything (normally the page's first load, which runs before an import can
        // start) has found the server refuses the browser's zone, the first upload already sends UTC.
        var sentUtc = Viewer.FellBackToUtc;
        var attempt = await UploadNativeOnceAsync(generation, calendarId, file, allowRemovals);
        // A refused zone costs a second upload of the WHOLE file: the server binds defaultTimeZoneId from the multipart
        // form, which it buffers in full (the IFormFile) before model validation answers the 400. Nothing is applied by
        // then, so resending is safe, only slow for a large file — which is why the session state above is checked first
        // and this retry stays rare (PR #528 frontend review). The server is deliberately left as it is.
        if (attempt is { Aborted: false } && !sentUtc
            && ViewerTimeZone.RefusedZone(attempt.Result.Status, attempt.Result.Problem, DefaultZoneField))
        {
            Viewer.UseUtc();
            attempt = await UploadNativeOnceAsync(generation, calendarId, file, allowRemovals);
        }

        return attempt;
    }

    private async Task<Attempt> UploadNativeOnceAsync(int generation, Guid calendarId, IBrowserFile file, bool allowRemovals)
    {
        var fields = ImportFields(allowRemovals, DefaultZone);
        try
        {
            var outcome = await NativeTransfer.TryUploadAsync<IcsImportResult>(
                UploadId, file, CalendarApi.ImportUrl(calendarId), "text/calendar", fields,
                () => _ = InvokeAsync(() => OnUploadComplete(generation)), _lifetime.Token);
            if (outcome is null)
            {
                return new Attempt(
                    ApiResult<IcsImportResult>.Failure(new InvalidOperationException(
                        "The chosen file is no longer available. Choose the file again with Browse, then import.")),
                    Aborted: false, UploadComplete: false, ConnectionLost: false);
            }

            var result = outcome.Result;
            var lost = outcome.UploadComplete && !outcome.Aborted
                && !result.IsSuccess && (result.Status == 0 || !outcome.JsonBody);
            return new Attempt(result, outcome.Aborted, outcome.UploadComplete, lost);
        }
        catch (Exception ex) when (ex is JSException or TaskCanceledException or ObjectDisposedException)
        {
            return new Attempt(ApiResult<IcsImportResult>.Failure(ex), Aborted: false, UploadComplete: false, ConnectionLost: false);
        }
    }

    private async Task<Attempt> UploadTypedAsync(Guid calendarId, IBrowserFile file, bool allowRemovals)
    {
        // Same effective value used for the pre-check above and for OpenReadStream (issue #343 fe C2).
        // A zone the server does not know is retried once in UTC, which the session then keeps (PR #528).
        var result = await Viewer.WithFallbackAsync(DefaultZoneField,
            () => CalendarApi.ImportAsync(calendarId, file.ToApiUpload(_maxImportBytes), allowRemovals, DefaultZone));

        // A failure that never reached a parsed response (no status, or a body that was not problem JSON) is a lost
        // connection: the server may already have imported part of the file.
        var lost = !result.IsSuccess
            && (result.Status == 0 || result.Problem is { Title: null, Detail: null, Code: null });
        return new Attempt(result, Aborted: false, UploadComplete: true, ConnectionLost: lost);
    }

    /// <summary>The native upload's form fields: <c>allowRemovals</c> when ticked, <c>defaultTimeZoneId</c> when known.</summary>
    internal static IReadOnlyList<KeyValuePair<string, string>>? ImportFields(bool allowRemovals, string? defaultZone)
    {
        var fields = new List<KeyValuePair<string, string>>();
        if (allowRemovals)
        {
            fields.Add(new("allowRemovals", "true"));
        }

        if (!string.IsNullOrEmpty(defaultZone))
        {
            fields.Add(new("defaultTimeZoneId", defaultZone));
        }

        return fields.Count == 0 ? null : fields;
    }

    /// <summary>The browser has sent the whole body: the server is importing now, and nothing can stop it.</summary>
    private void OnUploadComplete(int generation)
    {
        if (_disposed || generation != _generation || _phase != Phase.Uploading)
        {
            return;
        }

        _phase = Phase.Server;
        if (_confirmOpen)
        {
            // The race guard (AC 14b): the confirmation closes itself, and focus goes to the phase line.
            _confirmReturnId = FootStatusId;
            _confirmOpen = false;
            Announce(CalendarImportCopy.FinishedWhileDeciding);
        }
        else
        {
            // Cancel upload, which may hold focus, is gone: focus moves to the phase line.
            _focusStatusPending = true;
        }

        StateHasChanged();
    }

    /// <summary>Applies an attempt's outcome, unless the dialog gave it up (closed, reset or reopened) meanwhile.</summary>
    private async Task ApplyAsync(int generation, Attempt attempt)
    {
        if (_disposed || generation != _generation)
        {
            return;
        }

        _allowRemovals = false;
        _confirmOpen = false;
        var outcome = attempt.Result;

        if (attempt.Aborted && !attempt.UploadComplete)
        {
            // Stopped before the body finished: the server buffers the whole body before importing, so nothing was.
            _phase = Phase.Compose;
            _focusPickerPending = true;
            Announce(CalendarImportCopy.Stopped);
            StateHasChanged();
            return;
        }

        if (outcome.IsSuccess && outcome.Value is { } result)
        {
            _result = result;
            _phase = Phase.Result;
            _focusResultPending = true;
            Announce(CalendarImportCopy.Announcement(result, CanDelete));
            StateHasChanged();

            if (result.ImportedCount > 0 || result.UpdatedCount > 0 || result.RemovedCount > 0)
            {
                await OnChanged.InvokeAsync();
            }

            return;
        }

        _phase = Phase.Compose;
        if (attempt.Aborted)
        {
            ShowAlert(CalendarImportCopy.Interrupted);
        }
        else if (CalendarImportCopy.CommittedEvents(outcome.Problem) is { } committed)
        {
            ShowAlert(outcome.Error ?? CalendarImportCopy.HandedOffFailure, CalendarImportCopy.PartialSentence(committed));
        }
        else if (attempt.ConnectionLost)
        {
            ShowAlert(CalendarImportCopy.ConnectionLost);
        }
        else if (string.Equals(outcome.Problem?.Code, CalendarImportCopy.AttachmentsForbiddenCode, StringComparison.Ordinal))
        {
            ShowAlert(CalendarImportCopy.AttachmentsForbidden);
        }
        else
        {
            ShowAlert(outcome.Error ?? "The file could not be imported.");
        }

        StateHasChanged();

        // After the upload had finished, part of the file may have been applied: the page refreshes.
        if (attempt.UploadComplete)
        {
            await OnChanged.InvokeAsync();
        }
    }

    /// <summary>Shows a failure in the error alert, which takes focus; the live region is emptied (§3 step 7).</summary>
    private void ShowAlert(string message, string? extra = null)
    {
        _error = message;
        _errorExtra = extra;
        _focusAlertPending = true;
        Announce(string.Empty);
    }

    // ── Cancel upload (§3 step 5) ─────────────────────────────────────────────

    private void OpenConfirm()
    {
        if (_phase != Phase.Uploading)
        {
            return;
        }

        _confirmReturnId = CancelUploadId;
        _confirmOpen = true;
    }

    /// <summary>
    /// Escape anywhere in the dialog while uploading — the header, the body, the footer, the dialog container — opens
    /// the confirmation (WCAG 2.1.1). The dialog is not dismissible in that phase, so MudDialog ignores the key; the
    /// watch (<c>odsDialogEscape</c>, a capture listener scoped to this dialog) reports it here. In every other phase
    /// the dialog's own Escape handling applies and this does nothing.
    /// </summary>
    [JSInvokable]
    public void OnDialogEscape()
    {
        if (_disposed || _phase != Phase.Uploading || _confirmOpen)
        {
            return;
        }

        OpenConfirm();
        StateHasChanged();
    }

    private void OnConfirmOpenChanged(bool open)
    {
        if (!open)
        {
            KeepUploading();
        }
    }

    /// <summary>Keep uploading, or Escape inside the confirmation: it closes and focus returns to Cancel upload.</summary>
    private void KeepUploading()
    {
        if (!_confirmOpen)
        {
            return;
        }

        _confirmReturnId = CancelUploadId;
        _confirmOpen = false;
    }

    /// <summary>Stop upload: aborts only while the body is still being sent — after that it does nothing.</summary>
    internal async Task StopUploadAsync()
    {
        if (_phase != Phase.Uploading)
        {
            _confirmOpen = false;
            return;
        }

        _confirmOpen = false;
        await NativeTransfer.AbortAsync(UploadId);
    }

    // ── Close, hand-off and reset ─────────────────────────────────────────────

    private void ToggleReason(string reason)
    {
        if (!_openReasons.Remove(reason))
        {
            _openReasons.Add(reason);
        }
    }

    private void Reset()
    {
        // A new generation: an attempt still settling can never apply its result here.
        _generation++;
        _pending = null;
        _phase = Phase.Compose;
        _result = null;
        _files = [];
        _calendarError = null;
        _fileError = null;
        _error = null;
        _errorExtra = null;
        _allowRemovals = false;
        _confirmOpen = false;
        _openReasons.Clear();
    }

    private async Task CloseAsync()
    {
        // The inline modal closes itself while the page tears down; this dialog has already settled its import's
        // owner in DisposeAsync, so a teardown close must not hand it to a page that is going too.
        if (_disposed)
        {
            return;
        }

        if (_phase == Phase.Uploading)
        {
            // Closing mid-upload is a confirmed stop, never a silent one.
            OpenConfirm();
            return;
        }

        if (_phase == Phase.Server && _pending is { } pending)
        {
            // Hand-off: the page owns the outcome from here. Not awaited — the page awaits the import itself.
            _ = OnBackgrounded.InvokeAsync(ResultOf(pending));
        }

        Reset();
        _calendarValue = null;
        // Drop the browser's reference to the picked file, so a closed dialog holds no handle to it. A request
        // already handed off keeps running.
        await ForgetAsync();
        await OpenChanged.InvokeAsync(false);
    }

    private static async Task<ApiResult<IcsImportResult>> ResultOf(Task<Attempt> pending) => (await pending).Result;

    private async Task ForgetAsync()
    {
        try
        {
            await NativeTransfer.ForgetAsync(UploadId);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException or ObjectDisposedException)
        {
            // The browser side is gone — it holds no handle any more either.
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (Open && _phase == Phase.Compose)
        {
            await NativeTransfer.WatchAsync(UploadId);
        }

        await WatchEscapeAsync();

        if (_focusStatusPending && _phase is Phase.Uploading or Phase.Server)
        {
            _focusStatusPending = false;
            await TryJsAsync("odsFocusById", FootStatusId);
        }

        // The result heading and the alert render inside MudDialog's (deferred) dialog content, so their
        // ElementReferences can be unconfigured on the render that first shows them — guard on Id so FocusAsync
        // never throws, treating focus as best-effort.
        if (_focusResultPending && !string.IsNullOrEmpty(_resultHeading.Id))
        {
            _focusResultPending = false;
            await TryFocusAsync(_resultHeading);
        }

        if (_focusAlertPending && !string.IsNullOrEmpty(_alertRef.Id))
        {
            _focusAlertPending = false;
            await TryFocusAsync(_alertRef);
        }

        if (_focusPickerPending && _phase == Phase.Compose)
        {
            _focusPickerPending = false;
            try
            {
                // Two frames, so it lands after the Stop-upload confirmation has returned focus.
                await JS.InvokeVoidAsync("odsFocusFirstIn", UploadId, false, 2);
            }
            catch (Exception ex) when (ex is JSException or TaskCanceledException)
            {
                // Best-effort focus.
            }
        }
    }

    private async Task WatchEscapeAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (Open && _escapeWatch is null && !_escapeWatchStarting)
        {
            _escapeWatchStarting = true;
            _selfRef ??= DotNetObjectReference.Create(this);
            try
            {
                var watch = await JS.InvokeAsync<int>("odsDialogEscape.watch", DialogClass, _selfRef);
                if (_disposed || !Open)
                {
                    // Closed (or gone) while the watch was being set up: take it down again at once.
                    await TryJsAsync("odsDialogEscape.unwatch", watch);
                }
                else
                {
                    _escapeWatch = watch;
                }
            }
            catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException or InvalidOperationException)
            {
                // Best-effort: the footer's Cancel upload still opens the confirmation.
            }
            finally
            {
                _escapeWatchStarting = false;
            }
        }
        else if (!Open && _escapeWatch is { } watch)
        {
            _escapeWatch = null;
            await TryJsAsync("odsDialogEscape.unwatch", watch);
        }
    }

    private async Task TryJsAsync(string identifier, params object?[] args)
    {
        try
        {
            await JS.InvokeVoidAsync(identifier, args);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException or ObjectDisposedException)
        {
            // Best-effort.
        }
    }

    private static async Task TryFocusAsync(ElementReference element)
    {
        try
        {
            await element.FocusAsync();
        }
        catch (Exception)
        {
            // The element went away between the guard and the focus call — ignore.
        }
    }

    /// <summary>
    /// The dialog goes away with its page (a navigation) while an import is in flight. Mid-upload, the upload is
    /// aborted: the server imports nothing from a body that never finished, so no import runs that nobody would
    /// report. In the server phase (or if the body finished just before the abort) the import may already be
    /// applying, so its outcome is still toasted — through the app-wide snackbar, which outlives the page. The page's
    /// hand-off does not cover this case: it happens only when the dialog is CLOSED, and a navigation disposes the
    /// open dialog without closing it.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        var phase = _phase;
        var pending = _pending;
        _disposed = true;
        _pending = null;
        _phase = Phase.Compose;

        if (phase is Phase.Uploading or Phase.Server && pending is not null)
        {
            if (phase == Phase.Uploading)
            {
                await TryAbortAsync();
            }

            _ = ToastWhenSettledAsync(pending);
        }

        if (_escapeWatch is { } watch)
        {
            _escapeWatch = null;
            await TryJsAsync("odsDialogEscape.unwatch", watch);
        }

        _selfRef?.Dispose();
        // Releases the upload-complete callback; a request already on its way keeps running.
        await _lifetime.CancelAsync();
        _lifetime.Dispose();
        await ForgetAsync();
    }

    private async Task TryAbortAsync()
    {
        try
        {
            await NativeTransfer.AbortAsync(UploadId);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException or ObjectDisposedException)
        {
            // The browser side is gone, and the request with it.
        }
    }

    /// <summary>The orphaned import's one toast — none for an upload stopped before its body finished.</summary>
    /// <remarks>
    /// Runs unobserved, so it never throws. An attempt that faults — including one cancelled from outside, which can
    /// only mean the request ended without an answer — is reported with the general failure wording: part of the file
    /// may already have been applied, and that wording says so and that importing again is safe.
    /// </remarks>
    private async Task ToastWhenSettledAsync(Task<Attempt> pending)
    {
        ApiResult<IcsImportResult> outcome;
        try
        {
            var attempt = await pending;
            if (attempt.Aborted && !attempt.UploadComplete)
            {
                return;
            }

            outcome = attempt.Result;
        }
        catch (Exception ex)
        {
            outcome = ApiResult<IcsImportResult>.Failure(ex);
        }

        try
        {
            CalendarImportCopy.ToastHandedOff(Snackbar, outcome);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The app itself is shutting down: there is nowhere left to show it.
        }
    }
}
