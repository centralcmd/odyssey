using Odyssey.Core.FileStorage.Files;

namespace Odyssey.Core.Finance;

/// <summary>
/// The legacy-photo image reader, by file id, over the one read path to a file's bytes (issue #448 §3.4): a file still in
/// <c>FileBlob</c> or one the file move has put on disk alike.
/// </summary>
public sealed class ImageContentReader(StoredFileContentSource source) : IImageContentReader
{
    public Task<byte[]?> ReadPrefixAsync(Guid fileId, int maxBytes, CancellationToken cancellationToken = default) =>
        source.ReadPrefixAsync(fileId, maxBytes, cancellationToken);

    public async Task<ImageContent?> ReadAllAsync(Guid fileId, long maxBytes, CancellationToken cancellationToken = default) =>
        await source.ReadAllAsync(fileId, maxBytes, cancellationToken) switch
        {
            null => null,
            (_, true) => ImageContent.OverLimit,
            ({ } bytes, false) => new ImageContent(bytes, TooLarge: false),
            _ => null,
        };
}
