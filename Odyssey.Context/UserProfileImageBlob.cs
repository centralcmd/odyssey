using System.ComponentModel.DataAnnotations;

namespace Odyssey.Context;

/// <summary>
/// The bytes behind a <see cref="UserProfileImage"/> (issue #94 §6).
///
/// <para>
/// <b>The blob is the <i>dependent</i>, and that is load-bearing.</b> The Files store's former pair
/// (<c>FileMetadata.FileBlobId</c> → <c>FileBlob.Id</c>, removed when every file moved onto the object
/// store) had the <i>blob</i> as the principal, so its cascade ran blob → metadata and never the reverse.
/// Copying that shape here would leave every deleted user's facial image bytes as a permanent orphan
/// with no row pointing at them. Making the blob the <i>dependent</i> gives an unbroken
/// <c>AspNetUsers → UserProfileImage → UserProfileImageBlob</c> chain, so the GDPR Art. 17 erasure
/// claim holds through a bare <c>userManager.DeleteAsync</c> inside
/// <c>UserAdministrationService.DeleteAsync</c>'s existing transaction, which carries no explicit
/// purge code of its own.
/// </para>
///
/// <para>
/// The table is split from the metadata because the read
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
