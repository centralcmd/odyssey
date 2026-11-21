using Odyssey.Context;
using Odyssey.Core.SystemSettings;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos;

namespace Odyssey.Api.SystemSettings;

/// <summary>
/// The single declaration of every admin-configurable setting (issue #421 Wave 0). One entry per key,
/// replacing the five parallel per-field blocks <see cref="SystemSettingsService"/> used to carry.
///
/// <para>
/// The claim split is the one issue #349/#343 established, unchanged: policy and cosmetic values take
/// <see cref="PermissionClaims.SystemSettingsUpdate"/>; the authentication perimeter, and the
/// availability knob that import/export <em>size</em> caps represent, take the stricter
/// <see cref="PermissionClaims.SystemSettingsSecurityUpdate"/>. Note the count caps and the size caps
/// deliberately differ from each other on this.
/// </para>
///
/// <para>
/// Accessors are explicit delegates rather than reflection over property names. That is deliberate: a
/// reflection-driven registry would let a renamed <see cref="SystemSettingsUpdate"/> property silently
/// lose its claim, which is the very failure mode the registry exists to prevent. Reflection appears
/// only in the guard tests, where it is the checker rather than the mechanism.
/// </para>
/// </summary>
internal static partial class SystemSettingsRegistry
{
    /// <summary>Every descriptor, in the order the read DTO presents them.</summary>
    public static readonly IReadOnlyList<SystemSettingDescriptor> All =
    [
        ..SecurityToggles(),
        ..ImportExportCaps(),
        ..FileAnalysisPolicy(),
        ..TransactionalEmail(),
        ..RequestCaps(),
        ..ContractsSummaryWindows(),
        ..TuningConstants(),
        ..FileAnalysisKillSwitch(),
        ..PromptGuidance(),
        ..ContractSmartTags(),
        ..PropertySmartTags(),
        ..Weather(),
        ..ExchangeRateSync(),
        ..InstrumentPrices(),
        ..NewsReader(),
        ..NewsOpmlImport(),
        ..Podcasts(),
        ..FileObjectStorage(),
        ..BackgroundJobs(),
    ];

    /// <summary>Descriptors by <see cref="SystemSettingDescriptor.Key"/>.</summary>
    public static readonly IReadOnlyDictionary<string, SystemSettingDescriptor> ByKey =
        All.ToDictionary(descriptor => descriptor.Key, StringComparer.Ordinal);

    /// <summary>
    /// The default a missing row falls back to. Throws on an unknown key, exactly as the switch it
    /// replaced did — a key outside the registry is a programming error, not a runtime condition.
    /// </summary>
    public static string DefaultValueFor(string key) =>
        ByKey.TryGetValue(key, out var descriptor)
            ? descriptor.DefaultValue
            : throw new InvalidOperationException($"Unknown system setting key '{key}'.");

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Int(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // The two import/export factories exist only to keep the sixteen entries above readable — the caps
    // differ solely in key, field, default and claim, so spelling out sixteen full initialisers would
    // bury the one thing worth reading (which claim each carries). They return a single-element array
    // so the entries can sit inside the collection expression above via a spread.
    private static SystemSettingDescriptor[] Capacity(
        string key, string fieldName, string defaultValue,
        Func<SystemSettingsUpdate, CapacityLimit?> read, Action<SystemSettingsDto, int?> write) =>
    [
        new CapacitySetting
        {
            Key = key,
            FieldName = fieldName,
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = defaultValue,
            CacheKeyToEvict = ImportExportLimitsLookup.CacheKey,
            Read = read,
            Write = write,
        },
    ];

    // All eight megabyte caps share one bound pair, so the factory carries it rather than taking it
    // eight times: SystemSettingsBounds declares a per-key pair for each of them, and every one is
    // 1-1024. The per-key constants are what AC 22(a) asserts against the [Range]; this is where they
    // are consumed. Passing the pair per call would be eight identical arguments and one more place
    // for a transcription slip.
    private static SystemSettingDescriptor[] Megabytes(
        string key, string fieldName, int defaultValue,
        Func<SystemSettingsUpdate, int?> read, Action<SystemSettingsDto, int> write,
        int min = MegabytesMin, int max = MegabytesMax,
        Func<SystemSettingsDto, AdvisoryContext, string?>? advise = null) =>
    [
        new IntSetting
        {
            Key = key,
            FieldName = fieldName,
            RequiredClaim = PermissionClaims.SystemSettingsSecurityUpdate,
            DefaultValue = Int(defaultValue),
            CacheKeyToEvict = ImportExportLimitsLookup.CacheKey,
            Min = min,
            Max = max,
            Read = read,
            Write = write,
            Advise = advise,
        },
    ];

    /// <summary>
    /// The shared megabyte bound pair for the vCard size keys. Named off one of the per-key constants
    /// rather than a literal; the task, journal and calendar keys pass their own, wider pair, because their
    /// files carry attached files inline or stream. <c>SystemSettingsBoundsTests</c> asserts each descriptor against its own pair.
    /// </summary>
    private const int MegabytesMin = SystemSettingsBounds.ContactVCardMaxImportMegabytesMin;

    private const int MegabytesMax = SystemSettingsBounds.ContactVCardMaxImportMegabytesMax;
}
