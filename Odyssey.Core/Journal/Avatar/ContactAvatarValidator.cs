using Odyssey.Core.Imaging;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Journal.Avatar;

/// <summary>
/// The contact-image surface's policy over the shared still-image pipeline (issue #86 §4, §5.2;
/// issue #94 §5). It supplies <see cref="ContactAvatarLimits"/> and nothing else — the allow-list,
/// magic-byte check, container walk, animation rejection, metadata strip and re-validation all live
/// in <see cref="StillImageValidator"/>, where there is exactly one copy of them.
///
/// <para>
/// It stays transport-agnostic for the same reason it always was: the vCard import path has no
/// <c>IFormFile</c> at all and must run the <i>identical</i> pipeline.
/// </para>
/// </summary>
public static class ContactAvatarValidator
{
    /// <summary>The contact surface's caps and vocabulary, handed to the shared pipeline.</summary>
    public static readonly StillImagePolicy Policy = new(
        ContactAvatarLimits.AllowedContentTypes,
        ContactAvatarLimits.TypeLabel,
        ContactAvatarLimits.MaxAvatarDimension,
        "a contact image");

    /// <summary>
    /// Validates, strips and re-validates. Throws <see cref="DomainValidationException"/> — a
    /// <c>400</c> — naming the actual limit or the accepted types, never echoing a filename or any
    /// part of the image.
    /// </summary>
    /// <param name="bytes">The uploaded or decoded container.</param>
    /// <param name="declaredContentType">The client-declared media type; attacker-controlled.</param>
    /// <param name="effectiveMaxBytes">
    /// <c>min(global upload cap, <see cref="ContactAvatarLimits.MaxAvatarBytes"/>)</c>. <c>min</c> is
    /// the only correct direction: lowering the instance-wide cap must still bind here.
    /// </param>
    public static ValidatedImage Validate(ReadOnlySpan<byte> bytes, string? declaredContentType, long effectiveMaxBytes) =>
        StillImageValidator.Validate(bytes, declaredContentType, effectiveMaxBytes, Policy);
}
