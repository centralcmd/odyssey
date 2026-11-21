using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Odyssey.Api.Identity;
using Odyssey.Core.Books;
using Odyssey.Core.Identity;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Books;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Controllers;

/// <summary>
/// The household book catalogue (issue #527). Gated on <c>books.*</c>; what a caller sees of contacts, photos, files
/// and tags follows <c>contacts.read</c>, <c>photos.read</c>, <c>files.read</c> and <c>books.tags.read</c>, decided in
/// <see cref="BookService"/> from the flags this controller reads.
/// </summary>
[ApiController]
[Route("api/books")]
public sealed class BooksController(BookService service, IUserDisplayNameResolver displayNames) : ControllerBase
{
    [HttpGet(Name = "GetBooks")]
    [Authorize(Policy = PermissionClaims.BooksRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<BookSummary>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "List books with search, status/format/tag/author/cover/file filters, sort and paging.")]
    public async Task<IActionResult> Get([FromQuery] BooksQueryParams query, CancellationToken cancellationToken = default) =>
        Ok(await service.ListAsync(query, Caller(string.Empty), cancellationToken));

    [HttpGet("{id}", Name = "GetBook")]
    [Authorize(Policy = PermissionClaims.BooksRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingBook))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Get one book.")]
    public async Task<IActionResult> Get([FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        var book = await service.GetAsync(id, Caller(string.Empty), cancellationToken);
        return book is null ? NotFound(id) : Ok(await WithNamesAsync(book, cancellationToken));
    }

    [HttpPost(Name = "PostBook")]
    [Authorize(Policy = PermissionClaims.BooksCreate)]
    [RequestSizeLimit(BookLimits.MaxRequestBytes)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingBook))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Create a book. Links are scalar ids; each non-empty array needs its read claim.")]
    public async Task<IActionResult> Post([FromBody] NewBook request, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        var created = await service.CreateAsync(request, Caller(userId), cancellationToken);
        return CreatedAtRoute("GetBook", new { id = created.BookId }, await WithNamesAsync(created, cancellationToken));
    }

    [HttpPut("{id}", Name = "PutBook")]
    [Authorize(Policy = PermissionClaims.BooksUpdate)]
    [RequestSizeLimit(BookLimits.MaxRequestBytes)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingBook))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Replace a book (not an upsert). version must match; a null array leaves that set unchanged.")]
    public async Task<IActionResult> Put(
        [FromRoute(Name = "id")] Guid id, [FromBody] UpdateBook request, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        var updated = await service.UpdateAsync(id, request, Caller(userId), cancellationToken);
        return updated is null ? NotFound(id) : Ok(await WithNamesAsync(updated, cancellationToken));
    }

    [HttpDelete("{id}", Name = "DeleteBook")]
    [Authorize(Policy = PermissionClaims.BooksDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Delete a book and its links. Photos, files, contacts and tags are untouched.")]
    public async Task<IActionResult> Delete([FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        return await service.DeleteAsync(id, userId, cancellationToken) ? NoContent() : NotFound(id);
    }

    private ObjectResult NotFound(Guid id) => this.NotFoundProblem($"Book ID {id} not found.");

    private BookCaller Caller(string userId) => new(
        userId,
        CanReadContacts: User.HasPermission(PermissionClaims.ContactsRead),
        CanReadPhotos: User.HasPermission(PermissionClaims.PhotosRead),
        CanReadFiles: User.HasPermission(PermissionClaims.FilesRead),
        CanReadTags: User.HasPermission(PermissionClaims.BookTagsRead));

    private async Task<ExistingBook> WithNamesAsync(ExistingBook book, CancellationToken cancellationToken)
    {
        var names = await displayNames.ResolveAsync(User, [book.CreatedByUserId, book.UpdatedByUserId], cancellationToken);
        book.CreatedByName = names.NameForOptional(book.CreatedByUserId);
        book.UpdatedByName = names.NameForOptional(book.UpdatedByUserId);
        return book;
    }
}
