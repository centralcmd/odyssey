using Microsoft.AspNetCore.Components.Forms;

namespace Odyssey.Client.Tests;

/// <summary>A picked browser file for driving an upload path in bUnit, which has no real file picker.</summary>
internal sealed class FakeBrowserFile(string name, long size, string contentType = "application/pdf") : IBrowserFile
{
    public string Name { get; } = name;
    public DateTimeOffset LastModified { get; } = DateTimeOffset.UnixEpoch;
    public long Size { get; } = size;
    public string ContentType { get; } = contentType;

    public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) =>
        new MemoryStream(new byte[Math.Min(Size, 16)]);
}
