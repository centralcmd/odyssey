using System.ComponentModel.DataAnnotations;

namespace Odyssey.Context;

/// <summary>
/// The bytes behind a <see cref="UserProfileImage"/> (issue #94 §6).
///
/// <para>
/// <b>The FK direction is inverted relative to <see cref="FileMetadata"/>/<see cref="FileBlob"/>, and
/// that inversion is load-bearing.</b> In the shipped pair the <i>blob</i> is the principal
/// (<c>FileMetadata.FileBlobId</c> → <c>FileBlob.Id</c>), so the cascade runs blob → metadata and
/// never the reverse — which is why <c>ContactAvatarRelease</c> has to remove both rows explicitly.
/// Copying that shape here would leave every deleted user's facial image bytes as a permanent orphan
/// with no row pointing at them. Making the blob the <i>dependent</i> gives an unbroken
/// <c>AspNetUsers → UserProfileImage → UserProfileImageBlob</c> chain, so the GDPR Art. 17 erasure
/// claim holds through a bare <c>userManager.DeleteAsync</c> inside
/// <c>UserAdministrationService.DeleteAsync</c>'s existing transaction, which carries no explicit
/// purge code of its own.
/// </para>
///
/// <para>
/// The table is still split from the metadata for the same reason <see cref="FileBlob"/> is: the read
/// path is <c>no-cache</c> with a strong <c>ETag</c>, which makes <b>revalidation the hot path</b>.
/// Answering a conditional request must cost the metadata read and nothing else — materialising the
/// <c>LONGBLOB</c> for a response that carries no body would make every return visit on every surface
/// pay full cost. The shared primary key also removes the need for a separate index.
/// </para>
/// </summary>
public class UserProfileImageBlob
{
    /// <summary>The primary key <b>and</b> the foreign key, cascading from <see cref="UserProfileImage"/>.</summary>
    [Key]
    public Guid UserProfileImageId { get; set; }

    [Required]
    public byte[] Content { get; set; } = [];

    public UserProfileImage? Image { get; set; }
}
