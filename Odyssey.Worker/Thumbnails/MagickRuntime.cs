using ImageMagick;
using ImageMagick.Configuration;
using Odyssey.Core.Journal.Thumbnails;

namespace Odyssey.Worker.Thumbnails;

/// <summary>
/// The process-wide ImageMagick hardening (issue #362 §6), applied once, idempotently, through a static
/// <see cref="Lazy{T}"/>: the coder policy, the resource limits and the temporary path.
/// </summary>
/// <remarks>
/// <para>
/// <b>The policy must be installed before anything else touches ImageMagick.</b> The Phase 0 spike found that reading
/// even <c>MagickNET.Version</c> first initialises the library with its default policy, after which the custom one is
/// silently ignored — so this is the only entry point the renderer and the self-test use.
/// </para>
/// <para>
/// Two values differ from the spec, both measured in the spike (ImageMagick 7.1.2, Alpine musl): the list-length
/// limit is 2, because 1 refuses every image; and a memory breach surfaces as <see cref="MagickCacheErrorException"/>,
/// which the renderer's callers classify as <c>decode</c> like any other decoder exception.
/// </para>
/// </remarks>
internal static class MagickRuntime
{
    public const ulong ListLength = 2;
    public const ulong Threads = 2;
    public const ulong TimeSeconds = 60;

    private static readonly Lazy<bool> Configured = new(Configure, LazyThreadSafetyMode.ExecutionAndPublication);

    public static void EnsureConfigured() => _ = Configured.Value;

    private static bool Configure()
    {
        using (var policy = new StreamReader(
                   typeof(MagickRuntime).Assembly.GetManifestResourceStream("Odyssey.Worker.Thumbnails.policy.xml")
                   ?? throw new InvalidOperationException("The thumbnail decoder policy is not embedded.")))
        {
            var configuration = ConfigurationFiles.Default;
            configuration.Policy.Data = policy.ReadToEnd();
            MagickNET.Initialize(configuration);
        }

        // /tmp is a size-capped tmpfs in the worker container (§11); with disk at 0 the pixel cache never spills.
        MagickNET.SetTempDirectory(Path.GetTempPath());
        ResourceLimits.Memory = PhotoThumbnailLimits.MagickMemoryBytes;
        ResourceLimits.Disk = 0;
        ResourceLimits.ListLength = ListLength;
        ResourceLimits.Thread = Threads;
        // A backstop only: the job's wall-clock watchdog is the real bound. The spike found it per image, not
        // cumulative per process, so it does not end all decoding once the process passes 60 s.
        ResourceLimits.Time = TimeSeconds;
        ResourceLimits.Width = PhotoThumbnailLimits.JpegMaxSide;
        ResourceLimits.Height = PhotoThumbnailLimits.JpegMaxSide;
        // Pixels, not bytes (spike): the largest any gate admits.
        ResourceLimits.Area = (ulong)PhotoThumbnailLimits.BaselineJpegMaxPixels;
        return true;
    }
}
