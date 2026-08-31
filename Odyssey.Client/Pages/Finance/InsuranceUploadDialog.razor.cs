using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// Code-behind for <c>InsuranceUploadDialog.razor</c> — see that file's header for what the dialog is
/// and why its target is sometimes stated and sometimes offered. Only the two inline render fragments
/// stay in the markup, because <c>@&lt;text&gt;</c> is Razor syntax.
/// </summary>
public partial class InsuranceUploadDialog
{
    // The cap is admin-editable (issue #421 Wave 4), so it is read rather than compiled in and every
    // message names the number actually in force. Seeded with the shipped fallback so a render that
    // beats the fetch still validates against a sane number rather than zero.
    private UploadLimitsDto _uploadLimits = UploadLimitsCache.Fallback;

    protected override async Task OnInitializedAsync()
    {
        _uploadLimits = await UploadLimits.GetAsync();
    }

    [Parameter, EditorRequired] public ExistingInsurancePolicy Policy { get; set; } = default!;

    /// <summary>The period the documents attach to. Required — there is no other target. When the
    /// picker is shown this is its default, not its only value.</summary>
    [Parameter, EditorRequired] public Guid RenewalId { get; set; }

    /// <summary>The user opened this from that period's own panel, so the target is theirs and is
    /// shown rather than offered.</summary>
    [Parameter] public bool LockPeriod { get; set; }

    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>Raised after a successful attach, carrying the period the documents landed on, so the
    /// host can re-fetch and announce the right period's new count.</summary>
    [Parameter] public EventCallback<Guid> OnSaved { get; set; }

    private static readonly string[] AllowedExtensions = [".pdf", ".jpg", ".jpeg", ".png", ".webp"];

    // The PolicyFileType vocabulary projected to the OdsFileUpload kind shape (per-file picker).
    private static readonly IReadOnlyList<OdsFileKind> _kinds =
        [.. OdsTypeRegistries.PolicyFileTypes.Select(t => new OdsFileKind
        {
            Key = t.Key, Label = t.Label, Icon = t.Icon, Color = t.Color, Soft = t.Soft,
        })];

    private List<OdsUploadFile> _files = [];
    private string? _error;
    private bool _isUploading;

    private Guid _target;
    private List<OdsOption> _periodOptions = [];

    /// <summary>A picker only where there is a choice to make: the target was inferred rather than
    /// chosen, and there is more than one period to choose between.</summary>
    private bool _choosable;


    // OnParametersSet, not OnInitialized: the host re-keys this dialog per open, but a re-target on
    // the SAME instance would otherwise keep naming the period the dialog first mounted with.
    protected override void OnParametersSet()
    {
        // Newest period first, matching the renewal history's own order.
        var periods = Policy.Renewals
            .OrderByDescending(r => r.FromDate)
            .ThenByDescending(r => r.CreatedAtUtc)
            .ToList();

        _periodOptions = [.. periods.Select(r => new OdsOption(r.PolicyRenewalId.ToString(), PeriodLabel(r)))];

        // A re-target keeps the user's own pick; a re-key (a fresh open) resets to the host's.
        if (!periods.Any(r => r.PolicyRenewalId == _target))
        {
            _target = RenewalId;
        }

        _choosable = !LockPeriod && periods.Count > 1;
    }

    private void OnTargetChanged(string value)
    {
        if (Guid.TryParse(value, out var renewalId))
        {
            _target = renewalId;
        }
    }

    private static string PeriodLabel(ExistingPolicyRenewal r) =>
        $"Period {r.FromDate:MMM dd, yyyy} → {r.ToDate:MMM dd, yyyy}";

    private string PeriodLabel(Guid renewalId) =>
        Policy.Renewals.FirstOrDefault(r => r.PolicyRenewalId == renewalId) is { } period
            ? PeriodLabel(period)
            : "this renewal period";

    // Controlled list — enforce the allow-list and per-file size cap before anything lands in the picker.
    private void OnFilesChanged(IReadOnlyList<OdsUploadFile> files)
    {
        var kept = new List<OdsUploadFile>();
        foreach (var f in files)
        {
            var ext = Path.GetExtension(f.Name).ToLowerInvariant();
            if (f.Source is not null && !AllowedExtensions.Contains(ext))
            {
                Snackbar.Add($"{f.Name}: unsupported type. Allowed: .pdf, .jpg, .jpeg, .png, .webp", Severity.Warning);
                continue;
            }
            if (f.SizeBytes > _uploadLimits.MaxUploadBytes)
            {
                Snackbar.Add($"{f.Name}: exceeds the {_uploadLimits.MaxUploadMegabytes} MB limit.", Severity.Warning);
                continue;
            }
            kept.Add(f);
        }
        _files = kept;
        if (_error is not null && _files.Count > 0) _error = null;
    }

    private static string GuessKind(string name)
    {
        var isPdf = name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
        var lower = name.ToLowerInvariant();
        if (lower.Contains("claim") || lower.Contains("skade")) return nameof(PolicyFileType.ClaimDocument);
        if (lower.Contains("invoice") || lower.Contains("premium") || lower.Contains("receipt") || lower.Contains("faktura")) return nameof(PolicyFileType.Invoice);
        if (lower.Contains("terms") || lower.Contains("wording") || lower.Contains("conditions") || lower.Contains("vilk")) return nameof(PolicyFileType.TermsAndConditions);
        return isPdf ? nameof(PolicyFileType.PolicyDocument) : nameof(PolicyFileType.Other);
    }

    private static PolicyFileType TypeOf(OdsUploadFile f) =>
        Enum.TryParse<PolicyFileType>(f.Kind, out var t) ? t : PolicyFileType.Other;

    private async Task SubmitAsync()
    {
        if (_isUploading) return;
        // The target can be deleted between render and submit. Guid is non-nullable, so the type does
        // not provide this guard on its own — a default Guid would post to a route that 404s.
        if (_target == Guid.Empty)
        {
            Snackbar.Add("Choose a renewal period to attach these documents to.", Severity.Warning);
            return;
        }
        if (_files.Count == 0) { _error = "Add at least one file."; return; }

        _isUploading = true;
        var attached = 0;
        try
        {
            foreach (var file in _files)
            {
                if (file.Source is null) continue;
                try
                {
                    var uploaded = await FilesApi.UploadAsync(file.Source.ToApiUpload(_uploadLimits.MaxUploadBytes));
                    var finalName = file.Name.Trim();
                    if (!string.IsNullOrEmpty(finalName) && finalName != file.Source.Name)
                        await FilesApi.UpdateMetadataAsync(uploaded.Id, null, finalName);
                    var request = new AttachInsurancePolicyFileRequest { FileId = uploaded.Id, FileType = TypeOf(file) };
                    var attach = await Insurance.AttachRenewalFileAsync(Policy.InsurancePolicyId, _target, request);
                    if (!attach.IsSuccess)
                        throw new InvalidOperationException(attach.Error);
                    attached++;
                }
                catch (Exception)
                {
                    // Keep the toast user-friendly — don't surface raw status codes / response bodies.
                    Snackbar.Add($"Couldn't attach \"{file.Name}\".", Severity.Error);
                }
            }

            if (attached > 0)
            {
                Snackbar.Add($"{attached} document(s) attached.", Severity.Success);
                // The period, not just the count: the row menu inferred it, and the picker may have
                // changed it, so the host cannot assume the id it opened the dialog with.
                await OnSaved.InvokeAsync(_target);
                await OpenChanged.InvokeAsync(false);
            }
        }
        finally
        {
            _isUploading = false;
        }
    }

    private async Task CloseAsync() => await OpenChanged.InvokeAsync(false);
}
