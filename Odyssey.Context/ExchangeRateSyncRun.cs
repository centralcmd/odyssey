using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Odyssey.Dtos.Finance;

namespace Odyssey.Context;

/// <summary>
/// One exchange-rate sync run or purge (issue #349 §4) — the operational audit, and the run guard.
/// </summary>
/// <remarks>
/// <para>
/// <b>The guard is a generated column, not application state.</b> <see cref="RunningGuard"/> is
/// <c>IF(Status = 0, 1, NULL)</c> with a unique index, so at most one <c>Running</c> row can exist and a
/// second run — or a purge overlapping a run — fails on insert. Only real MariaDB enforces it; the EF
/// InMemory provider computes no column, so the fast tiers rely on the application's pre-check.
/// </para>
/// <para>The newest 500 rows are kept; older ones are pruned by tracked <c>RemoveRange</c>.</para>
/// </remarks>
[Index(nameof(StartedAtUtc))]
[Index(nameof(RunningGuard), IsUnique = true)]
public class ExchangeRateSyncRun
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid ExchangeRateSyncRunId { get; set; }

    public ExchangeRateSyncTrigger Trigger { get; set; }

    public string? RequestedByUserId { get; set; }

    public ExchangeRateProvider Provider { get; set; }

    public ExchangeRateSyncRunStatus Status { get; set; }

    public ExchangeRateSyncErrorKind? ErrorKind { get; set; }

    public DateTime StartedAtUtc { get; set; }

    public DateTime? FinishedAtUtc { get; set; }

    public DateOnly? SpanFrom { get; set; }

    public DateOnly? SpanTo { get; set; }

    /// <summary>The run's currencies as a comma list, split into an array on the read DTO.</summary>
    [StringLength(256)]
    public string CurrencyCodes { get; set; } = string.Empty;

    public int RowsInserted { get; set; }

    public int RowsSkipped { get; set; }

    public int RowsRejected { get; set; }

    public int RowsDeleted { get; set; }

    /// <summary><c>1</c> while <see cref="Status"/> is <c>Running</c>, else <c>NULL</c>; database-generated.</summary>
    public int? RunningGuard { get; private set; }
}
