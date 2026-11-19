using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Odyssey.Context;

/// <summary>
/// The one retry configuration of every <see cref="OdysseyContext"/> on MariaDB (issue #519 implementation note 1): the
/// API, the worker, the migration job and the integration-test contexts that claim to mirror them all call
/// <see cref="Configure"/>, so they cannot disagree about what is transient.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>1205</c> (lock wait timeout) and <c>1213</c> (deadlock) are listed explicitly</b> rather than left to Pomelo's
/// built-in list, so the series lock's retry behaviour (issue #519 v5 §2) does not depend on a provider detail. This
/// also makes every plain <c>SaveChangesAsync</c> on these hosts retry a deadlock or a lock wait, which the release
/// notes say.
/// </para>
/// <para>
/// A lock wait timeout rolls back only the failing statement by default, never the transaction, so a delegate run
/// under this strategy must roll its whole transaction back before the strategy retries it. The series-lock helper
/// rolls back explicitly before rethrowing.
/// </para>
/// </remarks>
public static class OdysseyDatabaseRetry
{
    /// <summary>The MariaDB error numbers added to the provider's transient set.</summary>
    public static readonly IReadOnlyList<int> TransientErrorNumbers = [1205, 1213];

    /// <summary>The provider's default retry count, kept for every non-interactive writer.</summary>
    public const int MaxRetryCount = 6;

    /// <summary>The retry count of an interactive series write (implementation note 2): about 15 s worst case.</summary>
    public const int InteractiveMaxRetryCount = 2;

    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>Enables retry on failure with <see cref="TransientErrorNumbers"/>.</summary>
    public static void Configure(MySqlDbContextOptionsBuilder mySql) =>
        mySql.EnableRetryOnFailure(MaxRetryCount, MaxRetryDelay, [.. TransientErrorNumbers]);

    /// <summary>
    /// An execution strategy for one interactive unit of work: the context's own on a non-relational provider, else a
    /// retrying strategy with <see cref="InteractiveMaxRetryCount"/> and the same transient set.
    /// </summary>
    public static IExecutionStrategy InteractiveStrategy(DbContext context) =>
        context.Database.IsRelational()
            ? new MySqlRetryingExecutionStrategy(
                context, InteractiveMaxRetryCount, TimeSpan.FromSeconds(2), [.. TransientErrorNumbers])
            : context.Database.CreateExecutionStrategy();
}
