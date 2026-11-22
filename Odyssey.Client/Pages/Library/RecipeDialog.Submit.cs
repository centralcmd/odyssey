using System.Net;
using System.Text.RegularExpressions;
using Odyssey.Client.Components;
using Odyssey.Dtos.Recipes;

namespace Odyssey.Client.Pages.Library;

public partial class RecipeDialog
{
    private static readonly Regex RowKey = new(@"^(ingredients|steps)\[(\d+)\]\.(\w+)$", RegexOptions.Compiled);

    private static readonly Dictionary<string, string> DetailLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["title"] = "Title",
        ["description"] = "Description",
        ["servings"] = "Servings",
        ["servingslabel"] = "Servings label",
        ["prepminutes"] = "Prep",
        ["cookminutes"] = "Cook",
        ["source"] = "Source",
        ["notes"] = "Notes",
        ["tagids"] = "Tags",
    };

    /// <summary>The footer ErrorSummary's entries, each jumping to its field ("Ingredient 3 · Name the ingredient.").</summary>
    private IReadOnlyList<OdsErrorSummaryProblem> Problems => [.. _errors.Select(e => ProblemFor(e.Key, e.Value))];

    internal static OdsErrorSummaryProblem ProblemFor(string key, string message)
    {
        if (RowKey.Match(key) is { Success: true } m)
        {
            var row = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) + 1;
            var section = m.Groups[1].Value == "ingredients" ? $"Ingredient {row}" : $"Step {row}";
            return new OdsErrorSummaryProblem(message, section, RecipeDraft.FieldId(key));
        }

        var label = DetailLabels.GetValueOrDefault(key, key);
        return new OdsErrorSummaryProblem($"{label} — {message}", key == "tagids" ? "Tags" : "Details", RecipeDraft.FieldId(key));
    }

    /// <summary>A server field key (<c>Ingredients[2].Name</c>, <c>$.ingredients[2].name</c>) in the dialog's own form.</summary>
    internal static string NormalizeKey(string key)
    {
        var k = key.Trim();
        if (k.StartsWith("$.", StringComparison.Ordinal))
        {
            k = k[2..];
        }

        return k.ToLowerInvariant();
    }

    private Task JumpAsync(OdsErrorSummaryProblem problem)
    {
        _focusId = problem.TargetId;
        StateHasChanged();
        return Task.CompletedTask;
    }

    private async Task SubmitAsync()
    {
        if (_saving)
        {
            return;
        }

        _formAlert = null;
        _errors.Clear();
        foreach (var (key, message) in _draft.Validate())
        {
            _errors.TryAdd(key, message);
        }

        if (_errors.Count > 0)
        {
            _focusId = F(_errors.Keys.First());
            return;
        }

        _saving = true;
        try
        {
            // Inline-created tags hand the picker a temporary id; wait for their POSTs, then map to the real ids.
            await TagCreator.WhenSettledAsync();
            _draft.TagIds = [.. _draft.TagIds.Select(TagCreator.Resolve).Where(id => id is not null).Select(id => id!)];

            var result = _read is { } read
                ? await Recipes.UpdateAsync(read.RecipeId, _draft.ToUpdate(read, CanReadTags))
                : await Recipes.CreateAsync(_draft.ToNew(CanReadTags));
            if (result.IsSuccess && result.Value is { } saved)
            {
                _baseline = _draft.Fingerprint();
                _modalOpen = false;
                await OnSaved.InvokeAsync(saved);
                await OpenChanged.InvokeAsync(false);
                return;
            }

            Refused(result.Status, result.Problem);
        }
        finally
        {
            _saving = false;
        }
    }

    private void Refused(HttpStatusCode status, Odyssey.ApiClient.ApiProblem? problem)
    {
        if (status == HttpStatusCode.Conflict && problem?.Code == RecipeErrorCodes.VersionConflict)
        {
            _conflict = true;
            _focusId = ReloadId;
            return;
        }

        if (status == HttpStatusCode.NotFound && IsEdit)
        {
            _formAlert = "This recipe was deleted while you were editing it. Copy anything you want to keep, then close.";
            return;
        }

        foreach (var (key, messages) in problem?.Errors ?? new Dictionary<string, string[]>())
        {
            if (messages.Length > 0)
            {
                _errors.TryAdd(NormalizeKey(key), messages[0]);
            }
        }

        if (_errors.Count > 0)
        {
            _focusId = F(_errors.Keys.First());
            return;
        }

        _formAlert = problem?.Message ?? "The recipe couldn’t be saved. Try again.";
    }

    // ── Conflict ─────────────────────────────────────────────────────────────────────
    private void AskReload() => _reloadOpen = true;

    private async Task<bool> ReloadAsync()
    {
        if (_read is not { } read)
        {
            return true;
        }

        _reloading = true;
        try
        {
            var latest = await Recipes.GetAsync(read.RecipeId);
            if (latest.IsSuccess && latest.Value is { } fresh)
            {
                Seed(fresh);
                _focusId = F("title");
                // The submit is the confirm dialog's event, so this component re-renders only when asked.
                StateHasChanged();
                return true;
            }

            _formAlert = latest.Status == HttpStatusCode.NotFound
                ? "This recipe was deleted while you were editing it."
                : "Couldn’t reload this recipe. Try again.";
            StateHasChanged();
            return true;
        }
        finally
        {
            _reloading = false;
        }
    }

    // ── Closing ──────────────────────────────────────────────────────────────────────
    private bool Dirty => _draft.Fingerprint() != _baseline;

    private async Task RequestCloseAsync()
    {
        if (Dirty)
        {
            _discardOpen = true;
            return;
        }

        _modalOpen = false;
        await OpenChanged.InvokeAsync(false);
    }

    private void OnModalOpenChanged(bool open) => _modalOpen = open;

    // Esc, the scrim and the close button (OdsModal raises OnClose after it has already hidden itself, so it cannot be
    // vetoed): with changes, the discard question opens and "Keep editing" shows the dialog again.
    private async Task OnModalClosed()
    {
        if (_closing)
        {
            return;
        }

        if (Dirty)
        {
            _discardOpen = true;
            return;
        }

        await OpenChanged.InvokeAsync(false);
    }

    private void OnDiscardOpenChanged(bool open)
    {
        _discardOpen = open;
        if (!open && !_closing)
        {
            // Keep editing.
            _modalOpen = true;
        }
    }

    private async Task<bool> DiscardAsync()
    {
        _closing = true;
        _modalOpen = false;
        await OpenChanged.InvokeAsync(false);
        return true;
    }
}
