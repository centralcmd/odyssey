namespace Odyssey.ApiClient;

/// <summary>
/// A file downloaded from the API — the bytes plus whatever the server declared about them.
/// Unifies the three near-identical shapes the Blazor client used to carry separately
/// (<c>FileContent</c>, <c>IcsExportFile</c>, <c>VCardExportFile</c>).
/// </summary>
/// <param name="Bytes">The file content.</param>
/// <param name="FileName">
/// The name from the <c>Content-Disposition</c> header, or a caller-supplied default when the server
/// did not send one.
/// </param>
/// <param name="ContentType">The declared media type, or <c>null</c> when the server omitted it.</param>
public sealed record ApiFile(byte[] Bytes, string FileName, string? ContentType = null);

/// <summary>
/// A file being uploaded to the API. Deliberately not <c>IBrowserFile</c>: that type lives in
/// <c>Microsoft.AspNetCore.Components.Forms</c> and would pin this library to Blazor. The Blazor
/// client adapts with <c>IBrowserFile.ToApiUpload(maxSize)</c>; a console consumer can point
/// <paramref name="OpenRead"/> at a <see cref="FileStream"/>.
/// </summary>
/// <param name="FileName">The name to send in the multipart part.</param>
/// <param name="ContentType">
/// The media type. Blank is normalized to <c>application/octet-stream</c> at send time.
/// </param>
/// <param name="Size">The content length in bytes, for callers that pre-validate against a cap.</param>
/// <param name="OpenRead">
/// Opens the content stream. A factory rather than a live <see cref="Stream"/> so the caller keeps
/// ownership of the size cap and the stream is only opened if the request is actually made.
/// </param>
public sealed record ApiUpload(
    string FileName,
    string ContentType,
    long Size,
    Func<Stream> OpenRead);

/// <summary>An export download: the file, and the row count the server declared, when it declared a usable one.</summary>
/// <param name="File">The file.</param>
/// <param name="Rows">
/// <c>X-Odyssey-Export-Rows</c>, or <c>null</c> when the header is missing or not a non-negative integer.
/// </param>
public sealed record ApiExportDownload(ApiFile File, int? Rows);

/// <summary>
/// A news OPML export (issue #508 amendment v7): the file, and the number of feeds written from
/// <c>X-Odyssey-Export-Rows</c>, which the Feeds page's success message names. <c>null</c> when the header is missing
/// or not a non-negative integer.
/// </summary>
public sealed record NewsOpmlExportFile(ApiFile File, int? Rows);
