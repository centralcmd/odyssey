using Odyssey.Core;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Odyssey.Core.Finance;
using Odyssey.Core.Pagination;
using Odyssey.Context;
using Odyssey.Dtos.Journal;
using Odyssey.Core.Journal;
using Odyssey.Dtos;

namespace Odyssey.Core.Journal;

/// <summary>
/// CRUD, archival, and server-side listing for the shared journal. Cross-context references
/// (contacts, files) are validated for existence via narrow read-only Finance lookups, which run in
/// front of the DB foreign keys so a bad reference is a 400 rather than a constraint violation
/// crosses the context boundary (§5). Reads return link ids only — the client hydrates names (§10.2).
/// </summary>
public class JournalEntryService
{
    private readonly OdysseyContext context;
    private readonly IContactLookup contacts;
    private readonly IFileLookup files;
    private readonly IPhotoLookup photos;
    private readonly IJournalLimitsLookup journalLimits;
    private readonly TimeProvider timeProvider;

    public JournalEntryService(
        OdysseyContext context,
        IContactLookup contacts,
        IFileLookup files,
        IPhotoLookup photos,
        IJournalLimitsLookup journalLimits,
        TimeProvider? timeProvider = null)
    {
        this.context = context;
        this.contacts = contacts;
        this.files = files;
        this.photos = photos;
        this.journalLimits = journalLimits;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Server-side paged list (issue #277): free-text search + tag/contact/date-range/archival filters + allowlisted sort.</summary>
    public async Task<PagedResult<JournalEntrySummary>> ListAsync(
        JournalEntriesQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var q = ApplyFilters(
            context.JournalEntries.AsQueryable(),
            query.Search, query.TagIds, query.ContactIds, query.From, query.To);

        // Archived is a derived (column) state, hidden by default; included only when explicitly requested.
        if (query.Status == ArchivalStatus.Archived)
        {
            q = q.Where(e => e.Archived != null);
        }
        else
        {
            q = q.Where(e => e.Archived == null);
        }

        var ascending = ListQuery.Ascending(query.SortDir, naturalDefaultAscending: query.SortBy is JournalEntrySortBy.Title);
        IOrderedQueryable<JournalEntry> sorted = query.SortBy switch
        {
            JournalEntrySortBy.Title => ascending ? q.OrderBy(e => e.Title) : q.OrderByDescending(e => e.Title),
            JournalEntrySortBy.CreatedAt => ascending ? q.OrderBy(e => e.CreatedAt) : q.OrderByDescending(e => e.CreatedAt),
            _ => ascending ? q.OrderBy(e => e.EntryDate) : q.OrderByDescending(e => e.EntryDate),
        };

        var rows = sorted
            .ThenBy(e => e.JournalEntryId)
            .Select(e => new EntryRow
            {
                JournalEntryId = e.JournalEntryId,
                Title = e.Title,
                Content = e.Content,
                EntryDate = e.EntryDate,
                Location = e.Location,
                CreatedByUserId = e.CreatedByUserId,
                Archived = e.Archived,
                TagIds = e.EntryTags.Select(t => t.JournalTagId).ToList(),
                PhotoCount = e.Photos.Count,
                AttachmentCount = e.Attachments.Count,
                ContactCount = e.Contacts.Count,
            });

        return await rows.ToPagedResultAsync(query.Offset, query.Limit, row => new JournalEntrySummary
        {
            JournalEntryId = row.JournalEntryId,
            Title = row.Title,
            Snippet = JournalText.Truncate(row.Content, 200),
            EntryDate = row.EntryDate,
            Location = row.Location,
            CreatedByUserId = row.CreatedByUserId,
            Archived = row.Archived,
            TagIds = row.TagIds,
            PhotoCount = row.PhotoCount,
            AttachmentCount = row.AttachmentCount,
            ContactCount = row.ContactCount,
        }, cancellationToken);
    }

    public async Task<ExistingJournalEntry?> Get(Guid id, CancellationToken cancellationToken = default)
    {
        var entry = await LoadWithDetails(id, cancellationToken);
        return entry is null ? null : await ToDtoAsync(entry, cancellationToken);
    }

    public async Task<ExistingJournalEntry> Create(NewJournalEntry request, string userId, CancellationToken cancellationToken = default)
    {
        ValidateFields(request.Title, request.Content, request.EntryDate);
        var links = await ValidateLinks(
            request.TagIds, request.ContactIds, request.PhotoFileIds, request.AttachmentFileIds, cancellationToken);

        var externalUid = NormalizeExternalUid(request.ExternalUid) ?? NewExternalUid();
        await EnsureExternalUidAvailable(externalUid, excludeId: null, cancellationToken);

        // Photo-first, two separate transactions (§5): find-or-create the library Photos before writing
        // the journal link rows. If the journal save fails, the only artifact is an orphan library Photo
        // that the idempotent find-or-create re-links on retry.
        var photoIds = await ResolvePhotoIdsAsync(links.PhotoFileIds, userId, cancellationToken);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var entry = new JournalEntry
        {
            ExternalUid = externalUid,
            Title = request.Title,
            Content = request.Content,
            EntryDate = DateTimeNormalization.NormalizeToUtc(request.EntryDate),
            Location = request.Location,
            CreatedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        foreach (var tagId in links.TagIds)
        {
            entry.EntryTags.Add(new JournalEntryTag { JournalTagId = tagId });
        }

        foreach (var contactId in links.ContactIds)
        {
            entry.Contacts.Add(new JournalEntryContact { ContactId = contactId });
        }

        for (var i = 0; i < photoIds.Count; i++)
        {
            entry.Photos.Add(new JournalEntryPhoto { PhotoId = photoIds[i], Position = i, CreatedAt = now });
        }

        foreach (var fileId in links.AttachmentFileIds)
        {
            entry.Attachments.Add(new JournalEntryAttachment { FileId = fileId, CreatedAt = now });
        }

        context.JournalEntries.Add(entry);
        await context.SaveChangesAsync(cancellationToken);

        return await ToDtoAsync(entry, cancellationToken);
    }

    public async Task<ExistingJournalEntry?> Update(Guid id, UpdateJournalEntry request, string userId, CancellationToken cancellationToken = default)
    {
        var entry = await LoadWithDetailsForUpdate(id, cancellationToken);
        if (entry is null)
        {
            return null;
        }

        ValidateFields(request.Title, request.Content, request.EntryDate);
        var links = await ValidateLinks(
            request.TagIds, request.ContactIds, request.PhotoFileIds, request.AttachmentFileIds, cancellationToken);

        // An optional ExternalUid replaces the stored identity; a null leaves it untouched. A value that
        // already belongs to a different entry is rejected (400) before any DB unique-constraint fires.
        var newExternalUid = NormalizeExternalUid(request.ExternalUid);
        if (newExternalUid is not null && newExternalUid != entry.ExternalUid)
        {
            await EnsureExternalUidAvailable(newExternalUid, entry.JournalEntryId, cancellationToken);
            entry.ExternalUid = newExternalUid;
        }

        var photoIds = await ResolvePhotoIdsAsync(links.PhotoFileIds, userId, cancellationToken);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        entry.Title = request.Title;
        entry.Content = request.Content;
        entry.EntryDate = DateTimeNormalization.NormalizeToUtc(request.EntryDate);
        entry.Location = request.Location;
        entry.UpdatedByUserId = userId;
        entry.UpdatedAt = now;
        ApplyArchiveTransition(entry, request.Archived);

        DiffTags(entry, links.TagIds);
        DiffContacts(entry, links.ContactIds);
        DiffPhotos(entry, photoIds, now);
        DiffAttachments(entry, links.AttachmentFileIds, now);

        await context.SaveChangesAsync(cancellationToken);

        return await ToDtoAsync(entry, cancellationToken);
    }

    public async Task<bool> Delete(Guid id, CancellationToken cancellationToken = default)
    {
        var entry = await context.JournalEntries.FirstOrDefaultAsync(e => e.JournalEntryId == id, cancellationToken);
        if (entry is null)
        {
            return false;
        }

        // Owned photo/attachment/link rows cascade; the underlying Files-store blobs are left intact (§6).
        context.JournalEntries.Remove(entry);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>The shape of a new external identity when the caller doesn't supply one — a URN UUID,
    /// matching the convention across the ICS/vCard interop features (issue #339 §6).</summary>
    internal static string NewExternalUid() => $"urn:uuid:{Guid.NewGuid()}";

    private static string? NormalizeExternalUid(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task EnsureExternalUidAvailable(string externalUid, Guid? excludeId, CancellationToken cancellationToken)
    {
        var clash = await context.JournalEntries
            .AnyAsync(e => e.ExternalUid == externalUid && (excludeId == null || e.JournalEntryId != excludeId), cancellationToken);
        if (clash)
        {
            throw new DomainValidationException("External ID is already in use by another journal entry.");
        }
    }

    // The search / tag / contact / date-range filter surface shared by the server-side list (#277)
    // and the VJOURNAL export (#339). Archival status is applied by the caller, because the two paths
    // differ on the default: the list hides archived rows, the export includes them (§5).
    internal static IQueryable<JournalEntry> ApplyFilters(
        IQueryable<JournalEntry> q, string? search, Guid[]? tagIds, Guid[]? contactIds, DateTime? from, DateTime? to)
    {
        var term = ListQuery.NormalizeSearch(search);
        if (term is not null)
        {
            var pattern = ListQuery.ContainsPattern(term);
            q = q.Where(e =>
                EF.Functions.Like(e.Title, pattern) ||
                EF.Functions.Like(e.Content, pattern) ||
                (e.Location != null && EF.Functions.Like(e.Location, pattern)));
        }

        if (tagIds is { Length: > 0 } tags)
        {
            var ids = tags.Distinct().ToList();
            q = q.Where(e => e.EntryTags.Any(t => ids.Contains(t.JournalTagId)));
        }

        if (contactIds is { Length: > 0 } contacts)
        {
            var ids = contacts.Distinct().ToList();
            q = q.Where(e => e.Contacts.Any(c => ids.Contains(c.ContactId)));
        }

        if (from is { } fromDate)
        {
            q = q.Where(e => e.EntryDate >= fromDate);
        }

        if (to is { } toDate)
        {
            q = q.Where(e => e.EntryDate <= toDate);
        }

        return q;
    }

    // Set/clear the soft-archive timestamp from the desired boolean state (mirrors the tag services).
    private void ApplyArchiveTransition(JournalEntry entry, bool requestedArchived)
    {
        var currentArchived = entry.Archived is not null;
        if (!currentArchived && requestedArchived)
        {
            entry.Archived = timeProvider.GetUtcNow().UtcDateTime;
        }
        else if (currentArchived && !requestedArchived)
        {
            entry.Archived = null;
        }
    }

    // Update is the only caller that writes to what it loads; every other one turns the row straight
    // into a DTO, so it reads through the untracked overload.
    private async Task<JournalEntry?> LoadWithDetails(Guid id, CancellationToken cancellationToken) =>
        await WithDetails(context.JournalEntries.AsNoTracking())
            .FirstOrDefaultAsync(e => e.JournalEntryId == id, cancellationToken);

    private async Task<JournalEntry?> LoadWithDetailsForUpdate(Guid id, CancellationToken cancellationToken) =>
        await WithDetails(context.JournalEntries)
            .FirstOrDefaultAsync(e => e.JournalEntryId == id, cancellationToken);

    private static IQueryable<JournalEntry> WithDetails(IQueryable<JournalEntry> entries) => entries
        .Include(e => e.EntryTags)
        .Include(e => e.Contacts)
        .Include(e => e.Photos)
        .Include(e => e.Attachments);

    private static void ValidateFields(string title, string content, DateTime entryDate)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new DomainValidationException("Title is required.");
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new DomainValidationException("Content is required.");
        }

        if (entryDate.Year is < 1900 or > 2100)
        {
            throw new DomainValidationException("EntryDate must fall within the years 1900–2100.");
        }
    }

    private async Task<ValidatedLinks> ValidateLinks(
        Guid[] tagIds,
        Guid[] contactIds,
        Guid[] photoFileIds,
        Guid[] attachmentFileIds,
        CancellationToken cancellationToken)
    {
        // One read for the whole validation pass, so every kind is judged against the same cap.
        var maxLinksPerKind = (await journalLimits.GetAsync(cancellationToken)).JournalEntryMaxLinksPerKind;
        var tags = DistinctCapped(tagIds, "tag", maxLinksPerKind);
        var contactLinks = DistinctCapped(contactIds, "contact", maxLinksPerKind);
        var photos = DistinctCapped(photoFileIds, "photo", maxLinksPerKind);
        var attachments = DistinctCapped(attachmentFileIds, "attachment", maxLinksPerKind);

        await EnsureTagsExist(tags, cancellationToken);
        await EnsureContactsExist(contactLinks, cancellationToken);
        await EnsurePhotoFilesValid(photos, cancellationToken);
        await EnsureAttachmentFilesExist(attachments, cancellationToken);

        return new ValidatedLinks(tags, contactLinks, photos, attachments);
    }

    private static List<Guid> DistinctCapped(Guid[] ids, string kind, int maxLinksPerKind)
    {
        var distinct = ids.Distinct().ToList();
        if (distinct.Count > maxLinksPerKind)
        {
            throw new DomainUnprocessableException(
                $"An entry cannot have more than {maxLinksPerKind} {kind} links.");
        }

        return distinct;
    }

    private async Task EnsureTagsExist(IReadOnlyCollection<Guid> tagIds, CancellationToken cancellationToken)
    {
        if (tagIds.Count == 0)
        {
            return;
        }

        var found = await context.JournalTags
            .Where(t => tagIds.Contains(t.JournalTagId) && t.Archived == null)
            .Select(t => t.JournalTagId)
            .ToListAsync(cancellationToken);

        var missing = tagIds.Except(found).ToList();
        if (missing.Count > 0)
        {
            throw new DomainUnprocessableException(
                $"Unknown or archived journal tag(s): {string.Join(", ", missing)}.");
        }
    }

    private async Task EnsureContactsExist(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return;
        }

        var found = await contacts.ExistingIdsAsync(ids, cancellationToken);
        var missing = ids.Where(id => !found.Contains(id)).ToList();
        if (missing.Count > 0)
        {
            throw new DomainUnprocessableException(
                $"Unknown contact link(s): {string.Join(", ", missing)}.");
        }
    }

    private async Task EnsurePhotoFilesValid(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return;
        }

        var found = await files.ExistingImageIdsAsync(ids, cancellationToken);
        var missing = ids.Where(id => !found.Contains(id)).ToList();
        if (missing.Count > 0)
        {
            throw new DomainUnprocessableException(
                $"Photo file(s) are unknown or not an image type: {string.Join(", ", missing)}.");
        }
    }

    private async Task EnsureAttachmentFilesExist(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return;
        }

        var found = await files.ExistingIdsAsync(ids, cancellationToken);
        var missing = ids.Where(id => !found.Contains(id)).ToList();
        if (missing.Count > 0)
        {
            throw new DomainUnprocessableException(
                $"Unknown attachment file(s): {string.Join(", ", missing)}.");
        }
    }

    // ── PUT diffing (add/remove, keep stable rows) ──────────────────────────────

    private void DiffTags(JournalEntry entry, IReadOnlyCollection<Guid> desiredTagIds)
    {
        var desired = desiredTagIds.ToHashSet();

        foreach (var link in entry.EntryTags.Where(t => !desired.Contains(t.JournalTagId)).ToList())
        {
            entry.EntryTags.Remove(link);
            context.JournalEntryTags.Remove(link);
        }

        var existing = entry.EntryTags.Select(t => t.JournalTagId).ToHashSet();
        foreach (var tagId in desiredTagIds.Where(id => !existing.Contains(id)))
        {
            entry.EntryTags.Add(new JournalEntryTag { JournalEntryId = entry.JournalEntryId, JournalTagId = tagId });
        }
    }

    private void DiffContacts(JournalEntry entry, IReadOnlyCollection<Guid> desiredContactIds)
    {
        var desired = desiredContactIds.ToHashSet();

        foreach (var link in entry.Contacts.Where(c => !desired.Contains(c.ContactId)).ToList())
        {
            entry.Contacts.Remove(link);
            context.JournalEntryContacts.Remove(link);
        }

        var existing = entry.Contacts.Select(c => c.ContactId).ToHashSet();
        foreach (var contactId in desiredContactIds.Where(id => !existing.Contains(id)))
        {
            entry.Contacts.Add(new JournalEntryContact { JournalEntryId = entry.JournalEntryId, ContactId = contactId });
        }
    }

    private void DiffPhotos(JournalEntry entry, IReadOnlyList<Guid> desiredPhotoIds, DateTime now)
    {
        var desired = desiredPhotoIds.ToHashSet();

        foreach (var photo in entry.Photos.Where(p => !desired.Contains(p.PhotoId)).ToList())
        {
            entry.Photos.Remove(photo);
            context.JournalEntryPhotos.Remove(photo);
        }

        var existing = entry.Photos.ToDictionary(p => p.PhotoId);
        for (var i = 0; i < desiredPhotoIds.Count; i++)
        {
            var photoId = desiredPhotoIds[i];
            if (existing.TryGetValue(photoId, out var photo))
            {
                photo.Position = i;
            }
            else
            {
                entry.Photos.Add(new JournalEntryPhoto { JournalEntryId = entry.JournalEntryId, PhotoId = photoId, Position = i, CreatedAt = now });
            }
        }
    }

    // Resolve each validated image file id to its library Photo id (find-or-create, keyed on the
    // Photo.FileId unique index), preserving gallery order. The validated file ids are already distinct,
    // and FileId↔Photo is 1:1, so the resulting photo ids are distinct too (§5 v4).
    private async Task<List<Guid>> ResolvePhotoIdsAsync(IReadOnlyList<Guid> fileIds, string userId, CancellationToken cancellationToken)
    {
        var photoIds = new List<Guid>(fileIds.Count);
        foreach (var fileId in fileIds)
        {
            photoIds.Add(await photos.FindOrCreatePhotoIdForFileAsync(fileId, userId, cancellationToken));
        }

        return photoIds;
    }

    private void DiffAttachments(JournalEntry entry, IReadOnlyCollection<Guid> desiredFileIds, DateTime now)
    {
        var desired = desiredFileIds.ToHashSet();

        foreach (var attachment in entry.Attachments.Where(a => !desired.Contains(a.FileId)).ToList())
        {
            entry.Attachments.Remove(attachment);
            context.JournalEntryAttachments.Remove(attachment);
        }

        var existing = entry.Attachments.Select(a => a.FileId).ToHashSet();
        foreach (var fileId in desiredFileIds.Where(id => !existing.Contains(id)))
        {
            entry.Attachments.Add(new JournalEntryAttachment { JournalEntryId = entry.JournalEntryId, FileId = fileId, CreatedAt = now });
        }
    }

    private async Task<ExistingJournalEntry> ToDtoAsync(JournalEntry entry, CancellationToken cancellationToken)
    {
        var dto = entry.Adapt<ExistingJournalEntry>();
        dto.TagIds = entry.EntryTags.Select(t => t.JournalTagId).ToList();
        dto.ContactIds = entry.Contacts.Select(c => c.ContactId).ToList();

        // Enrich each photo link with its library Photo's FileId in one batched lookup (§5 v4). A link
        // whose PhotoId no longer resolves (the library Photo was deleted) is dropped entirely, so FileId
        // is never empty on a returned link (§18d).
        var fileIdByPhotoId = await photos.ResolveFileIdsAsync(
            entry.Photos.Select(p => p.PhotoId).ToList(), cancellationToken);
        dto.Photos = entry.Photos
            .OrderBy(p => p.Position)
            .Where(p => fileIdByPhotoId.ContainsKey(p.PhotoId))
            .Select(p => new JournalEntryPhotoDto
            {
                JournalEntryPhotoId = p.JournalEntryPhotoId,
                PhotoId = p.PhotoId,
                FileId = fileIdByPhotoId[p.PhotoId],
                Position = p.Position,
                CreatedAt = p.CreatedAt,
            })
            .ToList();
        dto.Attachments = entry.Attachments
            .OrderBy(a => a.CreatedAt)
            .Select(a => new JournalEntryAttachmentDto
            {
                JournalEntryAttachmentId = a.JournalEntryAttachmentId,
                FileId = a.FileId,
                CreatedAt = a.CreatedAt,
            })
            .ToList();
        return dto;
    }

    private sealed record ValidatedLinks(
        List<Guid> TagIds,
        List<Guid> ContactIds,
        List<Guid> PhotoFileIds,
        List<Guid> AttachmentFileIds);

    private sealed class EntryRow
    {
        public Guid JournalEntryId { get; set; }

        public string Title { get; set; } = string.Empty;

        public string Content { get; set; } = string.Empty;

        public DateTime EntryDate { get; set; }

        public string? Location { get; set; }

        public string? CreatedByUserId { get; set; }

        public DateTime? Archived { get; set; }

        public List<Guid> TagIds { get; set; } = [];

        public int PhotoCount { get; set; }

        public int AttachmentCount { get; set; }

        public int ContactCount { get; set; }
    }
}
