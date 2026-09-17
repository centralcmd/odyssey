using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

/// <summary>
/// The signed-in user's one profile picture (issue #94 §6) — 1:1 with <see cref="ApplicationUser"/>,
/// stored in its <b>own</b> pair of tables with <b>zero relationships in either direction</b> to
/// <see cref="FileMetadata"/> / <see cref="FileBlob"/>.
///
/// <para>
/// <b>Why a second blob store rather than the domain one.</b> The separation is the point, and it is
/// a security boundary: a holder of <c>files.read</c> cannot read a person's face and
/// <c>files.delete</c> cannot destroy it, and <c>AdminFileExportService</c> — which enumerates
/// <see cref="FileMetadata"/> and pulls the matching blobs wholesale — cannot sweep a face into an
/// admin file export. The cost is real and accepted: every storage-wide concern (encryption at rest,
/// quota accounting, orphan sweeps, backup tooling) now has two places to be applied. What is
/// <i>not</i> duplicated is the validation pipeline — see <c>Odyssey.Core.Imaging</c>, where there is
/// exactly one copy of the container walk and the metadata strip.
/// </para>
///
/// <para>
/// <b>Three columns from <see cref="FileMetadata"/> are deliberately not here.</b> <c>FileName</c>:
/// nothing ever downloads this as a file, and the extension is derived from
/// <see cref="ContentType"/> at the one point it is needed. <c>Description</c>: the contact store's
/// value is a fixed non-PII marker, and a constant column is not data. <c>UploadedByUserId</c>: the
/// uploader is always the owner, which <see cref="UserId"/> already states — adding an administrator
/// write path later is <b>the trigger</b> to add an attribution column, since from that point
/// uploader and subject can differ.
/// </para>
/// </summary>
[Index(nameof(UserId), IsUnique = true)]
public class UserProfileImage
{
    /// <summary>
    /// EF-generated, <b>not</b> <c>DatabaseGeneratedOption.Identity</c>: it is also the blob's shared
    /// primary key, so the value has to exist before <c>SaveChangesAsync</c> writes either row.
    /// </summary>
    [Key]
    public Guid UserProfileImageId { get; set; }

    /// <summary>
    /// FK to <see cref="ApplicationUser.Id"/>; unique (1:1) with cascade delete. The unique index is
    /// what turns a concurrent <b>first</b> upload race into a <c>409</c> rather than two rows, and the
    /// cascade is what makes the picture die with the account inside the existing delete transaction.
    ///
    /// <para>
    /// <b>No <c>MaxLength</c>, deliberately.</b> An FK column is not unbounded — it inherits the
    /// principal key's width. <c>AspNetUsers.Id</c> is <c>varchar(255)</c> here, and
    /// <see cref="UserProfile.UserId"/> carries a unique index over a plain un-annotated string and
    /// ships fine. <c>MaxLength(450)</c> is the SQL Server <c>nvarchar(450)</c> Identity convention,
    /// does not apply to this provider, and would make this the only FK-to-<c>AspNetUsers</c> column
    /// wider than the key it references.
    /// </para>
    /// </summary>
    [Required]
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Regenerated on <b>every</b> write, including a byte-identical re-upload: the version describes
    /// the <i>write</i>, where <see cref="Sha256Hash"/> describes the <i>bytes</i>. It is the read
    /// URL's cache key, so a replace re-keys the URL and the browser re-requests — without that the
    /// <c>src</c> string would be unchanged, Blazor would emit no DOM update, and the read path's
    /// <c>no-cache</c>/<c>ETag</c> machinery would never come into play at all.
    ///
    /// <para>
    /// It is <b>also the concurrency token</b> (configured in <c>OdysseyContext</c>), and that is what
    /// makes the <c>409</c> on a concurrent replace reachable at all. Under the update-in-place shape
    /// the write path mandates, the unique index can only fire for two concurrent <i>first</i> uploads;
    /// <c>DbUpdateConcurrencyException</c> needs an affected-row count of zero, which never happens
    /// when two requests <c>UPDATE</c> a row that exists. Without the token both writes succeed and the
    /// later silently wins.
    /// </para>
    /// </summary>
    [Required]
    public Guid ImageVersion { get; set; }

    /// <summary>The <b>validated</b> content type, never the client-declared one.</summary>
    [Required]
    [MaxLength(64)]
    public string ContentType { get; set; } = string.Empty;

    /// <summary>The length of the <b>stored</b> (stripped) bytes, not the uploaded length.</summary>
    [Required]
    [Range(0, long.MaxValue)]
    public long SizeBytes { get; set; }

    /// <summary>SHA-256 over the stored bytes — the strong <c>ETag</c> value.</summary>
    [Required]
    [MaxLength(64)]
    public string Sha256Hash { get; set; } = string.Empty;

    /// <summary>Pixel width, from the container's own header — never a client-supplied value.</summary>
    [Required]
    public int Width { get; set; }

    /// <summary>Pixel height, from the container's own header — never a client-supplied value.</summary>
    [Required]
    public int Height { get; set; }

    /// <summary>When the first picture was attached; <b>preserved</b> across a replace.</summary>
    [Required]
    public DateTime UploadedAtUtc { get; set; }

    /// <summary>Stamped on every write.</summary>
    [Required]
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>The bytes. Split from this row so a conditional request never materialises them.</summary>
    public UserProfileImageBlob? Blob { get; set; }
}
