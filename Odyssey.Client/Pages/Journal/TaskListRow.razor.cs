using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Odyssey.Client.Components;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Journal;

// "Task" is this component's own parameter, so the Task TYPE is written out in full throughout.
public partial class TaskListRow : IAsyncDisposable
{
    [Inject] private IJSRuntime JS { get; set; } = default!;

    [Parameter, EditorRequired] public JournalTaskSummary Task { get; set; } = default!;
    [Parameter] public IEnumerable<string> TagNames { get; set; } = [];
    [Parameter] public bool CanUpdate { get; set; }
    [Parameter] public bool CanDelete { get; set; }

    [Parameter] public EventCallback<JournalTaskStatus> OnStatus { get; set; }
    [Parameter] public EventCallback<JournalTaskSummary> OnEdit { get; set; }
    [Parameter] public EventCallback<JournalTaskSummary> OnExport { get; set; }
    [Parameter] public EventCallback<JournalTaskSummary> OnArchive { get; set; }
    [Parameter] public EventCallback<Guid> OnDelete { get; set; }
    [Parameter] public EventCallback<Guid> OnCopyId { get; set; }

    /// <summary>Offers "Attach documents" (<c>tasks.update</c> and a way to reach a file).</summary>
    [Parameter] public bool CanAttach { get; set; }

    /// <summary>Offers Download on an unfolded file row (<c>files.read</c>).</summary>
    [Parameter] public bool CanDownload { get; set; }

    [Parameter] public EventCallback<JournalTaskSummary> OnAttach { get; set; }

    /// <summary>Reads the task's hydrated files for the unfolded list — the summary carries only a count.</summary>
    [Parameter] public Func<Guid, Task<IReadOnlyList<FileMetadataResponse>>>? LoadFiles { get; set; }

    /// <summary>Raised with the file id on "Remove from task". The file stays in Files.</summary>
    [Parameter] public EventCallback<Guid> OnRemoveFile { get; set; }

    private bool _filesOpen;
    private IReadOnlyList<FileMetadataResponse>? _files;
    private int _filesLoadedForCount = -1;

    private string FilesListId => $"tk-files-{Task.JournalTaskId}";

    private string RowId => $"tk-row-{Task.JournalTaskId}";

    // Focus return after "Remove from task" (WCAG 2.4.3): the ⋯ menu that had focus is destroyed with
    // its row. The next file's menu, else the previous one's, else the count that opens the list, else
    // the row's own menu — resolved BEFORE the write, while the removed row still has a position. The
    // same chain JournalEntryCard uses.
    private string? _focusAfterRemoval;
    private bool _pendingFocus;
    private IJSObjectReference? _focusReturnJs;

    private async System.Threading.Tasks.Task RemoveFileAsync(Guid fileId)
    {
        var ids = (_files ?? []).Select(f => f.Id).ToList();
        var index = ids.IndexOf(fileId);
        _focusAfterRemoval = index < 0 ? null
            : index + 1 < ids.Count ? JournalFileRows.MenuId(FilesListId, ids[index + 1])
            : index > 0 ? JournalFileRows.MenuId(FilesListId, ids[index - 1])
            : null;

        await OnRemoveFile.InvokeAsync(fileId);
        _pendingFocus = true;
    }

    protected override async System.Threading.Tasks.Task OnAfterRenderAsync(bool firstRender)
    {
        // Wait out the list's reload: the neighbour's menu does not exist until the rows are back.
        if (!_pendingFocus || (_filesOpen && _files is null))
            return;

        _pendingFocus = false;
        var neighbour = _focusAfterRemoval;
        _focusAfterRemoval = null;
        try
        {
            _focusReturnJs ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/focus-return.js");
            string?[] candidates =
            [
                neighbour is null ? null : $"#{neighbour} button",
                $"#{RowId} .jec-filecount",
                $"#{RowId} .tk-list-top .je-cardmenu button",
            ];
            await _focusReturnJs.InvokeVoidAsync("focusFirst", candidates);
        }
        catch (Exception)
        {
            // Best-effort: the page's live region already announced the removal.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_focusReturnJs is not null)
        {
            try { await _focusReturnJs.DisposeAsync(); } catch (Exception) { /* JS already gone on teardown */ }
        }
    }

    private async System.Threading.Tasks.Task ToggleFiles()
    {
        _filesOpen = !_filesOpen;
        if (_filesOpen)
            await LoadFilesAsync();
    }

    private async System.Threading.Tasks.Task LoadFilesAsync()
    {
        _filesLoadedForCount = Task.AttachmentCount;
        _files = LoadFiles is null ? [] : await LoadFiles(Task.JournalTaskId);
    }

    private List<string> _tagNames = [];
    private TaskRail _rail = TaskRail.Undated;

    protected override async System.Threading.Tasks.Task OnParametersSetAsync()
    {
        _tagNames = [.. TagNames];
        _rail = TaskRail.For(Task);

        // An attach or a removal reloads the row with a new count; re-read an open list so it follows.
        if (_filesOpen && _filesLoadedForCount != Task.AttachmentCount)
        {
            _files = null;
            if (Task.AttachmentCount > 0)
                await LoadFilesAsync();
            else
                _filesOpen = false;
        }
    }

    private JournalTaskStatus _status => Task.Status;

    private JournalTaskStatus NextStatus => _status switch
    {
        JournalTaskStatus.Backlog => JournalTaskStatus.Doing,
        JournalTaskStatus.Doing => JournalTaskStatus.Done,
        JournalTaskStatus.Done => JournalTaskStatus.Archived,
        _ => JournalTaskStatus.Backlog,
    };

    private string StatusIcon => _status switch
    {
        JournalTaskStatus.Doing => "radio_button_checked",
        JournalTaskStatus.Done => "task_alt",
        JournalTaskStatus.Archived => "task_alt",
        _ => "radio_button_unchecked",
    };

    private string StatusButtonLabel => _status == JournalTaskStatus.Archived
        ? "Archived"
        : $"Status: {_status}. Activate to set {NextStatus}.";

    // The card's ⋯ menu, built from the caller's claims. An OdsMenu item list rather than inline
    // MudMenuItems: the shared menu is what gives the Copy ID row its trailing affordance and a danger
    // item its tint, and it labels its own trigger.
    private List<OdsMenuItem> BuildActions()
    {
        var archived = _status == JournalTaskStatus.Archived;
        var items = new List<OdsMenuItem>();

        if (CanUpdate)
        {
            items.Add(new OdsMenuItem
            {
                Icon = "edit", Label = "Edit task",
                OnClick = EventCallback.Factory.Create(this, () => OnEdit.InvokeAsync(Task)),
            });
        }

        if (CanAttach)
        {
            items.Add(new OdsMenuItem
            {
                Icon = "attach_file", Label = "Attach documents",
                OnClick = EventCallback.Factory.Create(this, () => OnAttach.InvokeAsync(Task)),
            });
        }

        items.Add(new OdsMenuItem
        {
            Icon = "event_note", Label = "Export as iCalendar",
            OnClick = EventCallback.Factory.Create(this, () => OnExport.InvokeAsync(Task)),
        });
        items.Add(new OdsMenuItem
        {
            Icon = "fingerprint", Label = "Copy ID", TrailingIcon = "content_copy",
            OnClick = EventCallback.Factory.Create(this, () => OnCopyId.InvokeAsync(Task.JournalTaskId)),
        });

        if (CanUpdate)
        {
            items.Add(new OdsMenuItem { Divider = true });
            items.Add(new OdsMenuItem
            {
                Icon = archived ? "unarchive" : "inventory_2",
                Label = archived ? "Unarchive" : "Archive",
                OnClick = EventCallback.Factory.Create(this, () => OnArchive.InvokeAsync(Task)),
            });
        }

        if (CanDelete)
        {
            items.Add(new OdsMenuItem
            {
                Icon = "delete", Label = "Delete", Danger = true,
                OnClick = EventCallback.Factory.Create(this, () => OnDelete.InvokeAsync(Task.JournalTaskId)),
            });
        }

        return items;
    }
}
