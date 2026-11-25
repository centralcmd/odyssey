using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Attachments;
using Odyssey.Client.Services;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Recipes;

namespace Odyssey.Client.Pages.Library;

/// <summary>What a new composition starts from: the text and the Library files of an import being started again.</summary>
public sealed record RecipeImportSeed(string? Text, IReadOnlyList<Guid> LibraryFileIds);

public partial class RecipeImportDialog
{
    private const string TextId = "ri-f-text";
    private const string FilesId = "ri-f-upload";
    private const string FilesLabelId = "ri-f-upload-label";
    private const string PhotoNoteId = "ri-photo-note";
    private const string ConsentHeadingId = "ri-consent-h";

    private sealed record Refusal(Severity Severity, string Text, bool ToList = false);

    [Inject] private IRecipeImportsApiClient Imports { get; set; } = default!;

    [Inject] private AuthenticationStateProvider AuthenticationState { get; set; } = default!;

    [Inject] private IJSRuntime Js { get; set; } = default!;

    [Parameter] public bool Open { get; set; }

    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>Start a new import from an earlier one: its text and its files, preselected; the box unticked.</summary>
    [Parameter] public RecipeImportSeed? Seed { get; set; }

    [Parameter] public string? ReturnFocusToId { get; set; }

    /// <summary>Raised with the created (or, for a repeated key, existing) import once the server accepted it.</summary>
    [Parameter] public EventCallback<RecipeImportDetail> OnSent { get; set; }

    private OdsLiveAnnouncer? _announcer;
    private RecipeImportOptions? _options;
    private string? _shownVersion;
    private bool _loadingOptions;
    private int _step;
    private string _text = string.Empty;
    private string _fileTab = DocumentSourcePanel.UploadTab;
    private List<OdsUploadFile> _uploads = [];
    private readonly HashSet<Guid> _picked = [];
    private List<DocumentSourceFile> _library = [];
    private bool _libraryLoading;
    private bool _libraryFailed;
    private bool _agree;
    private bool _stale;
    private bool _sending;
    private bool _focusPending;
    private Refusal? _refusal;
    private readonly Dictionary<string, string> _errors = [];

    // One key per composition (R2-N3): a retried post of THIS composition is answered with the same import.
    private Guid _clientRequestId = Guid.NewGuid();

    private bool _canUpload;
    private bool _canReadFiles;

    private bool CanProceed => _options is { Enabled: true };

    private IReadOnlyList<OdsWizardStep> WizardSteps =>
    [
        new("Recipe", State: _errors.Count > 0 ? OdsWizardStepState.Error : _step > 0 ? OdsWizardStepState.Done : OdsWizardStepState.None),
        new("Send"),
    ];

    private IReadOnlyList<OdsErrorSummaryProblem> Problems =>
        [.. _errors.Select(e => new OdsErrorSummaryProblem(e.Value, e.Key == "text" ? "Recipe text" : "Files", e.Key == "text" || !_canReadFiles ? TextId : FilesId))];

    /// <summary>Send needs consent and an import that is switched on; after a "turned off" refusal it stays off.</summary>
    private bool CanSend => _agree && _options is { Enabled: true };

    private string? ErrorOf(string key) => _errors.GetValueOrDefault(key);

    private string? AcceptAttribute => _options is { } o ? string.Join(',', o.AllowedContentTypes) : null;

    private string? UploadHint => _options is { } o
        ? $"{RecipeImportText.TypeList(o.AllowedContentTypes)} · photos up to {RecipeImportText.Megabytes(o.MaxFileBytes.GetValueOrDefault("image/jpeg"))}, "
          + $"PDFs up to {RecipeImportText.Megabytes(o.MaxFileBytes.GetValueOrDefault("application/pdf"))} · up to {o.MaxFilesPerImport} files, {RecipeImportText.Megabytes(o.MaxTotalBytes)} in all"
        : null;

    private int? LibraryCap => _options is { } o ? Math.Max(0, o.MaxFilesPerImport - _uploads.Count) : null;

    private string? CapReason => _options is { } o ? $"At most {o.MaxFilesPerImport} files" : null;

    private long RawTotal => _uploads.Sum(f => f.SizeBytes ?? 0) + _library.Where(f => _picked.Contains(f.Id)).Sum(f => f.SizeBytes);

    private string Summary => string.Join(", ", new[]
    {
        _text.Trim().Length > 0 ? $"Text ({RecipeImportText.Count(_text.Trim().Length)} characters)" : null,
        _uploads.Count > 0 ? $"{_uploads.Count} uploaded {(_uploads.Count == 1 ? "file" : "files")}" : null,
        _picked.Count > 0 ? $"{_picked.Count} {(_picked.Count == 1 ? "file" : "files")} from Files" : null,
    }.Where(s => s is not null));

    protected override async Task OnInitializedAsync()
    {
        var user = await AuthenticationState.GetUserAsync();
        _canReadFiles = user.HasPermission(PermissionClaims.FilesRead);
        // The upload endpoint needs files.create AND files.read; without both the panel is From Files only.
        _canUpload = _canReadFiles && user.HasPermission(PermissionClaims.FilesCreate);
        _text = Seed?.Text ?? string.Empty;
        if (Seed is { LibraryFileIds.Count: > 0 } seed)
        {
            _picked.UnionWith(seed.LibraryFileIds);
            _fileTab = DocumentSourcePanel.LibraryTab;
        }
        else if (!_canUpload)
        {
            _fileTab = DocumentSourcePanel.LibraryTab;
        }

        await ReloadOptionsAsync();
        await LoadLibraryAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusPending)
        {
            _focusPending = false;
            // The body is teleported into the dialog provider, which renders after this component (gotchas doc).
            await Js.InvokeVoidAsync("odsFocusFirstIn", _step == 0 ? TextId : ConsentHeadingId, true, 2);
        }
    }

    private async Task ReloadOptionsAsync()
    {
        _loadingOptions = true;
        var result = await Imports.GetOptionsAsync();
        _options = result.IsSuccess ? result.Value : null;
        _shownVersion = _options?.DisclosureVersion;
        _loadingOptions = false;
    }

    private async Task LoadLibraryAsync()
    {
        if (!_canReadFiles)
        {
            return;
        }

        _libraryLoading = true;
        _libraryFailed = false;
        var result = await Imports.ListLibraryFilesAsync();
        _libraryFailed = !result.IsSuccess;
        _library = [.. result.ValueOr(new()).Items.Select(f => new DocumentSourceFile(f.FileId, f.FileName, f.ContentType, f.SizeBytes, f.UploadedAt))];
        // A preselected file the server no longer offers (moved, deleted) is dropped, never sent by id.
        _picked.IntersectWith(_library.Select(f => f.Id));
        _libraryLoading = false;
        StateHasChanged();
    }

    private DocumentSourceState LibraryStateOf(DocumentSourceFile file) =>
        _options is { } o && !o.AllowedContentTypes.Contains(file.ContentType) ? DocumentSourceState.TypeNotAllowed : DocumentSourceState.Available;

    private void TogglePick(DocumentSourceFile file)
    {
        if (!_picked.Remove(file.Id))
        {
            _picked.Add(file.Id);
        }

        ClearFileErrors();
    }

    private void OnUploadsChanged(IReadOnlyList<OdsUploadFile> files)
    {
        _uploads = [.. files];
        ClearFileErrors();
    }

    private void OnTextChanged(string? value)
    {
        _text = value ?? string.Empty;
        _errors.Remove("text");
        _errors.Remove("sources");
    }

    private void ClearFileErrors()
    {
        _errors.Remove("upload");
        _errors.Remove("sources");
    }

    /// <summary>Step 1 checks what the client can; the server also checks the encoded size and the Library area.</summary>
    private void CheckSources()
    {
        _errors.Clear();
        if (_options is not { } o)
        {
            return;
        }

        var files = _uploads.Count + _picked.Count;
        if (_text.Trim().Length == 0 && files == 0)
        {
            _errors["sources"] = "Paste recipe text or add a file.";
        }

        if (_text.Length > o.MaxSourceTextLength)
        {
            _errors["text"] = $"The text is {RecipeImportText.Count(_text.Length)} characters; the limit is {RecipeImportText.Count(o.MaxSourceTextLength)}.";
        }

        if (files > o.MaxFilesPerImport)
        {
            _errors["sources"] = $"An import holds at most {o.MaxFilesPerImport} files. Remove {files - o.MaxFilesPerImport}.";
        }
        else if (RawTotal > o.MaxTotalBytes)
        {
            _errors["sources"] = $"The files add up to {RecipeImportText.Megabytes(RawTotal)}; one import holds at most {RecipeImportText.Megabytes(o.MaxTotalBytes)}.";
        }

        foreach (var upload in _uploads)
        {
            var type = RecipeImportText.ContentTypeFor(upload.Name);
            if (!o.AllowedContentTypes.Contains(type))
            {
                _errors["upload"] = $"“{UploadFailureText.Clean(upload.Name)}” can’t be read. Use a {RecipeImportText.TypeList(o.AllowedContentTypes)}.";
                break;
            }

            if (upload.SizeBytes > o.MaxFileBytes.GetValueOrDefault(type))
            {
                _errors["upload"] = $"“{UploadFailureText.Clean(upload.Name)}” is larger than the {RecipeImportText.Megabytes(o.MaxFileBytes[type])} limit for a {RecipeImportText.TypeLabel(type).ToLowerInvariant()}.";
                break;
            }
        }
    }

    private async Task NextAsync()
    {
        if (!CanProceed)
        {
            return;
        }

        CheckSources();
        if (_errors.Count > 0)
        {
            await Js.InvokeVoidAsync("odsFocusFirstIn", _errors.ContainsKey("text") || !_canReadFiles ? TextId : FilesId, true, 2);
            return;
        }

        await MoveToAsync(1);
    }

    private Task GoToAsync(int step) => _sending ? Task.CompletedTask : step == 1 ? NextAsync() : MoveToAsync(0);

    private async Task MoveToAsync(int step)
    {
        _step = step;
        _focusPending = true;
        if (_announcer is not null)
        {
            await _announcer.Announce($"Step {step + 1} of 2: {(step == 0 ? "Recipe" : "Send")}");
        }
    }

    private async Task JumpAsync(OdsErrorSummaryProblem problem)
    {
        if (_step != 0)
        {
            await MoveToAsync(0);
            return;
        }

        await Js.InvokeVoidAsync("odsFocusFirstIn", problem.TargetId ?? FilesId, true, 2);
    }

    /// <summary>
    /// Re-reads the options first and refuses to post when the disclosure changed since it was shown: the consent was
    /// given for different facts (A3). Everything typed and chosen survives every refusal.
    /// </summary>
    private async Task SendAsync()
    {
        if (_sending || !CanSend)
        {
            return;
        }

        _refusal = null;
        var fresh = await Imports.GetOptionsAsync();
        if (!fresh.IsSuccess || fresh.Value is null)
        {
            _options = null;
            return;
        }

        if (!string.Equals(fresh.Value.DisclosureVersion, _shownVersion, StringComparison.Ordinal))
        {
            ShowChanged(fresh.Value);
            return;
        }

        _sending = true;
        StateHasChanged();
        var maxUpload = _options!.MaxFileBytes.Values.DefaultIfEmpty(0).Max();
        var uploads = _uploads
            .Where(f => f.Source is not null)
            .Select(f => f.Source!.ToApiUpload(maxUpload) with { ContentType = RecipeImportText.ContentTypeFor(f.Name) })
            .ToList();
        var result = await Imports.CreateAsync(new RecipeImportSubmission(
            _clientRequestId, _text.Trim().Length > 0 ? _text : null, [.. _picked], uploads, _shownVersion!, ConsentAcknowledged: _agree));
        _sending = false;

        if (result.IsSuccess && result.Value is { } created)
        {
            await OnSent.InvokeAsync(created);
            await OpenChanged.InvokeAsync(false);
            return;
        }

        HandleRefusal(result, fresh.Value);
    }

    private void ShowChanged(RecipeImportOptions fresh)
    {
        _options = fresh;
        _shownVersion = fresh.DisclosureVersion;
        _stale = true;
        _agree = false;
        _focusPending = true;
    }

    private void HandleRefusal(ApiResult<RecipeImportDetail> result, RecipeImportOptions options)
    {
        var code = result.Problem?.Code;
        switch (code)
        {
            case RecipeImportErrorCodes.DisclosureChanged:
                ShowChanged(options);
                return;
            case RecipeImportErrorCodes.Disabled:
                _options = options with { Enabled = false };
                _refusal = new(Severity.Info, "AI recipe import was turned off by an administrator. Nothing was sent.");
                return;
            case RecipeImportErrorCodes.LimitReached:
                // The number comes from the server's own sentence; the client holds no copy of the cap.
                _refusal = new(Severity.Error,
                    $"{result.Problem?.Detail ?? "You have too many imports waiting."} Wait for one to finish, or cancel one.", ToList: true);
                return;
            case RecipeImportErrorCodes.Busy:
                _refusal = new(Severity.Warning, "Odyssey is busy with your other imports. Try again in a moment.");
                return;
            case RecipeImportErrorCodes.Unavailable:
                _options = null;
                return;
        }

        if (result.Status == HttpStatusCode.TooManyRequests)
        {
            _refusal = new(Severity.Warning, $"You’ve started a lot of imports in a short time. Try again {UploadRetryWording.When(result.RetryAfter)}.");
            return;
        }

        // A 400, 404, 413 or 415 is about what was added: back to the step that holds it.
        if (result.Status is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnsupportedMediaType)
        {
            _errors["sources"] = result.Status switch
            {
                HttpStatusCode.NotFound => "A chosen file is no longer in the Library area of Files. Remove it and choose again.",
                HttpStatusCode.UnsupportedMediaType => $"A file can’t be read. Use a {RecipeImportText.TypeList(options.AllowedContentTypes)}.",
                HttpStatusCode.RequestEntityTooLarge => result.Problem?.Detail ?? $"The files are too large to send; one import holds at most {RecipeImportText.Megabytes(options.MaxTotalBytes)}.",
                _ => result.Problem?.Detail ?? "Something you added can’t be sent.",
            };
            _step = 0;
            _focusPending = true;
            return;
        }

        _refusal = new(Severity.Error, "The import couldn’t be started. Nothing was sent. Try again.");
    }

    private static bool IsHttps(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>Closing mid-send would lose the answer to a post the server may still accept, so it waits.</summary>
    private Task CloseAsync() => _sending ? Task.CompletedTask : OpenChanged.InvokeAsync(false);
}
