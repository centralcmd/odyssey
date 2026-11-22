using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Core.Journal.Thumbnails;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Odyssey.Dtos.Books;

namespace Odyssey.Core.Books;

/// <summary>
/// The book catalogue (issue #527). Books are shared household data: no owner, access by the <c>books.*</c> claims,
/// with what a caller may see of contacts, photos, files and tags decided here from <see cref="BookCaller"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Claim before existence.</b> Every link array is checked against its read claim before any lookup, so a caller
/// without the claim gets the same fixed <c>403</c> whether the id exists or not. On <c>PUT</c> one rule covers all
/// four arrays: <c>null</c> leaves the set unchanged, any array needs that set's claim.
/// </para>
/// <para>
/// <b>Children are diffed in place.</b> No link row is deleted and re-inserted with the same key pair in one save,
/// so the <c>(BookId, …)</c> unique indexes cannot collide; the book's <see cref="Book.Version"/> serialises writers.
/// </para>
/// <para>
/// Logs carry ids and counts only — never a title, ISBN, note, contact name or file name.
/// </para>
/// </remarks>
public sealed class BookService(
    OdysseyContext context,
    IContactLookup contacts,
    IPhotoLookup photos,
    ILogger<BookService> logger,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<PagedResult<BookSummary>> ListAsync(
        BooksQueryParams query, BookCaller caller, CancellationToken cancellationToken = default)
    {
        if (!caller.CanReadContacts && (query.ContactId is not null || query.SortBy == BookSortBy.Author))
        {
            throw new DomainForbiddenException(BookForbidden.ContactFilter);
        }

        if (!caller.CanReadPhotos && query.HasCover is not null)
        {
            throw new DomainForbiddenException(BookForbidden.CoverFilter);
        }

        if (!caller.CanReadTags && query.TagIds is { Length: > 0 })
        {
            throw new DomainForbiddenException(BookForbidden.TagFilter);
        }

        if (!caller.CanReadFiles && query.HasAttachments is not null)
        {
            throw new DomainForbiddenException(BookForbidden.FileFilter);
        }

        var q = context.Books.AsNoTracking();

        var term = ListQuery.NormalizeSearch(query.Search);
        if (term is not null)
        {
            var pattern = ListQuery.ContainsPattern(term);
            var isbns = new List<string>();
            if (IsbnRules.TryNormalize(term, out var isbn) && isbn is not null)
            {
                isbns.Add(isbn);
                if (IsbnRules.Counterpart(isbn) is { } other)
                {
                    isbns.Add(other);
                }
            }

            // Contact names are contacts.read data: without the claim the name part is skipped silently, never a
            // 403, so the same search is valid for every role and only matches less (§5 item 1). NormalizedName is
            // the stored, upper-cased ContactNaming.Resolve value, matched in the one list query (no id list).
            var searchNames = caller.CanReadContacts;
            var namePattern = ListQuery.ContainsPattern(ContactNaming.Normalize(term));
            var people = context.Contacts;
            q = q.Where(b =>
                EF.Functions.Like(b.Title, pattern)
                || (b.Publisher != null && EF.Functions.Like(b.Publisher, pattern))
                || (b.Isbn != null && isbns.Contains(b.Isbn))
                || (searchNames
                    && (people.Any(c => c.ContactId == b.MainAuthorContactId && EF.Functions.Like(c.NormalizedName, namePattern))
                        || b.Contributors.Any(bc => people.Any(c => c.ContactId == bc.ContactId
                            && EF.Functions.Like(c.NormalizedName, namePattern))))));
        }

        if (query.Status is { Length: > 0 } statuses)
        {
            q = q.Where(b => statuses.Contains(b.Status));
        }

        if (query.Format is { Length: > 0 } formats)
        {
            q = q.Where(b => formats.Contains(b.Format));
        }

        foreach (var tagId in (query.TagIds ?? []).Distinct())
        {
            q = q.Where(b => b.TagLinks.Any(l => l.BookTagId == tagId));
        }

        if (query.ContactId is { } contactId)
        {
            q = q.Where(b => b.MainAuthorContactId == contactId || b.Contributors.Any(c => c.ContactId == contactId));
        }

        if (query.HasAttachments is { } hasAttachments)
        {
            q = q.Where(b => b.Attachments.Any() == hasAttachments);
        }

        if (query.HasCover is { } hasCover)
        {
            q = q.Where(b => b.Images.Any(i => i.IsCover) == hasCover);
        }

        var sortBy = query.SortBy ?? BookSortBy.Title;
        var ascending = ListQuery.Ascending(query.SortDir,
            naturalDefaultAscending: sortBy is BookSortBy.Title or BookSortBy.Author or BookSortBy.Year);
        var authors = context.Contacts;
        IOrderedQueryable<Book> sorted = sortBy switch
        {
            BookSortBy.Author => ascending
                ? q.OrderBy(b => authors.Where(c => c.ContactId == b.MainAuthorContactId).Select(c => c.NormalizedName).FirstOrDefault())
                : q.OrderByDescending(b => authors.Where(c => c.ContactId == b.MainAuthorContactId).Select(c => c.NormalizedName).FirstOrDefault()),
            // Nulls last in both directions (§5 item 1).
            BookSortBy.Year => ascending
                ? q.OrderBy(b => b.Year == null).ThenBy(b => b.Year)
                : q.OrderBy(b => b.Year == null).ThenByDescending(b => b.Year),
            BookSortBy.CreatedAt => ascending ? q.OrderBy(b => b.CreatedAt) : q.OrderByDescending(b => b.CreatedAt),
            BookSortBy.UpdatedAt => ascending ? q.OrderBy(b => b.UpdatedAt) : q.OrderByDescending(b => b.UpdatedAt),
            _ => ascending ? q.OrderBy(b => b.Title) : q.OrderByDescending(b => b.Title),
        };

        var page = await sorted.ThenBy(b => b.BookId)
            .Select(b => new SummaryRow(
                b.BookId, b.Title, b.Year, b.Isbn, b.Publisher, b.Format, b.Status, b.MainAuthorContactId,
                b.Contributors.Count,
                b.Attachments.Count,
                b.Images.Count,
                b.Images.Where(i => i.IsCover).Select(i => (Guid?)i.PhotoId).FirstOrDefault(),
                b.TagLinks.Select(l => l.BookTagId).ToList(),
                b.UpdatedAt))
            .ToPagedResultAsync(query.Offset, query.Limit, cancellationToken);

        var refs = await ContactRefsAsync(page.Items.Select(r => r.MainAuthorContactId).ToList(), caller, cancellationToken);
        var covers = caller.CanReadPhotos ? page.Items.Select(r => r.CoverPhotoId).OfType<Guid>().ToList() : [];
        var ready = await PhotoThumbnailService.ReadyAsync(context, covers, cancellationToken);
        var tags = await TagCandidatesAsync(page.Items.SelectMany(r => r.TagIds).ToList(), cancellationToken);

        return new PagedResult<BookSummary>
        {
            Offset = page.Offset,
            Limit = page.Limit,
            TotalCount = page.TotalCount,
            Items = page.Items.Select(r => TagIconProjection.Apply(new BookSummary
            {
                BookId = r.BookId,
                Title = r.Title,
                Year = r.Year,
                Isbn = r.Isbn,
                Publisher = r.Publisher,
                Format = r.Format,
                Status = r.Status,
                MainAuthor = Reference(r.MainAuthorContactId, refs, caller),
                ContributorCount = r.ContributorCount,
                AttachmentCount = caller.CanReadFiles ? r.AttachmentCount : null,
                ImageCount = caller.CanReadPhotos ? r.ImageCount : null,
                CoverPhotoId = caller.CanReadPhotos ? r.CoverPhotoId : null,
                CoverThumbnailsReady = caller.CanReadPhotos && r.CoverPhotoId is { } cover && ready.Contains(cover),
                UpdatedAt = r.UpdatedAt,
            }, r.TagIds, tags)).ToList(),
        };
    }

    public async Task<ExistingBook?> GetAsync(Guid id, BookCaller caller, CancellationToken cancellationToken = default)
    {
        var book = await LoadAsync(id, tracking: false, cancellationToken);
        return book is null ? null : await ProjectAsync(book, caller, cancellationToken);
    }

    public async Task<ExistingBook> CreateAsync(NewBook request, BookCaller caller, CancellationToken cancellationToken = default)
    {
        // POST: null means empty, and only a non-empty array needs its claim. The main author always does.
        if (!caller.CanReadContacts)
        {
            throw new DomainForbiddenException(BookForbidden.ContactLink);
        }

        EnsureClaim(request.PhotoIds is { Count: > 0 }, caller.CanReadPhotos, BookForbidden.PhotoLink);
        EnsureClaim(request.AttachmentFileIds is { Count: > 0 }, caller.CanReadFiles, BookForbidden.FileLink);
        EnsureClaim(request.TagIds is { Count: > 0 }, caller.CanReadTags, BookForbidden.TagLink);

        var fields = Validate(request);
        var contributors = request.Contributors ?? [];
        var photoIds = request.PhotoIds ?? [];
        var fileIds = request.AttachmentFileIds ?? [];
        var tagIds = request.TagIds ?? [];
        ValidateLinks(fields.MainAuthorContactId, contributors, photoIds, request.CoverPhotoId, fileIds, tagIds);

        await EnsureContactsAsync(fields.MainAuthorContactId, contributors, current: null, cancellationToken);
        await EnsurePhotosAsync(photoIds, cancellationToken);
        await EnsureFilesAsync(fileIds, cancellationToken);
        await EnsureTagsAsync(tagIds, new HashSet<Guid>(), cancellationToken);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var book = new Book
        {
            Title = fields.Title,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByUserId = caller.UserId,
            UpdatedByUserId = caller.UserId,
        };
        Apply(book, fields);

        for (var i = 0; i < contributors.Count; i++)
        {
            book.Contributors.Add(new BookContributor
            {
                ContactId = contributors[i].ContactId!.Value,
                Role = Clean(contributors[i].Role),
                Position = i,
            });
        }

        foreach (var photoId in photoIds)
        {
            book.Images.Add(new BookImage { PhotoId = photoId, IsCover = photoId == request.CoverPhotoId, CreatedAt = now });
        }

        foreach (var fileId in fileIds)
        {
            book.Attachments.Add(new BookAttachment { FileId = fileId, CreatedAt = now });
        }

        foreach (var tagId in tagIds)
        {
            book.TagLinks.Add(new BookTagLink { BookTagId = tagId });
        }

        context.Books.Add(book);
        await SaveAsync(cancellationToken);

        return (await GetAsync(book.BookId, caller, cancellationToken))!;
    }

    /// <summary>Full replacement. <c>null</c> for an unknown id; never an upsert.</summary>
    public async Task<ExistingBook?> UpdateAsync(
        Guid id, UpdateBook request, BookCaller caller, CancellationToken cancellationToken = default)
    {
        // Load first (§3 step 4): an unknown id is the same 404 for every caller, whatever it sent.
        var book = await LoadAsync(id, tracking: true, cancellationToken);
        if (book is null)
        {
            return null;
        }

        // Claims before anything else (§3 step 5).
        var mainAuthorChanges = request.MainAuthorContactId is { } requestedAuthor && requestedAuthor != book.MainAuthorContactId;
        EnsureClaim(mainAuthorChanges, caller.CanReadContacts, BookForbidden.ContactLink);
        EnsureClaim(request.Contributors is not null, caller.CanReadContacts, BookForbidden.ContactLink);
        EnsureClaim(request.PhotoIds is not null, caller.CanReadPhotos, BookForbidden.PhotoLink);
        EnsureClaim(request.AttachmentFileIds is not null, caller.CanReadFiles, BookForbidden.FileLink);
        EnsureClaim(request.TagIds is not null, caller.CanReadTags, BookForbidden.TagLink);

        if (request.Version != book.Version)
        {
            throw new BookVersionConflictException();
        }

        var fields = Validate(request);
        var contributors = request.Contributors
            ?? book.Contributors.OrderBy(c => c.Position).ThenBy(c => c.BookContributorId)
                .Select(c => new BookContributorRequest { ContactId = c.ContactId, Role = c.Role }).ToList();
        var photoIds = request.PhotoIds;
        var fileIds = request.AttachmentFileIds;
        var tagIds = request.TagIds;
        ValidateLinks(fields.MainAuthorContactId, contributors, photoIds ?? [], photoIds is null ? null : request.CoverPhotoId,
            fileIds ?? [], tagIds ?? []);

        await EnsureContactsAsync(fields.MainAuthorContactId, contributors, book, cancellationToken);
        if (photoIds is not null)
        {
            await EnsurePhotosAsync(photoIds, cancellationToken);
        }

        if (fileIds is not null)
        {
            await EnsureFilesAsync(fileIds, cancellationToken);
        }

        if (tagIds is not null)
        {
            await EnsureTagsAsync(tagIds, book.TagLinks.Select(l => l.BookTagId).ToHashSet(), cancellationToken);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        Apply(book, fields);

        if (request.Contributors is not null)
        {
            DiffContributors(book, contributors);
        }

        if (photoIds is not null)
        {
            DiffImages(book, photoIds, request.CoverPhotoId, now);
        }

        if (fileIds is not null)
        {
            DiffAttachments(book, fileIds, now);
        }

        if (tagIds is not null)
        {
            DiffTags(book, tagIds);
        }

        book.UpdatedAt = now;
        book.UpdatedByUserId = caller.UserId;
        book.Version++;
        await SaveAsync(cancellationToken);

        return await GetAsync(book.BookId, caller, cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid id, string userId, CancellationToken cancellationToken = default)
    {
        var book = await context.Books
            .Include(b => b.Contributors)
            .Include(b => b.Images)
            .Include(b => b.Attachments)
            .Include(b => b.TagLinks)
            .FirstOrDefaultAsync(b => b.BookId == id, cancellationToken);
        if (book is null)
        {
            return false;
        }

        // Tracked removal so the EF InMemory tier sweeps the children too; the database cascades them anyway.
        context.BookContributors.RemoveRange(book.Contributors);
        context.BookImages.RemoveRange(book.Images);
        context.BookAttachments.RemoveRange(book.Attachments);
        context.BookTagLinks.RemoveRange(book.TagLinks);
        context.Books.Remove(book);
        await context.SaveChangesAsync(cancellationToken);

        // ISO 27001 A.8.15 traceability for the only irreversible book action: ids only.
        logger.LogInformation("Book {BookId} deleted by user {UserId}.", id, userId);
        return true;
    }

    // ── Validation ─────────────────────────────────────────────────────────────────────────────

    private sealed record BookFields(
        string Title, string? Publisher, int? Year, string? Isbn, BookFormat Format, BookStatus Status,
        string? Description, string? Notes, Guid MainAuthorContactId);

    private static BookFields Validate(IBookFields request)
    {
        var title = request.Title?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            throw new DomainUnprocessableException("Enter a title.", "title");
        }

        if (request.Year == 0)
        {
            throw new DomainUnprocessableException("There is no year 0; use 1 or -1 (1 BC).", "year");
        }

        if (!IsbnRules.TryNormalize(request.Isbn, out var isbn))
        {
            throw new DomainUnprocessableException("This isn't a valid ISBN-10 or ISBN-13.", "isbn");
        }

        return new BookFields(
            title, Clean(request.Publisher), request.Year, isbn, request.Format!.Value, request.Status!.Value,
            Clean(request.Description), Clean(request.Notes), request.MainAuthorContactId!.Value);
    }

    private static void ValidateLinks(
        Guid mainAuthor, IReadOnlyList<BookContributorRequest> contributors, IReadOnlyList<Guid> photoIds, Guid? coverPhotoId,
        IReadOnlyList<Guid> fileIds, IReadOnlyList<Guid> tagIds)
    {
        var seen = new HashSet<Guid>();
        for (var i = 0; i < contributors.Count; i++)
        {
            var contactId = contributors[i].ContactId!.Value;
            if (contactId == mainAuthor)
            {
                throw new DomainUnprocessableException(
                    "The main author is already credited; remove them from the contributors.", $"contributors[{i}].contactId");
            }

            if (!seen.Add(contactId))
            {
                throw new DomainUnprocessableException("A contact is listed twice.", $"contributors[{i}].contactId");
            }
        }

        EnsureDistinct(photoIds, "photoIds");
        EnsureDistinct(fileIds, "attachmentFileIds");
        EnsureDistinct(tagIds, "tagIds");

        if (coverPhotoId is { } cover && !photoIds.Contains(cover))
        {
            throw new DomainUnprocessableException("The cover must be one of the book's images.", "coverPhotoId");
        }
    }

    private static void EnsureDistinct(IReadOnlyList<Guid> ids, string field)
    {
        if (ids.Distinct().Count() != ids.Count)
        {
            throw new DomainUnprocessableException("An id is listed twice.", field);
        }
    }

    private static void EnsureClaim(bool needed, bool held, string message)
    {
        if (needed && !held)
        {
            throw new DomainForbiddenException(message);
        }
    }

    private async Task EnsureContactsAsync(
        Guid mainAuthor, IReadOnlyList<BookContributorRequest> contributors, Book? current, CancellationToken cancellationToken)
    {
        var wanted = contributors.Select(c => c.ContactId!.Value).Append(mainAuthor).Distinct().ToList();
        var refs = await contacts.ResolveRefsAsync(wanted, cancellationToken);

        // A newly linked contact must not be archived; a kept one may be (§3 step 8).
        var kept = current is null
            ? new HashSet<Guid>()
            : current.Contributors.Select(c => c.ContactId).Append(current.MainAuthorContactId).ToHashSet();

        if (!refs.TryGetValue(mainAuthor, out var author) || (author.Archived is not null && !kept.Contains(mainAuthor)))
        {
            throw new DomainUnprocessableException("Choose an existing, active contact as the main author.", "mainAuthorContactId");
        }

        for (var i = 0; i < contributors.Count; i++)
        {
            var contactId = contributors[i].ContactId!.Value;
            if (!refs.TryGetValue(contactId, out var contact) || (contact.Archived is not null && !kept.Contains(contactId)))
            {
                throw new DomainUnprocessableException("Choose an existing, active contact.", $"contributors[{i}].contactId");
            }
        }
    }

    private async Task EnsurePhotosAsync(IReadOnlyCollection<Guid> photoIds, CancellationToken cancellationToken)
    {
        if (photoIds.Count == 0)
        {
            return;
        }

        var existing = await photos.ExistingIdsAsync(photoIds, cancellationToken);
        if (photoIds.Any(id => !existing.Contains(id)))
        {
            throw new DomainUnprocessableException("One or more photos do not exist.", "photoIds");
        }
    }

    private async Task EnsureFilesAsync(IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken)
    {
        if (fileIds.Count == 0)
        {
            return;
        }

        var ids = fileIds.ToList();
        var visible = await context.FileMetadata.AsNoTracking()
            .Where(FileReferenceCounter.VisibleInFilesStore(context))
            .Where(f => ids.Contains(f.Id))
            .Select(f => f.Id)
            .ToListAsync(cancellationToken);
        if (ids.Except(visible).Any())
        {
            throw new DomainUnprocessableException("One or more files do not exist.", "attachmentFileIds");
        }
    }

    private async Task EnsureTagsAsync(IReadOnlyCollection<Guid> tagIds, IReadOnlySet<Guid> currentTagIds, CancellationToken cancellationToken)
    {
        if (tagIds.Count == 0)
        {
            return;
        }

        var ids = tagIds.ToList();
        var found = await context.BookTags.AsNoTracking()
            .Where(t => ids.Contains(t.BookTagId))
            .Select(t => new { t.BookTagId, t.Archived })
            .ToListAsync(cancellationToken);
        if (found.Count != ids.Count)
        {
            throw new DomainUnprocessableException("One or more tags do not exist.", "tagIds");
        }

        // Keeping an already-linked archived tag is allowed; adding one is not.
        if (found.Any(t => t.Archived is not null && !currentTagIds.Contains(t.BookTagId)))
        {
            throw new DomainUnprocessableException("An archived tag cannot be added.", "tagIds");
        }
    }

    // ── Writes ─────────────────────────────────────────────────────────────────────────────────

    private static void Apply(Book book, BookFields fields)
    {
        book.Title = fields.Title;
        book.Publisher = fields.Publisher;
        book.Year = fields.Year;
        book.Isbn = fields.Isbn;
        book.Format = fields.Format;
        book.Status = fields.Status;
        book.Description = fields.Description;
        book.Notes = fields.Notes;
        book.MainAuthorContactId = fields.MainAuthorContactId;
    }

    private void DiffContributors(Book book, IReadOnlyList<BookContributorRequest> requested)
    {
        var wanted = requested.Select((c, i) => (ContactId: c.ContactId!.Value, Role: Clean(c.Role), Position: i))
            .ToDictionary(c => c.ContactId);
        foreach (var row in book.Contributors.Where(c => !wanted.ContainsKey(c.ContactId)).ToList())
        {
            book.Contributors.Remove(row);
            context.BookContributors.Remove(row);
        }

        foreach (var (contactId, role, position) in wanted.Values)
        {
            var row = book.Contributors.FirstOrDefault(c => c.ContactId == contactId);
            if (row is null)
            {
                book.Contributors.Add(new BookContributor { ContactId = contactId, Role = role, Position = position });
            }
            else
            {
                row.Role = role;
                row.Position = position;
            }
        }
    }

    private void DiffImages(Book book, IReadOnlyList<Guid> photoIds, Guid? coverPhotoId, DateTime now)
    {
        var wanted = photoIds.ToHashSet();
        foreach (var row in book.Images.Where(i => !wanted.Contains(i.PhotoId)).ToList())
        {
            book.Images.Remove(row);
            context.BookImages.Remove(row);
        }

        foreach (var photoId in photoIds)
        {
            var row = book.Images.FirstOrDefault(i => i.PhotoId == photoId);
            if (row is null)
            {
                book.Images.Add(new BookImage { PhotoId = photoId, IsCover = photoId == coverPhotoId, CreatedAt = now });
            }
            else
            {
                row.IsCover = photoId == coverPhotoId;
            }
        }
    }

    private void DiffAttachments(Book book, IReadOnlyList<Guid> fileIds, DateTime now)
    {
        var wanted = fileIds.ToHashSet();
        foreach (var row in book.Attachments.Where(a => !wanted.Contains(a.FileId)).ToList())
        {
            book.Attachments.Remove(row);
            context.BookAttachments.Remove(row);
        }

        foreach (var fileId in fileIds.Where(id => book.Attachments.All(a => a.FileId != id)))
        {
            book.Attachments.Add(new BookAttachment { FileId = fileId, CreatedAt = now });
        }
    }

    private void DiffTags(Book book, IReadOnlyList<Guid> tagIds)
    {
        var wanted = tagIds.ToHashSet();
        foreach (var row in book.TagLinks.Where(l => !wanted.Contains(l.BookTagId)).ToList())
        {
            book.TagLinks.Remove(row);
            context.BookTagLinks.Remove(row);
        }

        foreach (var tagId in tagIds.Where(id => book.TagLinks.All(l => l.BookTagId != id)))
        {
            book.TagLinks.Add(new BookTagLink { BookTagId = tagId });
        }
    }

    /// <summary>
    /// Saves, mapping a lost race (a concurrency exception or a duplicate child key) to <c>409 book-version-conflict</c>
    /// and a link whose target vanished between validation and save (<c>1452</c>) to a <c>422</c> on that field.
    /// </summary>
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new BookVersionConflictException();
        }
        catch (DbUpdateException exception) when (DbErrors.IsDuplicateKey(exception))
        {
            throw new BookVersionConflictException();
        }
        catch (DbUpdateException exception) when (DbErrors.IsMissingReference(exception))
        {
            var field = DbErrors.MissingReferenceNames(exception, "Contacts") ? "mainAuthorContactId"
                : DbErrors.MissingReferenceNames(exception, "Photos") ? "photoIds"
                : DbErrors.MissingReferenceNames(exception, "FileMetadata") ? "attachmentFileIds"
                : DbErrors.MissingReferenceNames(exception, "BookTags") ? "tagIds"
                : "links";
            throw new DomainUnprocessableException("A linked item was removed while the book was being saved.", field);
        }
    }

    // ── Reads ──────────────────────────────────────────────────────────────────────────────────

    private sealed record SummaryRow(
        Guid BookId, string Title, int? Year, string? Isbn, string? Publisher, BookFormat Format, BookStatus Status,
        Guid MainAuthorContactId, int ContributorCount, int AttachmentCount, int ImageCount, Guid? CoverPhotoId, List<Guid> TagIds,
        DateTime UpdatedAt);

    private Task<Book?> LoadAsync(Guid id, bool tracking, CancellationToken cancellationToken)
    {
        var books = tracking ? context.Books : context.Books.AsNoTracking();
        return books
            .Include(b => b.Contributors)
            .Include(b => b.Images)
            .Include(b => b.Attachments)
            .Include(b => b.TagLinks)
            .AsSplitQuery()
            .FirstOrDefaultAsync(b => b.BookId == id, cancellationToken);
    }

    private async Task<ExistingBook> ProjectAsync(Book book, BookCaller caller, CancellationToken cancellationToken)
    {
        var contributors = book.Contributors.OrderBy(c => c.Position).ThenBy(c => c.BookContributorId).ToList();
        var refs = await ContactRefsAsync(
            contributors.Select(c => c.ContactId).Append(book.MainAuthorContactId).ToList(), caller, cancellationToken);

        List<BookImageDto>? images = null;
        Guid? coverPhotoId = null;
        if (caller.CanReadPhotos)
        {
            var photoIds = book.Images.Select(i => i.PhotoId).ToList();
            var info = await photos.ResolveAsync(photoIds, cancellationToken);
            var ready = await PhotoThumbnailService.ReadyAsync(context, photoIds, cancellationToken);
            images = book.Images
                .Where(i => info.ContainsKey(i.PhotoId))
                .OrderBy(i => i.CreatedAt).ThenBy(i => i.BookImageId)
                .Select(i => new BookImageDto
                {
                    BookImageId = i.BookImageId,
                    PhotoId = i.PhotoId,
                    IsCover = i.IsCover,
                    Title = info[i.PhotoId].Title,
                    FileName = info[i.PhotoId].FileName,
                    ThumbnailsReady = ready.Contains(i.PhotoId),
                    CreatedAt = i.CreatedAt,
                })
                .ToList();
            coverPhotoId = images.FirstOrDefault(i => i.IsCover)?.PhotoId;
        }

        List<BookAttachmentDto>? attachments = null;
        if (caller.CanReadFiles)
        {
            var fileIds = book.Attachments.Select(a => a.FileId).ToList();
            var files = await context.FileMetadata.AsNoTracking()
                .Where(f => fileIds.Contains(f.Id))
                .Select(f => new { f.Id, f.FileName, f.ContentType, f.SizeBytes })
                .ToDictionaryAsync(f => f.Id, cancellationToken);
            attachments = book.Attachments
                .Where(a => files.ContainsKey(a.FileId))
                .OrderBy(a => a.CreatedAt).ThenBy(a => a.BookAttachmentId)
                .Select(a => new BookAttachmentDto
                {
                    BookAttachmentId = a.BookAttachmentId,
                    FileId = a.FileId,
                    FileName = files[a.FileId].FileName,
                    ContentType = files[a.FileId].ContentType,
                    SizeBytes = files[a.FileId].SizeBytes,
                    CreatedAt = a.CreatedAt,
                })
                .ToList();
        }

        var tagIds = book.TagLinks.Select(l => l.BookTagId).ToList();
        var tags = await TagCandidatesAsync(tagIds, cancellationToken);

        return TagIconProjection.Apply(new ExistingBook
        {
            BookId = book.BookId,
            Version = book.Version,
            Title = book.Title,
            Publisher = book.Publisher,
            Year = book.Year,
            Isbn = book.Isbn,
            Format = book.Format,
            Status = book.Status,
            Description = book.Description,
            Notes = book.Notes,
            MainAuthor = Reference(book.MainAuthorContactId, refs, caller),
            Contributors = contributors.Select(c => new ExistingBookContributor
            {
                BookContributorId = c.BookContributorId,
                Position = c.Position,
                Role = c.Role,
                Contact = Reference(c.ContactId, refs, caller),
            }).ToList(),
            Images = images,
            CoverPhotoId = coverPhotoId,
            Attachments = attachments,
            CreatedAt = book.CreatedAt,
            UpdatedAt = book.UpdatedAt,
            CreatedByUserId = book.CreatedByUserId,
            UpdatedByUserId = book.UpdatedByUserId,
        }, tagIds, tags);
    }

    private async Task<IReadOnlyDictionary<Guid, ContactRef>> ContactRefsAsync(
        IReadOnlyCollection<Guid> ids, BookCaller caller, CancellationToken cancellationToken) =>
        caller.CanReadContacts && ids.Count > 0
            ? await contacts.ResolveRefsAsync(ids, cancellationToken)
            : new Dictionary<Guid, ContactRef>();

    /// <summary>
    /// The purpose-built contact projection (§7.3): id always; name, type and archived only with
    /// <c>contacts.read</c>. Without the claim <c>archived</c> is <c>null</c> — unknown, never <c>false</c>.
    /// </summary>
    private static BookContactReference Reference(Guid contactId, IReadOnlyDictionary<Guid, ContactRef> refs, BookCaller caller) =>
        caller.CanReadContacts && refs.TryGetValue(contactId, out var contact)
            ? new BookContactReference
            {
                ContactId = contactId,
                Name = contact.Name,
                Type = contact.Type,
                Archived = contact.Archived is not null,
            }
            : new BookContactReference { ContactId = contactId };

    private Task<Dictionary<Guid, TagIconCandidate>> TagCandidatesAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        var distinct = ids.Distinct().ToList();
        return context.BookTags.AsNoTracking()
            .Where(tag => distinct.Contains(tag.BookTagId))
            .Select(tag => new TagIconCandidate(tag.BookTagId, tag.Name, tag.Icon, tag.Color))
            .ToDictionaryAsync(tag => tag.Id, cancellationToken);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
