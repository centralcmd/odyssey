using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Odyssey.Api.Identity;
using Odyssey.Core.Finance;
using Odyssey.Core.Identity;
using Odyssey.Core.Recipes.Import;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Recipes;

namespace Odyssey.Api.Controllers;

/// <summary>
/// AI recipe import (issue #602 §5). Every action requires <c>recipes.import</c>; further claims are stacked
/// <c>[Authorize]</c> attributes, enforced before the body is bound, or passed to the service, which checks them
/// before any lookup. Every response carries <c>Cache-Control: no-store</c> (<see cref="NoStoreAttribute"/>). Route ids
/// are <c>guid</c>-constrained so the literal <c>options</c> and <c>library-files</c> segments cannot be shadowed.
/// </summary>
[ApiController]
[Route("api/recipe-imports")]
[Authorize(Policy = PermissionClaims.RecipesImport)]
[NoStore]
public sealed class RecipeImportsController(
    RecipeImportService service,
    IUserDisplayNameResolver displayNames,
    IOptions<FileStorageOptions> fileStorage,
    IHostEnvironment environment) : ControllerBase
{
    [HttpGet("options", Name = "GetRecipeImportOptions")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(RecipeImportOptions))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [EndpointSummary("The processor disclosure, the server-rendered consent sentence, the on/off state and every limit, for this caller.")]
    public async Task<IActionResult> GetOptions(CancellationToken cancellationToken = default) =>
        Ok(await service.GetOptionsAsync(CanReadTags(), TransportCeiling, cancellationToken));

    [HttpPost(Name = "PostRecipeImport")]
    [Authorize(Policy = PermissionClaims.RecipesCreate)]
    [EnableRateLimiting(RecipeImportRateLimiting.CreatePolicy)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(RecipeImportDetail))]
    [ProducesResponseType(StatusCodes.Status202Accepted, Type = typeof(RecipeImportDetail))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [EndpointSummary("Start an import from pasted text and/or Library files. A repeated clientRequestId returns the existing import (200).")]
    public async Task<IActionResult> Post([FromBody] CreateRecipeImport request, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        return await CreateAsync(
            new RecipeImportCreate(request.ClientRequestId!.Value, request.Text, request.ExistingFileIds, [],
                request.ConsentAcknowledged, request.DisclosureVersion),
            userId, cancellationToken);
    }

    [HttpPost("uploads", Name = "PostRecipeImportUpload")]
    [Authorize(Policy = PermissionClaims.RecipesCreate)]
    [Authorize(Policy = PermissionClaims.FilesCreate)]
    [Authorize(Policy = PermissionClaims.FilesRead)]
    [EnableRateLimiting(RecipeImportRateLimiting.CreatePolicy)]
    [ImportSizeLimit(ImportSurface.RecipeImports)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(RecipeImportDetail))]
    [ProducesResponseType(StatusCodes.Status202Accepted, Type = typeof(RecipeImportDetail))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [EndpointSummary("Start an import with new files (multipart), optionally with pasted text and Library files.")]
    public async Task<IActionResult> PostUploads([FromForm] RecipeImportUploadForm form, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        var uploads = form.Files
            .Select(f => new FileUpload(f.FileName, f.ContentType, f.Length, f.OpenReadStream))
            .ToList();
        if (uploads.Count == 0)
        {
            throw new Odyssey.Core.DomainValidationException("Add at least one file, or use the text-only endpoint.", null, "files");
        }

        return await CreateAsync(
            new RecipeImportCreate(form.ClientRequestId!.Value, form.Text, form.ExistingFileIds, uploads,
                form.ConsentAcknowledged, form.DisclosureVersion),
            userId, cancellationToken);
    }

    [HttpGet(Name = "GetRecipeImports")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<RecipeImportSummary>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [EndpointSummary("List imports, newest first; deleted imports are never listed and purged ones only with includePurged.")]
    public async Task<IActionResult> Get([FromQuery] RecipeImportsQueryParams query, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        return Ok(await service.ListAsync(query, Caller(userId), Names, cancellationToken));
    }

    [HttpGet("{id:guid}", Name = "GetRecipeImport")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(RecipeImportDetail))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [EndpointSummary("One import with its sources and draft recipe.")]
    public async Task<IActionResult> Get([FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        var detail = await service.GetAsync(id, Caller(userId), Names, cancellationToken);
        return detail is null ? NotFound(id) : Ok(detail);
    }

    [HttpPut("{id:guid}/drafts/{draftId:guid}", Name = "PutRecipeImportDraft")]
    [RequestSizeLimit(RecipeLimits.MaxRequestBytes)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(RecipeImportDraftItem))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [EndpointSummary("Save and finish later: replace the draft recipe. tagIds null leaves the suggested tags unchanged.")]
    public async Task<IActionResult> PutDraft(
        [FromRoute(Name = "id")] Guid id,
        [FromRoute(Name = "draftId")] Guid draftId,
        [FromBody] UpdateRecipeImportDraft request,
        CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        var draft = await service.UpdateDraftAsync(id, draftId, request, Caller(userId), Names, cancellationToken);
        return draft is null ? NotFound(id) : Ok(draft);
    }

    [HttpPost("{id:guid}/drafts/{draftId:guid}/accept", Name = "AcceptRecipeImportDraft")]
    [Authorize(Policy = PermissionClaims.RecipesCreate)]
    [RequestSizeLimit(RecipeLimits.MaxRequestBytes)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(AcceptRecipeImportDraftResult))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [EndpointSummary("Create the recipe from the reviewed draft, optionally attaching the source files, in one transaction.")]
    public async Task<IActionResult> Accept(
        [FromRoute(Name = "id")] Guid id,
        [FromRoute(Name = "draftId")] Guid draftId,
        [FromBody] AcceptRecipeImportDraft request,
        CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        var result = await service.AcceptAsync(id, draftId, request, Caller(userId), cancellationToken);
        return result is null ? NotFound(id) : Created($"/api/recipes/{result.RecipeId}", result);
    }

    [HttpPost("{id:guid}/drafts/{draftId:guid}/discard", Name = "DiscardRecipeImportDraft")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [EndpointSummary("Discard the draft recipe. The import stays; no recipe is created or deleted.")]
    public async Task<IActionResult> Discard(
        [FromRoute(Name = "id")] Guid id, [FromRoute(Name = "draftId")] Guid draftId, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        return await service.DiscardAsync(id, draftId, Caller(userId), cancellationToken) ? NoContent() : NotFound(id);
    }

    [HttpPost("{id:guid}/cancel", Name = "CancelRecipeImport")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [EndpointSummary("Cancel an import that is still waiting; nothing has been sent.")]
    public async Task<IActionResult> Cancel([FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        return await service.CancelAsync(id, Caller(userId), cancellationToken) ? NoContent() : NotFound(id);
    }

    [HttpDelete("{id:guid}", Name = "DeleteRecipeImport")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [EndpointSummary("Delete an import: its sources and draft are removed and its own files released. A recipe made from it stays.")]
    public async Task<IActionResult> Delete([FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        return await service.DeleteAsync(id, Caller(userId), cancellationToken) ? NoContent() : NotFound(id);
    }

    [HttpGet("library-files", Name = "GetRecipeImportLibraryFiles")]
    [Authorize(Policy = PermissionClaims.FilesRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<RecipeImportLibraryFile>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [EndpointSummary("Library files of the types recipe import accepts, newest first — the composer's file picker.")]
    public async Task<IActionResult> GetLibraryFiles([FromQuery] RecipeImportLibraryQuery query, CancellationToken cancellationToken = default) =>
        Ok(await service.ListLibraryFilesAsync(query, cancellationToken));

    private async Task<IActionResult> CreateAsync(RecipeImportCreate request, string userId, CancellationToken cancellationToken)
    {
        var caller = Caller(userId);
        var created = await service.CreateAsync(request, caller, TransportCeiling, Names, cancellationToken);
        if (!created.Created)
        {
            return Ok(created.Import);
        }

        var id = created.Import.Summary.Id;

        // Testing hosts run no worker, so the import runs here (the AnalyzeAccountFile precedent). Selected by the
        // environment NAME alone, never a setting, so no Production host can take this path.
        if (environment.IsEnvironment("Testing"))
        {
            var runner = HttpContext.RequestServices.GetRequiredService<RecipeImportRunner>();
            await service.RunInlineAsync(id, runner, cancellationToken);
        }

        var detail = await service.GetAsync(id, caller, Names, cancellationToken) ?? created.Import;
        return AcceptedAtRoute("GetRecipeImport", new { id }, detail);
    }

    /// <summary>The deploy-time body ceiling: a multipart import can never be larger than the transport accepts.</summary>
    private long TransportCeiling => fileStorage.Value.MaxFileSizeBytes;

    private RecipeImportCaller Caller(string userId) => new(
        userId,
        CanReadFiles: User.HasPermission(PermissionClaims.FilesRead),
        CanReadTags: CanReadTags(),
        CanReadPhotos: User.HasPermission(PermissionClaims.PhotosRead));

    private bool CanReadTags() => User.HasPermission(PermissionClaims.RecipeTagsRead);

    private Task<IReadOnlyDictionary<string, string>> Names(IEnumerable<string?> userIds, CancellationToken cancellationToken) =>
        displayNames.ResolveAsync(User, userIds, cancellationToken);

    private IActionResult NotFound(Guid id) => this.NotFoundProblem($"Recipe import ID {id} not found.");
}

/// <summary>The multipart body of <c>POST /api/recipe-imports/uploads</c>.</summary>
public sealed class RecipeImportUploadForm
{
    [Required]
    public Guid? ClientRequestId { get; set; }

    [StringLength(RecipeImportLimits.MaxSourceTextLength)]
    public string? Text { get; set; }

    [MaxLength(RecipeImportLimits.MaxFilesPerImport)]
    public List<IFormFile> Files { get; set; } = [];

    [MaxLength(RecipeImportLimits.MaxFilesPerImport)]
    public List<Guid> ExistingFileIds { get; set; } = [];

    public bool ConsentAcknowledged { get; set; }

    [StringLength(16)]
    public string? DisclosureVersion { get; set; }
}
