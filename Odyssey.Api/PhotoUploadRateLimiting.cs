namespace Odyssey.Api;

/// <summary>
/// The per-user limit in front of the photo upload (issue #428 §7.9): 120 uploads per 10 minutes per user, a fixed
/// window. Together with the upload reserve it keeps one caller from filling the photo volume quickly. Deploy-time
/// configuration for the reason <see cref="ProfileImageRateLimiting"/> gives.
/// </summary>
public static class PhotoUploadRateLimiting
{
    public const string UploadPolicy = "photo-upload";

    public static IServiceCollection AddPhotoUploadRateLimiter(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddPerUserFixedWindowOptions<PhotoUploadRateLimitOptions>(configuration, PhotoUploadRateLimitOptions.SectionName);

        // Its own AddRateLimiter call setting neither OnRejected nor GlobalLimiter, so it composes with the others and
        // inherits IdentityRateLimiting's RFC 7807 429.
        services.AddRateLimiter(options => options.AddPerUserFixedWindowPolicy<PhotoUploadRateLimitOptions>(UploadPolicy));
        return services;
    }
}

/// <summary>Bound from <c>RateLimiting:PhotoUpload</c>: 120 uploads per user per 10 minutes.</summary>
public sealed class PhotoUploadRateLimitOptions()
    : PerUserFixedWindowRateLimitOptions(permitLimit: 120, windowSeconds: 600)
{
    public const string SectionName = "RateLimiting:PhotoUpload";
}
