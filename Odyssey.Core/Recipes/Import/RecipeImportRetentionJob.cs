using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Jobs;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Recipes;

namespace Odyssey.Core.Recipes.Import;

/// <summary>
/// The recipe-import retention job (issue #602 §4 "Retention", A13), catalogued as
/// <see cref="JobType.RecipeImportRetention"/>. Hourly, and on a manual request: every terminal import finished more than
/// the retention period ago gets its content purged and its import-owned files released. Status, draft status, the
/// accepted recipe and the audit columns stay. Each import is its own save fenced by <see cref="RecipeImport.Generation"/>,
/// so a purge that raced an accept fails and is retried next pass, by which time the attachment is a counted reference.
/// </summary>
public sealed class RecipeImportRetentionJob(
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<RecipeImportRetentionJob> logger) : IPeriodicJob
{
    private DateTime? lastPass;

    public string Name => "recipe-import-retention";

    public TimeSpan Interval => RecipeImportJobTiming.RetentionPoll;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        JobRunRecorder recorder;
        await using (var scope = scopes.CreateAsyncScope())
        {
            recorder = scope.ServiceProvider.GetRequiredService<JobRunRecorder>();
        }

        await recorder.HeartbeatAsync(JobType.RecipeImportRetention, cancellationToken);
        var manualRunId = await recorder.ClaimManualAsync(JobType.RecipeImportRetention, cancellationToken);
        var scheduled = Now - lastPass is not { } since || since >= RecipeImportJobTiming.RetentionTick;
        if (manualRunId is null && !scheduled)
        {
            return;
        }

        var (purged, released, failure) = (0, 0, (JobFailureCategory?)null);
        try
        {
            (purged, released) = await PassAsync(cancellationToken);
            lastPass = Now;
        }
        catch (Exception ex)
        {
            failure = ex is OperationCanceledException ? JobFailureCategory.Cancelled : JobFailureCategory.Internal;
            throw;
        }
        finally
        {
            void Add(RecipeImportRetentionJobRun run)
            {
                run.ImportsPurged += purged;
                run.ImportFilesReleased += released;
            }

            if (manualRunId is { } runId)
            {
                await recorder.FinishAsync<RecipeImportRetentionJobRun>(JobType.RecipeImportRetention, runId, Add,
                    failure is { } category ? (JobRunStatus.Failed, category) : (JobRunStatus.Succeeded, null), CancellationToken.None);
            }
            else if (purged > 0)
            {
                await recorder.ExtendBurstAsync<RecipeImportRetentionJobRun>(JobType.RecipeImportRetention, Add, CancellationToken.None);
                await recorder.CloseBurstAsync(JobType.RecipeImportRetention, CancellationToken.None);
            }
        }
    }

    /// <summary>Whether anything is due (the catalogue's <c>HasWork</c>).</summary>
    public async Task<bool> HasWorkAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var days = await scope.ServiceProvider.GetRequiredService<IRecipeImportSettingsLookup>().GetRetentionDaysAsync(cancellationToken);
        return await Due(context, Now.AddDays(-days)).AnyAsync(cancellationToken);
    }

    /// <summary>One pass: every due import, a chunk at a time. Returns the imports purged and files released.</summary>
    public async Task<(int Purged, int Released)> PassAsync(CancellationToken cancellationToken)
    {
        int days;
        await using (var scope = scopes.CreateAsyncScope())
        {
            days = await scope.ServiceProvider.GetRequiredService<IRecipeImportSettingsLookup>().GetRetentionDaysAsync(cancellationToken);
        }

        var cutoff = Now.AddDays(-days);
        var (purged, released) = (0, 0);
        var skipped = new HashSet<Guid>();
        while (true)
        {
            List<Guid> chunk;
            await using (var scope = scopes.CreateAsyncScope())
            {
                chunk = await Due(scope.ServiceProvider.GetRequiredService<OdysseyContext>(), cutoff)
                    .Where(i => !skipped.Contains(i.RecipeImportId))
                    .OrderBy(i => i.FinishedAt).ThenBy(i => i.RecipeImportId)
                    .Select(i => i.RecipeImportId)
                    .Take(RecipeImportJobTiming.RetentionChunk)
                    .ToListAsync(cancellationToken);
            }

            if (chunk.Count == 0)
            {
                break;
            }

            foreach (var id in chunk)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await PurgeAsync(id, cutoff, cancellationToken) is { } files)
                {
                    purged++;
                    released += files;
                }
                else
                {
                    skipped.Add(id);
                }
            }
        }

        if (purged > 0)
        {
            logger.LogInformation("Recipe-import retention purged {Count} import(s) and released {Files} file(s).", purged, released);
        }

        return (purged, released);
    }

    /// <summary>Terminal, finished before the cutoff, not yet purged.</summary>
    public static IQueryable<RecipeImport> Due(OdysseyContext context, DateTime cutoff) =>
        context.RecipeImports.AsNoTracking().Where(i =>
            i.ContentPurgedAt == null
            && (i.Status == RecipeImportStatus.Completed || i.Status == RecipeImportStatus.Failed || i.Status == RecipeImportStatus.Cancelled)
            && i.FinishedAt != null && i.FinishedAt < cutoff);

    private async Task<int?> PurgeAsync(Guid id, DateTime cutoff, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var store = scope.ServiceProvider.GetService<IObjectStore>();
        var import = await context.RecipeImports
            .Include(i => i.Sources)
            .Include(i => i.Drafts)
            .FirstOrDefaultAsync(i => i.RecipeImportId == id, cancellationToken);
        if (import is null || import.ContentPurgedAt is not null || import.FinishedAt is not { } finished || finished >= cutoff
            || !RecipeImportTerminal.IsTerminal(import.Status))
        {
            return null;
        }

        try
        {
            var released = await RecipeImportContentPurge.StageAsync(context, store, import, Now, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
            return released;
        }
        catch (DbUpdateConcurrencyException)
        {
            return null;
        }
    }
}
