using Microsoft.JSInterop;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.Dtos.Application;
using Odyssey.Client.Components;

namespace Odyssey.Client.Pages;

public partial class Settings
{
    private async Task Save()
    {
        if (_isSaving || !CanSave || _phase != Phase.Ready)
        {
            return;
        }

        // A blocked attempt is the right moment to announce what is wrong — once, politely. Announcing
        // from ErrorFor instead would fire on every keystroke.
        if (HasErrors)
        {
            if (BlockingSummary is { } blocking)
            {
                Announce(blocking);
            }

            await JumpToFirstError();
            return;
        }

        // The destructive gate (issue #8 §3). Checked AFTER the error gate — there is no point
        // warning about a consequence of a save that cannot proceed — and before anything is sent.
        if (!_clearConfirmed && CredentialClearTrigger is { } trigger)
        {
            OpenClearConfirmation(trigger);
            return;
        }

        // The News allow-list gate (issue #473 §3.6): a change to the hosts the worker may reach on the
        // household's network is confirmed with the added and removed hosts listed, before anything is sent.
        // Checked after the credential gate and before either confirmation is consumed, so a save that trips
        // both asks twice and sends once.
        if (!_allowListConfirmed && PendingAllowListChange is { } allowListChange)
        {
            OpenAllowListConfirmation(allowListChange);
            return;
        }

        // One-shot: consumed here so a later save that no longer trips a trigger is not silently
        // pre-confirmed, and so the same confirmation cannot cover a second destructive edit.
        _clearConfirmed = false;
        _allowListConfirmed = false;

        _isSaving = true;
        _justSaved = false;

        // Rule one of two: a new attempt supersedes whatever the last one was rejected for. Without
        // this the overlay would keep Save disabled forever, since HasErrors gates the button AND
        // this method's own early return.
        _serverErrors.Clear();

        // Per §3: null — never a copy of the loaded value — for every field the caller cannot
        // edit. Sending the loaded value there would let this save silently clobber another
        // admin's concurrent change to a field this page never showed as editable. The claim
        // check on the API side keys off the CapacityLimit OBJECT, never `.Value` (sec F3), so a
        // count field this caller can't edit must be sent as a literal null, not a partially-filled
        // CapacityLimit.
        // One Write per catalogue entry (issue #421 Wave 0b). Each helper gates on the row's own
        // Claim, so "which claim covers which field" is stated once, in the catalogue, instead of
        // being re-derived here — the duplication that made a mismatch possible.
        var request = new SystemSettingsUpdate();
        foreach (var item in AllItems)
        {
            item.Write?.Invoke(this, request);
        }

        var result = await SystemSettings.UpdateAsync(request);
        _isSaving = false;

        if (result.IsSuccess && result.Value is { } dto)
        {
            ApplyLoaded(dto);
            _justSaved = true;
            Announce(AdvisorySuffixed("System settings saved."));
            // A successful save invalidates the client-side import-limits cache (issue #343 fe R9)
            // — without this, an admin who lowers a limit and then opens an import dialog in the
            // same session would pre-validate against the old value indefinitely.
            ImportLimits.Invalidate();
            // Same reasoning for the upload cap (issue #421 Wave 4) — its cache is session-lifetime too,
            // and lowering the cap is precisely the flow where a stale client number would matter.
            UploadLimits.Invalidate();
            // Same reasoning again for the smart-tag cap (issue #434 key 15): the Accounts page's
            // section reads it through a session-lifetime cache, so lowering the cap and then expanding
            // an account without this would pre-validate against the old number for the rest of the
            // session.
            AccountLimits.Invalidate();
            // And the contract cap (issue #166), which has its own key, its own cache entry and its
            // own eviction on the server — so it needs its own invalidation here for the same reason.
            ContractLimits.Invalidate();
            // And the property cap (issue #167) — its own key and its own cache, so its own call.
            PropertyLimits.Invalidate();
            // The processor disclosure (issue #421 Wave 1, extended by #439). All three of the switch,
            // the model and the destination feed the consent gate — the switch decides whether the
            // Analyze affordance is offered at all — so an administrator toggling analysis off must see
            // the affordance change without a reload. Note this reaches only THIS browser: a user with
            // an open gate is covered server-side by the disclosureVersion check, not by this call.
            Disclosures.Invalidate();
            // The household default rides on the weather-point picker list (issue #388 §5).
            WeatherOptions.Invalidate();
            StateHasChanged();
            await Task.Delay(OdsTiming.ConfirmFlashMs);
            _justSaved = false;
            StateHasChanged();
        }
        else if (result.Status == System.Net.HttpStatusCode.Forbidden)
        {
            // The write claim was withdrawn after sign-in: the cookie still names the claims this page
            // was rendered for, so nothing on it can be saved until the reader signs in again. A
            // persistent alert with the remedy on it, never a toast that fades before it is acted on.
            // One announcement, not three: focus moves to the alert, which is named by its title and
            // described by its body, so no live region or role="alert" repeats it (WCAG 4.1.3).
            _authDenied = true;
            StateHasChanged();
            try
            {
                await JS.InvokeVoidAsync("odsFocusById", AuthAlertId);
            }
            catch
            {
                // Not rendered (a concurrent re-render) — non-fatal; the alert stands, and the next render shows it.
            }
        }
        else
        {
            Snackbar.Add($"Couldn't save settings: {result.Error}", Severity.Error);

            // Per-field messages render on their own rows. The server keys `errors` by the DTO field
            // name; the catalogue keys rows by its own key, so the Write delegate's target property is
            // the join between them — resolved here off the same catalogue, not a second mapping table.
            ApplyServerFieldErrors(result.Problem);

            // A server-rejected round-trip conflict (a race the client's own check missed — e.g. a
            // concurrent edit between load and save) maps to the responsible group's alert via
            // ApiProblem.Code (issue #343 fe D2/R10); an unrecognised code falls back to the
            // snackbar above, which already fired.
            var group = RoundTripGroupFor(result.Problem?.Code);
            if (group is not null)
            {
                StateHasChanged();
                try
                {
                    await JS.InvokeVoidAsync("odsFocusById", RoundTripAlertId(group));
                }
                catch
                {
                    // The element isn't present (e.g. the client's own check no longer agrees with
                    // the server, so no alert rendered) — non-fatal; the snackbar already told the
                    // caller what happened.
                }
            }
        }
    }

    // "system-settings.invalid.round-trip.<pair>" → the group name that pair belongs to.
    private static string? RoundTripGroupFor(string? code)
    {
        const string prefix = "system-settings.invalid.round-trip.";
        if (code is null || !code.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var pair = code[prefix.Length..];
        return pair switch
        {
            "contacts" => "Contacts import & export",
            "calendars" => "Calendars import & export",
            "tasks" => "Tasks import & export",
            "journal-entries" => "Journal import & export",
            _ => null,
        };
    }

    private static string RoundTripAlertId(string group) => $"ss-rtalert-{group}";

    /// <summary>
    /// Maps a rejected save's per-field messages onto their rows (issue #421 Wave 0b).
    ///
    /// <para>
    /// Two server paths fill <c>ApiProblem.Errors</c>: <c>[ApiController]</c> model validation, which
    /// reports EVERY data-annotation failure at once, and a field-attributed
    /// <c>DomainValidationException</c>. That "all at once" property is why the overlay is keyed by
    /// field rather than driven off <c>Code</c> — a single code can only ever name one field.
    /// </para>
    /// </summary>
    private void ApplyServerFieldErrors(ApiProblem? problem)
    {
        if (problem is null || problem.Errors.Count == 0)
        {
            return;
        }

        foreach (var item in AllItems)
        {
            if (item.Field is { } field && problem.ErrorFor(field) is { } message)
            {
                _serverErrors[item.Key] = message;
            }
        }
    }

    /// <summary>
    /// The rows a Save attempt cannot proceed with, in catalogue order — the source for both the
    /// summary beside the button and the jump target.
    /// </summary>
    private IReadOnlyList<SettingItem> ErroredRows =>
        VisibleSections.SelectMany(section => section.Items)
            .Where(item => ErrorFor(item) is not null)
            .ToList();

    /// <summary>
    /// Sections whose round-trip rule is violated — a group-level failure with no single offending row.
    /// </summary>
    private IReadOnlyList<SettingSection> ErroredSections =>
        VisibleSections.Where(section => RoundTripError(section) is not null).ToList();

    /// <summary>
    /// A one-line summary of what is blocking Save, naming the sections involved. At 22 rows an admin
    /// could scroll to find the offending control; the catalogue is heading for 42 across eleven
    /// sections, where a disabled button with an error two screens away is an unexplained dead end.
    /// </summary>
    private string? BlockingSummary
    {
        get
        {
            if (!HasErrors)
            {
                return null;
            }

            var groups = VisibleSections
                .Where(section => RoundTripError(section) is not null
                                  || section.Items.Any(item => ErrorFor(item) is not null))
                .Select(section => section.Group)
                .ToList();

            var count = ErroredRows.Count + ErroredSections.Count;
            var noun = count == 1 ? "problem" : "problems";
            return $"{count} {noun} to fix in {string.Join(", ", groups)}";
        }
    }

    /// <summary>Unsaved-change count, so the page says how much is pending rather than only dotting rows.</summary>
    private int DirtyCount => VisibleSections.SelectMany(section => section.Items).Count(IsDirty);

    /// <summary>
    /// The Save button's count pill — the pending changes this press will commit. Suppressed during
    /// the post-save confirmation, where the button is saying "Saved" and there is nothing pending.
    /// </summary>
    private string? PendingBadge =>
        DirtyCount > 0 && !_justSaved ? DirtyCount.ToString("N0") : null;

    /// <summary>
    /// Every blocking problem, in catalogue order, for the <c>OdsErrorSummary</c> beside a disabled
    /// Save — the rows first, then the group-level round-trip conflicts.
    ///
    /// <para>
    /// Built from <c>VisibleSections</c>, deliberately: an entry whose row a search filter has removed
    /// from the DOM is a jump target that does not exist, and <c>window.odsFocusById</c> is a bare
    /// <c>.focus()</c> that no-ops silently on a missing id — a dead end presented as a fix.
    /// </para>
    ///
    /// <para>
    /// Each label is the row's own title plus the failure, because the panel is read away from the
    /// row: "Match timeout" alone does not say what is wrong with it.
    /// </para>
    /// </summary>
    private IReadOnlyList<OdsErrorSummaryProblem> BlockingProblems =>
    [
        .. VisibleSections.SelectMany(section => section.Items
            .Where(item => ErrorFor(item) is not null)
            .Select(item => new OdsErrorSummaryProblem(
                $"{item.Title} — {ErrorFor(item)}", section.Group, TitleId(item.Key)))),
        .. ErroredSections.Select(section => new OdsErrorSummaryProblem(
            RoundTripError(section)!, section.Group, RoundTripAlertId(section.Group))),
    ];

    /// <summary>
    /// Moves focus to the picked problem's control and flashes it — the same jump-to-record gesture
    /// the Accounts / Contracts / Tax statements lists use, so a sighted keyboard user can see where focus
    /// landed on a page where the target may be a full screen away from the button they pressed.
    /// </summary>
    private Task JumpToProblem(OdsErrorSummaryProblem problem) =>
        problem.TargetId is null ? Task.CompletedTask : JumpToElementAsync(problem.TargetId);

    /// <summary>
    /// The gesture itself: flash the target, move focus to it, then clear the flash. Shared by the
    /// blocking-problems summary and the credential signal, which aim at different kinds of row —
    /// a <c>SettingItem</c>'s control and an <c>OdsSecretSettingField</c>'s label — but perform the
    /// identical move once they have an element id.
    /// </summary>
    private async Task JumpToElementAsync(string targetId)
    {
        _flashTargetId = targetId;
        StateHasChanged();

        try
        {
            await JS.InvokeVoidAsync("odsFocusById", targetId);
        }
        catch
        {
            // The element isn't rendered (a concurrent re-render or filter change) — non-fatal; the
            // summary still names the section or credential the problem is in.
        }

        await Task.Delay(OdsTiming.RowFlashMs);

        // Guarded, so a second jump started while this one was waiting keeps ITS flash rather than
        // having it cleared out from under it by the first jump's timer.
        if (_flashTargetId == targetId)
        {
            _flashTargetId = null;
            StateHasChanged();
        }
    }

    // The row currently wearing the one-shot attention ring, by element id. Null the rest of the time.
    private string? _flashTargetId;

    /// <summary>
    /// The row field's own classes: <c>locked</c> greys the frame and its value while leaving the
    /// helper line fully legible — the reason it is locked is the part still worth reading — and
    /// <c>ss-flash</c> is the one-shot ring after a jump.
    /// </summary>
    private string? FieldClass(SettingItem item)
    {
        var parts = new List<string>(2);
        if (!Editable(item.Claim))
        {
            parts.Add("locked");
        }

        if (_flashTargetId == TitleId(item.Key))
        {
            parts.Add("ss-flash");
        }

        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    /// <summary>
    /// Moves focus to the first offending row, or to the first violated section's alert when the failure
    /// is group-level. Deliberately the first RENDERED one: <c>ErrorFor</c> returns null for rows the
    /// caller cannot edit, and a search filter removes rows from <c>VisibleSections</c> entirely, so
    /// "first in the catalogue" could be a row that is not on screen.
    /// </summary>
    private async Task JumpToFirstError()
    {
        var targetId = ErroredRows.Count > 0
            ? TitleId(ErroredRows[0].Key)
            : ErroredSections.Count > 0 ? RoundTripAlertId(ErroredSections[0].Group) : null;

        if (targetId is null)
        {
            return;
        }

        try
        {
            await JS.InvokeVoidAsync("odsFocusById", targetId);
        }
        catch
        {
            // The element isn't rendered (a concurrent re-render or filter change) — non-fatal, and
            // the summary text still names the section.
        }
    }
}
