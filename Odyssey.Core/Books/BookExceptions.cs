using System.Net;

namespace Odyssey.Core.Books;

/// <summary>
/// A book write that lost to another writer (issue #527 §9): a stale <c>version</c>, an EF concurrency exception, or a
/// racing duplicate child link. <c>409</c> with code <see cref="VersionConflict"/>.
/// </summary>
public sealed class BookVersionConflictException : DomainException
{
    public const string VersionConflict = "book-version-conflict";

    public BookVersionConflictException()
        : base("The book was changed by someone else. Reload and try again.")
    {
    }

    public override int StatusCode => (int)HttpStatusCode.Conflict;

    public override string? Code => VersionConflict;
}

/// <summary>The fixed <c>403</c> messages (issue #527 §9). Fixed text, so a missing claim never echoes an id.</summary>
public static class BookForbidden
{
    public const string ContactLink = "Linking contacts requires the contacts.read permission.";
    public const string PhotoLink = "Linking photos requires the photos.read permission.";
    public const string FileLink = "Linking files requires the files.read permission.";
    public const string TagLink = "Linking tags requires the books.tags.read permission.";
    public const string ContactFilter = "Filtering or sorting books by author requires the contacts.read permission.";
    public const string CoverFilter = "Filtering books by cover requires the photos.read permission.";
    public const string TagFilter = "Filtering books by tag requires the books.tags.read permission.";
    public const string FileFilter = "Filtering books by files requires the files.read permission.";
}
