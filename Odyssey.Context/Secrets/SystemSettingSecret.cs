using System.ComponentModel.DataAnnotations;

namespace Odyssey.Context.Secrets;

/// <summary>
/// One secret-valued setting, stored as Data-Protection ciphertext (issue #444 §6).
///
/// <para>
/// <strong>A separate table from <see cref="SystemSetting"/>, deliberately.</strong>
/// <c>SystemSettingsService.GetAsync</c> reads <em>every</em> settings row and projects each one onto
/// the read DTO, so ciphertext living in that table would ride along with every present and future
/// enumeration of settings, with nothing but a remembered filter keeping it off the wire. A separate
/// <c>DbSet</c> makes that impossible rather than merely discouraged. The shapes also genuinely
/// differ: every <see cref="SystemSetting"/> key has a compiled default and a seed row, whereas a
/// secret has <em>no</em> default and an absent row is its correct initial state.
/// </para>
/// </summary>
public sealed class SystemSettingSecret
{
    /// <summary>
    /// The <c>SecretSettingKeys</c> constant this row holds. A natural primary key, matching
    /// <see cref="SystemSetting.Key"/>'s shape and the same no-surrogate-id rationale.
    /// </summary>
    [Key]
    [StringLength(100)]
    public string Key { get; set; } = null!;

    /// <summary>
    /// The base64url payload from <c>IDataProtector.Protect</c>.
    ///
    /// <para>
    /// <strong>Sized from the byte worst case, not the character cap.</strong> <c>[StringLength]</c>
    /// counts UTF-16 code units, so a 1,024-character plaintext of 3-byte BMP characters is 3,072
    /// bytes and protects to roughly <c>4/3 × (n + 60)</c> ≈ 4,176 — over a <c>varchar(4000)</c>.
    /// MariaDB outside strict mode truncates silently, which would return <c>204</c> and leave a
    /// permanently unreadable credential with no error at write time. The plaintext is separately
    /// constrained to printable ASCII, but the column is sized so the invariant does not depend on
    /// that rule staying true.
    /// </para>
    /// </summary>
    [StringLength(6000)]
    public string Ciphertext { get; set; } = null!;

    /// <summary>
    /// A forward-compatibility tag, today always <see cref="CurrentProtectionScheme"/>. If the
    /// envelope format or the purpose derivation ever changes, existing rows stay identifiable rather
    /// than silently unreadable, and a future migration can convert them.
    /// </summary>
    [StringLength(32)]
    public string ProtectionScheme { get; set; } = CurrentProtectionScheme;

    public DateTime UpdatedAt { get; set; }

    // Matches AspNetUsers.Id's actual column type in this repo, not the framework-default 450 — a
    // loose reference (ApplicationUser.Id) with no FK constraint, exactly as SystemSetting.UpdatedBy.
    [StringLength(255)]
    public string? UpdatedBy { get; set; }

    /// <summary>The only scheme this build can read; anything else reports <c>Unreadable</c>.</summary>
    public const string CurrentProtectionScheme = "dp-v1";
}
