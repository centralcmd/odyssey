namespace Odyssey.Core.Finance;

/// <summary>
/// An uploaded file as the domain sees it: the client's name and declared content type, the length, and
/// a way to read the bytes. It keeps <c>Odyssey.Core</c> free of the ASP.NET <c>IFormFile</c> (issue
/// #287 L9); the API adapts a multipart part with <c>IFormFile.ToFileUpload()</c>.
/// </summary>
/// <param name="FileName">The client-supplied name, unsanitised.</param>
/// <param name="ContentType">The client-declared content type, unverified.</param>
/// <param name="Length">The body length in bytes.</param>
/// <param name="OpenReadStream">Opens a fresh read stream over the body on every call; validation and
/// the upload each open their own.</param>
public sealed record FileUpload(string FileName, string ContentType, long Length, Func<Stream> OpenReadStream);
