using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

/// <summary>
/// One deleted occurrence of a series, by its key (issue #519 §4): a later series edit or re-import does not bring it
/// back while its key is still in the regenerated key set. A key is never both a row and an exclusion of one series,
/// which holds because every writer takes the series lock.
/// </summary>
/// <remarks>
/// The key to the series is <c>CASCADE</c>, and the series delete also removes these rows by tracked
/// <c>RemoveRange</c>, so the EF InMemory provider, which enforces no keys, behaves the same.
/// </remarks>
[Index(nameof(RecurrencePatternId), nameof(OccurrenceKey), IsUnique = true)]
public class RecurrenceExclusion
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid RecurrenceExclusionId { get; set; }

    public Guid RecurrencePatternId { get; set; }

    [ForeignKey(nameof(RecurrencePatternId))]
    public RecurrencePattern? RecurrencePattern { get; set; }

    /// <summary>The excluded occurrence's local wall-clock start, with <c>Kind = Unspecified</c>.</summary>
    public DateTime OccurrenceKey { get; set; }

    public DateTime CreatedAt { get; set; }

    public string? CreatedByUserId { get; set; }
}
