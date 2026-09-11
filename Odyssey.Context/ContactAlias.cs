using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Odyssey.Dtos.Journal;

namespace Odyssey.Context;

/// <summary>
/// An alternative name a <see cref="Contact"/> is actually known by (issue #48) — a nickname, a
/// maiden name, a former company name, an abbreviation — carrying an optional free-text
/// <see cref="Label"/> saying what kind of alias it is. n:1 to the parent, cascade-deleted with it,
/// and attached to the <b>contact</b> rather than either detail sub-record so Persons and
/// Organizations use one mechanism.
///
/// <para>
/// <b>One index, unique on <c>(ContactId, Value)</c>.</b> The composite leads on
/// <see cref="ContactId"/>, so it serves uniqueness, the correlated <c>EXISTS</c> behind the search
/// arm <b>and</b> InnoDB's foreign-key-index requirement in one. A standalone <c>ContactId</c> index
/// would be redundant and a standalone <c>Value</c> index is never led on.
/// </para>
///
/// <para>
/// <b><see cref="Value"/> carries NO explicit collation, deliberately.</b> Both the unique index and
/// the search arm rely on the column's <b>default</b> MariaDB collation (<c>utf8mb4_*_ci</c>), which
/// is neither case- nor accent-sensitive — the precedent is <see cref="PhotoTag.Name"/>. Do not
/// "match <see cref="Contact.ExternalUid"/>": that column is <c>utf8mb4_bin</c> precisely because a
/// vCard UID matches ordinally, and copying it here would make this index case-sensitive and defeat
/// the design. The service's own duplicate check uses
/// <c>CompareOptions.IgnoreCase | IgnoreNonSpace</c> so it, the index, and the accent- and
/// case-sensitive EF InMemory provider all agree.
/// </para>
///
/// <para>
/// There is also <b>no stored normalized column</b>, unlike <see cref="Contact.NormalizedName"/>:
/// that one is <i>derived</i> (from <c>DisplayName</c> or <c>FirstName + LastName</c>) and so has to
/// be materialized to be indexed. An alias has no derivation — <see cref="Value"/> <i>is</i> the
/// value.
/// </para>
/// </summary>
[Index(nameof(ContactId), nameof(Value), IsUnique = true)]
public class ContactAlias
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public Guid ContactId { get; set; }

    public Contact Contact { get; set; } = null!;

    /// <summary>The alternative name, stored trimmed and whitespace-collapsed.</summary>
    [Required]
    [StringLength(ContactAliasRules.MaxValueLength)]
    public required string Value { get; set; }

    /// <summary>
    /// Optional free text ("maiden name", "nickname", "trading as"). Deliberately not an enum, and
    /// deliberately not part of the unique key — "Hansen" cannot be stored twice under two labels.
    /// </summary>
    [StringLength(ContactAliasRules.MaxLabelLength)]
    public string? Label { get; set; }
}
