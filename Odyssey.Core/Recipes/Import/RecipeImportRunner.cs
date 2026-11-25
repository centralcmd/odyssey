using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Context.Secrets;
using Odyssey.Core.Ai;
using Odyssey.Core.FileStorage.Files;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Recipes;

namespace Odyssey.Core.Recipes.Import;

/// <summary>What one claimed run ended as, for the worker's run log.</summary>
public enum RecipeImportRunResult
{
    Completed,

    /// <summary>The transfer started and the run did not produce a draft.</summary>
    Failed,

    /// <summary>A pre-send check refused the import; nothing was sent.</summary>
    Refused,

    /// <summary>The row was gone, not this run's, or a sweep won the race.</summary>
    Lost,
}

/// <summary>
/// Runs ONE claimed import (issue #602 §3 steps 7–9). Every pre-send check runs from an uncached settings read and a
/// mismatch refuses, never substitutes; the switch is read last. The transfer stamp is saved, fenced, before the call,
/// so a sweep that took the row stops the run with nothing sent. Terminal writes use <see cref="CancellationToken.None"/>.
/// </summary>
public sealed class RecipeImportRunner(
    OdysseyContext context,
    IRecipeExtractionProvider provider,
    IAiProcessorSettingsLookup processorSettings,
    IRecipeImportSettingsLookup settings,
    ISecretSettingsReader secrets,
    StoredFileContentSource files,
    TimeProvider time,
    ILogger<RecipeImportRunner> logger)
{
    internal static readonly JsonSerializerOptions DraftJson = new(JsonSerializerDefaults.Web);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<RecipeImportRunResult> RunAsync(Guid importId, CancellationToken cancellationToken)
    {
        var import = await context.RecipeImports
            .Include(i => i.Sources)
            .FirstOrDefaultAsync(i => i.RecipeImportId == importId, cancellationToken);
        if (import is not { Status: RecipeImportStatus.Running, RunId: not null })
        {
            return RecipeImportRunResult.Lost;
        }

        try
        {
            return await RunClaimedAsync(import, cancellationToken);
        }
        catch (Exception ex)
        {
            var code = ex switch
            {
                AiCredentialUnavailableException => RecipeImportFailureCodes.CredentialUnavailable,
                RecipeExtractionException { Failure: RecipeExtractionFailure.DocumentRejected } => RecipeImportFailureCodes.DocumentRejected,
                OperationCanceledException when cancellationToken.IsCancellationRequested => RecipeImportFailureCodes.Interrupted,
                _ => RecipeImportFailureCodes.ProviderError,
            };

            // The exception TYPE only: no message, because no provider text and no source may reach a log (S8).
            logger.LogError("Recipe import {ImportId} failed ({Code}, {Failure}).", import.RecipeImportId, code, ex.GetType().Name);
            var sent = import.TransferStartedAt is not null;
            return await FinishAsync(import, code, sent ? RecipeImportRunResult.Failed : RecipeImportRunResult.Refused);
        }
    }

    private async Task<RecipeImportRunResult> RunClaimedAsync(RecipeImport import, CancellationToken cancellationToken)
    {
        var sources = import.Sources.OrderBy(s => s.Ordinal).ToList();
        var hasFiles = sources.Any(s => s.Kind == RecipeImportSourceKind.File);

        // 7.1 — the requester still exists, is not disabled, and still holds every claim the import needs.
        string[] needed =
        [
            PermissionClaims.RecipesImport, PermissionClaims.RecipesCreate,
            .. hasFiles ? [PermissionClaims.FilesRead] : Array.Empty<string>(),
            .. import.SendsTagNames ? [PermissionClaims.RecipeTagsRead] : Array.Empty<string>(),
        ];
        var held = await RequesterClaims.HeldAsync(context, import.RequestedByUserId, needed, cancellationToken);
        if (needed.Any(claim => !held.Contains(claim)))
        {
            return await FinishAsync(import, RecipeImportFailureCodes.RequesterRevoked, RecipeImportRunResult.Refused);
        }

        // 7.2 — every file source still present, still in the Library area, still visible in the Files store.
        var fileIds = sources.Where(s => s.Kind == RecipeImportSourceKind.File).Select(s => s.FileId).ToList();
        if (fileIds.Any(id => id is null))
        {
            return await FinishAsync(import, RecipeImportFailureCodes.SourceMissing, RecipeImportRunResult.Refused);
        }

        var ids = fileIds.Select(id => id!.Value).ToList();
        var present = await context.FileMetadata.AsNoTracking()
            .Where(Odyssey.Core.Finance.FileReferenceCounter.VisibleInFilesStore(context))
            .Where(f => ids.Contains(f.Id) && f.LibraryStoredObjectId != null)
            .Select(f => new { f.Id, f.ContentType })
            .ToDictionaryAsync(f => f.Id, f => f.ContentType, cancellationToken);
        if (ids.Any(id => !present.ContainsKey(id)))
        {
            return await FinishAsync(import, RecipeImportFailureCodes.SourceMissing, RecipeImportRunResult.Refused);
        }

        // 7.3 — one fresh settings snapshot; destination before disclosure (A2).
        var snapshot = await processorSettings.GetUncachedAsync(cancellationToken);
        if (snapshot.ReadFailed || snapshot.IsDegraded || snapshot.Model is not { } model || snapshot.BaseUrl is not { } baseUrl)
        {
            // The disclosure cannot be verified against an unusable snapshot.
            return await FinishAsync(import, RecipeImportFailureCodes.DisclosureChanged, RecipeImportRunResult.Refused);
        }

        if (!string.Equals(import.AnalyzerBaseUrlOrigin, AiDestination.Origin(baseUrl), StringComparison.OrdinalIgnoreCase))
        {
            return await FinishAsync(import, RecipeImportFailureCodes.DestinationChanged, RecipeImportRunResult.Refused);
        }

        if (!string.Equals(import.DisclosureVersionInForce, RecipeImportDisclosure.Compute(snapshot, import.SendsTagNames), StringComparison.Ordinal))
        {
            return await FinishAsync(import, RecipeImportFailureCodes.DisclosureChanged, RecipeImportRunResult.Refused);
        }

        // 7.4 — the sources, prepared exactly as they will be sent. An image is stripped again here: a Library image
        // stored by an earlier path may still carry its metadata, and the unstripped bytes are never sent (§6 step 2).
        var prepared = new List<RecipeExtractionSource>();
        var sizes = new List<(bool IsFile, long Bytes)>();
        foreach (var source in sources)
        {
            if (source.Kind == RecipeImportSourceKind.Text)
            {
                prepared.Add(new RecipeExtractionSource(RecipeImportSourceKind.Text, source.Text ?? string.Empty, null, null));
                sizes.Add((false, Encoding.UTF8.GetByteCount(source.Text ?? string.Empty)));
                continue;
            }

            var contentType = present[source.FileId!.Value];
            var file = await PrepareFileAsync(source.FileId.Value, contentType, cancellationToken);
            if (file is null)
            {
                return await FinishAsync(import, RecipeImportFailureCodes.SourceInvalid, RecipeImportRunResult.Refused);
            }

            prepared.Add(file);
            sizes.Add(file.Bytes is { } bytes ? (true, bytes.LongLength) : (false, Encoding.UTF8.GetByteCount(file.Text ?? string.Empty)));
        }

        if (RecipeImportSourcePreparation.EncodedTotal(sizes) > RecipeImportLimits.MaxEncodedRequestBytes)
        {
            return await FinishAsync(import, RecipeImportFailureCodes.SourceInvalid, RecipeImportRunResult.Refused);
        }

        // 7.5 — a usable credential, so a missing key is "not sent", not a provider failure.
        var credential = await secrets.GetAsync(SecretSettingKeys.FileAnalysisApiKey, cancellationToken);
        if (credential.State != SecretReadState.Found)
        {
            return await FinishAsync(import, RecipeImportFailureCodes.CredentialUnavailable, RecipeImportRunResult.Refused);
        }

        // 7.6 — the switch, LAST, read live.
        if (!await settings.IsEnabledAsync(cancellationToken))
        {
            return await FinishAsync(import, RecipeImportFailureCodes.Disabled, RecipeImportRunResult.Refused);
        }

        var (tags, reduced) = import.SendsTagNames ? await TagVocabularyAsync(cancellationToken) : ([], false);

        // 8 — the transfer stamp, fenced BEFORE the call.
        import.TransferStartedAt = Now;
        import.TagVocabularyCount = tags.Count;
        import.TagVocabularyReduced = import.SendsTagNames ? reduced : null;
        import.Generation++;
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogInformation("Recipe import {ImportId} was taken by the stale sweep before its transfer; nothing was sent.", import.RecipeImportId);
            return RecipeImportRunResult.Lost;
        }

        var result = await provider.ExtractAsync(
            new RecipeExtractionRequest(
                prepared,
                [.. tags.Select(t => new RecipeExtractionTag(t.Ref, t.Name))],
                model,
                baseUrl,
                import.MaxTokensInForce ?? snapshot.MaxTokens),
            cancellationToken);

        // 9 — validate and sanitise.
        switch (result.Outcome)
        {
            case RecipeExtractionOutcome.Truncated:
                return await FinishAsync(import, RecipeImportFailureCodes.Truncated, RecipeImportRunResult.Failed);
            case RecipeExtractionOutcome.Malformed:
                return await FinishAsync(import, RecipeImportFailureCodes.MalformedOutput, RecipeImportRunResult.Failed);
        }

        if (result.Recipes.Count == 0)
        {
            return await FinishAsync(import, RecipeImportFailureCodes.NoRecipeFound, RecipeImportRunResult.Failed);
        }

        var refs = tags.ToDictionary(t => t.Ref, t => t.Id, StringComparer.Ordinal);
        var sanitized = RecipeDraftSanitizer.Sanitize(result.Recipes[0], refs, result.Recipes.Count > RecipeImportLimits.MaxRecipesPerImport);
        var now = Now;
        context.RecipeImportDrafts.Add(new RecipeImportDraft
        {
            RecipeImportId = import.RecipeImportId,
            Ordinal = 0,
            Status = RecipeImportDraftStatus.Pending,
            Title = sanitized.Content.Title,
            DraftJson = JsonSerializer.Serialize(sanitized.Content, DraftJson),
            Warnings = JsonSerializer.Serialize(sanitized.Warnings, DraftJson),
            Version = 1,
        });

        import.Status = RecipeImportStatus.Completed;
        import.CompletedAt = now;
        import.FinishedAt = now;
        import.RunId = null;
        import.LeaseExpiresAt = null;
        import.Generation++;
        try
        {
            await context.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogWarning("Recipe import {ImportId} completed after the stale sweep had failed it; the draft was discarded.", import.RecipeImportId);
            DiscardPending();
            return RecipeImportRunResult.Lost;
        }

        logger.LogInformation("Recipe import {ImportId} completed with {Warnings} warning(s).", import.RecipeImportId, sanitized.Warnings.Count);
        return RecipeImportRunResult.Completed;
    }

    /// <summary>A file source as it will be sent, or <c>null</c> when it cannot be (too large, unreadable, invalid).</summary>
    private async Task<RecipeExtractionSource?> PrepareFileAsync(Guid fileId, string contentType, CancellationToken cancellationToken)
    {
        var cap = RecipeImportLimits.IsImage(contentType)
            ? RecipeImportLimits.MaxExistingImageRawBytes
            : RecipeImportLimits.MaxBytesFor(contentType);
        if (cap <= 0)
        {
            return null;
        }

        var read = await files.ReadAllAsync(fileId, cap, cancellationToken);
        if (read is not { TooLarge: false, Bytes: { } bytes })
        {
            return null;
        }

        if (RecipeImportLimits.IsImage(contentType))
        {
            try
            {
                var image = RecipeImportSourcePreparation.StripImage(bytes, contentType);
                return new RecipeExtractionSource(RecipeImportSourceKind.File, null, image.Bytes, image.ContentType);
            }
            catch (Exception ex) when (ex is DomainValidationException or ImageTooLargeAfterStripException)
            {
                return null;
            }
        }

        if (contentType == "text/plain")
        {
            return RecipeImportSourcePreparation.DecodeText(bytes) is { } text
                ? new RecipeExtractionSource(RecipeImportSourceKind.File, text, null, contentType)
                : null;
        }

        return new RecipeExtractionSource(RecipeImportSourceKind.File, null, bytes, contentType);
    }

    private sealed record VocabularyTag(string Ref, Guid Id, string Name);

    /// <summary>
    /// The tag vocabulary within its budget (§13): active tags only, the most-used first, names cut to 64 characters,
    /// at most 200 and 8 KB. Returns whether the budget dropped any.
    /// </summary>
    private async Task<(List<VocabularyTag> Tags, bool Reduced)> TagVocabularyAsync(CancellationToken cancellationToken)
    {
        var active = await context.RecipeTags.AsNoTracking()
            .Where(t => t.Archived == null)
            .Select(t => new { t.RecipeTagId, t.Name, Uses = t.Recipes.Count })
            .OrderByDescending(t => t.Uses)
            .ThenBy(t => t.Name)
            .ToListAsync(cancellationToken);

        var tags = new List<VocabularyTag>();
        var bytes = 0;
        foreach (var tag in active)
        {
            var name = RecipeDraftSanitizer.Clean(tag.Name).ReplaceLineEndings(" ");
            if (name.Length > RecipeImportLimits.MaxTagNameLengthSent)
            {
                name = name[..RecipeImportLimits.MaxTagNameLengthSent];
            }

            var reference = $"t{tags.Count + 1}";
            var size = Encoding.UTF8.GetByteCount(name) + reference.Length + 3;
            if (tags.Count >= RecipeImportLimits.MaxTagVocabulary || bytes + size > RecipeImportLimits.MaxTagVocabularyBytes)
            {
                break;
            }

            bytes += size;
            tags.Add(new VocabularyTag(reference, tag.RecipeTagId, name));
        }

        return (tags, tags.Count < active.Count);
    }

    private async Task<RecipeImportRunResult> FinishAsync(RecipeImport import, string code, RecipeImportRunResult result)
    {
        DiscardPending();
        RecipeImportTerminal.Fail(import, code, Now);
        try
        {
            await context.SaveChangesAsync(CancellationToken.None);
            if (result == RecipeImportRunResult.Refused)
            {
                logger.LogInformation("Recipe import {ImportId} was refused before sending ({Code}).", import.RecipeImportId, code);
            }

            return result;
        }
        catch (DbUpdateConcurrencyException)
        {
            return RecipeImportRunResult.Lost;
        }
    }

    /// <summary>Drops every pending change except the import row, so a half-built draft is never saved.</summary>
    private void DiscardPending()
    {
        foreach (var entry in context.ChangeTracker.Entries()
                     .Where(e => e.Entity is not RecipeImport && e.State != EntityState.Unchanged)
                     .ToList())
        {
            entry.State = entry.State == EntityState.Added ? EntityState.Detached : EntityState.Unchanged;
        }
    }
}
