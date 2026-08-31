using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Finance;

public partial class AccountFilesSection
{
    [Parameter] public Guid AccountId { get; set; }

    /// <summary>
    /// The disclosure shell. False renders the section bare — no OdsCollapsible, no header — for a host
    /// that introduces it with its own OdsSectionDivider (an OdsRecordCard body).
    /// </summary>
    [Parameter] public bool Chrome { get; set; } = true;
    [Parameter] public bool CanAnalyze { get; set; }
    [Parameter] public bool CanEdit { get; set; }
    [Parameter] public string? AccountName { get; set; }

    private bool _isOpen = false;
    // Bound to OdsAccountFileTypeSelect (the DS picker keys on the enum name).
    private string _selectedFileType = nameof(AccountFileType.Other);
    private AccountFileType SelectedFileTypeEnum =>
        Enum.TryParse<AccountFileType>(_selectedFileType, out var fileType) ? fileType : AccountFileType.Other;

    protected override Task<ApiResult<List<ExistingAccountFile>>> ListFilesAsync() =>
        Accounts.ListFilesAsync(AccountId);

    protected override Task<ApiResult> DetachFileAsync(Guid fileId) =>
        Accounts.DetachFileAsync(AccountId, fileId);
    protected override string EntityNoun => "account";
    protected override ExistingFileMetadata MetadataOf(ExistingAccountFile file) => file.FileMetadata;
    protected override Task AttachAsync(Guid fileMetadataId) =>
        Files.AttachToAccountAsync(AccountId, fileMetadataId, SelectedFileTypeEnum);

    protected override void ShowPreview(ExistingAccountFile file, string blobUrl, string contentType)
    {
        _preview = new PreviewState(blobUrl, contentType, file.FileMetadata.FileName,
            file.FileMetadata.SizeBytes, file.FileMetadata.UploadedAtUtc, file.FileType, AccountName);
        _previewKey = Guid.NewGuid();
        _previewOpen = true;
    }

    private void ToggleOpen() => _isOpen = !_isOpen;

    // The flat Files page is not yet account-scoped; until it is, "View all" lands on the
    // global list. See Files.razor for the scoping follow-up.
    private void ViewAllFiles() => Navigation.NavigateTo("/files");

    private PreviewState? _preview;
    private Guid _previewKey;
    private bool _previewOpen;

    private ExistingAccountFile? _analysisFile;
    private Guid _analysisKey;
    private bool _analysisOpen;
    private FileAnalysisDialog.StartMode _analysisStart = FileAnalysisDialog.StartMode.Consent;
    private ResumableAnalysisSummary? _resumeSummary;

    // The account-scoped resumable map, keyed by file id — the single read that drives the
    // "Review pending" chip, the Resume review menu action, and the dialog's initial phase.
    private Dictionary<Guid, ResumableAnalysisSummary> _resumableByFile = new();

    private sealed record PreviewState(
        string BlobUrl, string ContentType, string FileName, long SizeBytes,
        DateTime UploadedAtUtc, AccountFileType FileType, string? AccountName);

    private Task CopyFileId(ExistingAccountFile af) =>
        Clipboard.CopyAsync(af.FileMetadata.Id.ToString(), "File ID copied.");

    // Opens the inline analysis modal in a host-resolved initial phase. The host already holds the
    // resumable map, so it decides: Resume review → straight to loading the saved review; Analyze with
    // a resumable job present → the resume-vs-reanalyze fork (no silent duplicate); Analyze with none →
    // the consent gate. The consent gate + analyze/import requests run inside the dialog.
    private void OpenAnalysis(ExistingAccountFile af, FileAnalysisDialog.StartMode start, ResumableAnalysisSummary? summary)
    {
        _analysisFile = af;
        _analysisStart = start;
        _resumeSummary = summary;
        _analysisKey = Guid.NewGuid();
        _analysisOpen = true;
    }

    // A review finished (imported) — re-read the resumable map so the file's "Review pending" hint
    // clears (no stale indicator) while the dialog's done screen is still showing.
    private async Task HandleAnalysisResolvedAsync()
    {
        await LoadResumableMapAsync();
        StateHasChanged();
    }

    // Re-read the map whenever the dialog closes, for any outcome — an import, a re-analyze, or a
    // resume that found the job already gone (NoLongerAvailable). This catches the cases OnResolved
    // doesn't, so the chip/menu never go stale after the dialog closes.
    private async Task OnAnalysisOpenChanged(bool open)
    {
        _analysisOpen = open;
        if (!open)
            await LoadResumableMapAsync();
    }

    // One account-scoped read for the whole Files section. Fail-closed: any error leaves the map empty,
    // so the section simply shows no Resume affordances (Analyze still works).
    private async Task LoadResumableMapAsync()
    {
        if (!CanAnalyze)
        {
            _resumableByFile = new();
            return;
        }

        // Silent by design: the file-analysis flag being off answers 503, which is not an error here.
        var list = (await Accounts.GetResumableAnalysisJobsAsync(AccountId)).ValueOr([]);
        _resumableByFile = list
            .GroupBy(s => s.FileId)
            .ToDictionary(g => g.Key, g => g.First());
    }

    // ── OdsFilesTable adapters ──
    // The shared table reads denormalized rows; the AccountFileType drives the kind
    // chip + avatar (its registry icon/color/soft), and a row maps back to its file
    // by id for the kind lookup and the action menu.
    private IEnumerable<OdsFilesRow> FileRows => items.Select(f => new OdsFilesRow
    {
        Id = f.FileMetadata.Id.ToString(),
        Name = f.FileMetadata.FileName,
        // The enum key (not the registry label) so the inline edit picker round-trips on it.
        Kind = f.FileType.ToString(),
        SizeBytes = f.FileMetadata.SizeBytes,
        UploadedAtUtc = f.FileMetadata.UploadedAtUtc,
        ValidFrom = f.ValidFrom,
        ValidTo = f.ValidTo,
        IssuedAt = f.IssuedAt,
        IssuedBy = f.IssuedBy,
        // An open, resumable review surfaces as an additive "Review pending" chip — meaning carried as
        // text, with a full accessible name (file + count).
        StatusBadge = ResumableFor(f) is { } rj
            ? new OdsFilesRowStatusBadge
            {
                Text = $"Review pending · {rj.PendingCount}",
                AriaLabel = $"Resume analysis review for {f.FileMetadata.FileName} — {rj.PendingCount} candidate{(rj.PendingCount == 1 ? "" : "s")} pending",
            }
            : null,
    });

    private ResumableAnalysisSummary? ResumableFor(ExistingAccountFile file) =>
        _resumableByFile.GetValueOrDefault(file.FileMetadata.Id);

    // Contacts feed the "Issued by" picker (edit panel) and resolve the issuer
    // id to a display name in the detail well.
    private List<ExistingContact> _contacts = [];
    private IReadOnlyList<OdsOption> _issuerOptions = [];

    private string? IssuerName(Guid? issuedBy) =>
        issuedBy is null ? null : _contacts.FirstOrDefault(c => c.ContactId == issuedBy)?.ResolvedDisplayName;

    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
        if (!OperatingSystem.IsBrowser())
            return;

        _contacts = [.. await ReferenceData.ContactsAsync()];
        _issuerOptions = [.. _contacts
            .Where(c => c.Archived is null)
            .Select(c => new OdsOption(c.ContactId.ToString(), c.ResolvedDisplayName))];

        await LoadResumableMapAsync();
        await LoadAnalysisAvailabilityAsync();
    }

    /// <summary>
    /// Whether AI document analysis is switched on instance-wide (issue #439).
    ///
    /// <para>
    /// Fetched so the Analyze affordance can render <em>disabled with a reason</em> rather than letting
    /// a user pick a document, read the consent gate, affirm it and only then receive a <c>503</c>.
    /// That was always a poor sequence for a consent interaction and became reachable at runtime once
    /// the switch became admin-editable.
    /// </para>
    ///
    /// <para>
    /// Starts <see langword="false"/> and stays false when the fetch fails, matching the gate's own
    /// posture: an unresolved disclosure means the affordance is not offered. It is never a default of
    /// true.
    /// </para>
    /// </summary>
    private bool _analysisEnabled;

    private async Task LoadAnalysisAvailabilityAsync()
    {
        if (!CanAnalyze)
            return;

        // Stale-while-revalidate, and invalidated by a settings save in this browser — so an admin who
        // toggles the switch sees the menu change without a reload.
        var disclosure = await Disclosures.GetAsync();
        _analysisEnabled = Disclosures.IsResolved && disclosure.Enabled;
    }

    private ExistingAccountFile FileById(string id) =>
        items.First(f => f.FileMetadata.Id.ToString() == id);

    private static OdsFileKindMeta KindMeta(AccountFileType type)
    {
        var t = OdsTypeRegistries.AccountFileTypeOf(type);
        return new OdsFileKindMeta(t.Icon, t.Color, t.Soft);
    }

    // Edit / Delete are owned by the shared table (the Edit-file dialog · OnDelete);
    // the host supplies only the file-specific items.
    private IReadOnlyList<OdsMenuItem> BuildMenu(ExistingAccountFile file)
    {
        var id = file.FileMetadata.Id;
        var menu = new List<OdsMenuItem>();

        if (CanDownload && IsPreviewable(file.FileMetadata.ContentType))
            menu.Add(new OdsMenuItem { Icon = "visibility", Label = "Preview", Disabled = previewingFiles.Contains(id), OnClick = EventCallback.Factory.Create(this, () => PreviewFileAsync(file)) });

        if (CanDownload)
            menu.Add(new OdsMenuItem { Icon = "download", Label = "Download", Disabled = downloadingFiles.Contains(id), OnClick = EventCallback.Factory.Create(this, () => DownloadFileAsync(file)) });

        if (CanAnalyze && file.FileType == AccountFileType.Statement)
        {
            var resumable = ResumableFor(file);

            // Resume review — only when this file has an open, resumable job.
            if (resumable is not null)
                menu.Add(new OdsMenuItem { Icon = "history", Label = "Resume review", OnClick = EventCallback.Factory.Create(this, () => OpenAnalysis(file, FileAnalysisDialog.StartMode.Resume, resumable)) });

            // Analyze distinguishes resume-vs-reanalyze instead of silently creating a duplicate: a
            // resumable job present → the confirm fork; otherwise the normal consent gate.
            //
            // With analysis switched off instance-wide the item renders DISABLED with the reason in
            // text (issue #439) — never greyed out and left to be inferred, and never opening a consent
            // gate for a transfer that cannot happen. The Resume item above needs no equivalent: with
            // the switch off the resumable-map read 503s and yields an empty map, so no Resume
            // affordance renders in the first place.
            menu.Add(new OdsMenuItem
            {
                Icon = "auto_fix_high",
                Label = "Analyze",
                Disabled = !_analysisEnabled,
                Description = _analysisEnabled ? null : "AI document analysis is turned off for this instance.",
                OnClick = EventCallback.Factory.Create(this, () => OpenAnalysis(file, resumable is not null ? FileAnalysisDialog.StartMode.ReanalyzeConfirm : FileAnalysisDialog.StartMode.Consent, resumable)),
            });
        }

        menu.Add(new OdsMenuItem { Icon = "fingerprint", TrailingIcon = "content_copy", Label = "Copy ID", OnClick = EventCallback.Factory.Create(this, () => CopyFileId(file)) });

        return menu;
    }

    // ── Inline edit + delete (table-owned lifecycle) ──
    // Edit appears only when CanEdit; the default panel commits an OdsFileEdit
    // (name + AccountFileType key), persisted via the file-metadata + account-file
    // endpoints — the same two PUTs the retired EditFileDialog made.
    private EventCallback<OdsRecordSaveEventArgs> SaveAction =>
        CanEdit ? EventCallback.Factory.Create<OdsRecordSaveEventArgs>(this, HandleSaveAsync) : default;

    private EventCallback<OdsFilesRow> DeleteAction =>
        CanDelete ? EventCallback.Factory.Create<OdsFilesRow>(this, row => ConfirmDeleteAsync(FileById(row.Id))) : default;

    private async Task HandleSaveAsync(OdsRecordSaveEventArgs args)
    {
        if (args.Patch is not OdsFileEdit patch || args.Key is not string key)
            return;

        var file = items.FirstOrDefault(f => f.FileMetadata.Id.ToString() == key);
        if (file is null)
            return;

        var newName = patch.Name.Trim();
        var newType = Enum.TryParse<AccountFileType>(patch.Kind, out var parsed) ? parsed : file.FileType;
        var nameChanged = !string.Equals(newName, file.FileMetadata.FileName, StringComparison.Ordinal);
        var typeChanged = newType != file.FileType;
        var validityChanged =
            patch.ValidFrom != file.ValidFrom || patch.ValidTo != file.ValidTo
            || patch.IssuedAt != file.IssuedAt || patch.IssuedBy != file.IssuedBy;

        if (!nameChanged && !typeChanged && !validityChanged)
            return;

        // Rename via the file-metadata endpoint — the existing description is sent
        // back unchanged so a rename doesn't wipe it (the service overwrites it).
        if (nameChanged
            && await Files.UpdateMetadataAsync(file.FileMetadata.Id, file.FileMetadata.Description, newName) is null)
        {
            Snackbar.Add("Unable to rename file.", Severity.Error);
            return;
        }

        // Document type + validity metadata via the account-file endpoint.
        if ((typeChanged || validityChanged)
            && !(await Accounts.UpdateFileAsync(
                AccountId,
                file.FileMetadata.Id,
                new UpdateAccountFileRequest
                {
                    FileType = newType,
                    ValidFrom = patch.ValidFrom,
                    ValidTo = patch.ValidTo,
                    IssuedAt = patch.IssuedAt,
                    IssuedBy = patch.IssuedBy,
                })).Toast(Snackbar, "Unable to update document type"))
            return;

        Snackbar.Add("File updated.", Severity.Success);
        await LoadFilesAsync();
    }
}
