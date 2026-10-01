using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging;
using Odyssey.Api.SystemSettings;
using Odyssey.Api.Tests.Infrastructure;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Direct coverage of <see cref="IntSettingResolver"/> (issue #287 H1): the outcome each stored state
/// reports, both conservative directions, and the logging contract every lookup now inherits — one line
/// per key per TTL window, the level naming the condition, and never the stored value.
/// <see cref="SettingsLookupReadPathContractTests"/> asserts the same contract through each lookup.
/// </summary>
public class IntSettingResolverTests
{
    private const string Key = "SomeCap";

    private static readonly Dictionary<string, string> NoRows = [];

    private static IntSettingSpec Spec(ConservativeDirection direction = ConservativeDirection.Smaller) =>
        new(Key, Default: 20, Min: 1, Max: 50, direction);

    private static (IntSettingResolver Resolver, CapturingLogger<IntSettingResolverTests> Logger, ManualClock Clock) Create()
    {
        var clock = new ManualClock();
        var logger = new CapturingLogger<IntSettingResolverTests>();
        var cache = new MemoryCache(new MemoryCacheOptions { Clock = clock });
        return (new IntSettingResolver(cache, logger, "test"), logger, clock);
    }

    private static Dictionary<string, string> Row(string value) => new() { [Key] = value };

    [Theory]
    [InlineData(null, 20, IntSettingOutcome.Healthy)]
    [InlineData("35", 35, IntSettingOutcome.Healthy)]
    [InlineData("51", 50, IntSettingOutcome.Clamped)]
    [InlineData("0", 1, IntSettingOutcome.Clamped)]
    [InlineData("abc", 20, IntSettingOutcome.Degraded)]
    public void EachStoredState_ReportsItsOutcome(string? stored, int expected, IntSettingOutcome outcome)
    {
        var (resolver, _, _) = Create();

        var result = resolver.Resolve(Spec(), stored is null ? NoRows : Row(stored), readFailed: false);

        Assert.Equal(expected, result.Value);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(outcome == IntSettingOutcome.Degraded, result.IsDegraded);
    }

    [Fact]
    public void AFailedQuery_IsDegraded_EvenWithARowInHand()
    {
        var (resolver, _, _) = Create();

        var result = resolver.Resolve(Spec(), Row("35"), readFailed: true);

        Assert.Equal(IntSettingOutcome.Degraded, result.Outcome);
        Assert.Equal(20, result.Value);
    }

    /// <summary>
    /// The two directions, each against a watermark on both sides of the default. <c>Smaller</c> is a cap
    /// and takes the lesser; <c>Larger</c> is a floor (the auto-link threshold, the mail-throttle window)
    /// and takes the greater. A shared <c>Math.Min</c> would pass the first two rows and fail the last two.
    /// </summary>
    [Theory]
    [InlineData(ConservativeDirection.Smaller, "5", 5)]
    [InlineData(ConservativeDirection.Smaller, "40", 20)]
    [InlineData(ConservativeDirection.Larger, "5", 20)]
    [InlineData(ConservativeDirection.Larger, "40", 40)]
    public void ADegradedRead_MovesTowardTheSaferEnd(ConservativeDirection direction, string healthy, int expected)
    {
        var (resolver, _, _) = Create();
        resolver.Resolve(Spec(direction), Row(healthy), readFailed: false);

        var degraded = resolver.Resolve(Spec(direction), NoRows, readFailed: true);

        Assert.Equal(IntSettingOutcome.Degraded, degraded.Outcome);
        Assert.Equal(expected, degraded.Value);
    }

    [Theory]
    [InlineData(ConservativeDirection.Smaller, 3)]
    [InlineData(ConservativeDirection.Larger, 30)]
    public void ColdFallback_AppliesOnlyWithoutALiveWatermark(ConservativeDirection direction, int expectedCold)
    {
        var (resolver, _, clock) = Create();
        var spec = Spec(direction) with { ColdFallback = direction == ConservativeDirection.Smaller ? 3 : 30 };

        resolver.Resolve(spec, Row("10"), readFailed: false);
        var warm = resolver.Resolve(spec, NoRows, readFailed: true);

        clock.UtcNow += IntSettingResolver.CacheTtl + TimeSpan.FromSeconds(1);
        var cold = resolver.Resolve(spec, NoRows, readFailed: true);

        Assert.Equal(direction == ConservativeDirection.Smaller ? 10 : 20, warm.Value);
        Assert.Equal(expectedCold, cold.Value);
    }

    [Fact]
    public void Clamped_LogsAWarningNamingKeyAndBound_ButNotTheStoredValue()
    {
        var (resolver, logger, _) = Create();

        resolver.Resolve(Spec(), Row("9137"), readFailed: false);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(Key, entry.Message, StringComparison.Ordinal);
        Assert.Contains("50", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("9137", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unparseable_LogsAnErrorNamingTheKey_ButNotTheStoredValue()
    {
        var (resolver, logger, _) = Create();

        resolver.Resolve(Spec(), Row("s3cr3t-looking"), readFailed: false);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains(Key, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFaultyRow_LogsOncePerKeyPerTtlWindow()
    {
        var (resolver, logger, clock) = Create();

        resolver.Resolve(Spec(), Row("abc"), readFailed: false);
        resolver.Resolve(Spec(), Row("abc"), readFailed: false);
        Assert.Single(logger.Entries);

        // Another key has its own line rather than being swallowed by the first key's throttle.
        resolver.Resolve(Spec() with { Key = "OtherCap" }, new Dictionary<string, string> { ["OtherCap"] = "abc" }, readFailed: false);
        Assert.Equal(2, logger.Entries.Count);

        clock.UtcNow += IntSettingResolver.CacheTtl + TimeSpan.FromSeconds(1);
        resolver.Resolve(Spec(), Row("abc"), readFailed: false);
        Assert.Equal(3, logger.Entries.Count);
    }

    [Fact]
    public void AHealthyRow_LogsNothing()
    {
        var (resolver, logger, _) = Create();

        resolver.Resolve(Spec(), Row("35"), readFailed: false);
        resolver.Resolve(Spec(), NoRows, readFailed: false);

        Assert.Empty(logger.Entries);
    }

    private sealed class ManualClock : ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
    }
}
