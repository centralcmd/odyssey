using Microsoft.Extensions.Options;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Journal;

namespace Odyssey.Api.SystemSettings;

/// <summary>
/// Hard ceilings for the request caps that cannot be raised (issue #421 Waves 3/4).
///
/// <para>
/// <see cref="PhotoLimits.MaxLinksPerKind"/> and <see cref="PhotoLimits.MaxAlbumMembers"/> are
/// compile-time constants consumed by <c>[MaxLength]</c> on ten photo request DTOs. Model validation
/// therefore rejects an over-cap request <em>before</em> the service check runs, so a setting raised
/// above the constant would change nothing at all — the precise "I raised the limit and it did not take
/// effect" failure this feature refuses to ship (it is why rate limits were excluded outright).
/// </para>
///
/// <para>
/// <see cref="UploadMegabytes"/> is the same rule with a different mechanism: Kestrel's
/// <c>MaxRequestBodySize</c> and the multipart length limit are fixed at startup from
/// <c>FileStorage:MaxFileSizeBytes</c>, and an upload cap raised above them would be refused by the
/// transport before any application code ran.
/// </para>
///
/// <para>
/// So these are tighten-only: lowering works and takes effect, raising is rejected with a <c>400</c>
/// naming the ceiling, and each ceiling is surfaced on the read DTO so the control bounds itself
/// instead of offering a value the API will refuse. Lowering is the useful direction anyway — a
/// defensive cap gets tightened, not loosened.
/// </para>
/// </summary>
/// <remarks>
/// An injected instance rather than a static holder, because the upload ceiling is startup
/// configuration rather than a constant. A <c>static</c> field carrying it would be process-wide while
/// the configuration that set it is per-<c>WebApplicationFactory</c>, so one test class configuring a
/// different ceiling would silently change the ceiling seen by every other test running beside it.
/// </remarks>
public sealed class RequestCapCeilings
{
    private const long BytesPerMegabyte = 1024 * 1024;

    public RequestCapCeilings(IOptions<FileStorageOptions> fileStorage)
    {
        // Floors at 1: a transport ceiling configured below one megabyte would otherwise produce a
        // ceiling of 0 and make every value — including the compiled default — unsettable.
        UploadMegabytes = (int)Math.Max(1, fileStorage.Value.MaxFileSizeBytes / BytesPerMegabyte);
    }

    /// <summary>The startup transport ceiling, in megabytes.</summary>
    public int UploadMegabytes { get; }

    public static int PhotoLinksPerKind => PhotoLimits.MaxLinksPerKind;

    public static int PhotoAlbumMembers => PhotoLimits.MaxAlbumMembers;

    public string? ValidateUploadMegabytes(int value) =>
        value > UploadMegabytes
            ? $"cannot exceed {UploadMegabytes}: the transport request-body limit is fixed at startup "
              + "from FileStorage:MaxFileSizeBytes, so a larger upload would be rejected by the server "
              + "before this setting was consulted. Lower it, or raise the configured limit first."
            : null;

    public static string? ValidatePhotoLinksPerKind(int value) =>
        Validate(value, PhotoLinksPerKind, "photo link");

    public static string? ValidatePhotoAlbumMembers(int value) =>
        Validate(value, PhotoAlbumMembers, "album member");

    private static string? Validate(int value, int ceiling, string what) =>
        value > ceiling
            ? $"cannot exceed {ceiling}: the {what} cap is also enforced by request-model validation, "
              + "which would reject an over-cap request before this setting was consulted. Lower it, or "
              + "raise the compile-time limit first."
            : null;
}
