using Odyssey.Client.Components;
using Odyssey.Client.Pages.Storage;
using Odyssey.Dtos.Application;

namespace Odyssey.Client.Pages;

/// <summary>
/// The Storage card's health panel (issue #346 A3.1/A3.2; Odyssey Design System · StorageHealth.jsx). Its own
/// fetch, independent of the settings load: a failure here never blocks the settings, and the panel shows its
/// own Retry. Gated on <c>file-storage.read</c> — no claim, no panel and no request.
/// </summary>
public partial class Settings
{
    /// <summary>The <see cref="SettingSection.Panel"/> value of the Storage card.</summary>
    internal const string StorageHealthPanelKey = "storageHealth";

    private bool _canReadStorage;
    private IReadOnlyList<FileStorageVolumeStatus>? _storageVolumes;
    private StorageHealthPhase _storagePhase = StorageHealthPhase.Loading;
    private bool _storageRefreshing;
    private DateTime? _storageCheckedAt;
    private bool _storageRefreshFailed;

    private async Task LoadStorageHealthAsync()
    {
        if (!_canReadStorage)
        {
            return;
        }

        var result = await FileStorage.GetVolumesAsync();
        if (result.IsSuccess && result.Value is { } volumes)
        {
            _storageVolumes = volumes;
            _storageCheckedAt = Time.GetUtcNow().UtcDateTime;
            _storagePhase = StorageHealthPhase.Ready;
            _storageRefreshFailed = false;
        }
        else if (_storagePhase == StorageHealthPhase.Ready)
        {
            // A failed REFRESH keeps what was shown, and the panel says how old it is.
            _storageRefreshFailed = true;
        }
        else
        {
            _storagePhase = StorageHealthPhase.Error;
        }

        StateHasChanged();
    }

    private async Task RefreshStorageHealthAsync()
    {
        if (_storageRefreshing)
        {
            return;
        }

        _storageRefreshing = true;
        StateHasChanged();
        var before = _storageCheckedAt;
        await LoadStorageHealthAsync();
        _storageRefreshing = false;
        Announce(_storageCheckedAt != before ? "Storage health refreshed." : "Couldn't refresh storage health.");
        StateHasChanged();
    }

    /// <summary>
    /// Retry replaces the button that was clicked with the loading skeleton, so focus would fall to
    /// <c>&lt;body&gt;</c> (WCAG 2.4.3). It is announced, and focus goes to the panel itself (<c>tabindex="-1"</c>)
    /// once the result is in.
    /// </summary>
    private async Task RetryStorageHealthAsync()
    {
        _storagePhase = StorageHealthPhase.Loading;
        Announce("Loading storage health.");
        StateHasChanged();
        await LoadStorageHealthAsync();
        Announce(_storagePhase == StorageHealthPhase.Ready ? "Storage health loaded." : "Couldn't load storage health.");
        await JumpToElementAsync(StorageHealth.PanelId);
    }

    /// <summary>
    /// Storage's ONE row in the page-header rollup, at the worst severity among its conditions (the panel lists
    /// them all). Informational conditions stay in the panel. A stopped worker silently stops erasure, which is
    /// why this is a page-level signal and not only a panel.
    /// </summary>
    private PageHeaderProblem? StorageProblem
    {
        get
        {
            if (!_canReadStorage || _storagePhase != StorageHealthPhase.Ready)
            {
                return null;
            }

            var issues = StorageHealth.Issues(_storageVolumes, Time.GetUtcNow()).Where(i => i.Severity != StorageIssueSeverity.Info).ToList();
            if (issues.Count == 0)
            {
                return null;
            }

            var first = issues[0];
            var more = issues.Count - 1;
            return new PageHeaderProblem
            {
                Severity = first.Severity == StorageIssueSeverity.Error ? PageHeaderSeverity.Error : PageHeaderSeverity.Warning,
                Lead = first.Title.TrimEnd('.'),
                Message = more > 0 ? $"And {more} more storage condition{(more == 1 ? string.Empty : "s")}." : first.Text,
                Where = "In Storage.",
                ViewLabel = "View",
                OnView = Microsoft.AspNetCore.Components.EventCallback.Factory.Create(this, () => JumpToElementAsync(StorageHealth.PanelId)),
            };
        }
    }
}
