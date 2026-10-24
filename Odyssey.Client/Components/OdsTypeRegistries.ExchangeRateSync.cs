using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Components;

/// <summary>A vocabulary value drawn as a chip: its visible label, tone and whether it carries a dot.</summary>
/// <remarks>The meaning is always the visible <see cref="Label"/>; the tone and dot only reinforce it.</remarks>
public sealed record OdsChipValue(string Label, OdsChipTone Tone, bool Dot = false);

/// <summary>
/// The seven exchange-rate sync vocabularies (issue #351; Odyssey Design System · docs/reference-data.md,
/// "Reference data — Exchange-rate sync"), mirrored one-to-one: every surface reads these rather than
/// spelling a label or tone inline. Each lookup has a fallback for a value the client does not know — a
/// server newer than the client during a rolling deploy — so an unknown value never throws and never
/// renders its raw enum name: <c>Unknown</c> in the outline tone, or <c>Sync error</c> for an error kind.
/// </summary>
public static partial class OdsTypeRegistries
{
    /// <summary>The fallback for an unknown value in every chip vocabulary.</summary>
    public static readonly OdsChipValue UnknownChip = new("Unknown", OdsChipTone.Outline);

    /// <summary>The fallback for an unknown run error kind (including <c>Internal</c>, which has no label of its own).</summary>
    public const string SyncErrorFallback = "Sync error";

    /// <summary>EXCHANGE_RATE_SOURCES — keyed <c>Manual</c> or the provider that fetched the rate.</summary>
    public static readonly IReadOnlyDictionary<string, OdsChipValue> ExchangeRateSources = new Dictionary<string, OdsChipValue>
    {
        ["Manual"] = new("Manual", OdsChipTone.Outline),
        [nameof(ExchangeRateProvider.NorgesBank)] = new("Norges Bank", OdsChipTone.Default),
    };

    public static readonly IReadOnlyDictionary<ExchangeRateStatus, OdsChipValue> ExchangeRateStatuses = new Dictionary<ExchangeRateStatus, OdsChipValue>
    {
        [ExchangeRateStatus.Current] = new("Current", OdsChipTone.Info, Dot: true),
        [ExchangeRateStatus.Historical] = new("Historical", OdsChipTone.Default, Dot: true),
        [ExchangeRateStatus.Upcoming] = new("Upcoming", OdsChipTone.Pending, Dot: true),
    };

    public static readonly IReadOnlyDictionary<ExchangeRateSyncState, OdsChipValue> ExchangeRateSyncStates = new Dictionary<ExchangeRateSyncState, OdsChipValue>
    {
        [ExchangeRateSyncState.Idle] = new("Not syncing", OdsChipTone.Outline),
        [ExchangeRateSyncState.Scheduled] = new("Scheduled", OdsChipTone.Info),
        [ExchangeRateSyncState.CatchingUp] = new("Fetching history", OdsChipTone.Info),
        [ExchangeRateSyncState.Running] = new("Running", OdsChipTone.Info),
        [ExchangeRateSyncState.BackingOff] = new("Retrying after failures", OdsChipTone.Warning),
    };

    public static readonly IReadOnlyDictionary<ExchangeRateSyncRunStatus, OdsChipValue> ExchangeRateSyncRunStatuses = new Dictionary<ExchangeRateSyncRunStatus, OdsChipValue>
    {
        [ExchangeRateSyncRunStatus.Running] = new("Running", OdsChipTone.Info),
        [ExchangeRateSyncRunStatus.Succeeded] = new("Succeeded", OdsChipTone.Income),
        [ExchangeRateSyncRunStatus.PartiallySucceeded] = new("Partly succeeded", OdsChipTone.Warning),
        [ExchangeRateSyncRunStatus.Failed] = new("Failed", OdsChipTone.Error),
        [ExchangeRateSyncRunStatus.Skipped] = new("Skipped", OdsChipTone.Outline),
    };

    /// <summary>EXCHANGE_RATE_SYNC_TRIGGERS — plain text in its cell, no chip.</summary>
    public static readonly IReadOnlyDictionary<ExchangeRateSyncTrigger, string> ExchangeRateSyncTriggers = new Dictionary<ExchangeRateSyncTrigger, string>
    {
        [ExchangeRateSyncTrigger.Scheduled] = "Scheduled",
        [ExchangeRateSyncTrigger.Manual] = "Run now",
        [ExchangeRateSyncTrigger.Purge] = "Fetched rates deleted",
    };

    /// <summary>The backfill state a currency row derives: off, complete, or still fetching history.</summary>
    public enum ExchangeRateBackfillState { Off, InProgress, Complete }

    public static readonly IReadOnlyDictionary<ExchangeRateBackfillState, OdsChipValue> ExchangeRateBackfillStates = new Dictionary<ExchangeRateBackfillState, OdsChipValue>
    {
        [ExchangeRateBackfillState.Complete] = new("Complete", OdsChipTone.Income),
        [ExchangeRateBackfillState.InProgress] = new("In progress", OdsChipTone.Info),
        [ExchangeRateBackfillState.Off] = new("Not synced", OdsChipTone.Outline),
    };

    public static readonly IReadOnlyDictionary<ExchangeRateSyncErrorKind, string> ExchangeRateSyncErrors = new Dictionary<ExchangeRateSyncErrorKind, string>
    {
        [ExchangeRateSyncErrorKind.ProviderUnavailable] = "Norges Bank didn’t respond",
        [ExchangeRateSyncErrorKind.ProviderRateLimited] = "Norges Bank asked us to slow down",
        [ExchangeRateSyncErrorKind.ProviderMalformed] = "Norges Bank sent data we couldn’t read",
        [ExchangeRateSyncErrorKind.UnsupportedCurrency] = "A switched-on currency is no longer published",
        [ExchangeRateSyncErrorKind.SettingsUnusable] = "A sync setting is invalid",
        [ExchangeRateSyncErrorKind.TimeZoneUnavailable] = "The server is missing time zone data",
        [ExchangeRateSyncErrorKind.Cancelled] = "Stopped when the server shut down",
        [ExchangeRateSyncErrorKind.Abandoned] = "Stopped unexpectedly",
    };

    /// <summary>The source chip for a rate: <c>Manual</c>, or the provider that fetched it.</summary>
    public static OdsChipValue ExchangeRateSourceOf(ExistingExchangeRate rate) =>
        ExchangeRateSources.GetValueOrDefault(
            rate.Source == ExchangeRateSource.Manual ? "Manual" : rate.Provider?.ToString() ?? string.Empty,
            UnknownChip);

    public static OdsChipValue ExchangeRateStatusOf(ExchangeRateStatus status) =>
        ExchangeRateStatuses.GetValueOrDefault(status, UnknownChip);

    public static OdsChipValue ExchangeRateSyncStateOf(ExchangeRateSyncState state) =>
        ExchangeRateSyncStates.GetValueOrDefault(state, UnknownChip);

    public static OdsChipValue ExchangeRateSyncRunStatusOf(ExchangeRateSyncRunStatus status) =>
        ExchangeRateSyncRunStatuses.GetValueOrDefault(status, UnknownChip);

    public static string ExchangeRateSyncTriggerOf(ExchangeRateSyncTrigger trigger) =>
        ExchangeRateSyncTriggers.GetValueOrDefault(trigger, UnknownChip.Label);

    public static string ExchangeRateSyncErrorOf(ExchangeRateSyncErrorKind kind) =>
        ExchangeRateSyncErrors.GetValueOrDefault(kind, SyncErrorFallback);

    public static ExchangeRateBackfillState ExchangeRateBackfillOf(ExistingExchangeRateSyncCurrency currency) =>
        !currency.Enabled ? ExchangeRateBackfillState.Off
        : currency.BackfillComplete ? ExchangeRateBackfillState.Complete
        : ExchangeRateBackfillState.InProgress;
}
