using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Journal;

public partial class ImportJournalEntriesDialog
{
    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>Fires after an import that created or updated any rows, so the page reloads its list.</summary>
    [Parameter] public EventCallback OnChanged { get; set; }

    private IReadOnlyList<OdsUploadFile> _files = [];
    private string? _fileError;
    private string? _error;
    private bool _isImporting;

    private JournalEntryIcsImportResult? _result;
    private string _fileName = "file";
    private string? _announce;
    private bool _focusResultPending;
    private ElementReference _resultHeading;

    private readonly HashSet<string> _openReasons = new(StringComparer.Ordinal);

    private int SkippedTotal => _result?.Skipped.Sum(g => g.Count) ?? 0;

    private int SoftSkips => _result is null
        ? 0
        : _result.SkippedTagLinkCount + _result.SkippedContactLinkCount
          + _result.SkippedAttachmentCount + _result.SkippedPhotoCount;

    // The four link-level skip tallies, joined into one sentence (§3 #6 / §6): "3 tag links, 1 contact
    // link, and 2 attachments couldn't be resolved and were left off — the entries themselves imported."
    private string SoftSkipMessage
    {
        get
        {
            var parts = new List<string>();
            AddPart(parts, _result?.SkippedTagLinkCount ?? 0, "tag link", "tag links");
            AddPart(parts, _result?.SkippedContactLinkCount ?? 0, "contact link", "contact links");
            AddPart(parts, _result?.SkippedAttachmentCount ?? 0, "attachment", "attachments");
            AddPart(parts, _result?.SkippedPhotoCount ?? 0, "photo", "photos");

            var single = parts.Count == 1 && SoftSkips == 1;
            var joined = parts.Count switch
            {
                1 => parts[0],
                2 => $"{parts[0]} and {parts[1]}",
                _ => $"{string.Join(", ", parts.Take(parts.Count - 1))}, and {parts[^1]}",
            };
            return $"{joined} couldn't be resolved and {(single ? "was" : "were")} left off — the entries themselves imported. See reasons above.";
        }
    }

    private static void AddPart(List<string> parts, int count, string singular, string plural)
    {
        if (count > 0)
        {
            parts.Add($"{count} {(count == 1 ? singular : plural)}");
        }
    }

    // Effective limit (issue #343 §3/§11) — read from the live IImportLimitsCache, which itself
    // falls back to the shipped default when the load fails, so the dialog still opens and accepts
    // a file at that limit rather than erroring out (fe C4 — the fallback lives in the cache, not
    // here; see ImportLimitsCache.Fallback).
    private int _maxImportMb = ImportLimitsCache.Fallback.JournalIcsMaxImportMegabytes;
    private long _maxImportBytes = ImportLimitsCache.Fallback.JournalIcsMaxImportMegabytes * 1024L * 1024;

    protected override async Task OnParametersSetAsync()
    {
        if (!Open)
        {
            return;
        }

        var limits = await ImportLimits.GetAsync();
        _maxImportMb = limits.JournalIcsMaxImportMegabytes;
        _maxImportBytes = _maxImportMb * 1024L * 1024;
    }

    private void OnFilesChanged(IReadOnlyList<OdsUploadFile> files)
    {
        _files = files;
        _fileError = null;
        _error = null;
    }

    private async Task SubmitAsync()
    {
        if (_isImporting)
        {
            return;
        }

        _fileError = null;
        _error = null;

        var file = _files.FirstOrDefault()?.Source;
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
            _fileError = $"That file is larger than the {_maxImportMb} MB limit.";
        }

        if (_fileError is not null)
        {
            return;
        }

        _isImporting = true;
        try
        {
            // Same effective value used for the pre-check above and for OpenReadStream (issue #343 fe C2).
            var outcome = await Journal.ImportAsync(file!.ToApiUpload(_maxImportBytes));
            if (!outcome.IsSuccess)
            {
                _error = outcome.Error ?? "The file could not be imported.";
                return;
            }

            _fileName = string.IsNullOrWhiteSpace(file!.Name) ? "file" : file.Name;
            _result = outcome.Value;

            if (_result!.ImportedCount > 0 || _result.UpdatedCount > 0)
            {
                await OnChanged.InvokeAsync();
            }

            _announce = $"{_result.ImportedCount} imported, {_result.UpdatedCount} updated, {SkippedTotal} skipped.";
            _focusResultPending = true;
        }
        finally
        {
            _isImporting = false;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // The result heading is rendered inside MudDialog's (deferred) dialog content, so its
        // ElementReference can still be unconfigured on the render that first sets the result — guard on
        // Id (empty ⇒ not yet wired) so FocusAsync never throws, and treat focus as best-effort (the
        // summary is also announced via the live region for assistive tech).
        if (_focusResultPending && !string.IsNullOrEmpty(_resultHeading.Id))
        {
            _focusResultPending = false;
            try
            {
                await _resultHeading.FocusAsync();
            }
            catch (Exception)
            {
                // The element went away between the guard and the focus call — ignore.
            }
        }
    }

    private void ToggleReason(string reason)
    {
        if (!_openReasons.Remove(reason))
        {
            _openReasons.Add(reason);
        }
    }

    private void Reset()
    {
        _result = null;
        _files = [];
        _fileError = null;
        _error = null;
        _announce = null;
        _openReasons.Clear();
    }

    private Task CloseAsync()
    {
        Reset();
        return OpenChanged.InvokeAsync(false);
    }
}
