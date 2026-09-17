using System.Security.Claims;
using Odyssey.Dtos.Finance;

namespace Odyssey.Api.Identity;

/// <summary>
/// Resolves the attribution ids carried by the finance file surfaces (issue #106).
/// </summary>
/// <remarks>
/// <para>
/// <c>ExistingTransactionFile.AttachedByUserId</c>, <c>ExistingAccountFile.AttachedByUserId</c> and the
/// nested <c>ExistingFileMetadata.UploadedByUserId</c> used to be returned as bare identifiers. Guest
/// holds <c>transactions.read</c>, <c>accounts.read</c> and <c>files.read</c>, so those fields were a
/// harvesting primitive: a caller the application never names anyone to could still collect user ids
/// and map them onto the <c>profile-images.read</c> surface. Routing them through
/// <see cref="IUserDisplayNameResolver"/> — which is claim-conditional and never discloses an email to
/// a caller without <c>users.read</c> — removes the primitive rather than merely making it revocable.
/// </para>
/// <para>
/// Both columns are labelled with <see cref="UserDisplayNameExtensions.NameForAuthor"/>, not
/// <c>NameForOptional</c>: a file row always HAD an attacher and an uploader, and the columns are
/// nullable only because the user-attribution foreign keys null them out when the account is deleted.
/// A null id therefore means the same thing an unresolvable id means, and both answer
/// <see cref="UserDisplayNameResolver.UnknownUser"/> — the label the resolver already returns
/// everywhere else, so nothing here adds disclosure.
/// </para>
/// <para>
/// Every entry point batches ONE <see cref="IUserDisplayNameResolver.ResolveAsync(ClaimsPrincipal, IEnumerable{string?}, CancellationToken)"/>
/// call over the whole page — attachers and uploaders together — so enriching a list stays a single
/// query rather than one per row.
/// </para>
/// </remarks>
public static class FileAttributionExtensions
{
    /// <summary>Label the file attribution on every transaction in <paramref name="transactions"/>.</summary>
    public static Task EnrichFileAttributionAsync(
        this IUserDisplayNameResolver resolver,
        ClaimsPrincipal caller,
        IEnumerable<ExistingTransaction> transactions,
        CancellationToken cancellationToken) =>
        resolver.EnrichFileAttributionAsync(
            caller,
            transactions.SelectMany(transaction => transaction.TransactionFiles),
            cancellationToken);

    /// <summary>Label the file attribution on a transaction's own file list.</summary>
    public static Task EnrichFileAttributionAsync(
        this IUserDisplayNameResolver resolver,
        ClaimsPrincipal caller,
        IEnumerable<ExistingTransactionFile> files,
        CancellationToken cancellationToken) =>
        EnrichAsync(
            resolver,
            caller,
            files,
            file => file.AttachedByUserId,
            file => file.FileMetadata,
            (file, name) => file.AttachedByName = name,
            cancellationToken);

    /// <summary>Label the file attribution on an account's file list.</summary>
    public static Task EnrichFileAttributionAsync(
        this IUserDisplayNameResolver resolver,
        ClaimsPrincipal caller,
        IEnumerable<ExistingAccountFile> files,
        CancellationToken cancellationToken) =>
        EnrichAsync(
            resolver,
            caller,
            files,
            file => file.AttachedByUserId,
            file => file.FileMetadata,
            (file, name) => file.AttachedByName = name,
            cancellationToken);

    private static async Task EnrichAsync<TFile>(
        IUserDisplayNameResolver resolver,
        ClaimsPrincipal caller,
        IEnumerable<TFile> files,
        Func<TFile, string?> attachedBy,
        Func<TFile, ExistingFileMetadata?> metadata,
        Action<TFile, string?> setAttachedByName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(caller);

        var rows = files as IReadOnlyList<TFile> ?? files.ToList();
        if (rows.Count == 0)
        {
            return;
        }

        var ids = new List<string?>(rows.Count * 2);
        foreach (var row in rows)
        {
            ids.Add(attachedBy(row));
            ids.Add(metadata(row)?.UploadedByUserId);
        }

        var names = await resolver.ResolveAsync(caller, ids, cancellationToken);
        foreach (var row in rows)
        {
            setAttachedByName(row, names.NameForAuthor(attachedBy(row)));
            if (metadata(row) is { } fileMetadata)
            {
                fileMetadata.UploadedByName = names.NameForAuthor(fileMetadata.UploadedByUserId);
            }
        }
    }
}
