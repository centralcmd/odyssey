using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;

namespace Odyssey.Core.FileStorage;

/// <summary>How a feature's files are named on disk (issue #346 §3.3).</summary>
public enum StorageNamingMode
{
    /// <summary>The sanitized stem of the user's original file name. The default, and a user decision.</summary>
    OriginalName,

    /// <summary><c>&lt;storedObjectId:N&gt;&lt;suffix&gt;&lt;ext&gt;</c>: no user text reaches the disk or the database path.</summary>
    Opaque,
}

/// <summary>
/// What a release costs in time (issue #346 §3.1). Required, with no default, so a feature holding personal
/// data has to say so: bytes released by a <em>cascade</em> — an account deletion runs no purge code — then
/// skip the grace too.
/// </summary>
public enum StorageReleaseMode
{
    /// <summary>Removed after the <c>FileStorageDeletionGraceHours</c> setting, which protects backup consistency.</summary>
    Graced,

    /// <summary>Removed on the next maintenance pass, whatever the grace.</summary>
    Immediate,
}

/// <summary>
/// The column that references a feature's objects. The maintenance job's unreferenced sweep reads it, which is
/// how a cascade delete of a consumer row releases its bytes; the guard tests read <see cref="EntityType"/> and
/// <see cref="PropertyName"/> to prove every key to <c>StoredObjects</c> belongs to a feature and is RESTRICT.
/// </summary>
public sealed class StorageFeatureReference
{
    private readonly Func<OdysseyContext, IQueryable<Guid>> referencedIds;

    private StorageFeatureReference(Type entityType, string propertyName, Func<OdysseyContext, IQueryable<Guid>> referencedIds)
    {
        EntityType = entityType;
        PropertyName = propertyName;
        this.referencedIds = referencedIds;
    }

    public Type EntityType { get; }

    public string PropertyName { get; }

    /// <summary>
    /// The referencing column, as <c>e =&gt; e.StoredObjectId</c> (cast a non-nullable key: <c>e =&gt; (Guid?)e.Id</c>).
    /// </summary>
    public static StorageFeatureReference To<TEntity>(Expression<Func<TEntity, Guid?>> column) where TEntity : class
    {
        var body = column.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert ? convert.Operand : column.Body;
        if (body is not MemberExpression member || member.Expression != column.Parameters[0])
        {
            throw new ArgumentException("The reference must name one property of the entity.", nameof(column));
        }

        return new StorageFeatureReference(
            typeof(TEntity),
            member.Member.Name,
            context => context.Set<TEntity>().Select(column).Where(id => id != null).Select(id => id!.Value));
    }

    /// <summary>Every object id the referencing table names, as a composable query.</summary>
    public IQueryable<Guid> ReferencedIds(OdysseyContext context) => referencedIds(context);
}

/// <summary>
/// One storage feature, declared in code (issue #346 §3.1). The catalogue is empty in production in v1; the
/// first real feature is photo thumbnails (issue #362).
/// </summary>
public sealed class StorageFeature
{
    private StorageFeature(
        string key,
        string rootFolder,
        Func<object?, IReadOnlyList<string>> layout,
        StorageNamingMode namingMode,
        StorageReleaseMode releaseMode,
        StorageFeatureReference reference)
    {
        Key = key;
        RootFolder = rootFolder;
        this.layout = layout;
        NamingMode = namingMode;
        ReleaseMode = releaseMode;
        Reference = reference;
    }

    private readonly Func<object?, IReadOnlyList<string>> layout;

    /// <summary>The stable canonical key in PascalCase, used verbatim in the database and in API responses.</summary>
    public string Key { get; }

    /// <summary>The feature's folder directly under the volume root, unique across the catalogue.</summary>
    public string RootFolder { get; }

    public StorageNamingMode NamingMode { get; }

    public StorageReleaseMode ReleaseMode { get; }

    public StorageFeatureReference Reference { get; }

    /// <summary>
    /// Declares a feature. <paramref name="layout"/> produces the folder segments below the root folder for one
    /// write; it must keep every folder at ≤ 10 000 entries at the feature's design volume (§3.3), and
    /// <see cref="StorageLayout.Shard"/> helps when sharding by id.
    /// </summary>
    public static StorageFeature Create<TLayoutArgs>(
        string key,
        string rootFolder,
        Func<TLayoutArgs, IEnumerable<string>> layout,
        StorageReleaseMode releaseMode,
        StorageFeatureReference reference,
        StorageNamingMode namingMode = StorageNamingMode.OriginalName)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(reference);
        if (!Enum.IsDefined(releaseMode))
        {
            throw new ArgumentOutOfRangeException(nameof(releaseMode));
        }

        return new StorageFeature(
            key,
            rootFolder,
            args => args is TLayoutArgs typed
                ? layout(typed).ToList()
                : throw new StoreContractViolationException($"Storage feature '{key}' was given layout arguments of the wrong type."),
            namingMode,
            releaseMode,
            reference);
    }

    /// <summary>The layout segments for one write. Their shape is checked by the store, not trusted.</summary>
    internal IReadOnlyList<string> LayoutFor(object? args) => layout(args);
}

/// <summary>Layout helpers for feature declarations.</summary>
public static class StorageLayout
{
    /// <summary>Two hex segments from an id — 65 536 buckets: <c>3fa29c0e…</c> → <c>["3f", "a2"]</c>.</summary>
    public static IReadOnlyList<string> Shard(Guid id)
    {
        var hex = id.ToString("N");
        return [hex[..2], hex[2..4]];
    }
}

/// <summary>
/// Every storage feature this build knows (issue #346 §3.1), assembled from the <see cref="StorageFeature"/>
/// registrations in DI. Validated once: keys and root folders well-formed and unique.
/// </summary>
public sealed class StorageFeatureCatalogue
{
    private readonly Dictionary<string, StorageFeature> byKey = new(StringComparer.OrdinalIgnoreCase);

    public StorageFeatureCatalogue(IEnumerable<StorageFeature> features)
    {
        var roots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var feature in features)
        {
            if (!StorageNames.IsValidName(feature.Key))
            {
                throw new InvalidOperationException($"Storage feature key '{feature.Key}' does not match {StorageNames.NamePattern}.");
            }

            if (!StorageNames.IsValidRootFolder(feature.RootFolder))
            {
                throw new InvalidOperationException(
                    $"Storage feature '{feature.Key}' has root folder '{feature.RootFolder}', which does not match {StorageNames.RootFolderPattern}.");
            }

            if (!byKey.TryAdd(feature.Key, feature))
            {
                throw new InvalidOperationException($"Storage feature key '{feature.Key}' is declared twice.");
            }

            if (!roots.Add(feature.RootFolder))
            {
                throw new InvalidOperationException($"Storage root folder '{feature.RootFolder}' is declared by two features.");
            }
        }

        Features = [.. byKey.Values.OrderBy(f => f.RootFolder, StringComparer.Ordinal)];
    }

    /// <summary>Ordered by root folder, ordinally — the disk reconciliation's walk order.</summary>
    public IReadOnlyList<StorageFeature> Features { get; }

    public bool IsEmpty => Features.Count == 0;

    /// <summary>Configuration lookups are case-insensitive, as configuration keys are.</summary>
    public StorageFeature? Find(string key) => byKey.GetValueOrDefault(key);

    public bool IsRootFolder(string folder) => Features.Any(f => string.Equals(f.RootFolder, folder, StringComparison.Ordinal));
}
