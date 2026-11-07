using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Odyssey.Core.Finance;

/// <summary>
/// A short, stable fingerprint of an administrator prompt addition (issue #433), in the same format as
/// <see cref="FileAnalysisDisclosureVersion"/>: the first 16 characters of the base64url SHA-256. The
/// audit endpoint and the settings audit line carry this instead of the text, so the text itself never
/// leaves the database or reaches a log.
/// </summary>
public static class PromptAdditionDigest
{
    private const int Length = 16;

    /// <summary>The digest, or <see langword="null"/> for an absent or empty addition (never digested).</summary>
    public static string? Of(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Base64Url.EncodeToString(digest)[..Length];
    }
}
