using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Recipes;

namespace Odyssey.Client.Pages.Library;

/// <summary>The claims beyond <c>recipes.read</c> that decide a recipe's ⋯ menu (design system components-recipes.md · Claims).</summary>
internal sealed record RecipeRowClaims(bool CanUpdate, bool CanDelete, bool CanReadPhotos, bool CanReadFiles);

/// <summary>A card's cooking state as the menu needs it.</summary>
/// <param name="Cookable">The card's read has not failed or gone.</param>
/// <param name="WakeLockSupported">The browser has <c>navigator.wakeLock</c>.</param>
/// <param name="Awake">This card holds the screen wake lock.</param>
/// <param name="Checked">How many lines and steps are checked off.</param>
/// <param name="PhotoCount">The linked photos, when the read is known (the summary has no count).</param>
/// <param name="FileCount">The linked files (<c>attachmentCount</c>).</param>
internal sealed record RecipeRowState(bool Cookable, bool WakeLockSupported, bool Awake, int Checked, int? PhotoCount, int? FileCount);

/// <summary>What each ⋯ item does.</summary>
internal sealed record RecipeRowCallbacks(
    EventCallback ChangeServings, EventCallback ToggleWake, EventCallback ClearChecks, EventCallback Edit, EventCallback AddPhotos,
    EventCallback AttachFiles, EventCallback ToggleArchive, EventCallback Delete);

public partial class RecipesPage
{
    /// <summary>
    /// A recipe's ⋯ menu: the cooking actions first, for every role — Change servings… · Keep screen awake / Let screen
    /// sleep (only where the Wake Lock API exists) · Clear checks (n) (only once something is checked) — then divider ·
    /// Edit… · Add photos (update + photos.read; absent at the cap) · Attach files (update + files.read; absent at the cap)
    /// · Archive / Restore · divider · Delete… (danger). An action the card cannot take is ABSENT, never dimmed.
    /// </summary>
    internal static IReadOnlyList<OdsMenuItem> BuildRowActions(
        RecipeSummary r, RecipeRowClaims claims, RecipeRowState state, RecipeRowCallbacks on)
    {
        var items = new List<OdsMenuItem>();
        if (state.Cookable)
        {
            items.Add(new OdsMenuItem { Icon = "groups", Label = "Change servings…", AriaLabel = $"Change servings… — {r.Title}", OnClick = on.ChangeServings });
            if (state.WakeLockSupported)
            {
                items.Add(state.Awake
                    ? new OdsMenuItem { Icon = "bedtime", Label = "Let screen sleep", OnClick = on.ToggleWake }
                    : new OdsMenuItem { Icon = "light_mode", Label = "Keep screen awake", OnClick = on.ToggleWake });
            }

            if (state.Checked > 0)
            {
                items.Add(new OdsMenuItem { Icon = "remove_done", Label = $"Clear checks ({state.Checked})", OnClick = on.ClearChecks });
            }
        }

        if (claims.CanUpdate)
        {
            items.Add(new OdsMenuItem { Divider = true });
            items.Add(new OdsMenuItem { Icon = "edit", Label = "Edit…", AriaLabel = $"Edit… — {r.Title}", OnClick = on.Edit });
            if (claims.CanReadPhotos && (state.PhotoCount ?? 0) < RecipeLimits.MaxPhotos)
            {
                items.Add(new OdsMenuItem { Icon = "add_photo_alternate", Label = "Add photos", AriaLabel = $"Add photos to {r.Title}", OnClick = on.AddPhotos });
            }

            if (claims.CanReadFiles && (state.FileCount ?? 0) < RecipeLimits.MaxAttachments)
            {
                items.Add(new OdsMenuItem { Icon = "attach_file", Label = "Attach files", AriaLabel = $"Attach files to {r.Title}", OnClick = on.AttachFiles });
            }

            var archived = r.Archived is not null;
            items.Add(new OdsMenuItem
            {
                Icon = archived ? "unarchive" : "inventory_2",
                Label = archived ? "Restore" : "Archive",
                AriaLabel = $"{(archived ? "Restore" : "Archive")} {r.Title}",
                OnClick = on.ToggleArchive,
            });
        }

        if (claims.CanDelete)
        {
            items.Add(new OdsMenuItem { Divider = true });
            items.Add(new OdsMenuItem { Icon = "delete", Label = "Delete…", AriaLabel = $"Delete… — {r.Title}", Danger = true, OnClick = on.Delete });
        }

        // A leading divider (no cooking actions above it) is dropped here; OdsMenu drops any other orphan.
        while (items.Count > 0 && items[0].Divider)
        {
            items.RemoveAt(0);
        }

        return items;
    }

    private IReadOnlyList<OdsMenuItem> RowActions(RecipeSummary r)
    {
        var cook = _cook.GetValueOrDefault(r.RecipeId);
        var state = new RecipeRowState(
            cook?.Cookable ?? true, _wakeSupported, _awakeId == r.RecipeId, cook?.CheckedCount ?? 0,
            cook?.Detail?.Photos?.Count, cook?.Detail?.Attachments?.Count ?? r.AttachmentCount);
        return BuildRowActions(r, new RecipeRowClaims(_canUpdate, _canDelete, _canPhotos, _canFiles), state, new RecipeRowCallbacks(
            ChangeServings: Do(() => ChangeServingsAsync(r)),
            ToggleWake: Do(() => ToggleWakeAsync(r)),
            ClearChecks: Do(() => ClearChecksAsync(r)),
            Edit: Do(() => EditAsync(r)),
            AddPhotos: Do(() => AddPhotosAsync(r)),
            AttachFiles: Do(() => AttachFilesAsync(r)),
            ToggleArchive: Do(() => ToggleArchiveAsync(r)),
            Delete: Do(() => DeleteClicked(r))));
    }

    private EventCallback Do(Func<Task> action) => EventCallback.Factory.Create(this, action);

    private EventCallback Do(Action action) => EventCallback.Factory.Create(this, action);

    // ── Expand ──
    private Guid? _expandedId;

    private async Task ToggleAsync(RecipeSummary r, bool open)
    {
        if (!open)
        {
            await CollapseAsync();
            return;
        }

        await CollapseAsync();
        _expandedId = r.RecipeId;
        await LoadDetailAsync(r.RecipeId);
    }

    /// <summary>Closes the open card: the wake lock goes with it, and a recipe found gone leaves the list.</summary>
    private async Task CollapseAsync()
    {
        if (_expandedId is not { } id)
        {
            return;
        }

        _expandedId = null;
        await ReleaseWakeLockAsync(announce: false);
        if (_cook.GetValueOrDefault(id)?.Phase == RecipeCardPhase.Gone)
        {
            _rows.RemoveAll(x => x.RecipeId == id);
            _totalCount = _totalCount is { } t ? Math.Max(0, t - 1) : null;
            _cook.Remove(id);
        }
    }

    /// <summary>A cooking action on a closed card opens it first.</summary>
    private async Task EnsureOpenAsync(RecipeSummary r)
    {
        if (_expandedId != r.RecipeId)
        {
            await ToggleAsync(r, true);
        }
        else if (Cook(r.RecipeId).Detail is null)
        {
            await LoadDetailAsync(r.RecipeId);
        }
    }

    private static void Toggle(HashSet<int> set, RecipeCheck check)
    {
        if (check.Checked)
        {
            set.Add(check.Index);
        }
        else
        {
            set.Remove(check.Index);
        }
    }

    // ── Cooking actions ──
    private RecipeSummary? _servingsFor;
    private Guid _servingsKey;
    private bool _servingsOpen;

    private async Task ChangeServingsAsync(RecipeSummary r)
    {
        await EnsureOpenAsync(r);
        if (Cook(r.RecipeId).Detail is null)
        {
            return;
        }

        _servingsFor = r;
        _servingsKey = Guid.NewGuid();
        _servingsOpen = true;
    }

    private async Task ClearChecksAsync(RecipeSummary r)
    {
        Cook(r.RecipeId).ClearChecks();
        await Announce("Checks cleared.");
    }

    private bool _wakeSupported;
    private Guid? _awakeId;
    private IJSObjectReference? _wakeModule;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && Interactivity.IsInteractive)
        {
            try
            {
                _wakeModule = await Js.InvokeAsync<IJSObjectReference>("import", "./js/wake-lock.js");
                _wakeSupported = await _wakeModule.InvokeAsync<bool>("supported");
                if (_wakeSupported)
                {
                    StateHasChanged();
                }
            }
            catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException)
            {
                _wakeSupported = false;
            }
        }

        await FocusPendingAsync();
    }

    private async Task ToggleWakeAsync(RecipeSummary r)
    {
        if (_awakeId == r.RecipeId)
        {
            await ReleaseWakeLockAsync(announce: true);
            return;
        }

        await EnsureOpenAsync(r);
        var held = false;
        try
        {
            held = _wakeModule is not null && await _wakeModule.InvokeAsync<bool>("request");
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException)
        {
            held = false;
        }

        _awakeId = held ? r.RecipeId : null;
        await Announce(held ? "The screen stays awake while this recipe is open." : "The screen can’t be kept awake right now.");
        StateHasChanged();
    }

    private async Task ReleaseWakeLockAsync(bool announce)
    {
        if (_awakeId is null)
        {
            return;
        }

        _awakeId = null;
        try
        {
            if (_wakeModule is not null)
            {
                await _wakeModule.InvokeVoidAsync("release");
            }
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException)
        {
            // Leaving the page releases the lock anyway.
        }

        if (announce)
        {
            await Announce("The screen can sleep again.");
        }
    }

    // ── Focus (design system components-recipes.md · States) ──
    private string[]? _focusChain;

    private void FocusAfterRender(params string[] chain)
    {
        _focusChain = chain;
        StateHasChanged();
    }

    private async Task FocusPendingAsync()
    {
        if (_focusChain is not { } chain)
        {
            return;
        }

        _focusChain = null;
        try
        {
            await Js.InvokeVoidAsync("odsFocusChainOrHeading", (object)chain);
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException)
        {
            // No JS runtime (bUnit, teardown).
        }
    }
}
