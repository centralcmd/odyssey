using System.Text;
using ImageMagick;
using Odyssey.Core.Journal.Thumbnails;

namespace Odyssey.Worker.Thumbnails;

/// <summary>
/// The startup self-test (issue #362 §6 item 4) against the real policy. Negative probes, each under its own pin,
/// must raise <see cref="MagickPolicyErrorException"/> specifically; positive controls catch a policy that denies too
/// much. The payloads are inert: no network URL, no write path, and the path probe reads only <c>/etc/hostname</c>.
/// </summary>
public sealed class MagickDecoderSelfTest : IThumbnailDecoderSelfTest
{
    private static readonly (string Name, ThumbnailSourceFormat Format, string Resource)[] PositiveControls =
    [
        ("decode-jpeg", ThumbnailSourceFormat.Jpeg, "small.jpg"),
        ("decode-png", ThumbnailSourceFormat.Png, "small.png"),
        ("decode-gif", ThumbnailSourceFormat.Gif, "small.gif"),
        ("decode-webp", ThumbnailSourceFormat.WebP, "small.webp"),
    ];

    public ThumbnailSelfTestResult Run()
    {
        MagickRuntime.EnsureConfigured();

        if (!Refused(() => new MagickImage(Encoding.ASCII.GetBytes("viewbox 0 0 4 4\nfill red rectangle 0 0 2 2\n"), new MagickReadSettings { Format = MagickFormat.Mvg })))
        {
            return new ThumbnailSelfTestResult("mvg");
        }

        if (!Refused(() => new MagickImage(Encoding.ASCII.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' width='4' height='4'><rect width='2' height='2'/></svg>"), new MagickReadSettings { Format = MagickFormat.Svg })))
        {
            return new ThumbnailSelfTestResult("svg");
        }

        if (!Refused(() => new MagickImage("text:@/etc/hostname")))
        {
            return new ThumbnailSelfTestResult("text-path");
        }

        foreach (var (name, format, resource) in PositiveControls)
        {
            try
            {
                using var image = new MagickImage(Embedded(resource), new MagickReadSettings { Format = MagickThumbnailRenderer.Pin(format) });
                if (name == "decode-webp")
                {
                    image.ToByteArray(MagickFormat.WebP);
                }
            }
            catch (MagickException)
            {
                return new ThumbnailSelfTestResult(name);
            }
        }

        return ThumbnailSelfTestResult.Passed;
    }

    private static bool Refused(Func<IDisposable> probe)
    {
        try
        {
            probe().Dispose();
            return false;
        }
        catch (MagickPolicyErrorException)
        {
            return true;
        }
        catch (MagickException)
        {
            // Any other outcome fails the test: a different exception means the policy is not what refused it.
            return false;
        }
    }

    private static byte[] Embedded(string name)
    {
        using var stream = typeof(MagickDecoderSelfTest).Assembly.GetManifestResourceStream("Odyssey.Worker.Thumbnails.SelfTest." + name)
            ?? throw new InvalidOperationException("A self-test control image is not embedded.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
