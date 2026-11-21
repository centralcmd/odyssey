namespace Odyssey.Dtos.Books;

/// <summary>
/// The book feature's compile-time product limits (issue #527 §8.1), named by the DTO annotations and the client alike
/// so the two can never disagree. None is admin-editable; the upload size cap is the separate, existing setting.
/// </summary>
public static class BookLimits
{
    public const int MaxTitleLength = 500;
    public const int MaxPublisherLength = 200;
    public const int MaxIsbnInputLength = 32;
    public const int MaxIsbnLength = 13;
    public const int MaxDescriptionLength = 4000;
    public const int MaxNotesLength = 8000;
    public const int MaxRoleLength = 64;
    public const int MinYear = -9999;
    public const int MaxYear = 9999;

    public const int MaxContributors = 50;
    public const int MaxImages = 20;
    public const int MaxAttachments = 20;
    public const int MaxTags = 20;

    public const int MaxTagNameLength = 64;
    public const int MaxTagDescriptionLength = 256;

    /// <summary>The request-body cap on create and update: every valid body at the field caps fits with room to spare.</summary>
    public const int MaxRequestBytes = 256 * 1024;
}
