using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Context.Secrets;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using FileAnalysisJobStatus = Odyssey.Context.FileAnalysisJobStatus;

namespace Odyssey.Core.Finance;

/// <summary>What one claimed run ended as, for the worker's run log.</summary>
public enum FileAnalysisRunResult
{
    /// <summary>Candidates extracted; the job is <c>Completed</c>.</summary>
    Completed,

    /// <summary>The transfer started and the run failed (provider error, interruption, anything unexpected).</summary>
    Failed,

    /// <summary>A pre-send check refused the job; nothing was sent.</summary>
    Refused,

    /// <summary>The row was gone, not this run's, or a sweep won the race; nothing of this run was saved.</summary>
    Lost,
}

/// <summary>
/// Runs ONE claimed extraction job (issue #444 §3 steps 4–7). Every pre-send check runs before a byte leaves,
/// from an UNCACHED settings read, and a mismatch is a refusal, never a substitution. The transfer stamp is
/// saved — fenced by <see cref="FileAnalysisJob.ExtractionGeneration"/> — immediately before the provider call,
/// so a sweep that already took the row aborts the run before anything is sent. Every terminal write uses
/// <see cref="CancellationToken.None"/>: a worker shutting down must never strand a row in <c>Running</c>.
/// </summary>
public sealed class FileAnalysisExtractionRunner(
    OdysseyContext context,
    IFileAnalysisProvider provider,
    IFileAnalysisSettingsLookup settingsLookup,
    IFileAnalysisPromptTemplate template,
    ISecretSettingsReader secrets,
    TimeProvider time,
    ILogger<FileAnalysisExtractionRunner> logger,
    Odyssey.Core.FileStorage.Files.StoredFileContentSource? source = null)
{
    private readonly Odyssey.Core.FileStorage.Files.StoredFileContentSource source =
        source ?? new Odyssey.Core.FileStorage.Files.StoredFileContentSource(context, store: null);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<FileAnalysisRunResult> RunAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await context.FileAnalysisJobs
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
        if (job is not { Status: FileAnalysisJobStatus.Running, RunId: not null })
        {
            return FileAnalysisRunResult.Lost;
        }

        try
        {
            return await RunClaimedAsync(job, cancellationToken);
        }
        catch (Exception ex)
        {
            var code = ex switch
            {
                FileAnalysisCredentialException => FileAnalysisCodes.ProviderCredentialError,
                FileAnalysisProviderException => FileAnalysisCodes.ProviderError,
                // Transport, timeout and open-breaker failures are the provider being unreachable, not a defect
                // here; the client keys its copy on the code, so they must not read as an internal error.
                HttpRequestException or Polly.Timeout.TimeoutRejectedException or Polly.CircuitBreaker.BrokenCircuitException
                    => FileAnalysisCodes.ProviderError,
                OperationCanceledException when cancellationToken.IsCancellationRequested => FileAnalysisCodes.Interrupted,
                _ => FileAnalysisCodes.InternalError,
            };

            // The raw exception (incl. any provider body) is logged for diagnosis; the persisted message is
            // curated, because the admin analysis log surfaces it.
            logger.LogError(ex, "Analysis job {JobId} failed ({Code}).", job.Id, code);
            var sent = job.TransferStartedAt is not null;
            return await FinishAsync(job, code, sent ? FileAnalysisRunResult.Failed : FileAnalysisRunResult.Refused);
        }
    }

    private async Task<FileAnalysisRunResult> RunClaimedAsync(FileAnalysisJob job, CancellationToken cancellationToken)
    {
        // 4.1 — the requester still exists, is not administratively disabled, and still holds the claim.
        if (!await RequesterMayAnalyzeAsync(job.RequestedByUserId, cancellationToken))
        {
            return await FinishAsync(job, FileAnalysisCodes.RequesterUnavailable, FileAnalysisRunResult.Refused);
        }

        // 4.2 — the file still exists, is still a statement, and its bytes are the ones consented to.
        var file = await context.AccountFiles.AsNoTracking()
            .Where(af => af.Id == job.AccountFileId)
            .Select(af => new
            {
                af.FileType,
                af.FileMetadata!.ContentType,
                af.FileMetadataId,
                CurrencyCode = af.Account!.CurrencyCode,
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (file is null || file.FileType != Odyssey.Context.AccountFileType.Statement)
        {
            return await FinishAsync(job, FileAnalysisCodes.FileChanged, FileAnalysisRunResult.Refused);
        }

        // The ceiling was enforced at enqueue against the metadata; the one read path (issue #448) re-checks the stored
        // bytes' length before materialising them — on disk or in a legacy blob — so a file swapped since is never
        // loaded whole here.
        var read = await source.ReadAllAsync(file.FileMetadataId, FileAnalysisJobTiming.MaxAnalysisFileBytes, cancellationToken);
        if (read is not { TooLarge: false, Bytes: { } content }
            || content is null
            || (job.FileSha256 is { } consented
                && !string.Equals(consented, Convert.ToHexStringLower(SHA256.HashData(content)), StringComparison.OrdinalIgnoreCase)))
        {
            return await FinishAsync(job, FileAnalysisCodes.FileChanged, FileAnalysisRunResult.Refused);
        }

        // 4.3 — one fresh settings snapshot, the one the request is built from. A failed read, or a model or
        // destination that cannot be used, refuses; a degraded value elsewhere is caught by 4.4 if it moved.
        var settings = await settingsLookup.GetUncachedAsync(cancellationToken);
        if (settings.ReadFailed || settings.Model is not { } model || settings.BaseUrl is not { } baseUrl)
        {
            return await FinishAsync(job, FileAnalysisCodes.ConfigurationUnavailable, FileAnalysisRunResult.Refused);
        }

        var target = new FileAnalysisTarget(baseUrl, model);

        // 4.4 — origin (scheme, host, port), model, processor and region, then the whole disclosure.
        if (!Same(job.AnalyzerBaseUrlOrigin, FileAnalysisService.OriginOf(baseUrl), StringComparison.OrdinalIgnoreCase)
            || !Same(job.AnalyzerModel, FileAnalysisService.Truncate(model, 256), StringComparison.Ordinal)
            || !Same(job.ProcessorInForce, FileAnalysisService.Truncate(settings.Processor, 128), StringComparison.Ordinal)
            || !Same(job.ProcessorRegionInForce, FileAnalysisService.Truncate(settings.ProcessorRegion, 128), StringComparison.Ordinal))
        {
            return await FinishAsync(job, FileAnalysisCodes.DestinationChanged, FileAnalysisRunResult.Refused);
        }

        if (!Same(job.DisclosureVersionInForce, FileAnalysisDisclosureVersion.Compute(settings), StringComparison.Ordinal))
        {
            return await FinishAsync(job, FileAnalysisCodes.DisclosureChanged, FileAnalysisRunResult.Refused);
        }

        // 4.5 — the prompt this process would send is the one the job was consented under.
        if (!Same(job.PromptTemplateHash, template.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return await FinishAsync(job, FileAnalysisCodes.PromptVersionMismatch, FileAnalysisRunResult.Refused);
        }

        // 4.6 — a usable credential, resolved here so a missing key is "not sent", not a provider failure.
        var credential = await secrets.GetAsync(SecretSettingKeys.FileAnalysisApiKey, cancellationToken);
        if (credential.State != SecretReadState.Found)
        {
            return await FinishAsync(job, FileAnalysisCodes.ProviderCredentialError, FileAnalysisRunResult.Refused);
        }

        // 4.7 — the kill switch, LAST, read live: the closest check to the transfer.
        if (!await settingsLookup.IsEnabledAsync(cancellationToken))
        {
            return await FinishAsync(job, FileAnalysisCodes.Disabled, FileAnalysisRunResult.Refused);
        }

        // 5 — the transfer stamp, saved and fenced BEFORE the call: a concurrency loss here means a sweep took
        // the row, and the run ends with nothing sent.
        var started = Now;
        job.TransferStartedAt = started;
        job.StartedAt = started;
        job.ExtractionGeneration++;
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogInformation("Analysis job {JobId} was taken by the stale sweep before its transfer; nothing was sent.", job.Id);
            return FileAnalysisRunResult.Lost;
        }

        // Built from the job's stamps, not live values: the request is exactly what the job records.
        var extracted = await provider.ExtractTransactionsAsync(
            content,
            file.ContentType,
            file.CurrencyCode,
            template.Text,
            target,
            job.MaxTokensInForce ?? settings.MaxTokens,
            job.ExtractionPromptAdditionInForce,
            cancellationToken);

        var maxFutureDate = Now.AddDays(settings.MaxFutureTransactionDays);
        var candidates = extracted
            .Where(t => t.TransactionDate <= maxFutureDate)
            .Select(t => FileAnalysisService.ToCandidate(job.Id, t, file.CurrencyCode))
            .ToList();
        context.FileAnalysisCandidateTransactions.AddRange(candidates);

        job.Status = FileAnalysisJobStatus.Completed;
        job.CompletedAt = Now;
        job.FileTypeDetected = file.ContentType;
        job.RunId = null;
        job.LeaseUntilUtc = null;
        job.ExtractionGeneration++;
        try
        {
            await context.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A sweep failed the row (interrupted) while the provider call was in flight: its write wins, and
            // this run's candidates are discarded with it.
            logger.LogWarning("Analysis job {JobId} completed after the stale sweep had failed it; the result was discarded.", job.Id);
            DiscardPending();
            return FileAnalysisRunResult.Lost;
        }

        logger.LogInformation("Analysis job {JobId} completed with {Count} candidates.", job.Id, candidates.Count);
        return FileAnalysisRunResult.Completed;
    }

    /// <summary>A terminal write, saved with <see cref="CancellationToken.None"/>; a lost race is <c>Lost</c>.</summary>
    private async Task<FileAnalysisRunResult> FinishAsync(FileAnalysisJob job, string code, FileAnalysisRunResult result)
    {
        DiscardPending();
        FileAnalysisTerminal.Fail(job, code, Now);
        job.ExtractionGeneration++;
        try
        {
            await context.SaveChangesAsync(CancellationToken.None);
            if (result == FileAnalysisRunResult.Refused)
            {
                logger.LogInformation("Analysis job {JobId} was refused before sending ({Code}).", job.Id, code);
            }

            return result;
        }
        catch (DbUpdateConcurrencyException)
        {
            return FileAnalysisRunResult.Lost;
        }
        catch (DbUpdateException ex)
        {
            // The file (and with it the job, by cascade) was deleted mid-run: nothing is left to record.
            logger.LogInformation("Analysis job {JobId} could not record its outcome ({Failure}).", job.Id, ex.GetType().Name);
            return FileAnalysisRunResult.Lost;
        }
    }

    /// <summary>Drops every pending change except the job row, so a half-built candidate set is never saved.</summary>
    private void DiscardPending()
    {
        foreach (var entry in context.ChangeTracker.Entries()
                     .Where(e => e.Entity is not FileAnalysisJob && e.State != EntityState.Unchanged)
                     .ToList())
        {
            entry.State = entry.State == EntityState.Added ? EntityState.Detached : EntityState.Unchanged;
        }
    }

    private async Task<bool> RequesterMayAnalyzeAsync(string? userId, CancellationToken cancellationToken)
    {
        if (userId is null)
        {
            return false;
        }

        // Inline comparison against the sentinel (CLAUDE.md): EF translates no arbitrary static method call,
        // and only an ADMINISTRATIVE disable counts — a transient lockout does not stop a consented analysis.
        var user = await context.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.LockoutEnd })
            .FirstOrDefaultAsync(cancellationToken);
        if (user is null || user.LockoutEnd == AccountLockout.DisabledLockoutEnd)
        {
            return false;
        }

        var viaRole = await (
                from userRole in context.UserRoles
                where userRole.UserId == userId
                join roleClaim in context.RoleClaims on userRole.RoleId equals roleClaim.RoleId
                where roleClaim.ClaimType == PermissionClaims.Type && roleClaim.ClaimValue == PermissionClaims.FileAnalysisCreate
                select roleClaim.Id)
            .AnyAsync(cancellationToken);

        return viaRole || await context.UserClaims.AnyAsync(
            c => c.UserId == userId && c.ClaimType == PermissionClaims.Type && c.ClaimValue == PermissionClaims.FileAnalysisCreate,
            cancellationToken);
    }

    /// <summary>A null stamp is a legacy row that predates the stamp: that part of the check is skipped.</summary>
    private static bool Same(string? stamped, string? current, StringComparison comparison) =>
        stamped is null || string.Equals(stamped, current, comparison);
}
