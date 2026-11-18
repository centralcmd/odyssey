using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using FileAnalysisJobStatus = Odyssey.Context.FileAnalysisJobStatus;

namespace Odyssey.Core.Finance;

/// <summary>
/// The extraction queue's row-level coordination (issue #444 §3): the claim and the stale sweep. Replicas
/// coordinate only through the rows — <see cref="FileAnalysisJob.ExtractionGeneration"/> is the concurrency
/// token every write here is fenced by — never through process state, so every worker replica can run it.
/// The sweep also runs inline in the API for the one file or job a request touches, so a missing worker
/// cannot wedge a file behind a <c>Queued</c> row for ever.
/// </summary>
public sealed class FileAnalysisJobQueue(
    OdysseyContext context,
    IOptions<FileAnalysisOptions> options,
    TimeProvider time,
    ILogger<FileAnalysisJobQueue> logger)
{
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private TimeSpan Lease => FileAnalysisJobTiming.Lease(options.Value.TimeoutSeconds);

    /// <summary>
    /// Claims the oldest claimable <c>Queued</c> job: tries up to
    /// <see cref="FileAnalysisJobTiming.ClaimCandidates"/> candidates in FIFO order and moves to the next on
    /// a concurrency loss (another replica won that row). Returns the claimed job's id, or <c>null</c>.
    /// </summary>
    public async Task<Guid?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        var candidates = await context.FileAnalysisJobs.AsNoTracking()
            .Where(j => j.Status == FileAnalysisJobStatus.Queued)
            .OrderBy(j => j.QueuedAt)
            .ThenBy(j => j.Id)
            .Select(j => j.Id)
            .Take(FileAnalysisJobTiming.ClaimCandidates)
            .ToListAsync(cancellationToken);

        foreach (var id in candidates)
        {
            if (await TryClaimAsync(id, cancellationToken))
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>
    /// Claims one specific <c>Queued</c> job — the API's inline run under the <c>Testing</c> environment,
    /// where no worker exists (the <c>ExchangeRateSyncJob</c> run-now precedent).
    /// </summary>
    public Task<bool> TryClaimAsync(Guid jobId, CancellationToken cancellationToken) =>
        TryClaimCoreAsync(jobId, cancellationToken);

    private async Task<bool> TryClaimCoreAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await context.FileAnalysisJobs.FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
        if (job is not { Status: FileAnalysisJobStatus.Queued })
        {
            Detach(job);
            return false;
        }

        var now = Now;
        job.Status = FileAnalysisJobStatus.Running;
        job.RunId = Guid.NewGuid();
        job.ClaimedAt = now;
        job.LeaseUntilUtc = now + Lease;
        job.ExtractionGeneration++;
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            Detach(job);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another replica claimed it (or a sweep failed it) between the read and the save.
            Detach(job);
            return false;
        }
    }

    /// <summary>
    /// Fails stale rows (§3 step 8): a <c>Running</c> row whose lease expired — or a legacy one with no lease,
    /// left by the synchronous path, that started more than a lease ago — becomes <c>Failed / interrupted</c>;
    /// a <c>Queued</c> row no worker claimed within <see cref="FileAnalysisJobTiming.UnclaimedTimeout"/>
    /// becomes <c>Failed / not_started</c>. Nothing is ever re-sent. Narrowed to one file or one job when the
    /// API sweeps inline. Returns how many rows it failed.
    /// </summary>
    public async Task<int> SweepAsync(Guid? accountFileId = null, Guid? jobId = null, CancellationToken cancellationToken = default)
    {
        var now = Now;
        var leaseCutoff = now - Lease;
        var unclaimedCutoff = now - FileAnalysisJobTiming.UnclaimedTimeout;

        var query = context.FileAnalysisJobs.AsNoTracking()
            .Where(j => (j.Status == FileAnalysisJobStatus.Running
                    && ((j.LeaseUntilUtc != null && j.LeaseUntilUtc < now)
                        || (j.LeaseUntilUtc == null && j.StartedAt != null && j.StartedAt < leaseCutoff)
                        || (j.LeaseUntilUtc == null && j.StartedAt == null)))
                || (j.Status == FileAnalysisJobStatus.Queued && j.QueuedAt != null && j.QueuedAt < unclaimedCutoff));
        if (accountFileId is { } fileId)
        {
            query = query.Where(j => j.AccountFileId == fileId);
        }

        if (jobId is { } id)
        {
            query = query.Where(j => j.Id == id);
        }

        var stale = await query.Select(j => j.Id).Take(100).ToListAsync(cancellationToken);
        var failed = 0;
        foreach (var staleId in stale)
        {
            if (await FailStaleAsync(staleId, now, leaseCutoff, unclaimedCutoff, cancellationToken))
            {
                failed++;
            }
        }

        return failed;
    }

    private async Task<bool> FailStaleAsync(
        Guid jobId, DateTime now, DateTime leaseCutoff, DateTime unclaimedCutoff, CancellationToken cancellationToken)
    {
        var job = await context.FileAnalysisJobs.FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
        if (job is null)
        {
            return false;
        }

        // Re-checked on the tracked row: the claim or the run may have moved it since the query.
        string? code = job.Status switch
        {
            FileAnalysisJobStatus.Running when job.LeaseUntilUtc is { } lease && lease < now => FileAnalysisCodes.Interrupted,
            FileAnalysisJobStatus.Running when job.LeaseUntilUtc is null && (job.StartedAt is null || job.StartedAt < leaseCutoff)
                => FileAnalysisCodes.Interrupted,
            FileAnalysisJobStatus.Queued when job.QueuedAt is { } queued && queued < unclaimedCutoff => FileAnalysisCodes.NotStarted,
            _ => null,
        };
        if (code is null)
        {
            Detach(job);
            return false;
        }

        FileAnalysisTerminal.Fail(job, code, now);
        job.ExtractionGeneration++;
        try
        {
            await context.SaveChangesAsync(CancellationToken.None);
            logger.LogWarning("Analysis job {JobId} was failed by the stale sweep ({Code}).", job.Id, code);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
        finally
        {
            Detach(job);
        }
    }

    private void Detach(FileAnalysisJob? job)
    {
        if (job is not null)
        {
            context.Entry(job).State = EntityState.Detached;
        }
    }
}

/// <summary>
/// The curated terminal states (issue #444 §9): one place that pairs every code with its status and its
/// fixed <c>FailureMessage</c>. The message is never the provider body — it is surfaced in the admin
/// analysis log — and the client keys on the code, never on the message.
/// </summary>
public static class FileAnalysisTerminal
{
    private static readonly Dictionary<string, (FileAnalysisJobStatus Status, string Message)> Codes = new(StringComparer.Ordinal)
    {
        [FileAnalysisCodes.Disabled] = (FileAnalysisJobStatus.Cancelled, "Analysis was switched off before the document was sent."),
        [FileAnalysisCodes.RequesterUnavailable] = (FileAnalysisJobStatus.Cancelled,
            "The account that requested the analysis can no longer analyze files, so the document was not sent."),
        [FileAnalysisCodes.FileChanged] = (FileAnalysisJobStatus.Cancelled,
            "The file was changed or removed after the analysis was requested, so it was not sent."),
        [FileAnalysisCodes.ConfigurationUnavailable] = (FileAnalysisJobStatus.Failed,
            "The analysis settings could not be read, so the document was not sent."),
        [FileAnalysisCodes.DestinationChanged] = (FileAnalysisJobStatus.Failed,
            "The analysis destination changed after consent was given, so the document was not sent."),
        [FileAnalysisCodes.DisclosureChanged] = (FileAnalysisJobStatus.Failed,
            "The disclosure changed after consent was given, so the document was not sent."),
        [FileAnalysisCodes.PromptVersionMismatch] = (FileAnalysisJobStatus.Failed,
            "The server's analysis prompt is not the one consent was given for, so the document was not sent."),
        [FileAnalysisCodes.ProviderCredentialError] = (FileAnalysisJobStatus.Failed,
            "The analysis provider credential is missing or unreadable on this server."),
        [FileAnalysisCodes.NotStarted] = (FileAnalysisJobStatus.Failed, "No analysis worker started the job in time."),
        [FileAnalysisCodes.Interrupted] = (FileAnalysisJobStatus.Failed, "The analysis stopped before it finished."),
        [FileAnalysisCodes.ProviderError] = (FileAnalysisJobStatus.Failed, "The analysis provider returned an error."),
        [FileAnalysisCodes.InternalError] = (FileAnalysisJobStatus.Failed, "An internal error occurred during analysis."),
    };

    /// <summary>Writes the terminal status, code and curated message, and clears the claim bookkeeping.</summary>
    public static void Fail(FileAnalysisJob job, string code, DateTime now)
    {
        var (status, message) = Codes.TryGetValue(code, out var entry)
            ? entry
            : Codes[FileAnalysisCodes.InternalError];
        job.Status = status;
        job.FailureCode = code;
        job.FailureMessage = message;
        job.CompletedAt = now;
        job.RunId = null;
        job.LeaseUntilUtc = null;
    }

    /// <summary>
    /// Whether the document may have left the deployment, computed from the transfer stamp alone (§5.2) —
    /// never from the code. A legacy row (no <c>QueuedAt</c>) predates the stamp, so only Completed is known.
    /// </summary>
    public static FileAnalysisDocumentSent DocumentSent(FileAnalysisJob job) => DocumentSent(
        job.Status, job.QueuedAt, job.TransferStartedAt);

    public static FileAnalysisDocumentSent DocumentSent(FileAnalysisJobStatus status, DateTime? queuedAt, DateTime? transferStartedAt)
    {
        if (status == FileAnalysisJobStatus.Completed)
        {
            return FileAnalysisDocumentSent.Yes;
        }

        if (queuedAt is null)
        {
            return status is FileAnalysisJobStatus.Queued ? FileAnalysisDocumentSent.No : FileAnalysisDocumentSent.Possibly;
        }

        return transferStartedAt is null ? FileAnalysisDocumentSent.No : FileAnalysisDocumentSent.Possibly;
    }
}
