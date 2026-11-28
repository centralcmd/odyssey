using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Context.Secrets;
using Odyssey.Core.Ai;
using Odyssey.Core.FileStorage;
using Odyssey.Core.FileStorage.Files;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Recipes;

namespace Odyssey.Core.Recipes.Import;

/// <summary>Resolves user ids to display names for one caller; every wanted id gets an entry.</summary>
public delegate Task<IReadOnlyDictionary<string, string>> RecipeImportNames(IEnumerable<string?> userIds, CancellationToken cancellationToken);

/// <summary>What the caller holds, as the service needs it — decided by the controller from the principal.</summary>
public sealed record RecipeImportCaller(string UserId, bool CanReadFiles, bool CanReadTags, bool CanReadPhotos);

/// <summary>A new import's body: text, existing Library files and (multipart only) new files.</summary>
public sealed record RecipeImportCreate(
    Guid ClientRequestId,
    string? Text,
    IReadOnlyList<Guid> ExistingFileIds,
    IReadOnlyList<FileUpload> Uploads,
    bool ConsentAcknowledged,
    string? DisclosureVersion);

/// <summary>A create's answer: the import, and whether it is new (<c>202</c>) or an idempotent repeat (<c>200</c>).</summary>
public sealed record RecipeImportCreated(RecipeImportDetail Import, bool Created);

/// <summary>
/// AI recipe import (issue #602). Household-shared: any <c>recipes.import</c> holder sees and acts on any import; the
/// requester and actor columns are attribution, not access. Every relationship a request names is a scalar id, and
/// no request can set a server-owned column. Claims arrive as arguments and are checked before any lookup.
/// </summary>
public sealed class RecipeImportService(
    OdysseyContext context,
    IAiProcessorSettingsLookup processorSettings,
    IRecipeImportSettingsLookup settings,
    ISecretSettingsReader secrets,
    IUploadLimitsLookup uploadLimits,
    FileValidationService validation,
    RecipeService recipes,
    RecipeImportQueue queue,
    StoredFileContentSource files,
    TimeProvider time,
    ILogger<RecipeImportService> logger,
    StoredFileWriter? writer = null,
    IObjectStore? store = null)
{
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    // ── Options (§5 item 1) ─────────────────────────────────────────────────────────────────────────────

    public async Task<RecipeImportOptions> GetOptionsAsync(bool canReadTags, long transportCeilingBytes, CancellationToken cancellationToken)
    {
        var snapshot = await processorSettings.GetAsync(cancellationToken);
        if (snapshot.ReadFailed || snapshot.IsDegraded || snapshot.Model is null || snapshot.BaseUrl is null)
        {
            throw Unavailable();
        }

        var limits = await uploadLimits.GetAsync(cancellationToken);
        var allowed = AllowedContentTypes();
        return new RecipeImportOptions
        {
            Enabled = await settings.IsEnabledAsync(cancellationToken),
            Processor = snapshot.Processor,
            ProcessorRegion = snapshot.ProcessorRegion,
            LawfulBasis = snapshot.LawfulBasis,
            PrivacyNoticeUrl = snapshot.PrivacyNoticeUrl,
            Model = snapshot.Model,
            DisclosureVersion = RecipeImportDisclosure.Compute(snapshot, canReadTags),
            SendsTagNames = canReadTags,
            ConsentText = RecipeImportDisclosure.ConsentText(snapshot, canReadTags),
            MaxSourceTextLength = RecipeImportLimits.MaxSourceTextLength,
            MaxFilesPerImport = RecipeImportLimits.MaxFilesPerImport,
            MaxTotalBytes = Math.Min(RecipeImportLimits.MaxTotalBytes, transportCeilingBytes),
            MaxPdfPages = RecipeImportLimits.MaxPdfPages,
            RetentionDays = await settings.GetRetentionDaysAsync(cancellationToken),
            AllowedContentTypes = allowed,
            MaxFileBytes = allowed.ToDictionary(t => t, t => Math.Min(RecipeImportLimits.MaxBytesFor(t), limits.MaxUploadBytes)),
        };
    }

    /// <summary>The §6 types the administrator's Files-store allow-list also accepts (A9).</summary>
    private IReadOnlyList<string> AllowedContentTypes() =>
        [.. RecipeImportLimits.ContentTypes.Where(validation.IsAllowedContentType)];

    // ── Create (§5 items 2 and 3) ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates everything before any write (A5), then writes the new files, the import and its sources in one save.
    /// A repeated <see cref="RecipeImportCreate.ClientRequestId"/> returns the existing import, whatever the body says.
    /// </summary>
    public async Task<RecipeImportCreated> CreateAsync(
        RecipeImportCreate request, RecipeImportCaller caller, long transportCeilingBytes, RecipeImportNames names,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);

        // Claim before existence: the controller's attributes cover uploads; existing ids need files.read (S1).
        if (request.ExistingFileIds.Count > 0 && !caller.CanReadFiles)
        {
            throw new DomainForbiddenException("Choosing files from the Library requires the files.read permission.");
        }

        if (await ExistingForKeyAsync(caller.UserId, request.ClientRequestId, cancellationToken) is { } repeat)
        {
            ThrowIfTombstone(repeat);
            return new RecipeImportCreated(await DetailAsync(repeat, caller, names, cancellationToken), Created: false);
        }

        if (!await settings.IsEnabledAsync(cancellationToken))
        {
            throw new RecipeImportException(HttpStatusCode.ServiceUnavailable, RecipeImportErrorCodes.Disabled,
                "AI recipe import is turned off on this server.");
        }

        var snapshot = await processorSettings.GetUncachedAsync(cancellationToken);
        if (snapshot.ReadFailed || snapshot.IsDegraded || snapshot.Model is not { } model || snapshot.BaseUrl is not { } baseUrl)
        {
            throw Unavailable();
        }

        var credential = await secrets.GetAsync(SecretSettingKeys.FileAnalysisApiKey, cancellationToken);
        if (credential.State != SecretReadState.Found)
        {
            throw Unavailable();
        }

        if (!RecipeImportDisclosure.Matches(snapshot, caller.CanReadTags, request.DisclosureVersion))
        {
            throw new RecipeImportException(HttpStatusCode.Conflict, RecipeImportErrorCodes.DisclosureChanged,
                "The processor details changed. Review them and confirm again.");
        }

        if (!request.ConsentAcknowledged)
        {
            throw new RecipeImportException(HttpStatusCode.UnprocessableEntity, RecipeImportErrorCodes.ConsentRequired,
                "Confirm that the sources may be sent before starting the import.");
        }

        var text = string.IsNullOrWhiteSpace(request.Text) ? null : request.Text;
        if (text?.Length > RecipeImportLimits.MaxSourceTextLength)
        {
            throw new DomainValidationException(
                $"The pasted text can be at most {RecipeImportLimits.MaxSourceTextLength} characters.", null, "text");
        }

        if (text?.Contains('\0') == true)
        {
            throw new DomainValidationException("The pasted text holds a character that cannot be sent.", null, "text");
        }

        var existingIds = request.ExistingFileIds.Distinct().ToList();
        if (existingIds.Count != request.ExistingFileIds.Count)
        {
            throw new DomainValidationException("The same file is listed more than once.", null, "existingFileIds");
        }

        var fileCount = existingIds.Count + request.Uploads.Count;
        if (text is null && fileCount == 0)
        {
            throw new DomainValidationException("Paste recipe text or add a file.", null, "text");
        }

        if (fileCount > RecipeImportLimits.MaxFilesPerImport)
        {
            throw new DomainValidationException(
                $"An import can hold at most {RecipeImportLimits.MaxFilesPerImport} files.", null, "files");
        }

        var allowed = AllowedContentTypes();
        var uploadCap = (await uploadLimits.GetAsync(cancellationToken)).MaxUploadBytes;
        var existing = await ValidateExistingAsync(existingIds, allowed, cancellationToken);
        var uploads = await ValidateUploadsAsync(request.Uploads, allowed, uploadCap, cancellationToken);

        var rawTotal = existing.Sum(e => e.SizeBytes) + uploads.Sum(u => (long)u.Bytes.Length);
        var maxTotal = Math.Min(RecipeImportLimits.MaxTotalBytes, transportCeilingBytes);
        var encoded = RecipeImportSourcePreparation.EncodedTotal(
            [(false, Encoding.UTF8.GetByteCount(text ?? string.Empty)), .. existing.Select(e => (true, e.SizeBytes)), .. uploads.Select(u => (true, (long)u.Bytes.Length))]);
        if (rawTotal > maxTotal || encoded > RecipeImportLimits.MaxEncodedRequestBytes)
        {
            throw TooLarge($"The files together must be {Megabytes(maxTotal)} or smaller.");
        }

        if (uploads.Count > 0 && writer is null)
        {
            throw new FileStorageUnavailableException("File storage is not set up on this server.");
        }

        // Every check passed: write the new files' bytes (outside any transaction), then commit in one save.
        var prepared = new List<PreparedStoredFile>();
        try
        {
            foreach (var upload in uploads)
            {
                using var stream = new MemoryStream(upload.Bytes, writable: false);
                prepared.Add(await writer!.PrepareNewFileAsync(
                    StoredFileArea.Library, stream, upload.FileName, upload.ContentType, Math.Max(1, upload.Bytes.Length), cancellationToken));
            }

            var importId = await CommitAsync(request, caller, text, existing, uploads, prepared, snapshot, model, baseUrl, cancellationToken);
            prepared.Clear();
            var import = await context.RecipeImports.AsNoTracking().FirstAsync(i => i.RecipeImportId == importId, cancellationToken);
            return new RecipeImportCreated(await DetailAsync(import, caller, names, cancellationToken), Created: true);
        }
        catch (DbUpdateException ex) when (DbErrors.IsDuplicateKey(ex))
        {
            // Two requests with one key raced; the loser answers with the winner's import (R2-N3).
            var winner = await ExistingForKeyAsync(caller.UserId, request.ClientRequestId, CancellationToken.None);
            if (winner is null)
            {
                throw;
            }

            ThrowIfTombstone(winner);
            return new RecipeImportCreated(await DetailAsync(winner, caller, names, cancellationToken), Created: false);
        }
        catch (RetryLimitExceededException)
        {
            throw new RecipeImportException(HttpStatusCode.ServiceUnavailable, RecipeImportErrorCodes.Busy,
                "Too many imports are being started at once. Try again in a moment.", retryAfterSeconds: 5);
        }
        finally
        {
            foreach (var file in prepared)
            {
                await writer!.AbandonPreparedAsync(file, CancellationToken.None);
            }
        }
    }

    private sealed record ExistingSource(Guid FileId, string ContentType, long SizeBytes);

    private sealed record UploadSource(string FileName, string ContentType, byte[] Bytes);

    private async Task<List<ExistingSource>> ValidateExistingAsync(
        List<Guid> ids, IReadOnlyList<string> allowed, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        // Library only (S1): a Finance or Journal file is the same 404 as a missing one.
        var found = await LibraryFiles()
            .Where(f => ids.Contains(f.Id))
            .Select(f => new { f.Id, f.ContentType, f.SizeBytes })
            .ToDictionaryAsync(f => f.Id, cancellationToken);
        if (ids.FirstOrDefault(id => !found.ContainsKey(id)) is var missing && missing != Guid.Empty)
        {
            throw new DomainNotFoundException($"File {missing} was not found.", "existingFileIds");
        }

        var result = new List<ExistingSource>();
        foreach (var id in ids)
        {
            var file = found[id];
            if (!allowed.Contains(file.ContentType))
            {
                throw Unsupported();
            }

            if (RecipeImportLimits.IsImage(file.ContentType))
            {
                // The raw ceiling first, so an oversized image is never loaded; then the strip it will be sent through.
                if (file.SizeBytes > RecipeImportLimits.MaxExistingImageRawBytes)
                {
                    throw TooLarge("A chosen photo is too large to send.");
                }

                var read = await files.ReadAllAsync(id, RecipeImportLimits.MaxExistingImageRawBytes, cancellationToken);
                if (read is not { TooLarge: false, Bytes: { } bytes })
                {
                    throw TooLarge("A chosen photo is too large to send.");
                }

                result.Add(new ExistingSource(id, file.ContentType, Strip(bytes, file.ContentType).Bytes.LongLength));
                continue;
            }

            if (file.SizeBytes > RecipeImportLimits.MaxBytesFor(file.ContentType))
            {
                throw TooLarge($"A chosen file is over {Megabytes(RecipeImportLimits.MaxBytesFor(file.ContentType))}.");
            }

            result.Add(new ExistingSource(id, file.ContentType, file.SizeBytes));
        }

        return result;
    }

    private async Task<List<UploadSource>> ValidateUploadsAsync(
        IReadOnlyList<FileUpload> uploads, IReadOnlyList<string> allowed, long uploadCap, CancellationToken cancellationToken)
    {
        var result = new List<UploadSource>();
        foreach (var upload in uploads)
        {
            var contentType = StillImageValidatorContentType(upload.ContentType);
            if (contentType is null || !allowed.Contains(contentType))
            {
                throw Unsupported();
            }

            var rawCap = Math.Min(
                RecipeImportLimits.IsImage(contentType) ? RecipeImportLimits.MaxExistingImageRawBytes : RecipeImportLimits.MaxBytesFor(contentType),
                uploadCap);
            if (upload.Length > rawCap)
            {
                throw TooLarge($"A file is over {Megabytes(Math.Min(RecipeImportLimits.MaxBytesFor(contentType), uploadCap))}.");
            }

            // The Files store's own checks: magic bytes before the declared type, a usable name.
            try
            {
                validation.ValidateFile(upload with { ContentType = contentType }, rawCap);
            }
            catch (DomainValidationException)
            {
                throw Unsupported();
            }

            byte[] bytes;
            await using (var stream = upload.OpenReadStream())
            {
                bytes = await StoredFileContentSource.ReadUpToAsync(stream, (int)rawCap + 1, cancellationToken);
            }

            if (bytes.LongLength > rawCap)
            {
                throw TooLarge("A file is too large to send.");
            }

            if (RecipeImportLimits.IsImage(contentType))
            {
                // The stripped bytes are what is stored and what is sent (A10, S5).
                bytes = Strip(bytes, contentType).Bytes;
            }
            else if (contentType == "text/plain" && RecipeImportSourcePreparation.DecodeText(bytes) is null)
            {
                throw new DomainValidationException("A text file must be UTF-8 text.", null, "files");
            }

            result.Add(new UploadSource(validation.SanitizeFileName(upload.FileName), contentType, bytes));
        }

        return result;
    }

    private static string? StillImageValidatorContentType(string? declared) =>
        RecipeImportLimits.IsImage(Odyssey.Core.Imaging.StillImageValidator.NormalizeContentType(declared))
            ? Odyssey.Core.Imaging.StillImageValidator.NormalizeContentType(declared)
            : declared?.Split(';')[0].Trim().ToLowerInvariant();

    private static Odyssey.Core.Imaging.ValidatedImage Strip(byte[] bytes, string contentType)
    {
        try
        {
            return RecipeImportSourcePreparation.StripImage(bytes, contentType);
        }
        catch (ImageTooLargeAfterStripException)
        {
            throw TooLarge("A photo is too large to send, even without its metadata.");
        }
        catch (DomainValidationException)
        {
            throw Unsupported();
        }
    }

    private async Task<Guid> CommitAsync(
        RecipeImportCreate request, RecipeImportCaller caller, string? text, List<ExistingSource> existing, List<UploadSource> uploads,
        List<PreparedStoredFile> prepared, AiProcessorSettings snapshot, string model, string baseUrl, CancellationToken cancellationToken)
    {
        var strategy = context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();
            await using var transaction = context.Database.IsRelational()
                ? await context.Database.BeginTransactionAsync(cancellationToken)
                : null;

            // The open-imports cap, race-safe (R2-S2): lock the requester's own user row, then count, then insert.
            if (context.Database.IsRelational())
            {
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT Id FROM AspNetUsers WHERE Id = {caller.UserId} FOR UPDATE", cancellationToken);
            }

            var open = await context.RecipeImports.CountAsync(
                i => i.RequestedByUserId == caller.UserId
                    && (i.Status == RecipeImportStatus.Queued || i.Status == RecipeImportStatus.Running),
                cancellationToken);
            if (open >= RecipeImportLimits.MaxOpenImportsPerUser)
            {
                throw new RecipeImportException(HttpStatusCode.Conflict, RecipeImportErrorCodes.LimitReached,
                    $"You can have at most {RecipeImportLimits.MaxOpenImportsPerUser} imports waiting or being read at once.",
                    extensions: new Dictionary<string, object?> { ["limit"] = RecipeImportLimits.MaxOpenImportsPerUser });
            }

            var now = Now;
            var import = new RecipeImport
            {
                RecipeImportId = Guid.NewGuid(),
                Status = RecipeImportStatus.Queued,
                ClientRequestId = request.ClientRequestId,
                RequestedByUserId = caller.UserId,
                QueuedAt = now,
                Generation = 1,
                AnalyzerModel = Truncate(model, 256),
                PromptVersion = RecipeImportLimits.PromptVersion,
                MaxTokensInForce = snapshot.MaxTokens,
                AnalyzerBaseUrlOrigin = Truncate(AiDestination.Origin(baseUrl), 256),
                AnalyzerBaseUrlHost = Truncate(AiDestination.Host(baseUrl), 256),
                ProcessorInForce = Truncate(snapshot.Processor, 128),
                ProcessorRegionInForce = Truncate(snapshot.ProcessorRegion, 128),
                LawfulBasis = Truncate(snapshot.LawfulBasis, 128),
                ConsentRecorded = true,
                ConsentMethod = RecipeImportDisclosure.ConsentMethod,
                ConsentText = RecipeImportDisclosure.ConsentText(snapshot, caller.CanReadTags),
                DisclosureVersionInForce = RecipeImportDisclosure.Compute(snapshot, caller.CanReadTags),
                SendsTagNames = caller.CanReadTags,
            };

            var ordinal = 0;
            if (text is not null)
            {
                import.Sources.Add(new RecipeImportSource { Ordinal = ordinal++, Kind = RecipeImportSourceKind.Text, Text = text });
            }

            foreach (var file in existing)
            {
                import.Sources.Add(new RecipeImportSource
                {
                    Ordinal = ordinal++,
                    Kind = RecipeImportSourceKind.File,
                    FileId = file.FileId,
                    ImportOwned = false,
                    ContentType = file.ContentType,
                    SizeBytes = file.SizeBytes,
                });
            }

            for (var i = 0; i < prepared.Count; i++)
            {
                var metadata = writer!.AttachPrepared(context, prepared[i], caller.UserId);
                import.Sources.Add(new RecipeImportSource
                {
                    Ordinal = ordinal++,
                    Kind = RecipeImportSourceKind.File,
                    FileId = metadata.Id,
                    ImportOwned = true,
                    ContentType = uploads[i].ContentType,
                    SizeBytes = metadata.SizeBytes,
                });
            }

            context.RecipeImports.Add(import);
            await context.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            logger.LogInformation("Recipe import {ImportId} was queued with {Sources} source(s).", import.RecipeImportId, import.Sources.Count);
            return import.RecipeImportId;
        });
    }

    private async Task<RecipeImport?> ExistingForKeyAsync(string userId, Guid clientRequestId, CancellationToken cancellationToken)
    {
        context.ChangeTracker.Clear();
        return await context.RecipeImports.AsNoTracking()
            .FirstOrDefaultAsync(i => i.RequestedByUserId == userId && i.ClientRequestId == clientRequestId, cancellationToken);
    }

    /// <summary>
    /// The unique key covers tombstones too, so a deleted import's id can never be reused: say so with a <c>409</c>
    /// rather than answer with a deleted import or fail on the duplicate key.
    /// </summary>
    private static void ThrowIfTombstone(RecipeImport import)
    {
        if (import.DeletedAt is not null)
        {
            throw new RecipeImportException(HttpStatusCode.Conflict, RecipeImportErrorCodes.RequestIdUsed,
                "This request id belongs to an import that was deleted. Start a new import.");
        }
    }

    /// <summary>
    /// Claims and runs one import in this process — only for a host with no worker (the <c>Testing</c> environment, the
    /// <c>FileAnalysisService.RunInlineAsync</c> precedent).
    /// </summary>
    public async Task RunInlineAsync(Guid id, RecipeImportRunner runner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runner);
        context.ChangeTracker.Clear();
        if (await queue.TryClaimAsync(id, cancellationToken))
        {
            await runner.RunAsync(id, cancellationToken);
        }

        context.ChangeTracker.Clear();
    }

    // ── Reads (§5 items 4 and 5) ────────────────────────────────────────────────────────────────────────

    public async Task<PagedResult<RecipeImportSummary>> ListAsync(
        RecipeImportsQueryParams query, RecipeImportCaller caller, RecipeImportNames names, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var imports = context.RecipeImports.AsNoTracking().Where(i => i.DeletedAt == null);
        if (!query.IncludePurged)
        {
            imports = imports.Where(i => i.ContentPurgedAt == null);
        }

        if (query.Status is { Length: > 0 } statuses)
        {
            imports = imports.Where(i => statuses.Contains(i.Status));
        }

        if (query.HasPendingDraft is { } pending)
        {
            // "Ready to review" means a draft that can still be reviewed: a purged import's pending draft has no content.
            imports = imports.Where(i =>
                (i.ContentPurgedAt == null && i.Drafts.Any(d => d.Status == RecipeImportDraftStatus.Pending)) == pending);
        }

        var ascending = query.SortDir == SortDirection.Asc;
        IOrderedQueryable<RecipeImport> ordered = (query.SortBy ?? RecipeImportSortBy.QueuedAt) switch
        {
            RecipeImportSortBy.Status => ascending
                ? imports.OrderBy(i => i.Status).ThenByDescending(i => i.QueuedAt)
                : imports.OrderByDescending(i => i.Status).ThenByDescending(i => i.QueuedAt),
            RecipeImportSortBy.DraftTitle => ascending
                ? imports.OrderBy(i => i.Drafts.OrderBy(d => d.Ordinal).Select(d => d.Title).FirstOrDefault()).ThenByDescending(i => i.QueuedAt)
                : imports.OrderByDescending(i => i.Drafts.OrderBy(d => d.Ordinal).Select(d => d.Title).FirstOrDefault()).ThenByDescending(i => i.QueuedAt),
            _ => ascending ? imports.OrderBy(i => i.QueuedAt) : imports.OrderByDescending(i => i.QueuedAt),
        };
        imports = ordered.ThenBy(i => i.RecipeImportId);

        var total = await imports.CountAsync(cancellationToken);
        var offset = Math.Max(0, query.Offset);
        var limit = Math.Clamp(query.Limit, 1, ListDefaults.MaxLimit);
        var rows = await SummaryRows(imports.Skip(offset).Take(limit)).ToListAsync(cancellationToken);
        var retention = await settings.GetRetentionDaysAsync(cancellationToken);
        var resolved = await names(rows.Select(r => r.RequestedByUserId), cancellationToken);
        return new PagedResult<RecipeImportSummary>
        {
            Items = [.. rows.Select(r => ToSummary(r, caller, resolved, retention))],
            Offset = offset,
            Limit = limit,
            TotalCount = total,
        };
    }

    /// <summary>One import with its sources and drafts; <c>null</c> when missing or deleted. Runs the stale sweep first.</summary>
    public async Task<RecipeImportDetail?> GetAsync(Guid id, RecipeImportCaller caller, RecipeImportNames names, CancellationToken cancellationToken)
    {
        await queue.SweepAsync(id, cancellationToken);
        var import = await context.RecipeImports.AsNoTracking()
            .FirstOrDefaultAsync(i => i.RecipeImportId == id && i.DeletedAt == null, cancellationToken);
        return import is null ? null : await DetailAsync(import, caller, names, cancellationToken);
    }

    private sealed record SummaryRow(
        Guid Id, RecipeImportStatus Status, string? RequestedByUserId, DateTime QueuedAt, DateTime? FinishedAt,
        DateTime? ContentPurgedAt, string? FailureCode, bool HasText, int FileCount, string? FirstFileName,
        string? DraftTitle, int Pending, int Accepted, Guid? AcceptedRecipeId);

    /// <summary>The list projection: no <c>DraftJson</c> and no source text is ever loaded (§10).</summary>
    private IQueryable<SummaryRow> SummaryRows(IQueryable<RecipeImport> imports) =>
        imports.Select(i => new SummaryRow(
            i.RecipeImportId, i.Status, i.RequestedByUserId, i.QueuedAt, i.FinishedAt, i.ContentPurgedAt, i.FailureCode,
            i.Sources.Any(s => s.Kind == RecipeImportSourceKind.Text),
            i.Sources.Count(s => s.Kind == RecipeImportSourceKind.File),
            i.Sources.Where(s => s.Kind == RecipeImportSourceKind.File && s.FileId != null)
                .OrderBy(s => s.Ordinal)
                .Select(s => context.FileMetadata.Where(f => f.Id == s.FileId).Select(f => f.FileName).FirstOrDefault())
                .FirstOrDefault(),
            i.Drafts.OrderBy(d => d.Ordinal).Select(d => d.Title).FirstOrDefault(),
            i.Drafts.Count(d => d.Status == RecipeImportDraftStatus.Pending),
            i.Drafts.Count(d => d.Status == RecipeImportDraftStatus.Accepted),
            i.Drafts.Where(d => d.AcceptedRecipeId != null).OrderBy(d => d.Ordinal).Select(d => d.AcceptedRecipeId).FirstOrDefault()));

    private static RecipeImportSummary ToSummary(
        SummaryRow row, RecipeImportCaller caller, IReadOnlyDictionary<string, string> names, int retentionDays) => new()
        {
            Id = row.Id,
            Status = row.Status,
            HasText = row.HasText,
            FileCount = row.FileCount,
            FirstFileName = caller.CanReadFiles ? row.FirstFileName : null,
            RequestedByName = row.RequestedByUserId is { } user ? names.GetValueOrDefault(user) : null,
            DraftTitle = row.DraftTitle,
            PendingDraftCount = row.Pending,
            AcceptedDraftCount = row.Accepted,
            AcceptedRecipeId = row.AcceptedRecipeId,
            QueuedAt = row.QueuedAt,
            FinishedAt = row.FinishedAt,
            ExpiresAt = row.FinishedAt is { } finished && row.ContentPurgedAt is null ? finished.AddDays(retentionDays) : null,
            ContentPurged = row.ContentPurgedAt is not null,
            FailureCode = row.FailureCode,
        };

    private async Task<RecipeImportDetail> DetailAsync(
        RecipeImport import, RecipeImportCaller caller, RecipeImportNames names, CancellationToken cancellationToken)
    {
        var row = await SummaryRows(context.RecipeImports.AsNoTracking().Where(i => i.RecipeImportId == import.RecipeImportId))
            .FirstAsync(cancellationToken);
        var sources = await context.RecipeImportSources.AsNoTracking()
            .Where(s => s.RecipeImportId == import.RecipeImportId)
            .OrderBy(s => s.Ordinal)
            .Select(s => new
            {
                s.Ordinal,
                s.Kind,
                s.Text,
                s.FileId,
                s.ContentType,
                s.SizeBytes,
                File = context.FileMetadata.Where(f => f.Id == s.FileId).Select(f => new { f.FileName, f.ContentType, f.SizeBytes }).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);
        var drafts = await context.RecipeImportDrafts.AsNoTracking()
            .Where(d => d.RecipeImportId == import.RecipeImportId)
            .OrderBy(d => d.Ordinal)
            .ToListAsync(cancellationToken);
        var retention = await settings.GetRetentionDaysAsync(cancellationToken);
        var resolved = await names([import.RequestedByUserId, .. drafts.Select(d => d.UpdatedByUserId)], cancellationToken);

        return new RecipeImportDetail
        {
            Summary = ToSummary(row, caller, resolved, retention),
            Sources =
            [
                .. sources
                    .Where(s => s.Kind == RecipeImportSourceKind.Text || caller.CanReadFiles)
                    .Select(s => new RecipeImportSourceItem
                    {
                        Ordinal = s.Ordinal,
                        Kind = s.Kind,
                        Text = s.Text,
                        File = s.FileId is { } fileId && s.File is { } file
                            ? new RecipeImportSourceFile { FileId = fileId, FileName = file.FileName, ContentType = file.ContentType, Size = file.SizeBytes }
                            : null,
                        FileRemoved = s.Kind == RecipeImportSourceKind.File && s.File is null && import.ContentPurgedAt is null,
                    }),
            ],
            FailureMessage = import.FailureMessage,
            DocumentSent = RecipeImportTerminal.DocumentSent(import.Status, import.TransferStartedAt),
            Drafts = [.. drafts.Select(d => ToDraftItem(d, caller, resolved))],
        };
    }

    private static RecipeImportDraftItem ToDraftItem(RecipeImportDraft draft, RecipeImportCaller caller, IReadOnlyDictionary<string, string> names)
    {
        var content = draft.DraftJson is null ? null : JsonSerializer.Deserialize<RecipeImportDraftContent>(draft.DraftJson, RecipeImportRunner.DraftJson);
        if (content is not null && !caller.CanReadTags)
        {
            // Absent, not hidden (the recipes rule): a caller who cannot see tags gets null, and a PUT with null keeps them.
            content.TagIds = null;
        }

        var warnings = draft.Warnings is null ? [] : JsonSerializer.Deserialize<List<string>>(draft.Warnings, RecipeImportRunner.DraftJson) ?? [];
        return new RecipeImportDraftItem
        {
            Id = draft.RecipeImportDraftId,
            Ordinal = draft.Ordinal,
            Status = draft.Status,
            Content = content,
            Warnings = warnings,
            AcceptedRecipeId = draft.AcceptedRecipeId,
            UpdatedByName = draft.UpdatedByUserId is { } user ? names.GetValueOrDefault(user) : null,
            UpdatedAt = draft.UpdatedAt,
            Version = draft.Version,
        };
    }

    // ── Draft writes (§5 items 6–8) ─────────────────────────────────────────────────────────────────────

    /// <summary>Save and finish later: a full replacement, except <c>tagIds: null</c>, which keeps the stored ids.</summary>
    public async Task<RecipeImportDraftItem?> UpdateDraftAsync(
        Guid id, Guid draftId, UpdateRecipeImportDraft request, RecipeImportCaller caller, RecipeImportNames names,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var content = request.Content!;
        if (content.TagIds is not null && !caller.CanReadTags)
        {
            throw new DomainForbiddenException("Changing suggested tags requires the recipes.tags.read permission.");
        }

        var (import, draft) = await LoadDraftAsync(id, draftId, cancellationToken);
        if (draft is null)
        {
            return null;
        }

        EnsureEditable(import!, draft, request.Version!.Value);
        if (content.TagIds is null)
        {
            var stored = draft.DraftJson is null ? null : JsonSerializer.Deserialize<RecipeImportDraftContent>(draft.DraftJson, RecipeImportRunner.DraftJson);
            content.TagIds = stored?.TagIds ?? [];
        }
        else
        {
            content.TagIds = [.. content.TagIds.Distinct()];
        }

        content.SchemaVersion = RecipeImportLimits.DraftSchemaVersion;
        draft.DraftJson = JsonSerializer.Serialize(content, RecipeImportRunner.DraftJson);
        draft.Title = string.IsNullOrWhiteSpace(content.Title) ? null : content.Title.Trim();
        draft.UpdatedByUserId = caller.UserId;
        draft.UpdatedAt = Now;
        draft.Version++;
        await SaveFencedAsync(cancellationToken);
        var resolved = await names([caller.UserId], cancellationToken);
        return ToDraftItem(draft, caller, resolved);
    }

    /// <summary>
    /// Creates the recipe from the reviewed request in ONE transaction with the draft's acceptance (A4): the recipe, its
    /// attachments (the surviving source files, de-duplicated against the request's own) and the draft and import
    /// tokens, so a concurrent accept, purge or delete loses on a token and nothing is created twice.
    /// </summary>
    public async Task<AcceptRecipeImportDraftResult?> AcceptAsync(
        Guid id, Guid draftId, AcceptRecipeImportDraft request, RecipeImportCaller caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.AttachSourceFiles && !caller.CanReadFiles)
        {
            throw new DomainForbiddenException("Attaching the source files requires the files.read permission.");
        }

        var strategy = context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();
            await using var transaction = context.Database.IsRelational()
                ? await context.Database.BeginTransactionAsync(cancellationToken)
                : null;

            var (import, draft) = await LoadDraftAsync(id, draftId, cancellationToken);
            if (draft is null)
            {
                return null;
            }

            EnsureEditable(import!, draft, request.Version!.Value);

            var recipe = request.Recipe!;
            var sourceFiles = request.AttachSourceFiles
                ? import!.Sources.Where(s => s.Kind == RecipeImportSourceKind.File).OrderBy(s => s.Ordinal).Select(s => s.FileId).ToList()
                : [];
            var surviving = sourceFiles.OfType<Guid>().ToList();
            var visible = surviving.Count == 0
                ? []
                : await context.FileMetadata.AsNoTracking()
                    .Where(FileReferenceCounter.VisibleInFilesStore(context))
                    .Where(f => surviving.Contains(f.Id))
                    .Select(f => f.Id)
                    .ToListAsync(cancellationToken);
            var attach = surviving.Where(visible.Contains).ToList();
            var own = recipe.AttachmentFileIds ?? [];
            var merged = own.Concat(attach.Where(f => !own.Contains(f))).ToArray();
            if (merged.Length > RecipeLimits.MaxAttachments)
            {
                throw new DomainValidationException(
                    $"A recipe can have at most {RecipeLimits.MaxAttachments} attachments.", null, "attachmentFileIds");
            }

            var staged = recipe with { AttachmentFileIds = merged };
            var created = await recipes.StageCreateAsync(
                staged, caller.UserId, caller.CanReadPhotos, caller.CanReadTags, caller.CanReadFiles, cancellationToken);

            var now = Now;
            draft.Status = RecipeImportDraftStatus.Accepted;
            draft.AcceptedRecipeId = created.RecipeId;
            draft.AcceptedByUserId = caller.UserId;
            draft.AcceptedAt = now;
            draft.Version++;
            import!.Generation++;
            await SaveFencedAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            logger.LogInformation("Recipe import {ImportId} draft {DraftId} was accepted.", import.RecipeImportId, draft.RecipeImportDraftId);
            return new AcceptRecipeImportDraftResult
            {
                RecipeId = created.RecipeId,
                AttachedFileCount = attach.Count,
                MissingFileCount = sourceFiles.Count - attach.Count,
            };
        });
    }

    /// <summary>Discard draft recipe: the import stays; nothing becomes a recipe.</summary>
    public async Task<bool> DiscardAsync(Guid id, Guid draftId, RecipeImportCaller caller, CancellationToken cancellationToken)
    {
        var (import, draft) = await LoadDraftAsync(id, draftId, cancellationToken);
        if (draft is null)
        {
            return false;
        }

        EnsurePending(draft);
        draft.Status = RecipeImportDraftStatus.Discarded;
        draft.DiscardedByUserId = caller.UserId;
        draft.DiscardedAt = Now;
        draft.Version++;
        import!.Generation++;
        await SaveFencedAsync(cancellationToken);
        return true;
    }

    // ── Import writes (§5 items 9 and 10) ───────────────────────────────────────────────────────────────

    /// <summary>Cancels a Queued import (nothing was sent). <c>false</c> when it is missing.</summary>
    public async Task<bool> CancelAsync(Guid id, RecipeImportCaller caller, CancellationToken cancellationToken)
    {
        var import = await context.RecipeImports.FirstOrDefaultAsync(i => i.RecipeImportId == id && i.DeletedAt == null, cancellationToken);
        if (import is null)
        {
            return false;
        }

        if (import.Status != RecipeImportStatus.Queued)
        {
            throw new RecipeImportException(HttpStatusCode.Conflict, RecipeImportErrorCodes.AlreadySent,
                "This import has already been picked up and can no longer be cancelled.");
        }

        var now = Now;
        import.Status = RecipeImportStatus.Cancelled;
        import.CancelledByUserId = caller.UserId;
        import.CancelledAt = now;
        import.FinishedAt = now;
        import.Generation++;
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new RecipeImportException(HttpStatusCode.Conflict, RecipeImportErrorCodes.AlreadySent,
                "This import has already been picked up and can no longer be cancelled.");
        }

        return true;
    }

    /// <summary>
    /// Delete import: a tombstone (S3). The content is purged and import-owned files released at once; the row and its
    /// audit columns stay. A recipe made from the import is never touched.
    /// </summary>
    public async Task<bool> DeleteAsync(Guid id, RecipeImportCaller caller, CancellationToken cancellationToken)
    {
        var import = await context.RecipeImports
            .Include(i => i.Sources)
            .Include(i => i.Drafts)
            .FirstOrDefaultAsync(i => i.RecipeImportId == id && i.DeletedAt == null, cancellationToken);
        if (import is null)
        {
            return false;
        }

        if (import.Status is RecipeImportStatus.Queued or RecipeImportStatus.Running)
        {
            throw new RecipeImportException(HttpStatusCode.Conflict, RecipeImportErrorCodes.InProgress,
                "Cancel the import, or wait until it finishes, before deleting it.");
        }

        var now = Now;
        await RecipeImportContentPurge.StageAsync(context, store, import, now, cancellationToken);
        import.DeletedAt = now;
        import.DeletedByUserId = caller.UserId;
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw VersionConflict();
        }

        return true;
    }

    // ── The composer's file picker (§5 item 11) ─────────────────────────────────────────────────────────

    public async Task<PagedResult<RecipeImportLibraryFile>> ListLibraryFilesAsync(RecipeImportLibraryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var allowed = AllowedContentTypes();
        var library = LibraryFiles().Where(f => allowed.Contains(f.ContentType));
        if (ListQuery.NormalizeSearch(query.Search) is { } search)
        {
            var pattern = ListQuery.ContainsPattern(search);
            library = library.Where(f => EF.Functions.Like(f.FileName, pattern, "\\"));
        }

        var total = await library.CountAsync(cancellationToken);
        var offset = Math.Max(0, query.Offset);
        var limit = Math.Clamp(query.Limit, 1, ListDefaults.MaxLimit);
        var items = await library
            .OrderByDescending(f => f.UploadedAtUtc)
            .ThenBy(f => f.Id)
            .Skip(offset)
            .Take(limit)
            .Select(f => new RecipeImportLibraryFile
            {
                FileId = f.Id,
                FileName = f.FileName,
                ContentType = f.ContentType,
                SizeBytes = f.SizeBytes,
                UploadedAt = f.UploadedAtUtc,
            })
            .ToListAsync(cancellationToken);
        return new PagedResult<RecipeImportLibraryFile> { Items = items, Offset = offset, Limit = limit, TotalCount = total };
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Library-area files visible in the Files store (S1): the only files recipe import may send.</summary>
    private IQueryable<FileMetadata> LibraryFiles() =>
        context.FileMetadata.AsNoTracking()
            .Where(FileReferenceCounter.VisibleInFilesStore(context))
            .Where(f => f.LibraryStoredObjectId != null);

    private async Task<(RecipeImport? Import, RecipeImportDraft? Draft)> LoadDraftAsync(Guid id, Guid draftId, CancellationToken cancellationToken)
    {
        var import = await context.RecipeImports
            .Include(i => i.Sources)
            .FirstOrDefaultAsync(i => i.RecipeImportId == id && i.DeletedAt == null, cancellationToken);
        if (import is null)
        {
            return (null, null);
        }

        var draft = await context.RecipeImportDrafts
            .FirstOrDefaultAsync(d => d.RecipeImportDraftId == draftId && d.RecipeImportId == id, cancellationToken);
        return (import, draft);
    }

    private static void EnsureEditable(RecipeImport import, RecipeImportDraft draft, int version)
    {
        if (import.ContentPurgedAt is not null || draft.DraftJson is null)
        {
            throw new RecipeImportException(HttpStatusCode.Conflict, RecipeImportErrorCodes.ContentPurged,
                "This import's content was removed after its retention period.");
        }

        EnsurePending(draft);
        if (draft.Version != version)
        {
            throw VersionConflict();
        }
    }

    private static void EnsurePending(RecipeImportDraft draft)
    {
        if (draft.Status != RecipeImportDraftStatus.Pending)
        {
            throw new RecipeImportException(HttpStatusCode.Conflict, RecipeImportErrorCodes.DraftNotPending,
                draft.Status == RecipeImportDraftStatus.Accepted
                    ? "A recipe was already created from this draft."
                    : "This draft recipe was discarded.",
                extensions: new Dictionary<string, object?> { ["acceptedRecipeId"] = draft.AcceptedRecipeId });
        }
    }

    private async Task SaveFencedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw VersionConflict();
        }
    }

    private static RecipeImportException VersionConflict() =>
        new(HttpStatusCode.Conflict, RecipeImportErrorCodes.VersionConflict,
            "Someone else changed this draft recipe. Reload it to see their changes.");

    private static RecipeImportException Unavailable() =>
        new(HttpStatusCode.ServiceUnavailable, RecipeImportErrorCodes.Unavailable,
            "The AI service is not available on this server right now.");

    private static RecipeImportException Unsupported() =>
        new(HttpStatusCode.UnsupportedMediaType, RecipeImportErrorCodes.UnsupportedSourceType,
            "Use PDF, JPEG, PNG, WebP or plain-text files.");

    private static RecipeImportException TooLarge(string message) =>
        new(HttpStatusCode.RequestEntityTooLarge, RecipeImportErrorCodes.SourceTooLarge, message);

    private static string Megabytes(long bytes) => $"{bytes / 1_000_000.0:0.#} MB";

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
