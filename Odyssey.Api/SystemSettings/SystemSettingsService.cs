using Odyssey.Dtos.Application;
using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Odyssey.Api.Identity;
using Odyssey.Context;
using Odyssey.Core;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;

namespace Odyssey.Api.SystemSettings;

/// <summary>
/// CRUD for the admin-configurable runtime settings store (issue #349, extended by issue #343 and a
/// follow-up with sixteen import/export volume caps): a pure, side-effect-free <see cref="GetAsync"/>
/// assembled from the migration-seeded <see cref="SystemSetting"/> rows, and a partial-safe
/// <see cref="UpdateAsync"/> where every field on <see cref="SystemSettingsUpdate"/> is nullable —
/// <see langword="null"/> means "leave unchanged": no claim check, no validation, no touch to that
/// key's row. A non-null value means "set this field", gated per-field by whichever write claim it
/// belongs to (see §7/§10 of issue #349, §6/§10 of issue #343).
///
/// <para>
/// Per-field behaviour is declared once in <see cref="SystemSettingsRegistry"/> rather than spread
/// across five parallel blocks here (issue #421 Wave 0). What remains hand-written in this class is
/// only what is genuinely not per-field: the export ≤ import round-trip rule, whose error code is a
/// wire contract the client switches on, and the read DTO's server-set
/// <see cref="SystemSettingsDto.TwoFactorEnforced"/> and last-writer summary.
/// </para>
/// </summary>
public sealed class SystemSettingsService(
    OdysseyContext context,
    IMemoryCache cache,
    TimeProvider timeProvider,
    IUserDisplayNameResolver displayNames,
    RequestCapCeilings ceilings,
    SecretSettingsService secrets,
    ILogger<SystemSettingsService> logger)
{
    // Shared with SystemSettingsLookup, which reads the same two cosmetic/policy fields under this
    // key with a 30s TTL — a PUT that actually changes either one evicts it immediately so the
    // writing instance never serves its own stale value.
    internal const string InsuranceCacheKey = "system-settings:insurance-policy-settings";

    /// <summary>
    /// The finance-side per-request caps (issue #421 Wave 3). Its own key, not folded into
    /// <see cref="InsuranceCacheKey"/>: sharing one entry would make a contracts change evict the
    /// insurance settings and vice versa, which is the mistake that argued against reusing an existing
    /// lookup for the file-analysis settings too.
    /// </summary>
    internal const string FinanceCapsCacheKey = "system-settings:finance-request-caps";

    /// <summary>
    /// The subscriptions summary limits (issue #437). Its own key for the same reason
    /// <see cref="FinanceCapsCacheKey"/> has one: <see cref="SystemSettingDescriptor.CacheKeyToEvict"/>
    /// is a single string, so a shared entry would make a subscriptions change evict the insurance
    /// settings and vice versa.
    /// </summary>
    internal const string SubscriptionCacheKey = "system-settings:subscription-settings";

    /// <summary>
    /// One log line per faulted settings key per window, rather than one per request on an endpoint
    /// with no rate limiter (issue #437 §11, AC 28). Per <em>key</em>, so a corrupt insurance row
    /// cannot consume the subscriptions fault's line.
    /// </summary>
    private static readonly TimeSpan ProjectionLogThrottle = TimeSpan.FromSeconds(30);

    private const string ProjectionLogMarkerPrefix = "system-settings:projection-logged:";

    /// <summary>Never writes — a missing row for a known key (should not happen post-migration) falls back to its documented default.</summary>
    public async Task<SystemSettingsDto> GetAsync(ClaimsPrincipal caller, CancellationToken cancellationToken = default)
    {
        var rows = await context.SystemSettings.AsNoTracking().ToListAsync(cancellationToken);
        return await AssembleAsync(caller, rows, cancellationToken);
    }

    public async Task<SystemSettingsDto> UpdateAsync(
        ClaimsPrincipal caller,
        string actorUserId,
        SystemSettingsUpdate request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);

        // Phase 1 — validate every present field's claim BEFORE touching any entity. A caller lacking
        // the claim for one field is rejected wholesale (403 naming that field); nothing from the
        // request is persisted, including any other present field it did have permission for.
        //
        // Presence is decided by SystemSettingDescriptor.IsPresent, which keys off the OBJECT, never
        // an inner `.Value` — that is what makes issue #343's sec-F3 bug unmakeable here: a caller
        // lacking the claim cannot set a count cap to unlimited by sending only { "unlimited": true }.
        foreach (var descriptor in SystemSettingsRegistry.All)
        {
            if (!descriptor.IsPresent(request))
            {
                continue;
            }

            if (!caller.HasClaim(PermissionClaims.Type, descriptor.RequiredClaim))
            {
                throw new SystemSettingsForbiddenException(
                    $"Setting '{descriptor.FieldName}' requires the '{descriptor.RequiredClaim}' claim.");
            }
        }

        // Phase 1b — per-field shape validation (today: exactly one of unlimited/value on each
        // CapacityLimit, issue #343 §9). A violation is a 400 naming the field; nothing persists.
        foreach (var descriptor in SystemSettingsRegistry.All)
        {
            if (descriptor.IsPresent(request))
            {
                descriptor.Validate(request, ceilings);
            }
        }

        // Phases 1c and 2 run inside ONE transaction, unconditionally (issue #8 §5.8). The reason is
        // G4/G7: changing the SMTP host, or turning STARTTLS off, must clear the stored relay
        // credential, and the two writes have to land together or not at all. If they can interleave,
        // an interruption leaves the new host live with the old credential still stored — the exploit
        // G4 exists to close, reached without the attacker ever needing the credential themselves.
        //
        // UNCONDITIONALLY rather than "only when a clearing trigger is present": a pre-check would
        // have to re-derive "is this a clearing change?" ahead of the loop that already computes
        // valueChanged, and G7's trigger is direction-sensitive (only true → false clears), so the
        // pre-check and the loop could drift. Wrapping always lets the staging piggyback on the loop's
        // own computation.
        //
        // The CreateExecutionStrategy wrapper is mandatory, not decoration: EnableRetryOnFailure is
        // configured on this context (DatabaseExtension), and a retrying strategy refuses a
        // user-initiated transaction, so a bare BeginTransactionAsync throws. The whole sequence —
        // reading the rows, applying the descriptor writes, staging the secret removals, composing
        // the audit records — sits inside the delegate; a read of this context from outside while the
        // transaction is open is the failure mode this shape exists to prevent.
        //
        // Nothing is LOGGED inside. Every audit line, the settings ones included, is emitted after the
        // commit, so a rolled-back write cannot leave a line asserting a change that never landed.
        // (Before issue #8 the settings line was written before commit; there was no transaction then,
        // so it could not be wrong — under one, it could.)
        var rows = new Dictionary<string, SystemSetting>(StringComparer.Ordinal);
        var auditLines = new List<PendingAudit>();
        var stagedClears = new List<StagedSecretClear>();
        var cacheKeysToEvict = new HashSet<string>(StringComparer.Ordinal);
        var attempt = 0;

        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            // The delegate can run more than once. Everything it consumes is rebuilt here rather than
            // captured from before the strategy, and the tracker is reset on a RETRY so a failed
            // attempt's staged deletes and mutated rows cannot ride along into the next one. Only on
            // a retry: clearing on the first pass would discard whatever the surrounding scope was
            // legitimately tracking.
            if (attempt++ > 0)
            {
                context.ChangeTracker.Clear();
            }

            rows.Clear();
            auditLines.Clear();
            stagedClears.Clear();
            cacheKeysToEvict.Clear();

            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            await ApplyAsync(
                request, actorUserId, rows, auditLines, stagedClears, cacheKeysToEvict, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        });

        // AFTER the commit, in both directions: a rolled-back settings write leaves no line claiming
        // it changed, and a rolled-back clear leaves no line claiming a credential was removed.
        foreach (var line in auditLines)
        {
            logger.LogInformation(
                "System settings security-claim change by {ActorUserId}: {Field} {OldValue} -> {NewValue}.",
                actorUserId, line.Key, line.OldValue, line.NewValue);
        }

        foreach (var staged in stagedClears)
        {
            secrets.AuditStagedClear(actorUserId, staged);
        }

        // Invalidate synchronously on the writing instance the moment the PUT commits — bounds
        // cross-instance staleness to the 30s TTL without this instance ever serving its own stale
        // read back to the admin who just changed it.
        foreach (var cacheKey in cacheKeysToEvict)
        {
            cache.Remove(cacheKey);
        }

        return await AssembleAsync(caller, rows.Values, cancellationToken);
    }

    /// <summary>
    /// One audit line, composed inside the transaction and emitted after it commits. It carries the
    /// PROJECTED values — the projection runs where the old and new strings are still in hand, so a
    /// line can never be composed from an unprojected one by a later edit at the emit site.
    /// </summary>
    private sealed record PendingAudit(string Key, string OldValue, string NewValue);

    /// <summary>
    /// The write phase, extracted so the whole of it — not just <c>BeginTransactionAsync</c> — sits
    /// inside the execution strategy's delegate.
    ///
    /// <para>
    /// It must be safe to run more than once against a change tracker the caller has reset. It reads
    /// the rows it needs itself and writes nothing outside the collections it is handed, all of which
    /// the caller clears before each attempt.
    /// </para>
    /// </summary>
    private async Task ApplyAsync(
        SystemSettingsUpdate request,
        string actorUserId,
        Dictionary<string, SystemSetting> rows,
        List<PendingAudit> auditLines,
        List<StagedSecretClear> stagedClears,
        HashSet<string> cacheKeysToEvict,
        CancellationToken cancellationToken)
    {
        // Rows are fetched once, ahead of both the round-trip validation (Phase 1c, which evaluates
        // post-write state) and the mutation (Phase 2) below.
        foreach (var row in await context.SystemSettings.ToListAsync(cancellationToken))
        {
            rows[row.Key] = row;
        }

        // Phase 1c — the export/import round-trip rule (issue #343 §9): for each pair, the post-write
        // export cap must not exceed the post-write import cap, with unlimited treated as +∞ on both
        // sides. Evaluated against (requested ?? currently stored), so a PUT touching only one side is
        // validated against what the other side actually is. A violation is a 400 naming both fields
        // and both effective values; nothing persists. Applies only to the record-count pairs — the
        // size caps have no such rule (they sit behind the stricter security claim for a different
        // reason: import size bounds a real DoS surface, export size doesn't, so there's no safety
        // argument for forcing export-size <= import-size the way there is for record counts).
        //
        // Deliberately NOT registry-driven: it is a cross-field rule, not a per-field one, and its
        // error code `system-settings.invalid.round-trip.<pair>` is a wire contract the client
        // switches on. Future cross-field rules join it here.
        ValidateRoundTrip(
            rows, SystemSettingsKeys.ContactVCardMaxExportRows, SystemSettingsKeys.ContactVCardMaxImportEntries,
            request.ContactVCardMaxExportRows, request.ContactVCardMaxImportEntries, "contacts");
        ValidateRoundTrip(
            rows, SystemSettingsKeys.CalendarIcsMaxExportEvents, SystemSettingsKeys.CalendarIcsMaxImportEvents,
            request.CalendarIcsMaxExportEvents, request.CalendarIcsMaxImportEvents, "calendars");
        ValidateRoundTrip(
            rows, SystemSettingsKeys.TaskIcsMaxExportTasks, SystemSettingsKeys.TaskIcsMaxImportTasks,
            request.TaskIcsMaxExportTasks, request.TaskIcsMaxImportTasks, "tasks");
        ValidateRoundTrip(
            rows, SystemSettingsKeys.JournalIcsMaxExportRows, SystemSettingsKeys.JournalIcsMaxImportEntries,
            request.JournalIcsMaxExportRows, request.JournalIcsMaxImportEntries, "journal-entries");

        // Phase 2 — every claim/shape/round-trip check passed; now mutate. Not a Mapster Adapt call:
        // IgnoreNullValues defaults to false, so a bare Adapt would write false/0 for every null
        // source field, silently disabling both auth-perimeter gates on a cosmetic-only admin's
        // insurance-only save.
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // `anyPresent` decides whether to call SaveChanges at all, and is presence-based rather than
        // change-based — matching the pre-registry behaviour exactly. A GET→PUT round trip of
        // unchanged data therefore still calls SaveChanges, which correctly emits no UPDATE.
        var anyPresent = false;

        // Every trigger this save trips, in registry order. A LIST rather than a first-match: one
        // save can change the relay and turn STARTTLS off together, and reporting only the first
        // would under-state why the credential went in the one record that explains it.
        var clearTriggers = new List<string>();

        foreach (var descriptor in SystemSettingsRegistry.All)
        {
            if (!descriptor.IsPresent(request))
            {
                continue;
            }

            anyPresent = true;

            var row = GetOrCreateRow(rows, descriptor.Key, now);
            var oldValue = row.Value;
            var newValue = descriptor.Format(request);
            var valueChanged = !string.Equals(oldValue, newValue, StringComparison.Ordinal);

            // The five original #349 keys bump UpdatedAt whenever the field is present; every key
            // added since bumps it only on an actual change. Preserved verbatim — see the remarks on
            // TouchOnPresenceOnly, and ImportExportSettingsApiTests' GET→PUT no-op assertion.
            if (valueChanged || descriptor.TouchOnPresenceOnly)
            {
                row.Value = newValue;
                row.UpdatedAt = now;
                row.UpdatedBy = actorUserId;

                if (descriptor.CacheKeyToEvict is { } cacheKey)
                {
                    cacheKeysToEvict.Add(cacheKey);
                }
            }

            // Only an actual change is audited, so a full-permission admin's routine resave (which
            // resends every field it can edit, per the client's whole-resource-replace contract)
            // doesn't spam the log.
            if (descriptor.AuditChanges && valueChanged)
            {
                // Projected before the line is composed, not after (issue #439 §5.3b). The OLD value
                // here is whatever was stored — the write validator never saw it — so for the
                // file-analysis base URL, the SMTP host and the client base URL it can carry
                // `https://key:secret@host` planted by a restore. The projection reduces both ends to
                // their host, keeping the change reconstructable without letting the line carry a
                // credential. Every other setting logs verbatim (null projection).
                var project = descriptor.AuditProjection;
                auditLines.Add(new PendingAudit(
                    descriptor.Key,
                    project is null ? oldValue : project(oldValue),
                    project is null ? newValue : project(newValue)));
            }

            // G4/G7. Computed off the loop's own valueChanged rather than re-derived up front — see
            // the remarks above the transaction. Several triggers can fire; they still stage ONE
            // removal per secret, because the rows are the same rows.
            if (RelayCredentialClearTrigger(descriptor.Key, newValue, valueChanged) is { } trigger)
            {
                clearTriggers.Add(trigger);
            }
        }

        // Staged INSIDE the transaction, audited outside it. Nothing here decides whether the clear is
        // allowed — that was settled by the claim check on the triggering field in Phase 1, and a
        // clear the caller could decline would leave the credential live on a host it was never
        // entered for (see SecretSettingsService.StageClearAsync).
        if (clearTriggers.Count > 0)
        {
            var reason = SecretSettingsService.ComposeClearReason(clearTriggers);

            stagedClears.Add(
                await secrets.StageClearAsync(context, SecretSettingKeys.EmailUsername, reason, cancellationToken));
            stagedClears.Add(
                await secrets.StageClearAsync(context, SecretSettingKeys.EmailPassword, reason, cancellationToken));
            anyPresent = true;
        }

        if (anyPresent)
        {
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Whether this key's change is one that must take the stored relay credential with it, and under
    /// which audit fragment (issue #8 G4/G7).
    ///
    /// <para>
    /// <strong>A targeted branch keyed on the three triggering keys, not a general post-write event
    /// bus.</strong> The same reasoning that makes registry accessors explicit delegates rather than
    /// reflection applies here: a side-effect seam wide enough to register anything on is a seam
    /// wide enough to lose a security consequence in.
    /// </para>
    ///
    /// <para>
    /// <strong>What the three have in common</strong> is that each one changes where the credential
    /// goes or how it travels, and the SMTP client connects before it authenticates — so the relay on
    /// the far end receives the stored credential whatever it turns out to be.
    /// </para>
    ///
    /// <para>
    /// Two of the three are DIRECTIONAL, and getting either backwards reopens what it closes; the
    /// third deliberately is not.
    /// </para>
    /// <list type="bullet">
    /// <item><strong>Host</strong> clears only when it moves to a DIFFERENT, NON-EMPTY value.
    /// Re-saving the same host is not a change (AC 5) and must not cost the administrator their
    /// credential; clearing the host to empty turns mail off, so there is no new relay to reach.</item>
    /// <item><strong>Port</strong> clears on ANY change, with no direction to respect — unlike the
    /// host there is no "off" port, so every value is a live endpoint and every change moves the
    /// credential to a different listener. A port change usually rides along with a STARTTLS switch
    /// (587 → 465), which already cleared; this covers the case where it does not, including a
    /// listener the attacker controls on a port of a host that is otherwise legitimate.</item>
    /// <item><strong>STARTTLS</strong> clears only on true → false. false → true is a strengthening,
    /// and false → false is not a change at all (AC 3b).</item>
    /// </list>
    ///
    /// <para>
    /// The port trigger goes beyond issue #8's G4, which named the host alone. It was added from the
    /// PR security review: the goal G4 states — a credential is never presented to a relay it was not
    /// entered for — is about an ENDPOINT, and the host is only half of one.
    /// </para>
    /// </summary>
    private static string? RelayCredentialClearTrigger(string key, string newValue, bool valueChanged)
    {
        if (!valueChanged)
        {
            return null;
        }

        if (key == SystemSettingsKeys.EmailSmtpHost)
        {
            return newValue.Length > 0 ? SecretSettingsService.HostChangedTrigger : null;
        }

        if (key == SystemSettingsKeys.EmailSmtpPort)
        {
            return SecretSettingsService.PortChangedTrigger;
        }

        if (key == SystemSettingsKeys.EmailUseStartTls)
        {
            return newValue == "false" ? SecretSettingsService.StartTlsOffTrigger : null;
        }

        return null;
    }

    /// <summary>
    /// Runs every descriptor's <see cref="SystemSettingDescriptor.Advise"/> delegate against the
    /// freshly-projected read DTO (issue #434 §5). Keyed by <c>FieldName</c> — the same join key
    /// <c>ApiProblem.Errors</c> uses, so the client's existing field→row lookup works unchanged.
    ///
    /// <para>
    /// <strong>An advisory can never fail a request.</strong> A delegate that throws is swallowed and
    /// logged at Debug and that one advisory is omitted; the response is still the same <c>200</c> with
    /// the same values. That is the whole reason this is a separate channel from <c>errors</c>, and it
    /// is why the try/catch is per-delegate rather than around the loop.
    /// </para>
    /// </summary>
    private IReadOnlyDictionary<string, string> BuildWarnings(SystemSettingsDto dto)
    {
        // Nothing to build it from any more (issue #439 §5): the base URL the processor-correspondence
        // advisory needs is a SETTING now, so that delegate reads it off the DTO like every other
        // value. The type stays as the advisory parameter — it is the designed extension point that
        // keeps advisories pure, synchronous and unable to reach a DbContext, HttpContext or a secret.
        var context = AdvisoryContext.Empty;
        var warnings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var descriptor in SystemSettingsRegistry.All)
        {
            if (TryAdvise(descriptor, dto, context, logger) is { } message)
            {
                warnings[descriptor.FieldName] = message;
            }
        }

        return warnings;
    }

    /// <summary>
    /// Evaluates one descriptor's advisory, swallowing anything it throws. Returns the advisory text,
    /// or <see langword="null"/> when there is none — including when the delegate threw.
    ///
    /// <para>
    /// Extracted from <see cref="BuildWarnings"/> so the swallow is <em>reachable from a test</em>. The
    /// advisories themselves are declared in a static registry a test cannot substitute into, and every
    /// shipped delegate is defensively coded, so a test driving the HTTP surface can only ever show that
    /// the delegates in use today do not throw — it would pass with this <c>catch</c> deleted. That is
    /// exactly the vacuous shape this seam exists to avoid.
    /// </para>
    ///
    /// <para>
    /// <c>internal</c> rather than private for the same reason, and the try/catch is per-delegate rather
    /// than around the loop so one bad advisory omits only itself.
    /// </para>
    /// </summary>
    internal static string? TryAdvise(
        SystemSettingDescriptor descriptor, SystemSettingsDto dto, AdvisoryContext context, ILogger logger)
    {
        if (descriptor.Advise is not { } advise)
        {
            return null;
        }

        try
        {
            return advise(dto, context);
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                exception, "Advisory for system setting '{Key}' threw; omitting it.", descriptor.Key);
            return null;
        }
    }

    private void ValidateRoundTrip(
        Dictionary<string, SystemSetting> rows, string exportKey, string importKey,
        CapacityLimit? exportRequest, CapacityLimit? importRequest, string pairName)
    {
        var exportEffective = EffectiveCount(rows, exportKey, exportRequest);
        var importEffective = EffectiveCount(rows, importKey, importRequest);

        if (importEffective is { } importValue && (exportEffective is null || exportEffective > importValue))
        {
            throw new DomainValidationException(
                $"Export limit ({Describe(exportEffective)}) must not exceed the import limit "
                + $"({Describe(importEffective)}), or an exported file could not be imported back.",
                $"system-settings.invalid.round-trip.{pairName}");
        }
    }

    private static int? EffectiveCount(Dictionary<string, SystemSetting> rows, string key, CapacityLimit? request)
    {
        if (request is not null)
        {
            return request.Unlimited ? null : request.Value;
        }

        return SystemSettingsKeys.ParseCount(
            rows.TryGetValue(key, out var row) ? row.Value : SystemSettingsRegistry.DefaultValueFor(key));
    }

    private static string Describe(int? value) =>
        value is { } finite ? finite.ToString(CultureInfo.InvariantCulture) : "no limit";

    // Should never happen post-migration (Key is seeded for every known key) — a defensive
    // create-with-default rather than throwing, matching GetAsync's fallback behavior for reads.
    private SystemSetting GetOrCreateRow(Dictionary<string, SystemSetting> rows, string key, DateTime now)
    {
        if (rows.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var row = new SystemSetting { Key = key, Value = SystemSettingsRegistry.DefaultValueFor(key), UpdatedAt = now };
        context.SystemSettings.Add(row);
        rows[key] = row;
        return row;
    }

    /// <summary>
    /// The administrator-facing sentence for one projection fault (issue #437 §11).
    ///
    /// <para>
    /// <strong>Two conditions, two sentences.</strong> For a clamped row the value <em>was</em> read —
    /// it parsed, fell outside its pair, and is classed "clamped, reported, not degraded" — so telling
    /// the administrator it could not be read is false, and it omits the fact that matters: the number
    /// on the row is not the number they stored.
    /// </para>
    ///
    /// <para>
    /// The two verbs are deliberately asymmetric, and the asymmetry is load-bearing.
    /// <c>Unreadable</c> claims only what the ROW SHOWS, because the lookup resolves an unparseable row
    /// to <c>min(last-known-good, shipped default)</c> while this projection resolves it to
    /// <c>DefaultValue</c> — during a last-known-good window the engine and the row can legitimately
    /// disagree. <c>Clamped</c> claims BEHAVIOUR, because both sites clamp against the same pair. A
    /// copy edit to "…so the shipped default <em>is used</em>" makes the first sentence false.
    /// </para>
    ///
    /// <para>
    /// <strong>The stored value is never echoed</strong> (§10 item 2): the clamped sentence names the
    /// bound pair and the EFFECTIVE value, none of which is what was stored. And the range is written
    /// out in words rather than with an en dash — at default punctuation levels NVDA and JAWS speak
    /// neither <c>–</c> nor <c>-</c>, so "1 50" would be ambiguous with a list, on the one clause that
    /// exists to be acted on.
    /// </para>
    ///
    /// <para>
    /// No title prefix: there is no server-side title to interpolate, and the row renders this inside
    /// its own titled card, already prefixed with "Advisory —". The client's announcement adds the
    /// title and a kind-tag, and never carries this predicate.
    /// </para>
    /// </summary>
    internal static string ProjectionAdvisory(
        SystemSettingDescriptor descriptor, ProjectionOutcome outcome, string storedValue)
    {
        // Only IntSetting can report Clamped, so the pattern below is exhaustive rather than defensive.
        if (outcome != ProjectionOutcome.Clamped || descriptor is not IntSetting bounded)
        {
            return "The stored value couldn't be read, so the shipped default is shown.";
        }

        // The effective value is re-derived from the stored string and the descriptor's own pair — the
        // same two inputs Project used — rather than read back off the DTO by property name. Reflection
        // here would silently start reporting the wrong number the moment a DTO property was renamed,
        // which is the failure class the registry's explicit accessors exist to close.
        var effective = int.TryParse(storedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, bounded.Min, bounded.Max)
            : bounded.Min;

        return $"The stored value was outside the allowed range of {bounded.Min.ToString(CultureInfo.InvariantCulture)} "
            + $"to {bounded.Max.ToString(CultureInfo.InvariantCulture)} and is being read as "
            + $"{effective.ToString(CultureInfo.InvariantCulture)}.";
    }

    /// <summary>
    /// One line per faulted key per 30-second window. The throttle marker lives in
    /// <see cref="IMemoryCache"/> — this endpoint has no rate limiter, so an unthrottled line would be
    /// one per request for as long as the row stays corrupt.
    ///
    /// <para>
    /// <strong>One level per condition, across both read sites</strong> (AC 31): an unparseable value
    /// is an error, an out-of-bound value a warning. The lookup logs the same two conditions at the
    /// same two levels.
    /// </para>
    /// </summary>
    private void LogProjectionFault(SystemSettingDescriptor descriptor, ProjectionOutcome outcome)
    {
        var marker = ProjectionLogMarkerPrefix + descriptor.Key;
        if (cache.TryGetValue(marker, out _))
        {
            return;
        }

        cache.Set(marker, true, ProjectionLogThrottle);

        // The VALUE is never logged, for the same reason it is never echoed to the caller.
        if (outcome == ProjectionOutcome.Unparseable)
        {
            logger.LogError(
                "The stored system setting '{Key}' could not be parsed; projecting its shipped default.",
                descriptor.Key);
        }
        else
        {
            logger.LogWarning(
                "The stored system setting '{Key}' is outside its allowed range; projecting the nearer bound.",
                descriptor.Key);
        }
    }

    // Resolves UpdatedByDisplayName here (not in the controller): SystemSettingsService lives inside
    // Odyssey.Api and can depend on Odyssey.Api.Identity directly, the same way UserAdministrationService
    // resolves display names internally rather than leaving it to its controller.
    private async Task<SystemSettingsDto> AssembleAsync(
        ClaimsPrincipal caller, IReadOnlyCollection<SystemSetting> rows, CancellationToken cancellationToken)
    {
        var byKey = rows.ToDictionary(row => row.Key, row => row);
        var dto = new SystemSettingsDto
        {
            // Always false. A machine-readable sibling to RequireTwoFactor so no integration or
            // compliance export can misread the stored toggle as an active control — there is no
            // org-wide 2FA enforcement in this feature. Server-set, so not a registry entry.
            TwoFactorEnforced = false,

            // Hard ceilings on the two tighten-only photo caps (issue #421 Wave 3). Server-computed
            // from the compile-time constants that also drive [MaxLength] on the photo request DTOs,
            // so the client can bound its control instead of offering a value the API will reject.
            PhotoMaxLinksPerKindCeiling = RequestCapCeilings.PhotoLinksPerKind,
            PhotoMaxAlbumMembersCeiling = RequestCapCeilings.PhotoAlbumMembers,

            // The upload cap's ceiling is startup configuration, not a constant (issue #421 Wave 4):
            // Kestrel's request-body limit is fixed from FileStorage:MaxFileSizeBytes and cannot be
            // raised per-request, so a larger setting would be refused by the transport.
            UploadMegabytesCeiling = ceilings.UploadMegabytes,

            // The six issue #434 bound projections. Unlike the three above, these are NOT backed by a
            // RequestCapCeilings validator — their bound is the [Range] on SystemSettingsUpdate, which
            // model validation applies first (§9). They are published anyway because a WebAssembly
            // client cannot read a server attribute, and the control still has to bound itself; three
            // of them are the pinned end of a single-direction key, named by reference so the seed, the
            // attribute and the control cannot drift apart.
            CalendarIcsMaxAggregateExportRowsCeiling = 40_000,
            CalendarIcsMaxAggregateOccurrencesCeiling = 20_000,
            PhotoMetadataReadMegabytesCeiling = 16,
            RecurrenceMaxGeneratedOccurrencesCeiling = SystemSettingsDefaults.RecurrenceMaxGeneratedOccurrences,
            ContactVCardMaxRepeatablePropertiesPerEntryCeiling =
                SystemSettingsDefaults.ContactVCardMaxRepeatablePropertiesPerEntry,
            EmailMaxTrackedRecipientsFloor = SystemSettingsDefaults.EmailMaxTrackedRecipients,
        };

        // The projection outcomes (issue #437 Goals 9 and 12). Project no longer throws on row content,
        // so a corrupt row of ANY kind now yields a 200 with that key's default projected instead of a
        // 500 from GET — and, because UpdateAsync returns through this same method, instead of a 500
        // from a PUT that had already committed.
        var faults = new Dictionary<string, SettingFaultKind>(StringComparer.OrdinalIgnoreCase);
        var faultDetails = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var descriptor in SystemSettingsRegistry.All)
        {
            var stored = byKey.TryGetValue(descriptor.Key, out var row) ? row.Value : descriptor.DefaultValue;
            var outcome = descriptor.Project(stored, dto);
            if (outcome == ProjectionOutcome.Ok)
            {
                continue;
            }

            faults[descriptor.FieldName] = outcome == ProjectionOutcome.Unparseable
                ? SettingFaultKind.Unreadable
                : SettingFaultKind.Clamped;
            faultDetails[descriptor.FieldName] = ProjectionAdvisory(descriptor, outcome, stored);
            LogProjectionFault(descriptor, outcome);
        }

        dto.ProjectionFaults = faults;

        // The one site where the Project loop's outcomes meet BuildWarnings' output, and therefore the
        // one place that applies §11's precedence rule. It is a real merge, not a phrasing detail:
        // BuildWarnings writes through an indexer and its result used to REPLACE dto.Warnings
        // wholesale, so neither an added entry nor a second writer survived without this step.
        //
        // Precedence: a non-Ok projection outcome REPLACES any Advise output for that field. The cost
        // advisory describes a value the administrator chose and can re-derive from the number in front
        // of them; the projection advisory reports a fault they did not cause and which is visible
        // nowhere else in the product. The collision is not hypothetical — a SubscriptionMaxSummary-
        // Renewals row stored as "5000" clamps to 50, and 50 is above the shipped default of 6, so it
        // fires both.
        var warnings = new Dictionary<string, string>(BuildWarnings(dto), StringComparer.OrdinalIgnoreCase);
        foreach (var (field, message) in faultDetails)
        {
            warnings[field] = message;
        }

        dto.Warnings = warnings;

        // The single most recent change across ALL keys (not per-key) — a deliberate v1
        // simplification for the header's one summary line (issue #349 §3).
        var mostRecent = byKey.Values.OrderByDescending(row => row.UpdatedAt).FirstOrDefault();
        dto.UpdatedAt = mostRecent?.UpdatedAt ?? default;
        dto.UpdatedBy = mostRecent?.UpdatedBy;
        dto.UpdatedByDisplayName = await displayNames.ResolveAsync(caller, mostRecent?.UpdatedBy, cancellationToken);

        return dto;
    }
}
