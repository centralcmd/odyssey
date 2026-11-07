using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;

namespace Odyssey.Core.Finance;

/// <summary>A column that references <c>FileMetadata</c>, read from the EF model.</summary>
public sealed record FileReference(Type EntityType, string PropertyName);

/// <summary>
/// Who else uses a file (issue #428 §3.4.1). The photo-original backfill deletes a legacy photo's file when nothing
/// else references it, and a miss there would cascade someone's attachment away — so the answer is never a
/// hand-kept list: every foreign key whose principal is <c>FileMetadata</c>, except <c>Photo.FileId</c>, is queried.
/// A table added later is counted without a code change.
/// </summary>
public interface IFileReferenceCounter
{
    /// <summary>Every referencing column the counter queries, for the guard test.</summary>
    IReadOnlyList<FileReference> References { get; }

    /// <summary>Whether any row other than a photo's own <c>FileId</c> names <paramref name="fileId"/>.</summary>
    Task<bool> HasOtherReferencesAsync(Guid fileId, CancellationToken cancellationToken);
}

public sealed class FileReferenceCounter(OdysseyContext context) : IFileReferenceCounter
{
    private static readonly MethodInfo AnyMethod =
        typeof(FileReferenceCounter).GetMethod(nameof(AnyAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    public IReadOnlyList<FileReference> References { get; } = Discover(context);

    /// <summary>The referencing columns of a model: every foreign key to <c>FileMetadata</c> but <c>Photo.FileId</c>.</summary>
    public static IReadOnlyList<FileReference> Discover(DbContext context) =>
        [.. context.Model.GetEntityTypes()
            .SelectMany(e => e.GetForeignKeys())
            .Where(fk => fk.PrincipalEntityType.ClrType == typeof(FileMetadata))
            .Select(fk => new FileReference(fk.DeclaringEntityType.ClrType, fk.Properties.Single().Name))
            .Where(r => !(r.EntityType == typeof(Photo) && r.PropertyName == nameof(Photo.FileId)))
            .Distinct()
            .OrderBy(r => r.EntityType.Name, StringComparer.Ordinal)
            .ThenBy(r => r.PropertyName, StringComparer.Ordinal)];

    public async Task<bool> HasOtherReferencesAsync(Guid fileId, CancellationToken cancellationToken)
    {
        foreach (var reference in References)
        {
            var property = context.Model.FindEntityType(reference.EntityType)!.FindProperty(reference.PropertyName)!;
            var task = (Task<bool>)AnyMethod.MakeGenericMethod(reference.EntityType)
                .Invoke(null, [context, reference.PropertyName, property.ClrType, fileId, cancellationToken])!;
            if (await task)
            {
                return true;
            }
        }

        return false;
    }

    private static Task<bool> AnyAsync<TEntity>(
        OdysseyContext context, string propertyName, Type propertyType, Guid fileId, CancellationToken cancellationToken)
        where TEntity : class
    {
        var row = Expression.Parameter(typeof(TEntity), "row");
        var column = Expression.Call(typeof(EF), nameof(EF.Property), [propertyType], row, Expression.Constant(propertyName));
        var value = Expression.Constant(propertyType == typeof(Guid) ? fileId : (Guid?)fileId, propertyType);
        var predicate = Expression.Lambda<Func<TEntity, bool>>(Expression.Equal(column, value), row);
        return context.Set<TEntity>().AnyAsync(predicate, cancellationToken);
    }
}
