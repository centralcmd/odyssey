namespace Odyssey.Core.FileStorage;

/// <summary>
/// The free space a user-driven write leaves on its volume (issue #428 §7.9, issue #448 §7.9). Uploads and the two
/// moves stop this far above the store's own 1 GB floor, so a full library cannot starve thumbnails and maintenance,
/// which keep the floor. One constant for photos and files, so the two cannot drift. Not a setting.
/// </summary>
public static class FileStorageReserves
{
    public const long UploadReserveBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>The free space below which an upload or a move is refused with <c>507</c>.</summary>
    public const long UploadThresholdBytes = ObjectStore.FreeSpaceFloorBytes + UploadReserveBytes;
}
