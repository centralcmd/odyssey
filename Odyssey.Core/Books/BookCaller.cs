namespace Odyssey.Core.Books;

/// <summary>
/// What the acting caller may see beside <c>books.*</c> (issue #527 §3): the controller reads the claims, the service
/// decides. Core services carry no <c>ClaimsPrincipal</c>, so the four reads travel as flags.
/// </summary>
public sealed record BookCaller(
    string UserId,
    bool CanReadContacts,
    bool CanReadPhotos,
    bool CanReadFiles,
    bool CanReadTags);
