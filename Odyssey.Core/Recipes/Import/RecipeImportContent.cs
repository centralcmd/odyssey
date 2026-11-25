using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.FileStorage.Files;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Authorization;

namespace Odyssey.Core.Recipes.Import;

/// <summary>
/// Purges an import's content and releases its import-owned files (issue #602 §4 "File ownership and release"), staged
/// on the caller's context for ONE save fenced by <see cref="RecipeImport.Generation"/>. Retention and <c>DELETE</c>
/// both call it; neither ever removes the import row.
/// </summary>
public static class RecipeImportContentPurge
{
    /// <summary>
    /// Stages the purge on <paramref name="import"/> (loaded tracked, with its sources and drafts): nulls the source
    /// text, the draft content and warnings and every source's file link; stages the removal of each import-owned file
    /// that nothing else references; stamps <see cref="RecipeImport.ContentPurgedAt"/>. Returns the files released.
    /// </summary>
    public static async Task<int> StageAsync(
        OdysseyContext context, IObjectStore? store, RecipeImport import, DateTime now, CancellationToken cancellationToken)
    {
        var owned = new List<Guid>();
        foreach (var source in import.Sources)
        {
            source.Text = null;
            if (source.FileId is { } fileId)
            {
                if (source.ImportOwned)
                {
                    owned.Add(fileId);
                }

                source.FileId = null;
            }
        }

        foreach (var draft in import.Drafts)
        {
            draft.DraftJson = null;
            draft.Warnings = null;
        }

        var released = 0;
        foreach (var fileId in owned.Distinct())
        {
            if (await IsReferencedElsewhereAsync(context, fileId, import.RecipeImportId, cancellationToken))
            {
                continue;
            }

            var file = await context.FileMetadata.FirstOrDefaultAsync(f => f.Id == fileId, cancellationToken);
            if (file is null)
            {
                continue;
            }

            await StoredFileRemoval.StageRemoveAsync(context, store, file, cancellationToken);
            released++;
        }

        import.ContentPurgedAt ??= now;
        import.Generation++;
        return released;
    }

    /// <summary>
    /// Whether any column that references <c>FileMetadata</c> — read from the EF model, so a later table is counted
    /// too — names the file, other than this import's own source rows.
    /// </summary>
    public static async Task<bool> IsReferencedElsewhereAsync(
        OdysseyContext context, Guid fileId, Guid importId, CancellationToken cancellationToken)
    {
        foreach (var reference in FileReferenceCounter.Discover(context))
        {
            if (reference.EntityType == typeof(RecipeImportSource))
            {
                if (await context.RecipeImportSources.AnyAsync(
                        s => s.FileId == fileId && s.RecipeImportId != importId, cancellationToken))
                {
                    return true;
                }

                continue;
            }

            if (await AnyReferenceAsync(context, reference, fileId, cancellationToken))
            {
                return true;
            }
        }

        // A photo that wrapped this file before the photo move still names it (file-storage.md).
        return await context.Photos.AnyAsync(p => p.LegacyFileId == fileId, cancellationToken);
    }

    private static Task<bool> AnyReferenceAsync(OdysseyContext context, FileReference reference, Guid fileId, CancellationToken cancellationToken)
    {
        var method = typeof(RecipeImportContentPurge)
            .GetMethod(nameof(AnyOfAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(reference.EntityType);
        return (Task<bool>)method.Invoke(null, [context, reference.PropertyName, fileId, cancellationToken])!;
    }

    private static Task<bool> AnyOfAsync<TEntity>(OdysseyContext context, string property, Guid fileId, CancellationToken cancellationToken)
        where TEntity : class
    {
        var clrType = context.Model.FindEntityType(typeof(TEntity))!.FindProperty(property)!.ClrType;
        var row = Expression.Parameter(typeof(TEntity), "row");
        var column = Expression.Call(typeof(EF), nameof(EF.Property), [clrType], row, Expression.Constant(property));
        Expression value = clrType == typeof(Guid) ? Expression.Constant(fileId) : Expression.Constant((Guid?)fileId, typeof(Guid?));
        var predicate = Expression.Lambda<Func<TEntity, bool>>(Expression.Equal(column, value), row);
        return context.Set<TEntity>().AnyAsync(predicate, cancellationToken);
    }
}

/// <summary>What the requester holds right now, read from the database rather than a cookie (§3 step 7, S7).</summary>
public static class RequesterClaims
{
    /// <summary>
    /// The subset of <paramref name="claims"/> the user holds through a role or directly; empty when the user is gone
    /// or administratively disabled (a transient lockout does not stop a consented import).
    /// </summary>
    public static async Task<IReadOnlySet<string>> HeldAsync(
        OdysseyContext context, string? userId, IReadOnlyCollection<string> claims, CancellationToken cancellationToken)
    {
        var held = new HashSet<string>(StringComparer.Ordinal);
        if (userId is null)
        {
            return held;
        }

        var user = await context.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.LockoutEnd })
            .FirstOrDefaultAsync(cancellationToken);
        if (user is null || user.LockoutEnd == AccountLockout.DisabledLockoutEnd)
        {
            return held;
        }

        var viaRoles = await (
                from userRole in context.UserRoles
                where userRole.UserId == userId
                join roleClaim in context.RoleClaims on userRole.RoleId equals roleClaim.RoleId
                where roleClaim.ClaimType == PermissionClaims.Type && claims.Contains(roleClaim.ClaimValue!)
                select roleClaim.ClaimValue!)
            .ToListAsync(cancellationToken);
        var direct = await context.UserClaims
            .Where(c => c.UserId == userId && c.ClaimType == PermissionClaims.Type && claims.Contains(c.ClaimValue!))
            .Select(c => c.ClaimValue!)
            .ToListAsync(cancellationToken);
        held.UnionWith(viaRoles);
        held.UnionWith(direct);
        return held;
    }
}
