using System.Security.Claims;
using Odyssey.Dtos.Finance;

namespace Odyssey.Api.Identity;

/// <summary>
/// Resolves the attribution ids carried by the file surfaces of the finance module (issue #106).
/// </summary>
/// <remarks>
/// <para>
/// Every <see cref="IAttributedFile"/> pairs an <c>AttachedByUserId</c> with an
/// <see cref="ExistingFileMetadata"/> carrying an <c>UploadedByUserId</c>. Returned bare, they are a
/// harvesting primitive: a holder of the module's read claim collects user ids for people the
/// application never names to them and maps them onto the <c>profile-images.read</c> surface. Four of
/// the claims involved — <c>transactions.read</c>, <c>accounts.read</c>, <c>taxes.read</c> and
/// <c>budgets.read</c> — are held by Guest. Routing both ids through
/// <see cref="IUserDisplayNameResolver"/>, which is already claim-conditional and never discloses an
/// email to a caller without <c>users.read</c>, removes the primitive rather than merely making it
/// revocable.
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
/// Every entry point funnels into one
/// <see cref="IUserDisplayNameResolver.ResolveAsync(ClaimsPrincipal, IEnumerable{string?}, CancellationToken)"/>
/// call covering the whole response — attachers and uploaders together — so enriching a page stays a
/// single query rather than one per row. The aggregate overloads exist so a call site is one line and
/// cannot half-enrich a graph by reaching for the wrong collection.
/// </para>
/// </remarks>
public static class FileAttributionExtensions
{
    /// <summary>Label every file in <paramref name="files"/> and the metadata each one carries.</summary>
    public static async Task EnrichFileAttributionAsync(
        this IUserDisplayNameResolver resolver,
        ClaimsPrincipal caller,
        IEnumerable<IAttributedFile> files,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(caller);

        var rows = files as IReadOnlyList<IAttributedFile> ?? files.ToList();
        if (rows.Count == 0)
        {
            return;
        }

        var ids = new List<string?>(rows.Count * 2);
        foreach (var row in rows)
        {
            ids.Add(row.AttachedByUserId);
            ids.Add(row.FileMetadata?.UploadedByUserId);
        }

        var names = await resolver.ResolveAsync(caller, ids, cancellationToken);
        foreach (var row in rows)
        {
            row.AttachedByName = names.NameForAuthor(row.AttachedByUserId);
            if (row.FileMetadata is { } metadata)
            {
                metadata.UploadedByName = names.NameForAuthor(metadata.UploadedByUserId);
            }
        }
    }

    /// <summary>Label the files attached to every transaction in <paramref name="transactions"/>.</summary>
    public static Task EnrichFileAttributionAsync(
        this IUserDisplayNameResolver resolver,
        ClaimsPrincipal caller,
        IEnumerable<ExistingTransaction> transactions,
        CancellationToken cancellationToken) =>
        resolver.EnrichFileAttributionAsync(
            caller,
            transactions.SelectMany(transaction => transaction.TransactionFiles),
            cancellationToken);

    /// <summary>Label the files attached to every contract in <paramref name="contracts"/>.</summary>
    public static Task EnrichFileAttributionAsync(
        this IUserDisplayNameResolver resolver,
        ClaimsPrincipal caller,
        IEnumerable<ExistingContract> contracts,
        CancellationToken cancellationToken) =>
        resolver.EnrichFileAttributionAsync(
            caller,
            contracts.SelectMany(contract => contract.Files),
            cancellationToken);

    /// <summary>Label the files attached to every tax statement in <paramref name="statements"/>.</summary>
    public static Task EnrichFileAttributionAsync(
        this IUserDisplayNameResolver resolver,
        ClaimsPrincipal caller,
        IEnumerable<ExistingTaxStatement> statements,
        CancellationToken cancellationToken) =>
        resolver.EnrichFileAttributionAsync(
            caller,
            statements.SelectMany(statement => statement.Files),
            cancellationToken);
}
