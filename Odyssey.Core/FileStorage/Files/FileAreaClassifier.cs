using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.FileStorage.Files;

/// <summary>
/// Chooses the area a legacy file moves to, from the rows that link it (issue #448 §3.5.1). Every foreign key whose
/// principal is <c>FileMetadata</c> (but <c>Photo.FileId</c>, the photo move's) — read from the EF model by
/// <see cref="FileReferenceCounter.Discover"/> — must be mapped in <see cref="AreaOfReference"/>; a guard test fails on an
/// unmapped one, so a new attachment table cannot silently be filed under Finance.
/// <para>
/// Rule, in order: <b>Contacts</b> when every link is an avatar link, there is one, and the stored type is an avatar
/// type; <b>Journal</b> when every link is a Journal or avatar link and there is at least one Journal link; otherwise
/// <b>Finance</b> — no links, any Finance link, or an avatar-only file of a non-avatar type (no real journal link, so the
/// more conservative area).
/// </para>
/// </summary>
public sealed class FileAreaClassifier(IFileReferenceCounter references)
{
    /// <summary>The declared area of every referencing entity. The guard test compares it with the model.</summary>
    public static readonly IReadOnlyDictionary<Type, StoredFileArea> AreaOfReference = new Dictionary<Type, StoredFileArea>
    {
        [typeof(AccountFile)] = StoredFileArea.Finance,
        [typeof(TransactionFile)] = StoredFileArea.Finance,
        [typeof(TaxStatementFile)] = StoredFileArea.Finance,
        [typeof(ContractFile)] = StoredFileArea.Finance,
        [typeof(PropertyFile)] = StoredFileArea.Finance,
        [typeof(JournalEntryAttachment)] = StoredFileArea.Journal,
        [typeof(JournalTaskAttachment)] = StoredFileArea.Journal,
        [typeof(Contact)] = StoredFileArea.ContactAvatars,
    };

    public async Task<StoredFileArea> ClassifyAsync(Guid fileId, string contentType, CancellationToken cancellationToken)
    {
        var naming = await references.ReferencesNamingAsync(fileId, cancellationToken);
        return Classify(naming.Select(r => AreaOfReference.GetValueOrDefault(r.EntityType, StoredFileArea.Finance)), contentType);
    }

    /// <summary>The pure rule over the areas of the links, for unit tests.</summary>
    public static StoredFileArea Classify(IEnumerable<StoredFileArea> linkAreas, string contentType)
    {
        var areas = linkAreas.ToHashSet();
        if (areas.Count == 0 || areas.Contains(StoredFileArea.Finance))
        {
            return StoredFileArea.Finance;
        }

        if (areas.Contains(StoredFileArea.Journal))
        {
            return StoredFileArea.Journal;
        }

        // Only avatar links remain.
        return ContactAvatarLimits.IsAllowedContentType(contentType) ? StoredFileArea.ContactAvatars : StoredFileArea.Finance;
    }
}
