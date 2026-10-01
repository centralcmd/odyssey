namespace Odyssey.Context;

/// <summary>
/// A row linking one stored file to the aggregate that owns it — an account, transaction, tax
/// statement, contract or property (issue #287 H3). Every link table carries these columns under these
/// names and a unique index on <c>(owner id, FileMetadataId)</c>, which is what lets one service hold
/// the attach / update / detach / list rules for all five instead of five drifting copies.
/// </summary>
public interface IOwnedFileLink
{
    Guid FileMetadataId { get; }

    FileMetadata? FileMetadata { get; }

    string? AttachedByUserId { get; set; }

    DateTime AttachedAtUtc { get; set; }
}

/// <summary>
/// A file link that also records when the document is in force and who issued it — the account,
/// contract and property documents (issues #146, #210).
/// </summary>
public interface IDocumentValidityLink : IOwnedFileLink
{
    DateTime? ValidFrom { get; set; }

    DateTime? ValidTo { get; set; }

    DateTime? IssuedAt { get; set; }

    Guid? IssuedBy { get; set; }
}
