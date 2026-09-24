using System.Globalization;
using Odyssey.Core;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ContextInterval = Odyssey.Context.Interval;
using ContextTermValueUnit = Odyssey.Context.TermValueUnit;
using ContextTermDirection = Odyssey.Context.TermDirection;

namespace Odyssey.Core.Finance;

/// <summary>
/// Business logic for time-versioned terms (rates and the prices of named charges) on a
/// <b>contract</b>, the only owner a term has since issue #190 moved every account-owned term onto a
/// contract. Enforces label and value/unit/currency validation, and resolves the currently-effective
/// value of each SERIES by implicit supersession (latest <c>EffectiveFrom</c> on or before a date).
///
/// <para>
/// A series is <c>(ContractId, LabelKey)</c>. A contract can hold several concurrently in-force terms
/// told apart by a user-authored label, and supersession happens strictly within a label — a rise in
/// the foreign ATM charge is not a change to the domestic one.
/// </para>
/// </summary>
public class TermService
{
    private readonly OdysseyContext context;
    private readonly TimeProvider timeProvider;
    private readonly ISystemSettingsLookup? systemSettingsLookup;
    private readonly ILogger<TermService> logger;

    /// <param name="systemSettingsLookup">
    /// Source of the per-contract term cap. Optional so a direct construction in a unit test need not
    /// supply one; when it is absent the cap resolves to the shipped default, which is the same value
    /// a healthy absent settings row would resolve to.
    /// </param>
    /// <param name="logger">
    /// Sink for the per-term-write safety net (issue #154 §8.8). Optional-defaulted like the two above
    /// so a direct construction in a unit test need not supply one.
    /// </param>
    public TermService(
        OdysseyContext context,
        TimeProvider? timeProvider = null,
        ISystemSettingsLookup? systemSettingsLookup = null,
        ILogger<TermService>? logger = null)
    {
        this.context = context;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.systemSettingsLookup = systemSettingsLookup;
        this.logger = logger ?? NullLogger<TermService>.Instance;
    }

    private Task<bool> ContractExists(Guid contractId, CancellationToken cancellationToken) =>
        context.Contracts.AnyAsync(c => c.ContractId == contractId, cancellationToken);

    private async Task<int> ResolveTermCap(CancellationToken cancellationToken)
    {
        if (systemSettingsLookup is null)
            return SystemSettingsDefaults.ContractMaxTermsPerContract;

        return (await systemSettingsLookup.GetRequestCapsAsync(cancellationToken)).MaxTermsPerContract;
    }

    // ── Reads ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the full term history for a contract (newest <c>EffectiveFrom</c> first), or
    /// <c>null</c> if the contract does not exist. An ARCHIVED contract's history stays readable —
    /// only writes are refused (issue #135 §8 rule 3).
    /// </summary>
    public async Task<IList<ExistingTerm>?> GetContractHistory(Guid contractId, DateTime? asOf = null, CancellationToken cancellationToken = default)
    {
        if (!await ContractExists(contractId, cancellationToken))
            return null;

        var query = context.Terms.AsNoTracking().Where(term => term.ContractId == contractId);

        if (asOf is not null)
        {
            var cutoff = NormalizeToUtc(asOf.Value);
            query = query.Where(term => term.EffectiveFrom <= cutoff);
        }

        var terms = await query
            .OrderByDescending(term => term.EffectiveFrom)
            .ThenByDescending(term => term.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        return terms.Adapt<List<ExistingTerm>>();
    }

    /// <summary>
    /// Returns the currently-effective value of each SERIES that has at least one entry on or before
    /// <paramref name="asOf"/> (default now), or <c>null</c> if the contract does not exist. One entry
    /// per label, so a card charging four named fees returns four. An empty list is a healthy
    /// response — a contract with no recorded price is not a defect.
    /// </summary>
    public async Task<IList<CurrentTerm>?> GetContractCurrent(Guid contractId, DateTime? asOf = null, CancellationToken cancellationToken = default)
    {
        if (!await ContractExists(contractId, cancellationToken))
            return null;

        var cutoff = NormalizeToUtc(asOf ?? timeProvider.GetUtcNow().UtcDateTime);

        var terms = await context.Terms
            .AsNoTracking()
            .Where(term => term.ContractId == contractId && term.EffectiveFrom <= cutoff)
            .ToListAsync(cancellationToken);

        return TermSeries.Current(terms).Adapt<List<CurrentTerm>>();
    }

    // ── Writes ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new term entry on a contract. The owner comes from the route and from nowhere else —
    /// <see cref="NewTerm"/> carries no owner field.
    /// </summary>
    /// <exception cref="DomainNotFoundException">The contract does not exist.</exception>
    /// <exception cref="DomainValidationException">Validation or eligibility failed.</exception>
    /// <exception cref="DomainConflictException">A term in the same series with that effective date exists.</exception>
    /// <exception cref="DomainUnprocessableException">The per-contract term cap is reached.</exception>
    public async Task<ExistingTerm> CreateForContract(
        Guid contractId, NewTerm newTerm, string? userId, CancellationToken cancellationToken = default)
    {
        if (!await ContractExists(contractId, cancellationToken))
            throw new DomainNotFoundException($"Contract ID {contractId} not found.");

        var term = new Term
        {
            ContractId = contractId,
            CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
        };

        await ApplyAndValidate(term, newTerm, excludeTermId: null, cancellationToken);

        // Create only: an update replaces a row rather than adding one, so it is row-count-neutral and
        // is never refused by a cap — including on a contract already at or above one lowered later.
        // The cap's VALUE is read here, so the four routes that never consult it do not pay for a
        // settings lookup.
        var cap = await ResolveTermCap(cancellationToken);
        var count = await context.Terms.CountAsync(t => t.ContractId == contractId, cancellationToken);
        if (count >= cap)
            throw new DomainUnprocessableException(
                $"This contract already has the maximum of {cap} terms.");

        context.Terms.Add(term);

        // Staged after the mutation is applied and BEFORE the save, with the Term as it will be
        // persisted, so the event rides this very SaveChangesAsync — the atomicity issue #154 §8.6
        // requires. It is reached only after the cap check above, so a refused create writes no event
        // and logs no line, structurally rather than by a guard.
        var now = timeProvider.GetUtcNow().UtcDateTime;
        ContractEventRecorder.Stage(
            context, contractId, ContractEventCatalogue.Term(TermWriteAction.Added, term, now), userId, now);

        await context.SaveChangesAsync(cancellationToken);

        // Post-commit: a log line describes a committed fact. A create has no "before" half, and that
        // (none) is HARDCODED rather than derived — an entity in the Added state has no prior row, so
        // OriginalValues is meaningless there and would silently read back the new values (§8.7).
        LogTermWrite("added", contractId, before: null, TermSnapshot.Of(term), userId);

        return term.Adapt<ExistingTerm>();
    }

    /// <summary>
    /// Updates an existing term entry, applying the same validation as <see cref="CreateForContract"/>.
    /// Returns <c>false</c> when the contract does not exist, or when that term is not attached to
    /// <i>this</i> contract — which is what keeps the endpoint from being an existence oracle across
    /// contracts.
    /// </summary>
    public async Task<bool> UpdateForContract(
        Guid contractId, Guid termId, NewTerm putTerm, string? userId, CancellationToken cancellationToken = default)
    {
        if (!await ContractExists(contractId, cancellationToken))
            return false;

        // Both halves are captured by this closure, which also tells the wrapper whether the delegate
        // ran at all — it does not when the term id matches no row on this contract.
        TermSnapshot? before = null;
        TermSnapshot? after = null;
        var updated = await UpdateFor(contractId, termId, putTerm, cancellationToken, term =>
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            ContractEventRecorder.Stage(
                context, contractId, ContractEventCatalogue.Term(TermWriteAction.Changed, term, now), userId, now);
            before = TermSnapshot.Original(context, term);
            after = TermSnapshot.Of(term);
        });

        if (updated)
            LogTermWrite("changed", contractId, before, after, userId);

        return updated;
    }

    /// <remarks>
    /// <b>The lookup must stay a TRACKED query.</b> It is load-bearing rather than incidental since
    /// issue #154: <c>TermSnapshot.Original</c> reads <c>context.Entry(term).OriginalValues</c>, the
    /// snapshot EF took when the entity began being tracked, and a later performance pass adding
    /// <c>AsNoTracking()</c> here would empty the "before" half of every term log line <b>silently</b> —
    /// EF returns <c>OriginalValues == CurrentValues</c> with no exception for an untracked-then-attached
    /// entity, so the line would simply read <c>X -&gt; X</c>. If one is ever added, the entity must be
    /// attached <em>before</em> mutation, not after.
    /// </remarks>
    private async Task<bool> UpdateFor(
        Guid contractId, Guid termId, NewTerm putTerm, CancellationToken cancellationToken,
        Action<Term> stage)
    {
        var term = await context.Terms
            .FirstOrDefaultAsync(t => t.ContractId == contractId && t.TermId == termId, cancellationToken);
        if (term is null)
            return false;

        await ApplyAndValidate(term, putTerm, excludeTermId: termId, cancellationToken);

        // After ApplyAndValidate, so the entity already holds the new values; the PREVIOUS ones still
        // come off the change tracker's untouched original snapshot, at no extra query.
        stage(term);

        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Deletes a term entry. Returns <c>false</c> if the term is not attached to the given contract.
    /// The contract itself is untouched.
    /// </summary>
    public async Task<bool> DeleteForContract(
        Guid contractId, Guid termId, string? userId, CancellationToken cancellationToken = default)
    {
        if (!await ContractExists(contractId, cancellationToken))
            return false;

        TermSnapshot? before = null;
        var deleted = await DeleteFor(contractId, termId, cancellationToken, term =>
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            ContractEventRecorder.Stage(
                context, contractId, ContractEventCatalogue.Term(TermWriteAction.Removed, term, now), userId, now);
            before = TermSnapshot.Of(term);
        });

        // A delete hard-deletes the row (Non-Goal 10), so once the PriceChanged event is itself deleted
        // this line is the only surviving record of what the term used to say. Hence the empty "after".
        if (deleted)
            LogTermWrite("removed", contractId, before, after: null, userId);

        return deleted;
    }

    private async Task<bool> DeleteFor(
        Guid contractId, Guid termId, CancellationToken cancellationToken, Action<Term> stage)
    {
        var term = await context.Terms
            .FirstOrDefaultAsync(t => t.ContractId == contractId && t.TermId == termId, cancellationToken);
        if (term is null)
            return false;

        // Before Remove, for readability rather than correctness: Remove() only flips the tracked state
        // and does not clear the entity's properties, so either order would in fact work. Stated so
        // nobody has to re-derive it.
        stage(term);

        context.Terms.Remove(term);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    // ── The structured-log safety net for term writes (issue #154 §8.8) ──────────

    /// <summary>
    /// The values a term log line names, on one side of a write. Money amounts, a currency code, a
    /// date and an opaque id — <b>no names, no free text and no user-supplied <c>Label</c></b>, matching
    /// <c>ContractService.LogPartyWrite</c>'s rule. A <c>Guid</c> or a <c>decimal</c> cannot carry the
    /// CR/LF a forged log line would need, which is what makes them safe to record verbatim.
    /// </summary>
    /// <remarks>
    /// Note the deliberate asymmetry with the event text: the term's <c>Label</c> may appear in the
    /// <em>event</em>, which is contract data behind <c>contracts.read</c>, and may not appear
    /// <em>here</em>, which is operator-facing.
    /// </remarks>
    private sealed record TermSnapshot(
        Guid TermId, decimal Value, string? CurrencyCode, DateTime EffectiveFrom)
    {
        /// <summary>The term as it stands right now.</summary>
        public static TermSnapshot Of(Term term) =>
            new(term.TermId, term.Value, term.CurrencyCode, term.EffectiveFrom);

        /// <summary>
        /// The term as it stood before this update, read off the change tracker's original snapshot —
        /// untouched by property mutation, reset only by a <em>successful</em> <c>SaveChangesAsync</c>,
        /// and costing no second query. Requires a <b>tracked</b> entity in a state other than
        /// <c>Added</c>; both preconditions fail by returning the CURRENT values rather than by
        /// throwing, so a line reading <c>X -&gt; X</c> means one of them was violated (§8.7).
        /// </summary>
        public static TermSnapshot Original(OdysseyContext context, Term term)
        {
            var original = context.Entry(term).OriginalValues;
            return new TermSnapshot(
                term.TermId,
                original.GetValue<decimal>(nameof(Term.Value)),
                original.GetValue<string?>(nameof(Term.CurrencyCode)),
                original.GetValue<DateTime>(nameof(Term.EffectiveFrom)));
        }
    }

    /// <summary>What a log slot reads when there is no term on that side of the write.</summary>
    private const string NoTermValue = "(none)";

    /// <summary>
    /// One structured <c>Information</c> line per term write, emitted <b>after</b> the commit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> A term update mutates the row <em>in place</em> and a term delete
    /// <em>hard-deletes</em> it (issue #154 Non-Goal 10), so after either write the <c>PriceChanged</c>
    /// event is the only record of what the term used to say — and that event is itself deletable by any
    /// <c>contracts.update</c> holder. Deleting it would destroy the last evidence. This line goes to the
    /// application log, which no endpoint can edit or delete.
    /// </para>
    /// <para>
    /// A create has no before half and a delete no after half; both slots read <c>(none)</c> rather than
    /// being omitted, so the shape is one shape.
    /// </para>
    /// </remarks>
    private void LogTermWrite(
        string action, Guid contractId, TermSnapshot? before, TermSnapshot? after, string? userId)
    {
        if (before is null && after is null)
            return;

        var subject = after ?? before!;

        logger.LogInformation(
            "Contract term {Action}: contract {ContractId}, term {TermId}, " +
            "{BeforeValue} {BeforeCurrency} from {BeforeEffectiveFrom} -> " +
            "{AfterValue} {AfterCurrency} from {AfterEffectiveFrom}, by user {UserId}.",
            action,
            contractId,
            subject.TermId,
            before is null ? NoTermValue : before.Value.ToString(CultureInfo.InvariantCulture),
            LogCurrency(before?.CurrencyCode),
            before is null ? NoTermValue : before.EffectiveFrom.ToString("O", CultureInfo.InvariantCulture),
            after is null ? NoTermValue : after.Value.ToString(CultureInfo.InvariantCulture),
            LogCurrency(after?.CurrencyCode),
            after is null ? NoTermValue : after.EffectiveFrom.ToString("O", CultureInfo.InvariantCulture),
            userId ?? "(unknown)");
    }

    /// <summary>
    /// The currency slot, reduced to what a currency code can be: exactly three ASCII letters, or
    /// <see cref="NoTermValue"/>. Anything else never reaches the line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the one value on the line that starts life as a caller-supplied string</b>, and it is
    /// what CodeQL flagged (<c>cs/log-forging</c>, "log entries created from user input"). Everything
    /// else is a <c>Guid</c>, a closed enum, a <c>decimal</c> or a round-tripped <c>DateTime</c>, none
    /// of which can carry the CR/LF a forged log line needs.
    /// </para>
    /// <para>
    /// <c>ApplyAndValidate</c> does already normalize the code and refuse one that is not a supported,
    /// active currency, so no such value can be stored today — but that guarantee sits three call
    /// frames away, behind a database lookup, and a later change there would silently widen what
    /// reaches an operator's log. Issue #154 §8.8 states the line carries "ids, closed enums, dates and
    /// money amounts"; this makes that a property of the <em>log site</em> rather than an inference
    /// about its callers. The "before" half is read back off a stored row and gets the same treatment,
    /// since a row written by an earlier build is outside this build's validator entirely.
    /// </para>
    /// </remarks>
    private static string LogCurrency(string? code)
    {
        if (code is not { Length: 3 } || !code.All(char.IsAsciiLetter))
            return NoTermValue;

        // The shape check above already makes this a no-op — three ASCII letters contain no line
        // break. It is here because it is the form CodeQL recognises as a barrier for
        // `cs/log-forging`: a predicate that returns the original string is not one, however total,
        // so the first cut of this guard left the alert standing. Stated plainly rather than dressed
        // up as defence in depth: the security property comes from the check, the Replace comes from
        // the analyzer, and removing either would be a regression in a different sense.
        return code.Replace("\r", string.Empty, StringComparison.Ordinal)
                   .Replace("\n", string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// The single validation, normalization and supersession path. The owner id on
    /// <paramref name="term"/> is set by the caller from the route and is never read from
    /// <paramref name="source"/>, which carries no owner field at all.
    /// </summary>
    private async Task ApplyAndValidate(Term term, NewTerm source, Guid? excludeTermId, CancellationToken cancellationToken = default)
    {
        // Rules 9-10 — input validation, ahead of every coherence rule below. [ApiController] model
        // validation bounds the HTTP path, but a direct (non-HTTP) caller never reaches it: without
        // this, an undefined ordinal would fall through the Mapster converter's `_ => OneTime` arm
        // and be PERSISTED as OneTime, a write-path fail-open rather than a read-path degradation.
        if (source.Interval is not null && !Enum.IsDefined(source.Interval.Value))
            throw new DomainValidationException(
                $"Interval '{(int)source.Interval.Value}' is not a recognised value.");

        if (source.IntervalCount is { } requestedCount
            && (requestedCount < TermIntervalCount.Min || requestedCount > TermIntervalCount.Max))
            throw new DomainValidationException(
                $"IntervalCount must be between {TermIntervalCount.Min} and {TermIntervalCount.Max}.");

        // Same fail-closed reasoning as the Interval check above: the HTTP path is bounded by
        // [EnumDataType], but a direct caller never reaches model validation, and an undefined ordinal
        // would otherwise be persisted through the Mapster converter as whichever member it defaults to.
        if (!Enum.IsDefined(source.Direction))
            throw new DomainValidationException(
                $"Direction '{(int)source.Direction}' is not a recognised value.",
                code: null,
                field: nameof(NewTerm.Direction));

        // The label IS the series, so it is required: two unnamed terms on one contract could not be
        // told apart.
        var label = TermLabel.Normalize(source.Label)
            ?? throw new DomainValidationException(
                "A term requires a label naming what it prices.",
                code: null,
                field: nameof(NewTerm.Label));
        if (label.Length > TermLabel.MaxLength)
            throw new DomainValidationException(
                $"A term label must be {TermLabel.MaxLength} characters or fewer.");

        // Derived here and only here — LabelKey is on no request DTO and is never bound from one.
        var labelKey = TermLabel.Key(label);

        var unit = source.ValueUnit.Adapt<ContextTermValueUnit>();

        var direction = source.Direction.Adapt<ContextTermDirection>();

        // V2 is the ABSENCE of a rule: every contract term accepts a direction, including the ones the
        // roll-up ignores — a percentage-unit term, a OneTime/PerOccurrence/PerUnit term, and a term with
        // no interval at all. A one-off signing bonus is legitimate incoming record-keeping; the
        // roll-up's exclusions are about having no rate to PROJECT, not about direction.

        // A count is meaningful only for a periodic unit. Stored as 1 when a periodic interval
        // arrives without one (the identity cadence), and as null — never 1 — otherwise: a
        // meaningless 1 on a one-time fee would be indistinguishable from a deliberate one, and
        // would trip this very rule on the next read-modify-write round trip.
        var interval = source.Interval?.Adapt<ContextInterval>();
        int? intervalCount;
        if (interval is not null && interval.Value.IsPeriodic())
        {
            intervalCount = source.IntervalCount ?? TermIntervalCount.Min;
        }
        else
        {
            if (source.IntervalCount is not null)
                throw new DomainValidationException(
                    "IntervalCount is only allowed for a periodic interval (Daily, Weekly, Monthly, Annually).");

            intervalCount = null;
        }

        var anchorDate = source.AnchorDate is null ? (DateTime?)null : NormalizeToUtc(source.AnchorDate.Value);

        string? currencyCode;
        if (unit == ContextTermValueUnit.Percentage)
        {
            if (source.Value < -1m || source.Value > 1m)
                throw new DomainValidationException(
                    "A percentage value must be a fraction within [-1, 1] (e.g. 0.0325 for 3.25%).");

            // Currency is meaningless for a percentage; it is always stored null.
            currencyCode = null;
        }
        else
        {
            if (source.Value < 0m)
                throw new DomainValidationException("An amount value must be greater than or equal to zero.");

            // A contract has no currency of its own, so an explicit code is required. The two rejected
            // defaulting alternatives (its first account party's currency, an instance-wide base
            // currency) both assign a meaning nobody chose, and the first changes retroactively when
            // parties are detached or re-ordered.
            var requested = source.CurrencyCode;
            if (string.IsNullOrWhiteSpace(requested))
                throw new DomainValidationException(
                    "A money-valued term on a contract must name its currency — a contract has no currency of its own to fall back to.",
                    code: null,
                    field: nameof(NewTerm.CurrencyCode));

            var normalized = CurrencyValidationService.Normalize(requested);
            await CurrencyValidationService.EnsureSupportedAndActive(context, normalized, nameof(source.CurrencyCode));
            currencyCode = normalized;
        }

        var effectiveFrom = NormalizeToUtc(source.EffectiveFrom);

        // The guard is over the SERIES key, on the folded form, so "ATM abroad", "atm abroad" and
        // "  ATM   abroad  " collide while two differently-named fees on one date do not.
        var duplicateExists = await context.Terms.AnyAsync(existing =>
            existing.ContractId == term.ContractId
            && existing.LabelKey == labelKey
            && existing.EffectiveFrom == effectiveFrom
            && (excludeTermId == null || existing.TermId != excludeTermId), cancellationToken);
        if (duplicateExists)
            throw new DomainConflictException(
                $"'{label}' already has an entry effective from {effectiveFrom:yyyy-MM-dd} on this contract.");

        term.Label = label;
        term.LabelKey = labelKey;
        term.ValueUnit = unit;
        // V3 — assigned unconditionally on both create and replace, and read by NOTHING above: it is
        // not in the series key, the duplicate guard or supersession, so two entries differing only in
        // direction are the ordinary supersession case rather than two concurrent series.
        term.Direction = direction;
        term.Value = source.Value;
        term.CurrencyCode = currencyCode;
        term.Interval = interval;
        term.IntervalCount = intervalCount;
        term.AnchorDate = anchorDate;
        term.EffectiveFrom = effectiveFrom;
        term.Note = source.Note;
    }

    private static DateTime NormalizeToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
