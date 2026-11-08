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

    /// <summary>
    /// The Files store's visibility rule (issue #428, PR #441 security finding 1): a file is hidden from
    /// <c>/api/files</c> when a photo names it — as a legacy <c>FileId</c> or a backfilled <c>LegacyFileId</c> — and no
    /// other referencing column does. Such a file is a photo original, which only <c>photos.read</c> may reach; one that
    /// is also an attachment stays visible, because the attachment's own claim already reaches it. The other references
    /// are <see cref="Discover"/>'s, so a referencing table added later is counted here too.
    /// </summary>
    public static Expression<Func<FileMetadata, bool>> VisibleInFilesStore(OdysseyContext context)
    {
        var file = Expression.Parameter(typeof(FileMetadata), "file");
        var fileId = Expression.Property(file, nameof(FileMetadata.Id));
        var nullableFileId = Expression.Convert(fileId, typeof(Guid?));

        var photo = Expression.Parameter(typeof(Photo), "photo");
        var namedByPhoto = Expression.Lambda<Func<Photo, bool>>(
            Expression.OrElse(
                Expression.Equal(Expression.Property(photo, nameof(Photo.FileId)), nullableFileId),
                Expression.Equal(Expression.Property(photo, nameof(Photo.LegacyFileId)), nullableFileId)),
            photo);
        Expression photoOnly = AnyCall(context.Photos, namedByPhoto);

        Expression? elsewhere = null;
        foreach (var reference in Discover(context))
        {
            var property = context.Model.FindEntityType(reference.EntityType)!.FindProperty(reference.PropertyName)!;
            var row = Expression.Parameter(reference.EntityType, "row");
            var column = Expression.Call(typeof(EF), nameof(EF.Property), [property.ClrType], row, Expression.Constant(reference.PropertyName));
            var value = property.ClrType == typeof(Guid) ? (Expression)fileId : nullableFileId;
            var predicate = Expression.Lambda(Expression.Equal(column, value), row);
            var set = (IQueryable)typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!
                .MakeGenericMethod(reference.EntityType).Invoke(context, null)!;
            var any = AnyCall(set, predicate);
            elsewhere = elsewhere is null ? any : Expression.OrElse(elsewhere, any);
        }

        if (elsewhere is not null)
        {
            photoOnly = Expression.AndAlso(photoOnly, Expression.Not(elsewhere));
        }

        return Expression.Lambda<Func<FileMetadata, bool>>(Expression.Not(photoOnly), file);
    }

    private static MethodCallExpression AnyCall(IQueryable source, LambdaExpression predicate) =>
        Expression.Call(
            typeof(Queryable), nameof(Queryable.Any), [source.ElementType],
            source.Expression, Expression.Quote(predicate));

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
