namespace Odyssey.Dtos.Finance;

/// <summary>
/// A file-attachment DTO carrying user attribution that the API edge must resolve to a display label
/// before the response leaves the server (issue #106).
/// </summary>
/// <remarks>
/// <para>
/// The five implementations — transaction, account, contract, tax-statement and policy-renewal files —
/// each pair an <c>AttachedByUserId</c> with an <see cref="ExistingFileMetadata"/> that carries an
/// <c>UploadedByUserId</c>. Returned bare, those are a harvesting primitive: a caller who holds the
/// module's read claim collects user ids for people the application never names to them, and maps them
/// onto the <c>profile-images.read</c> surface. Several of the module read claims (<c>transactions.read</c>,
/// <c>accounts.read</c>, <c>taxes.read</c>, <c>budgets.read</c>) are held by Guest.
/// </para>
/// <para>
/// The interface exists so the family is enumerable rather than remembered: a guard test asserts that
/// every DTO in this project declaring an <c>AttachedByUserId</c> implements it, and that every
/// controller returning one depends on the resolver. A sixth file DTO added without implementing it
/// fails the build instead of shipping another bare id.
/// </para>
/// </remarks>
public interface IAttributedFile
{
    /// <summary>The raw id of the user who attached the file; <c>null</c> once that account is deleted.</summary>
    string? AttachedByUserId { get; }

    /// <summary>
    /// The display label for <see cref="AttachedByUserId"/>, resolved at the API edge under the
    /// CALLER's own claims. It accompanies the id rather than replacing it — a read-modify-write round
    /// trip still needs the identifier.
    /// </summary>
    string? AttachedByName { get; set; }

    /// <summary>The attached file's metadata, whose own uploader id is resolved in the same pass.</summary>
    ExistingFileMetadata FileMetadata { get; }
}
