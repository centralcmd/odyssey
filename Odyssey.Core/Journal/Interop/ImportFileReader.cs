using System.Text;
using Odyssey.Core;

namespace Odyssey.Core.Journal.Interop;

/// <summary>
/// Shared upload-reading and content-type validation for the ICS/vCard import pipelines
/// (Calendar/JournalEntry/Task/Contact, architect finding F-9). Previously reimplemented
/// byte-for-byte in each of the four services.
/// </summary>
internal static class ImportFileReader
{
    /// <summary>
    /// Reads <paramref name="stream"/> into memory as UTF-8 text, throwing
    /// <see cref="DomainValidationException"/> (→ 400) if it exceeds <paramref name="maxBytes"/>. This
    /// bounds the in-memory buffer the whole upload is read into — it protects against exhausting
    /// server memory, not against realistic usage. <paramref name="fileExtensionLabel"/> (e.g.
    /// <c>".ics"</c>, <c>".vcf"</c>) only affects the error message.
    /// </summary>
    public static async Task<string> ReadBoundedAsync(
        Stream stream, long maxBytes, string fileExtensionLabel, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new DomainValidationException(
                    $"The {fileExtensionLabel} file exceeds the {maxBytes / (1024 * 1024)} MB limit.");
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Streaming counterpart of <see cref="ReadBoundedAsync"/> (issue #343 §5 "Making the read path
    /// proportional"): wraps <paramref name="stream"/> in a byte cap enforced <i>while reading</i>,
    /// then hands back a <see cref="TextReader"/> a parser consumes line-by-line — no full-content
    /// <c>MemoryStream</c>/<c>string</c> is ever materialized here. The byte bound is still enforced
    /// while reading, so an under-declared <c>Content-Length</c> cannot smuggle a larger body past the
    /// transport-level check.
    /// </summary>
    public static TextReader OpenBoundedTextReader(Stream stream, long maxBytes, string fileExtensionLabel) =>
        new StreamReader(new BoundedReadStream(stream, maxBytes, fileExtensionLabel), Encoding.UTF8);

    // A read-only pass-through Stream that throws DomainValidationException (→ 400) the moment the
    // cumulative bytes read would exceed maxBytes — the streaming equivalent of ReadBoundedAsync's
    // buffered total-vs-maxBytes check, just enforced per chunk instead of after the fact.
    private sealed class BoundedReadStream(Stream inner, long maxBytes, string fileExtensionLabel) : Stream
    {
        private long total;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            await ReadAsync(buffer.AsMemory(offset, count), cancellationToken);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            total += read;
            if (total > maxBytes)
            {
                throw new DomainValidationException(
                    $"The {fileExtensionLabel} file exceeds the {maxBytes / (1024 * 1024)} MB limit.");
            }

            return read;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Whether a multipart part's declared content type is acceptable for import against
    /// <paramref name="acceptedTypes"/> — the file extension and the parse itself are the real validity
    /// gates, so a missing content type is accepted (common, not itself a signal of a bad file).
    /// </summary>
    public static bool IsAcceptedContentType(string? contentType, IReadOnlyCollection<string> acceptedTypes)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return true;
        }

        var baseType = contentType.Split(';', 2)[0].Trim();
        return acceptedTypes.Contains(baseType, StringComparer.OrdinalIgnoreCase);
    }
}
