using Odyssey.Dtos.Application;

namespace Odyssey.ApiClient.Resources;

/// <summary>
/// Typed client for the file-volume telemetry (issue #346 §5): each volume's state, usage and bindings, and the
/// instance's maintenance liveness. Gated on <c>file-storage.read</c>; an empty list means file storage is not set up.
/// </summary>
public interface IFileStorageApiClient
{
    Task<ApiResult<IReadOnlyList<FileStorageVolumeStatus>>> GetVolumesAsync(CancellationToken ct = default);
}

/// <inheritdoc cref="IFileStorageApiClient" />
public sealed class FileStorageApiClient(IOdysseyApi api) : IFileStorageApiClient
{
    public Task<ApiResult<IReadOnlyList<FileStorageVolumeStatus>>> GetVolumesAsync(CancellationToken ct = default) =>
        api.GetAsync<IReadOnlyList<FileStorageVolumeStatus>>("api/file-storage/volumes", ct);
}
