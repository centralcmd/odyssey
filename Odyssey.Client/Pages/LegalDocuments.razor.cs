using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using Odyssey.Dtos.Application;
using Odyssey.Client.Auth;
using Odyssey.Client.Services;

namespace Odyssey.Client.Pages;

/// <summary>
/// The admin Terms of Service authoring surface (issue #354 §3 state 5, §7.5–§7.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a page and not a panel inside <c>/settings</c>.</b> The spec described this as a bespoke panel
/// within System settings; the design system that followed it registers "Terms of Service" as its own
/// entry in the System nav module, gives it a page header with its own primary action, and ships a
/// standalone page preview. Both agree on the substantive point the spec was making — it must not be a
/// row in the one-control-per-row settings grid, which cannot host a 50,000-character editor or a
/// version table — and the design system is this repo's source of truth for UI, so it is a page.
/// </para>
/// <para>
/// It is gated by <c>users.manage</c>, the same claim as the version-management endpoints, rather than
/// the <c>system-settings.read</c> that gates <c>/settings</c>.
/// </para>
/// </remarks>
public partial class LegalDocuments
{
    private enum Phase { Loading, Ready, Error }

    private static readonly int[] EditorSkeletonWidths = [96, 90, 94, 70, 88, 60];

    private Phase _phase = Phase.Loading;

    /// <summary>Newest first — the server orders by <c>PublishedAt</c> descending, ties to the higher id.</summary>
    private List<ExistingTermsOfServiceVersion> _versions = [];

    private TermsOfServiceDocument? _current;
    private string _draft = string.Empty;

    private bool _blockedOnOwnCompliance;
    private bool _confirmOpen;
    private bool _publishing;
    private bool _justPublished;
    private string? _announce;

    private int? _viewId;
    private bool _viewLoading;
    private string? _viewContent;
    private string? _viewSubtitle;
    private bool _copied;

    private string? EditorError => TermsOfServiceDraft.Error(_draft);

    private bool CanPublish =>
        _phase == Phase.Ready
        && !_publishing
        && TermsOfServiceDraft.IsPublishable(_draft, _current?.Content);

    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
        {
            return;
        }

        await LoadAsync();
    }

    /// <summary>
    /// Check the admin's own compliance first, then load the editor and history. The precheck is not
    /// decoration: publishing while non-compliant would gate this very page out from under the admin at
    /// the next revalidation, mid-edit.
    /// </summary>
    private async Task LoadAsync()
    {
        _phase = Phase.Loading;
        _blockedOnOwnCompliance = false;
        StateHasChanged();

        var status = await Legal.GetStatusAsync();
        if (!status.IsSuccess)
        {
            _phase = Phase.Error;
            return;
        }

        if (status.Value is { } compliance && (!compliance.LicenseCompliant || !compliance.TosCompliant))
        {
            _blockedOnOwnCompliance = true;
            _phase = Phase.Ready;
            return;
        }

        var current = await Legal.GetCurrentTermsOfServiceAsync();
        var versions = await Legal.GetVersionsAsync();

        if (!current.IsSuccess || !versions.IsSuccess)
        {
            _phase = Phase.Error;
            return;
        }

        _current = current.Value;
        _versions = versions.Value ?? [];
        _draft = _current?.Content ?? string.Empty;
        _phase = Phase.Ready;
    }

    private void OnDraftChanged(string? value)
    {
        _draft = value ?? string.Empty;
        _justPublished = false;
    }

    private void GoToAcceptTerms() =>
        NavigationManager.NavigateTo(
            $"{LegalComplianceHandler.InterstitialPath}?returnUrl={Uri.EscapeDataString("/legal-documents")}");

    private async Task PublishAsync()
    {
        _confirmOpen = false;
        _publishing = true;

        var published = await Legal.PublishVersionAsync(new NewTermsOfServiceVersion { Content = _draft.Trim() });
        _publishing = false;

        if (published.OrToast(Snackbar, "Couldn't publish the Terms of Service") is null)
        {
            return;
        }

        _justPublished = true;
        _announce = "New Terms of Service version published. Every user will be asked to re-accept.";

        // Reload rather than patching state locally: publishing makes the publishing admin themselves
        // non-compliant, and the reload is what surfaces that as the blocker instead of leaving them on
        // an editor whose next call would 451.
        await LoadAsync();
    }

    /// <summary>
    /// The whole history row opens the version, so it is a keyboard control too — Enter and Space have
    /// to do what the click does, or the row is mouse-only.
    /// </summary>
    private Task OnRowKeyAsync(KeyboardEventArgs args, int id) =>
        args.Key is "Enter" or " " ? ViewVersionAsync(id) : Task.CompletedTask;

    private async Task ViewVersionAsync(int id)
    {
        _viewId = id;
        _viewLoading = true;
        _viewContent = null;
        _viewSubtitle = null;
        _copied = false;
        StateHasChanged();

        var detail = await Legal.GetVersionAsync(id);
        _viewLoading = false;

        if (detail.OrToast(Snackbar, "Couldn't load that version") is not { } version)
        {
            _viewId = null;
            return;
        }

        _viewContent = version.Content;
        _viewSubtitle =
            $"Published {version.PublishedAt.ToLocalTime():d MMM yyyy, HH:mm} · "
            + (version.PublishedByDisplayName is { Length: > 0 } name ? name : "deleted user");
    }

    /// <summary>
    /// Copy a historical version's text. Goes through <see cref="IClipboardService"/> rather than a raw
    /// interop call so a browser that blocks the clipboard surfaces the app's standard error toast.
    /// </summary>
    private async Task CopyViewedVersionAsync()
    {
        if (_viewContent is not { } content)
        {
            return;
        }

        _copied = await Clipboard.CopyAsync(content, "Terms of Service copied to the clipboard.");
    }
}
