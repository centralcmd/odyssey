using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

/// <summary>
/// A manually tracked recurring subscription (issue #293). Pure record-keeping: a subscription never
/// generates transactions, posts to accounts, or is reconciled against anything. A single
/// amount + currency + interval live directly on the entity — there is no child "renewal" collection
/// and no derived-status engine (unlike insurance policies).
/// </summary>
[Index(nameof(Interval), nameof(Archived))]
[Index(nameof(ContactId))]
[Index(nameof(Archived))]
[Index(nameof(Paused))]
public class Subscription
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid SubscriptionId { get; set; }

    [StringLength(128)]
    [Required]
    public required string Name { get; set; }

    // Optional external identifier the provider assigns: membership no., account no.,
    // subscription no., etc. Free text, not a DB key; searchable.
    [StringLength(128)]
    public string? ExternalId { get; set; }

    // Optional linked company. A real FK to Contact with ON DELETE SET NULL, declared in
    // OdysseyContext; validated on write and resolved for display via IContactLookup.
    public Guid? ContactId { get; set; }

    [Required]
    public required DateOnly StartDate { get; set; }

    public DateOnly? EndDate { get; set; }

    [Precision(18, 6)]
    [Required]
    public required decimal Amount { get; set; }

    [StringLength(3)]
    [Required]
    public string CurrencyCode { get; set; } = "USD";

    [Required]
    public BillingInterval Interval { get; set; } = BillingInterval.Monthly;

    // How many Interval units between billings — the cadence multiplier. 1 = every unit (every
    // month), 3 = every third unit (every 3 months / quarterly), etc. Combined with Interval it
    // expresses arbitrary "every N days/weeks/months/years" cadences. Always >= 1.
    [Required]
    public int IntervalCount { get; set; } = 1;

    // Anchor date of the first billing. The per-cycle billing position (day-of-month for
    // Monthly, month+day for Yearly, day-of-week for Weekly) is DERIVED from this at read
    // time and never stored. Tracking anchor only — nothing schedules or advances it.
    [Required]
    public required DateOnly FirstBillingDate { get; set; }

    [StringLength(1024)]
    public string? Notes { get; set; }

    // Independent state stamps (nullable). Non-null => in that state; the value records
    // when the state was entered. Paused = temporarily not billing but still visible;
    // Archived = hidden/retired. Orthogonal — a subscription may be both.
    public DateTime? Paused { get; set; }

    public DateTime? Archived { get; set; }

    [Required]
    public required DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
