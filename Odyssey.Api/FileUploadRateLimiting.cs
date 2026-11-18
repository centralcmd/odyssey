namespace Odyssey.Api;

/// <summary>
/// The per-user limit in front of every write to the file store's file areas (issue #448 §7.9): <c>POST /api/files</c>
/// and the contact image upload. 120 uploads per 10 minutes per user, a fixed window — the photo upload's numbers.
/// Together with the upload reserve it keeps one caller from filling a volume quickly; it counts uploads, not bytes,
/// and that residual is accepted (§7.9). Deploy-time configuration for the reason <see cref="ProfileImageRateLimiting"/>
/// gives.
/// </summary>
public static class FileUploadRateLimiting
{
    public const string UploadPolicy = "file-upload";

    public static IServiceCollection AddFileUploadRateLimiter(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddPerUserFixedWindowOptions<FileUploadRateLimitOptions>(configuration, FileUploadRateLimitOptions.SectionName);

        // Its own AddRateLimiter call setting neither OnRejected nor GlobalLimiter, so it composes with the others and
        // inherits IdentityRateLimiting's RFC 7807 429.
        services.AddRateLimiter(options => options.AddPerUserFixedWindowPolicy<FileUploadRateLimitOptions>(UploadPolicy));
        return services;
    }
}

/// <summary>Bound from <c>RateLimiting:FileUpload</c>: 120 uploads per user per 10 minutes.</summary>
public sealed class FileUploadRateLimitOptions()
    : PerUserFixedWindowRateLimitOptions(permitLimit: 120, windowSeconds: 600)
{
    public const string SectionName = "RateLimiting:FileUpload";
}
