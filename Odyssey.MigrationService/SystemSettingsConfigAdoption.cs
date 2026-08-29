using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Context;
using Odyssey.Dtos.Application;
using Odyssey.Dtos;

namespace Odyssey.MigrationService;

/// <summary>
/// Carries an operator's existing configuration into the settings store on upgrade (issue #421 Wave 2).
///
/// <para>
/// A migration cannot do this. <c>migrationBuilder.InsertData</c> is a compile-time constant, so it
/// seeds the shipped default and nothing else — but several migrated settings had live environment
/// plumbing (<c>EMAIL_FROM_ADDRESS</c> and <c>EMAIL_FROM_NAME</c> through Compose, <c>.env</c> and
/// Aspire). Without this step, an operator running with a configured sender identity would upgrade and
/// silently start sending as <c>no-reply@odyssey.local</c>: no error, no log, just different mail.
/// </para>
///
/// <para>
/// <strong>Ownership is decided by <c>UpdatedBy</c>, not by comparing values.</strong> The migration
/// seed writes no <c>UpdatedBy</c>; every write through the settings API sets one. So a null means "no
/// administrator has ever taken ownership of this setting", which is exactly the condition under which
/// deploy-time configuration should still win. Comparing the stored value against the default instead
/// cannot tell "never touched" from "an administrator deliberately set it back to the default" — and
/// would quietly overwrite the second one on every restart.
/// </para>
///
/// <para>
/// <strong>This runs in Production</strong>, unlike its neighbour <see cref="DemoDataSeeder"/>, which
/// seeds only in Development/Testing. The two look similar and are opposites: demo data must never reach a
/// real deployment, whereas preserving the behaviour a real deployment already had is precisely what
/// this is for. Do not copy the environment gate from next door.
/// </para>
///
/// <para>
/// It is a no-op once the values agree, so it is safe on every start. It deliberately does <em>not</em>
/// stamp <c>UpdatedBy</c> itself: leaving it null means configuration keeps being honoured until an
/// administrator takes over in the UI, and it keeps the "last changed by" line from attributing a
/// deploy-time adoption to a phantom user (an unresolvable id renders as "Unknown user").
/// </para>
/// </summary>
/// <remarks>
/// Takes <see cref="IServiceProvider"/> and opens its own scope, like every other
/// <see cref="IMigrationStep"/>. A constructor dependency on the scoped
/// <see cref="OdysseyContext"/> makes the container refuse to build at all — <c>Worker</c> is a
/// singleton <c>IHostedService</c>, so a scoped service cannot be reached from its graph — and the
/// failure is a startup crash of the migrations job, not a test failure, because a unit test that
/// news the class up never exercises the container.
/// </remarks>
public sealed class SystemSettingsConfigAdoption(
    IServiceProvider serviceProvider,
    IConfiguration configuration,
    ILogger<SystemSettingsConfigAdoption> logger) : ISystemSettingsConfigAdoption
{
    /// <summary>
    /// Settings whose value could have been supplied by configuration before it moved into the store,
    /// paired with the configuration key it came from. Only keys that genuinely had a config surface
    /// belong here — adopting one that never did would let a stray environment variable start
    /// overriding an administrator's setting.
    /// </summary>
    /// <param name="Validate">
    /// Rejects an adopted value the API itself would refuse. Adoption used to write <c>row.Value</c>
    /// unchecked, so an out-of-range environment value landed in the store and bypassed every bound a
    /// <c>PUT</c> enforces (issue #434 §9). Each entry validates against the <em>real</em>
    /// <see cref="SystemSettingsUpdate"/> data annotation for its field, so there is no second copy of
    /// the bounds to drift.
    /// </param>
    /// <param name="Audited">
    /// Whether an adoption of this key emits an audit line naming old and new. Set for the keys that
    /// carry <c>system-settings.security.update</c> on the API side, whose changes
    /// <c>SystemSettingDescriptor.AuditChanges</c> logs automatically — adoption writes OUTSIDE
    /// <c>SystemSettingsService</c>, so without this it would be the one path that can silently change
    /// an audited setting (issue #434 AC 29).
    /// </param>
    private readonly record struct AdoptableSetting(
        string SettingKey,
        string ConfigKey,
        Func<string, string?> Validate,
        bool Audited,
        Func<string, string?>? Convert = null);

    private static readonly AdoptableSetting[] Adoptable =
    [
        new(SystemSettingsKeys.EmailFromAddress, "Email:FromAddress",
            Text(nameof(SystemSettingsUpdate.EmailFromAddress), (u, v) => u.EmailFromAddress = v), Audited: true),
        new(SystemSettingsKeys.EmailFromName, "Email:FromName",
            Text(nameof(SystemSettingsUpdate.EmailFromName), (u, v) => u.EmailFromName = v), Audited: true),
        new(SystemSettingsKeys.EmailPerRecipientLimit, "Email:PerRecipientLimit",
            Number(nameof(SystemSettingsUpdate.EmailPerRecipientLimit), (u, v) => u.EmailPerRecipientLimit = v),
            Audited: true),
        new(SystemSettingsKeys.EmailPerRecipientWindowMinutes, "Email:PerRecipientWindowMinutes",
            Number(nameof(SystemSettingsUpdate.EmailPerRecipientWindowMinutes),
                (u, v) => u.EmailPerRecipientWindowMinutes = v),
            Audited: true),

        // The three FileAnalysis tuning keys retired from appsettings.json by issue #434. They are the
        // only three of the fifteen with an adoption entry: the other twelve were `const`s or POCO
        // defaults on a section with no configuration entry, so there was never a configured value to
        // carry over, and adopting one that never had a surface would let a stray environment variable
        // start overriding an administrator's saved setting.
        //
        // Adoption can only rescue a value the MIGRATIONS JOB can see, which is why these three needed
        // env plumbing added alongside (docker-compose, .env.example, AppHost). A value an operator
        // changed by editing the API's own appsettings.json is not adoptable by any mechanism — that
        // case is documented as a breaking change in the release notes instead.
        new(SystemSettingsKeys.FileAnalysisMaxTokens, "FileAnalysis:MaxTokens",
            Number(nameof(SystemSettingsUpdate.FileAnalysisMaxTokens), (u, v) => u.FileAnalysisMaxTokens = v),
            Audited: true),
        new(SystemSettingsKeys.FileAnalysisMatchMaxVocabulary, "FileAnalysis:Match:MaxVocabulary",
            Number(nameof(SystemSettingsUpdate.FileAnalysisMatchMaxVocabulary),
                (u, v) => u.FileAnalysisMatchMaxVocabulary = v),
            Audited: false),
        new(SystemSettingsKeys.FileAnalysisMatchTimeoutSeconds, "FileAnalysis:Match:TimeoutSeconds",
            Number(nameof(SystemSettingsUpdate.FileAnalysisMatchTimeoutSeconds),
                (u, v) => u.FileAnalysisMatchTimeoutSeconds = v),
            Audited: false),

        // The upload cap (issue #421 Wave 4). The one entry whose units differ on the two sides:
        // configuration held BYTES, the setting holds MEGABYTES, so this cannot be a verbatim copy the
        // way the four above are. Without it, an operator who had raised
        // FileStorage:MaxFileSizeBytes above 64 MB would upgrade into a silently *smaller* upload cap —
        // the setting seeds at the shipped 64 and, being the tighter of the two, would win.
        //
        // The configuration key itself is NOT retired: it keeps its startup role as the transport
        // ceiling, which is why this adopts its value rather than the key being deleted.
        //
        // No ceiling check is needed here even though the API validates one: the transport ceiling IS
        // FileStorage:MaxFileSizeBytes, and this value is derived from it by rounding down, so it can
        // never exceed it.
        // The file-analysis kill switch, model and destination (issue #439). All three had live
        // environment plumbing (FILE_ANALYSIS_ENABLED / _MODEL / _BASE_URL through Compose, .env and
        // Aspire), so without adoption an operator with analysis switched on and a model configured
        // would upgrade into the shipped defaults — analysis OFF at api.anthropic.com — silently.
        //
        // All three are Audited: they carry system-settings.security.update on the API side, and
        // adoption writes OUTSIDE SystemSettingsService, so without this it would be the one path able
        // to change an audited setting with no trace of what it used to be.
        new(SystemSettingsKeys.FileAnalysisEnabled, "FileAnalysis:Enabled",
            Boolean(nameof(SystemSettingsUpdate.FileAnalysisEnabled), (u, v) => u.FileAnalysisEnabled = v),
            Audited: true, Convert: CanonicalBoolean),
        new(SystemSettingsKeys.FileAnalysisModel, "FileAnalysis:Model",
            Text(nameof(SystemSettingsUpdate.FileAnalysisModel), (u, v) => u.FileAnalysisModel = v),
            Audited: true),
        // The shape validator runs here too, not just [StringLength]. Otherwise an http:// value in an
        // operator's .env would land in the store having bypassed the one rule that matters — and the
        // read path would then treat it as degraded and refuse every analysis.
        new(SystemSettingsKeys.FileAnalysisBaseUrl, "FileAnalysis:BaseUrl",
            BaseUrl(nameof(SystemSettingsUpdate.FileAnalysisBaseUrl), (u, v) => u.FileAnalysisBaseUrl = v),
            Audited: true,
            // Pass the ORIGINAL value through when the rule rejects it, so BaseUrl's validator is what
            // refuses it — with the message that names the rule — rather than the generic
            // "could not be converted to this setting's units" skip.
            Convert: candidate => FileAnalysisBaseUrlRule.Canonicalize(candidate) ?? candidate),

        new(SystemSettingsKeys.FileStorageMaxUploadMegabytes, "FileStorage:MaxFileSizeBytes",
            Number(nameof(SystemSettingsUpdate.FileStorageMaxUploadMegabytes),
                (u, v) => u.FileStorageMaxUploadMegabytes = v),
            Audited: true, Convert: BytesToWholeMegabytes),
    ];

    /// <summary>
    /// Validates a numeric candidate against the <c>[Range]</c> on the named
    /// <see cref="SystemSettingsUpdate"/> property. Returns null when acceptable, or the attribute's own
    /// error message — the one that names the bound and why it exists — when not.
    ///
    /// <para>
    /// The setter is an explicit delegate, never reflection over the property name: a renamed DTO
    /// property must break the build here exactly as it does in the API's registry, rather than quietly
    /// losing its bound check.
    /// </para>
    /// </summary>
    private static Func<string, string?> Number(string memberName, Action<SystemSettingsUpdate, int> assign) =>
        candidate =>
        {
            if (!int.TryParse(candidate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return "is not a whole number.";
            }

            var request = new SystemSettingsUpdate();
            assign(request, parsed);
            return ValidateMember(request, memberName);
        };

    /// <summary>
    /// Same, for a boolean-valued setting (issue #439). Accepts <c>true</c>/<c>false</c>
    /// case-insensitively and nothing else — an unparseable <c>FILE_ANALYSIS_ENABLED=yes</c> is logged
    /// and skipped, leaving the seeded value in place rather than writing something the read path would
    /// then treat as degraded and fail closed on.
    ///
    /// <para>
    /// The canonical lowercase form <c>BoolSetting.Format</c> stores is produced by
    /// <see cref="CanonicalBoolean"/> on the convert step, so <c>True</c> in an <c>.env</c> does not
    /// read as a change against a stored <c>true</c> on every restart.
    /// </para>
    /// </summary>
    private static Func<string, string?> Boolean(string memberName, Action<SystemSettingsUpdate, bool> assign) =>
        candidate =>
        {
            if (!bool.TryParse(candidate, out var parsed))
            {
                return "is not true or false.";
            }

            var request = new SystemSettingsUpdate();
            assign(request, parsed);
            return ValidateMember(request, memberName);
        };

    /// <summary>
    /// A string-valued setting whose shape matters as much as its length (issue #439): the
    /// <c>[StringLength]</c> on the DTO property <em>and</em> the shared
    /// <see cref="FileAnalysisBaseUrlRule"/> the <c>PUT</c> path enforces.
    ///
    /// <para>
    /// The rule is <em>the same code</em>, not a matching copy. It used to be hand-duplicated here,
    /// because this project does not reference <c>Odyssey.Api</c> and should not start doing so for one
    /// predicate — but a duplicated rule on the setting that decides which host receives the document
    /// and the API key is the one worth eliminating, so it moved to <c>Odyssey.Dtos</c> (zero
    /// project references, reachable from both halves) instead. Drift is now impossible rather than
    /// merely detected.
    /// </para>
    /// </summary>
    private static Func<string, string?> BaseUrl(string memberName, Action<SystemSettingsUpdate, string> assign) =>
        candidate =>
            Text(memberName, assign)(candidate)
            ?? FileAnalysisBaseUrlRule.Validate(candidate);

    /// <summary>
    /// Lowercases a parsed boolean to the form <c>BoolSetting.Format</c> stores.
    ///
    /// <para>
    /// An unparseable value passes through <em>unchanged</em> rather than becoming null. Null would
    /// skip with the generic "could not be converted to this setting's units" message and, worse, would
    /// mean <see cref="Boolean"/>'s rule could never fire — a decorative validator. Passing it through
    /// lets the validator reject it and say what the rule is.
    /// </para>
    /// </summary>
    private static string? CanonicalBoolean(string configured) =>
        bool.TryParse(configured, out var parsed) ? (parsed ? "true" : "false") : configured;

    /// <summary>Same, for a string-valued setting: the <c>[StringLength]</c> on its DTO property.</summary>
    private static Func<string, string?> Text(string memberName, Action<SystemSettingsUpdate, string> assign) =>
        candidate =>
        {
            var request = new SystemSettingsUpdate();
            assign(request, candidate);
            return ValidateMember(request, memberName);
        };

    private static string? ValidateMember(SystemSettingsUpdate request, string memberName)
    {
        var results = new List<ValidationResult>();
        var context = new ValidationContext(request) { MemberName = memberName };
        var value = typeof(SystemSettingsUpdate).GetProperty(memberName)!.GetValue(request);

        return Validator.TryValidateProperty(value, context, results)
            ? null
            : results[0].ErrorMessage ?? "is not an acceptable value.";
    }

    /// <summary>
    /// Bytes → whole megabytes, rounding <b>down</b> so adoption can never widen the cap beyond what
    /// was configured. Returns null for an unparseable or sub-megabyte value, which skips adoption and
    /// leaves the seeded default in place rather than storing a 0 that no upload could satisfy.
    /// </summary>
    private static string? BytesToWholeMegabytes(string configured)
    {
        if (!long.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes))
        {
            return null;
        }

        var megabytes = bytes / (1024 * 1024);
        return megabytes >= 1 ? megabytes.ToString(CultureInfo.InvariantCulture) : null;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        var adopted = 0;

        foreach (var (settingKey, configKey, validate, audited, convert) in Adoptable)
        {
            var configured = configuration[configKey];
            if (string.IsNullOrWhiteSpace(configured))
            {
                continue;
            }

            var row = await context.SystemSettings
                .FirstOrDefaultAsync(setting => setting.Key == settingKey, cancellationToken);

            if (row is null)
            {
                // The migration seeds every known key, so this means the migration has not run yet or
                // the key is unknown to this build. Either way, inventing a row here would be worse
                // than leaving it to the seed.
                logger.LogWarning(
                    "Skipping config adoption for '{SettingKey}': no row exists yet.", settingKey);
                continue;
            }

            if (row.UpdatedBy is not null)
            {
                // An administrator owns this setting now. Configuration no longer applies, whatever
                // it says — including when it happens to agree.
                continue;
            }

            var trimmed = convert is null ? configured.Trim() : convert(configured.Trim());
            if (trimmed is null)
            {
                logger.LogWarning(
                    "Skipping config adoption for '{SettingKey}': the configured value of '{ConfigKey}' "
                    + "could not be converted to this setting's units.", settingKey, configKey);
                continue;
            }

            if (string.Equals(row.Value, trimmed, StringComparison.Ordinal))
            {
                continue;
            }

            // Validate BEFORE writing. Without this, an out-of-range environment value would land in the
            // store and bypass every bound a PUT enforces — and the bound is the whole mechanism for the
            // three single-direction keys.
            if (validate(trimmed) is { } problem)
            {
                logger.LogWarning(
                    "Rejecting config adoption for '{SettingKey}': the value configured at '{ConfigKey}' {Problem} "
                    + "Leaving the seeded default in place.", settingKey, configKey, problem);
                continue;
            }

            logger.LogInformation(
                "Adopting configured value for '{SettingKey}' from '{ConfigKey}'; it had never been changed "
                + "by an administrator.", settingKey, configKey);

            // Adoption writes outside SystemSettingsService, so the derived AuditChanges path never runs
            // for it — this is the one path that could otherwise change a security-claim setting with no
            // trace of what it used to be (issue #434 AC 29).
            if (audited)
            {
                logger.LogInformation(
                    "System settings security-claim change by config adoption: {Field} {OldValue} -> {NewValue}.",
                    settingKey, row.Value, trimmed);
            }

            row.Value = trimmed;
            adopted++;
        }

        if (adopted > 0)
        {
            await context.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Adopted {Count} configured system setting(s).", adopted);
        }
    }
}
