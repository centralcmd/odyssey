namespace Odyssey.Core.Finance;

/// <summary>
/// Bound to the "FileStorage" configuration section. The single source of truth for the upload size
/// cap so the transport limits (Kestrel request body, multipart form length) and the application
/// validation (<see cref="FileValidationService"/>) stay in lockstep — change the cap in one place.
/// The reverse-proxy limits (nginx <c>client_max_body_size</c>, Caddy <c>request_body max_size</c>)
/// are static config and must be kept at or above this value with a little headroom for the
/// multipart envelope.
/// </summary>
public class FileStorageOptions
{
    public const string SectionName = "FileStorage";

    /// <summary>Maximum accepted file content size, in bytes. Defaults to 64 MB.</summary>
    public long MaxFileSizeBytes { get; set; } = 64 * 1024 * 1024;

    /// <summary>
    /// Extra bytes allowed on top of <see cref="MaxFileSizeBytes"/> at the transport layer to cover
    /// the multipart envelope (boundaries, part headers, the description field) so a full-size file
    /// isn't rejected before the validator runs. Defaults to 1 MB.
    /// </summary>
    public long RequestEnvelopeHeadroomBytes { get; set; } = 1 * 1024 * 1024;

    /// <summary>The transport-level request body cap: the file cap plus the multipart headroom.</summary>
    public long MaxRequestBodyBytes => MaxFileSizeBytes + RequestEnvelopeHeadroomBytes;
}
