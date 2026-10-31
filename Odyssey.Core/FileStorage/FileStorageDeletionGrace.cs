using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.SystemSettings;
using Odyssey.Dtos;

namespace Odyssey.Core.FileStorage;

/// <summary>
/// The deletion grace, resolved once for both readers: the sweeper, which erases by it, and the status service,
/// which counts what is overdue by it. Counting by a different number would flag files the sweeper is deliberately
/// still holding.
/// </summary>
public static class FileStorageDeletionGrace
{
    /// <summary>What every grace outcome but a healthy one resolves to (§3.7, A3/F6/R2-S4/R3-2).</summary>
    public const int FallbackHours = SystemSettingsBounds.FileStorageDeletionGraceHoursMax;

    private static readonly IntSettingSpec Spec = new(
        SystemSettingsKeys.FileStorageDeletionGraceHours,
        SystemSettingsDefaults.FileStorageDeletionGraceHours,
        SystemSettingsBounds.FileStorageDeletionGraceHoursMin,
        SystemSettingsBounds.FileStorageDeletionGraceHoursMax,
        ConservativeDirection.Larger);

    /// <summary>
    /// The grace in hours and the resolver's outcome. The resolver's degraded value and its below-minimum clamp
    /// (1 h) are both wrong for a retention period, so anything but a healthy read is <see cref="FallbackHours"/>.
    /// </summary>
    public static async Task<(int Hours, IntSettingOutcome Outcome)> ResolveAsync(
        OdysseyContext context, IMemoryCache cache, ILogger logger, CancellationToken cancellationToken)
    {
        var resolver = new IntSettingResolver(cache, logger, "file-storage-grace");
        var (values, readFailed) = await resolver.ReadAsync(context, [Spec.Key], "file-storage deletion grace", cancellationToken);
        var resolved = resolver.Resolve(Spec, values, readFailed);
        return (resolved.Outcome == IntSettingOutcome.Healthy ? resolved.Value : FallbackHours, resolved.Outcome);
    }
}
