namespace Odyssey.Dtos.Finance;

/// <summary>
/// The attach-time content-type allow-list shared by the contract and property document surfaces
/// (issue #210 §6). The server checks it against the file's <b>server-recorded</b>
/// <c>FileMetadata.ContentType</c>, never a client-declared one.
///
/// <para>
/// One declaration, not one per controller: a second copy is the drift-prone duplicate a shared rule
/// must not become, and a source-lint in the API tests pins that both attach paths reach this symbol.
/// It lives here rather than in <c>Odyssey.Core</c> because this project has zero project references
/// and is reachable from the WASM client, so the attach dialog's "not accepted" state names the same
/// symbol the server enforces — the <c>ContractPartyRoleMatrix</c> precedent — instead of a client-side
/// copy. The account surface applies no allow-list at all; that gap is pre-existing and out of scope,
/// and not a precedent to follow.
/// </para>
/// </summary>
public static class DocumentContentTypes
{
    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "image/png",
        "image/jpeg",
        "image/webp",
    };

    /// <summary>The allow-list as a reader-facing phrase, for hints and rejection messages.</summary>
    public const string Label = "PDF, PNG, JPEG or WebP";

    public static bool IsAllowed(string? contentType) =>
        contentType is not null && Allowed.Contains(contentType);
}
