using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Application;

namespace Odyssey.Client.Services;

/// <summary>
/// A per-session cache for the effective upload cap (issue #421 Wave 4), the same shape as
/// <see cref="IImportLimitsCache"/> rather than a bespoke one.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the cap used to be a <c>private const</c> repeated across seven upload sites,
/// each interpolating the literal into its own error text. That made lowering the cap useless (the
/// user uploaded the whole file before the server rejected it) and raising it impossible (the local
/// pre-check still refused at the old number). Every one of those constants is now gone; this is the
/// only place a fallback number is written down.
/// </para>
/// <para>
/// <b>Failures are never cached, and never surfaced as null.</b> A failed load — including the
/// degraded <c>503</c> — leaves the slot empty so the next reader retries, and this attempt resolves to
/// <see cref="Fallback"/>.
/// </para>
/// <para>
/// <b>Mutations must invalidate.</b> A successful save on <c>/settings</c> calls
/// <see cref="Invalidate"/>; otherwise an admin who lowers the cap and then opens an upload dialog in
/// the same session would pre-validate against the old value for the rest of that session.
/// </para>
/// </remarks>
public interface IUploadLimitsCache
{
    /// <summary>The effective upload cap — the live server value, or the shipped fallback.</summary>
    Task<UploadLimitsDto> GetAsync(CancellationToken ct = default);

    /// <summary>Drops the cached cap; the next reader re-fetches.</summary>
    void Invalidate();
}

/// <inheritdoc cref="IUploadLimitsCache" />
public sealed class UploadLimitsCache(IUploadLimitsApiClient api) : IUploadLimitsCache
{
    /// <summary>
    /// The shipped default, taken from the one constant that survives the migration —
    /// <see cref="FilesApiClient.DefaultMaxFileSizeBytes"/> — so the fallback cannot drift from the
    /// transport client's own idea of a default.
    /// </summary>
    public static readonly UploadLimitsDto Fallback = new()
    {
        MaxUploadBytes = FilesApiClient.DefaultMaxFileSizeBytes,
        MaxUploadMegabytes = (int)(FilesApiClient.DefaultMaxFileSizeBytes / (1024 * 1024)),
    };

    private Task<UploadLimitsDto?>? pending;

    public async Task<UploadLimitsDto> GetAsync(CancellationToken ct = default)
    {
        var task = pending ??= LoadAsync(ct);
        var result = await task;

        if (result is null && ReferenceEquals(pending, task))
        {
            pending = null;
        }

        return result ?? Fallback;
    }

    public void Invalidate() => pending = null;

    private async Task<UploadLimitsDto?> LoadAsync(CancellationToken ct)
    {
        var result = await api.GetAsync(ct);
        return result.IsSuccess ? result.Value : null;
    }
}

/// <summary>
/// Narrows the effective cap for a surface that has its own, tighter product limit (issue #421 Wave 4).
/// </summary>
public static class UploadLimitsExtensions
{
    /// <summary>
    /// The smaller of the instance-wide cap and this surface's own limit.
    ///
    /// <para>
    /// <c>min</c> is the only correct direction: a surface may be stricter than the instance — photo
    /// and contract attachments are deliberately capped tighter than a general file upload — but it must
    /// never override a cap an administrator has <em>lowered</em>. Taking the surface constant
    /// unconditionally would do exactly that, and the upload would then fail server-side at a number the
    /// dialog never mentioned.
    /// </para>
    /// </summary>
    public static UploadLimitsDto TightenTo(this UploadLimitsDto limits, int surfaceMegabytes) =>
        surfaceMegabytes < limits.MaxUploadMegabytes
            ? new UploadLimitsDto
            {
                MaxUploadMegabytes = surfaceMegabytes,
                MaxUploadBytes = (long)surfaceMegabytes * 1024 * 1024,
            }
            : limits;
}
