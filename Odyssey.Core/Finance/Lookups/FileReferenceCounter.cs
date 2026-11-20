using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;

namespace Odyssey.Core.Finance;

/// <summary>A column that references <c>FileMetadata</c>, read from the EF model.</summary>
public sealed record FileReference(Type EntityType, string PropertyName);

/// <summary>
/// Who uses a file, read from the EF model rather than a hand-kept list: every foreign key whose principal is
/// <c>FileMetadata</c>. A table added later is counted without a code change.
/// </summary>
public static class FileReferenceCounter
{
    /// <summary>The referencing columns of a model: every foreign key to <c>FileMetadata</c>.</summary>
    public static IReadOnlyList<FileReference> Discover(DbContext context) =>
        [.. context.Model.GetEntityTypes()
            .SelectMany(e => e.GetForeignKeys())
            .Where(fk => fk.PrincipalEntityType.ClrType == typeof(FileMetadata))
            .Select(fk => new FileReference(fk.DeclaringEntityType.ClrType, fk.Properties.Single().Name))
            .Distinct()
            .OrderBy(r => r.EntityType.Name, StringComparer.Ordinal)
            .ThenBy(r => r.PropertyName, StringComparer.Ordinal)];

    /// <summary>
    /// The Files store's visibility rule (issue #428, PR #441 security finding 1): a file is hidden from
    /// <c>/api/files</c> when a photo names it as its <c>LegacyFileId</c> — the file it wrapped before the move — and no
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
            Expression.Equal(Expression.Property(photo, nameof(Photo.LegacyFileId)), nullableFileId),
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
}
