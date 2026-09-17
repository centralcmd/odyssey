using Odyssey.Core.Imaging;
using Odyssey.Dtos.Application;

namespace Odyssey.Core.Profiles;

/// <summary>
/// The profile-picture surface's policy over the shared still-image pipeline (issue #94 §4, §5). It
/// supplies <see cref="UserProfileImageLimits"/> and nothing else — the allow-list, magic-byte check,
/// container walk, animation rejection, metadata strip and re-validation all live in
/// <see cref="StillImageValidator"/>, where there is exactly one copy of them.
///
/// <para>
/// The <b>storage</b> is deliberately separate from the contact image's; the <b>validation</b>
/// deliberately is not. Two copies of a parser on an untrusted-input path will diverge, and the copy
/// with fewer eyes on it is the one that will.
/// </para>
/// </summary>
public static class UserProfileImageValidator
{
    /// <summary>The profile-picture surface's caps and vocabulary, handed to the shared pipeline.</summary>
    public static readonly StillImagePolicy Policy = new(
        UserProfileImageLimits.AllowedContentTypes,
        UserProfileImageLimits.TypeLabel,
        UserProfileImageLimits.MaxImageDimension,
        "a profile picture");

    /// <summary>
    /// Validates, strips and re-validates. Throws <see cref="DomainValidationException"/> — a
    /// <c>400</c> — naming the actual limit or the accepted types, never echoing a filename or any
    /// part of the image.
    /// </summary>
    /// <param name="bytes">The uploaded container.</param>
    /// <param name="declaredContentType">The client-declared media type; attacker-controlled.</param>
    /// <param name="effectiveMaxBytes">
    /// <c>min(instance upload cap, <see cref="UserProfileImageLimits.MaxImageBytes"/>)</c>.
    /// <c>min</c> is the only correct direction: lowering the instance-wide cap must still bind here.
    /// </param>
    public static ValidatedImage Validate(ReadOnlySpan<byte> bytes, string? declaredContentType, long effectiveMaxBytes) =>
        StillImageValidator.Validate(bytes, declaredContentType, effectiveMaxBytes, Policy);
}
