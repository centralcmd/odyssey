using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.Ai;
using Odyssey.Dtos.Recipes;

namespace Odyssey.Core.Recipes.Import;

/// <summary>
/// The recipe-import queue's row-level coordination (issue #602 §3 step 6, A12): the claim and the stale sweep. Every
/// write is fenced by <see cref="RecipeImport.Generation"/>, so replicas coordinate through the rows alone. The API
/// runs the sweep inline for the one import a request reads.
/// </summary>
public sealed class RecipeImportQueue(
    OdysseyContext context,
    AiTransportOptions transport,
    TimeProvider time,
    ILogger<RecipeImportQueue> logger)
{
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private TimeSpan Lease => RecipeImportJobTiming.Lease(Math.Min(transport.TimeoutSeconds, RecipeImportJobTiming.MaxTimeoutSeconds));

    /// <summary>Claims the oldest claimable Queued import; <c>null</c> when none could be claimed.</summary>
    public async Task<Guid?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        var candidates = await context.RecipeImports.AsNoTracking()
            .Where(i => i.Status == RecipeImportStatus.Queued && i.DeletedAt == null)
            .OrderBy(i => i.QueuedAt)
            .ThenBy(i => i.RecipeImportId)
            .Select(i => i.RecipeImportId)
            .Take(RecipeImportJobTiming.ClaimCandidates)
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

    /// <summary>Claims one specific Queued import — also the API's inline run under <c>Testing</c>.</summary>
    public async Task<bool> TryClaimAsync(Guid importId, CancellationToken cancellationToken)
    {
        var import = await context.RecipeImports.FirstOrDefaultAsync(i => i.RecipeImportId == importId, cancellationToken);
        if (import is not { Status: RecipeImportStatus.Queued, DeletedAt: null })
        {
            Detach(import);
            return false;
        }

        var now = Now;
        import.Status = RecipeImportStatus.Running;
        import.RunId = Guid.NewGuid();
        import.ClaimedAt = now;
        import.LeaseExpiresAt = now + Lease;
        import.Generation++;
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
        finally
        {
            Detach(import);
        }
    }

    /// <summary>
    /// Fails stale rows: a Running import past its lease → <c>interrupted</c>; a Queued one no worker claimed within
    /// <see cref="RecipeImportJobTiming.UnclaimedTimeout"/> → <c>not_started</c>. Nothing is re-sent. Narrowed to one
    /// import when the API sweeps inline. Returns how many it failed.
    /// </summary>
    public async Task<int> SweepAsync(Guid? importId = null, CancellationToken cancellationToken = default)
    {
        var now = Now;
        var unclaimedCutoff = now - RecipeImportJobTiming.UnclaimedTimeout;
        var query = context.RecipeImports.AsNoTracking()
            .Where(i => (i.Status == RecipeImportStatus.Running && (i.LeaseExpiresAt == null || i.LeaseExpiresAt < now))
                || (i.Status == RecipeImportStatus.Queued && i.QueuedAt < unclaimedCutoff));
        if (importId is { } id)
        {
            query = query.Where(i => i.RecipeImportId == id);
        }

        var stale = await query.Select(i => i.RecipeImportId).Take(100).ToListAsync(cancellationToken);
        var failed = 0;
        foreach (var staleId in stale)
        {
            if (await FailStaleAsync(staleId, now, unclaimedCutoff))
            {
                failed++;
            }
        }

        return failed;
    }

    private async Task<bool> FailStaleAsync(Guid importId, DateTime now, DateTime unclaimedCutoff)
    {
        var import = await context.RecipeImports.FirstOrDefaultAsync(i => i.RecipeImportId == importId, CancellationToken.None);
        if (import is null)
        {
            return false;
        }

        var code = import.Status switch
        {
            RecipeImportStatus.Running when import.LeaseExpiresAt is null || import.LeaseExpiresAt < now => RecipeImportFailureCodes.Interrupted,
            RecipeImportStatus.Queued when import.QueuedAt < unclaimedCutoff => RecipeImportFailureCodes.NotStarted,
            _ => null,
        };
        if (code is null)
        {
            Detach(import);
            return false;
        }

        RecipeImportTerminal.Fail(import, code, now);
        try
        {
            await context.SaveChangesAsync(CancellationToken.None);
            logger.LogWarning("Recipe import {ImportId} was failed by the stale sweep ({Code}).", import.RecipeImportId, code);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
        finally
        {
            Detach(import);
        }
    }

    private void Detach(RecipeImport? import)
    {
        if (import is not null)
        {
            context.Entry(import).State = EntityState.Detached;
        }
    }
}

/// <summary>The curated terminal states (§9): every failure code with its fixed, provider-free message.</summary>
public static class RecipeImportTerminal
{
    private static readonly Dictionary<string, string> Messages = new(StringComparer.Ordinal)
    {
        [RecipeImportFailureCodes.NotStarted] = "No worker started the import in time, so nothing was sent.",
        [RecipeImportFailureCodes.Disabled] = "AI recipe import was switched off before the sources were sent.",
        [RecipeImportFailureCodes.DestinationChanged] = "The AI service's address changed after consent was given, so nothing was sent.",
        [RecipeImportFailureCodes.DisclosureChanged] = "The processor details changed after consent was given, so nothing was sent.",
        [RecipeImportFailureCodes.RequesterRevoked] = "The person who started the import can no longer import recipes, so nothing was sent.",
        [RecipeImportFailureCodes.SourceMissing] = "A source file was deleted or moved before sending, so nothing was sent.",
        [RecipeImportFailureCodes.SourceInvalid] = "A source could not be prepared for sending, so nothing was sent.",
        [RecipeImportFailureCodes.CredentialUnavailable] = "The AI service credential is missing or unreadable on this server.",
        [RecipeImportFailureCodes.Interrupted] = "The import stopped before it finished.",
        [RecipeImportFailureCodes.ProviderError] = "The AI service returned an error.",
        [RecipeImportFailureCodes.DocumentRejected] = "The AI service could not read one of the documents.",
        [RecipeImportFailureCodes.Truncated] = "The recipe was too long for one draft.",
        [RecipeImportFailureCodes.NoRecipeFound] = "No recipe was found in the sources.",
        [RecipeImportFailureCodes.MalformedOutput] = "The AI service's answer could not be read.",
    };

    /// <summary>Writes Failed with the code and its curated message, clears the claim and bumps the generation.</summary>
    public static void Fail(RecipeImport import, string code, DateTime now)
    {
        import.Status = RecipeImportStatus.Failed;
        import.FailureCode = code;
        import.FailureMessage = Messages.GetValueOrDefault(code, Messages[RecipeImportFailureCodes.ProviderError]);
        import.FinishedAt = now;
        import.RunId = null;
        import.LeaseExpiresAt = null;
        import.Generation++;
    }

    /// <summary>Whether anything left the deployment — from the transfer stamp alone, never from the code.</summary>
    public static bool DocumentSent(RecipeImportStatus status, DateTime? transferStartedAt) =>
        status == RecipeImportStatus.Completed || transferStartedAt is not null;

    public static bool IsTerminal(RecipeImportStatus status) =>
        status is RecipeImportStatus.Completed or RecipeImportStatus.Failed or RecipeImportStatus.Cancelled;
}
