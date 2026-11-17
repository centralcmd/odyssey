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

    // The picker's container: the browser keeps the picked File for it, so a large file uploads straight from disk.
    private const string UploadId = "journal-import-upload";

    // The most the typed-client fallback reads into the WASM heap. Only reached when the browser holds no handle to
    // the picked file. A named per-surface megabyte constant, not a server cap: it narrows nothing the server enforces.
    private const int FallbackMaxMegabytes = 512;
    private const long FallbackMaxBytes = FallbackMaxMegabytes * 1024L * 1024;

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

    private bool HasAttachedContent => _result is { } r
        && (r.StoredFileCount > 0 || r.ReusedFileCount > 0 || r.StoredPhotoCount > 0 || r.ReusedPhotoCount > 0);

    // "2 attached files stored; 1 photo added to the library; 3 matched to files or photos already stored, so not
    // stored again."
    private string AttachedContentMessage
    {
        get
        {
            var parts = new List<string>();
            var storedFiles = _result?.StoredFileCount ?? 0;
            var storedPhotos = _result?.StoredPhotoCount ?? 0;
            var reused = (_result?.ReusedFileCount ?? 0) + (_result?.ReusedPhotoCount ?? 0);
            if (storedFiles > 0)
            {
                parts.Add($"{storedFiles} attached {(storedFiles == 1 ? "file" : "files")} stored");
            }

            if (storedPhotos > 0)
            {
                parts.Add($"{storedPhotos} {(storedPhotos == 1 ? "photo" : "photos")} added to the photo library");
            }

            if (reused > 0)
            {
                parts.Add($"{reused} matched to {(reused == 1 ? "a file or photo" : "files or photos")} already stored, so not stored again");
            }

            return string.Join("; ", parts) + ".";
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
            _fileError = $"That file is larger than the {ByteSize.Format(_maxImportBytes)} limit.";
        }

        if (_fileError is not null)
        {
            return;
        }

        _isImporting = true;
        // A file carrying files and photos can take minutes to upload; say it started (WCAG 4.1.3).
        _announce = "Importing journal entries…";
        try
        {
            // The browser posts the picked file itself when it can — an import carrying files and photos can be far
            // larger than the WASM heap. The typed client is the fallback, with the same effective value used for the
            // pre-check above and for OpenReadStream (issue #343 fe C2).
            var native = await NativeTransfer.TryUploadAsync<JournalEntryIcsImportResult>(UploadId, file!, Journal.ImportUrl, "text/calendar");
            if (native is null && file!.Size > FallbackMaxBytes)
            {
                _error = $"This browser could not stream the file, and files over {ByteSize.Format(FallbackMaxBytes)} "
                    + "cannot be read into the page. Choose the file again with Browse, then import.";
                return;
            }

            var outcome = native ?? await Journal.ImportAsync(file!.ToApiUpload(_maxImportBytes));
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

            _announce = $"{_result.ImportedCount} imported, {_result.UpdatedCount} updated, {SkippedTotal} skipped."
                + (HasAttachedContent ? " " + AttachedContentMessage : "");
            _focusResultPending = true;
        }
        finally
        {
            _isImporting = false;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (Open && _result is null)
        {
            await NativeTransfer.WatchAsync(UploadId);
        }

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

    private async Task CloseAsync()
    {
        Reset();
        // Drop the browser's reference to the picked file, so a closed dialog holds no handle to it.
        await NativeTransfer.ForgetAsync(UploadId);
        await OpenChanged.InvokeAsync(false);
    }
}
