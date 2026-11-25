using System.Net;
using Microsoft.AspNetCore.Components;
using Odyssey.Dtos.Recipes;

namespace Odyssey.Client.Pages.Library;

/// <summary>What the dialog reviews in import mode: one draft recipe of one import, and its source files.</summary>
/// <param name="SourceFileCount">The import's surviving source files (what Attach would attach).</param>
/// <param name="CanFiles"><c>files.read</c>: without it the files are not attached and a note says why.</param>
public sealed record RecipeImportReview(Guid ImportId, RecipeImportDraftItem Draft, int SourceFileCount, bool CanFiles);

/// <summary>A draft recipe that became a recipe: the server's answer and the title it was created with.</summary>
public sealed record RecipeImportAccepted(AcceptRecipeImportDraftResult Result, string Title, bool AttachedSources);

public partial class RecipeDialog
{
    internal const string ImportBannerId = "rc-import-h";

    /// <summary>
    /// Import mode (issue #604; design system · Review — the Recipes dialog in import mode): the same three steps
    /// prefilled from a draft recipe, the AI-drafted banner on every step, Things to check, Attach the source files,
    /// and Save and finish later. Create recipe accepts the draft (<c>POST …/accept</c>) instead of <c>POST /recipes</c>.
    /// </summary>
    [Parameter] public RecipeImportReview? ImportReview { get; set; }

    /// <summary>Save and finish later stored the edits on the import; nothing is in Recipes.</summary>
    [Parameter] public EventCallback<RecipeImportDraftItem> OnImportSaved { get; set; }

    /// <summary>Create recipe accepted the draft: the recipe exists now.</summary>
    [Parameter] public EventCallback<RecipeImportAccepted> OnImportAccepted { get; set; }

    private bool IsImport => ImportReview is not null;

    private bool _attachSources = true;
    private bool _savingLater;
    private int _importVersion;
    private List<(string Text, int? Step)> _importWarnings = [];

    private void SeedImport(RecipeImportReview review)
    {
        _read = null;
        _draft = review.Draft.Content is { } content ? RecipeDraft.FromImport(content) : RecipeDraft.New();
        _baseline = _draft.Fingerprint();
        _importVersion = review.Draft.Version;
        _importWarnings = [.. review.Draft.Warnings.Select(RecipeImportText.Warning).OfType<(string, int?)>().Distinct()];
        _attachSources = review.CanFiles && review.SourceFileCount > 0;
        _errors.Clear();
        _formAlert = null;
        _conflict = false;
        // Focus opens on the banner's heading, so the first thing read is what this dialog holds.
        _focusId = ImportBannerId;
    }

    /// <summary>The warnings about one step, repeated above its list.</summary>
    private IReadOnlyList<string> WarningsFor(int step) =>
        [.. _importWarnings.Where(w => w.Step == step && step != RecipeDraft.DetailsStep).Select(w => w.Text)];

    private string ServingsHelp =>
        IsImport && _importWarnings.Any(w => w.Text == RecipeImportText.Warning(RecipeImportWarningCodes.ServingsMissing)?.Text)
            ? "Servings weren’t in the source — fill them in. How many the amounts in Ingredients are written for."
            : "How many the amounts in Ingredients are written for.";

    /// <summary>Save and finish later: no wizard validation — a draft recipe may lack a title — and the dialog closes.</summary>
    private async Task SaveForLaterAsync()
    {
        if (ImportReview is not { } review || _saving || _savingLater)
        {
            return;
        }

        _savingLater = true;
        _formAlert = null;
        try
        {
            var result = await Imports.UpdateDraftAsync(review.ImportId, review.Draft.Id, new UpdateRecipeImportDraft
            {
                Content = _draft.ToImportContent(CanReadTags),
                Version = _importVersion,
            });
            if (result.IsSuccess && result.Value is { } saved)
            {
                _importVersion = saved.Version;
                _baseline = _draft.Fingerprint();
                _closing = true;
                _modalOpen = false;
                await OnImportSaved.InvokeAsync(saved);
                await OpenChanged.InvokeAsync(false);
                return;
            }

            _formAlert = ImportRefusal(result.Status, result.Problem)
                ?? result.Problem?.Message
                ?? "Your edits couldn’t be saved. Try again.";
        }
        finally
        {
            _savingLater = false;
        }
    }

    private async Task SaveFromLeaveAsync()
    {
        _discardOpen = false;
        _modalOpen = true;
        await SaveForLaterAsync();
    }

    /// <summary>Create recipe in import mode: full validation (servings required), then the one-transaction accept.</summary>
    private async Task AcceptImportAsync()
    {
        if (ImportReview is not { } review)
        {
            return;
        }

        _saving = true;
        try
        {
            await TagCreator.WhenSettledAsync();
            _draft.TagIds = [.. _draft.TagIds.Select(TagCreator.Resolve).Where(id => id is not null).Select(id => id!)];
            var attach = review.CanFiles && review.SourceFileCount > 0 && _attachSources;
            var recipe = _draft.ToNew(CanReadTags);
            var result = await Imports.AcceptAsync(review.ImportId, review.Draft.Id, new AcceptRecipeImportDraft
            {
                Recipe = recipe,
                AttachSourceFiles = attach,
                Version = _importVersion,
            });
            if (result.IsSuccess && result.Value is { } accepted)
            {
                _baseline = _draft.Fingerprint();
                _closing = true;
                _modalOpen = false;
                await OnImportAccepted.InvokeAsync(new RecipeImportAccepted(accepted, recipe.Title, attach));
                await OpenChanged.InvokeAsync(false);
                return;
            }

            if (ImportRefusal(result.Status, result.Problem) is { } refusal)
            {
                _formAlert = refusal;
                return;
            }

            if (Refused(result.Status, result.Problem))
            {
                await ShowErrorsAsync();
            }
        }
        finally
        {
            _saving = false;
        }
    }

    /// <summary>The import's own refusals in words; <c>null</c> for anything the recipe rules explain.</summary>
    private static string? ImportRefusal(HttpStatusCode status, Odyssey.ApiClient.ApiProblem? problem) => problem?.Code switch
    {
        RecipeImportErrorCodes.VersionConflict =>
            "Someone else changed this draft recipe while you were reviewing it. Close and open it again to see their changes.",
        RecipeImportErrorCodes.DraftNotPending =>
            "This draft recipe was already created as a recipe or discarded by someone else. Close to see it in Recipe imports.",
        RecipeImportErrorCodes.ContentPurged =>
            "This import’s content was removed after its retention period, so the draft recipe can no longer be used.",
        _ when status == HttpStatusCode.NotFound => "This import was deleted while you were reviewing it.",
        _ => null,
    };
}
