using System.Globalization;
using System.Linq.Expressions;
using Odyssey.Core;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ContextAccountType = Odyssey.Context.AccountType;
using ContextInterval = Odyssey.Context.Interval;
using ContextTermKind = Odyssey.Context.TermKind;
using ContextTermValueUnit = Odyssey.Context.TermValueUnit;
using ContextTermDirection = Odyssey.Context.TermDirection;
using DtoTermKind = Odyssey.Dtos.Finance.TermKind;

namespace Odyssey.Core.Finance;

/// <summary>
/// Business logic for time-versioned terms (interest rates, expected returns, and fee prices) on
/// either of the two owners the table serves — an <b>account</b> or a <b>contract</b> (issue #135).
/// Enforces per-owner kind eligibility and value/unit/currency validation, and resolves the
/// currently-effective value of each SERIES by implicit supersession (latest <c>EffectiveFrom</c> on
/// or before a date).
///
/// <para>
/// A series is <c>(owner, TermKind, LabelKey)</c>. One kind can hold several concurrently in-force
/// terms told apart by a user-authored label, and supersession happens strictly within a label — a
/// rise in the foreign ATM charge is not a change to the domestic one. An account series and a
/// contract series never interact, however identical their kind and label.
/// </para>
///
/// <para>
/// <b>There is exactly one validation path.</b> Both owners run <see cref="ApplyAndValidate"/>; what
/// differs between them arrives as <see cref="TermOwnerFacts"/> resolved from the route. A second
/// copy of this validator for contracts would diverge, and the copy with fewer eyes on it is the one
/// that would.
/// </para>
/// </summary>
public class TermService
{
    private readonly OdysseyContext context;
    private readonly TimeProvider timeProvider;
    private readonly ISystemSettingsLookup? systemSettingsLookup;
    private readonly ILogger<TermService> logger;

    /// <param name="systemSettingsLookup">
    /// Source of the per-contract term cap. Optional because the account surface has no cap at all
    /// (issue #135 Non-Goal 4) and its callers therefore need none; when it is absent the contract cap
    /// resolves to the shipped default, which is the same value a healthy absent settings row would
    /// resolve to.
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

    // The eligibility matrix lives in code (not the database) so it can evolve without a migration.
    // Unknown is never permitted; Fee is permitted on every account type.
    private static readonly IReadOnlySet<ContextAccountType> InterestRateAccountTypes = new HashSet<ContextAccountType>
    {
        ContextAccountType.CheckingAccount,
        ContextAccountType.SavingsAccount,
        ContextAccountType.PensionAccount,
        ContextAccountType.CreditCard,
        ContextAccountType.Mortgage,
        ContextAccountType.StudentLoan,
        ContextAccountType.PersonalLoan,
        ContextAccountType.CarLoan,
        ContextAccountType.TaxDebt,
    };

    private static readonly IReadOnlySet<ContextAccountType> ExpectedReturnAccountTypes = new HashSet<ContextAccountType>
    {
        ContextAccountType.InvestmentAccount,
        ContextAccountType.PensionAccount,
    };

    /// <summary>
    /// What a contract may be priced in (issue #135 §8). <c>Fee</c> and <c>InterestRate</c> on every
    /// contract type — <c>ContractType</c>'s four values (Employment, Service, Rental, Other) are
    /// coarse and none of them is financing-specific, so a per-type matrix would be arbitrary rather
    /// than informative. <c>ExpectedReturn</c> is refused: it prices invested principal, which a
    /// contract does not hold.
    /// </summary>
    private static readonly IReadOnlySet<ContextTermKind> ContractTermKinds = new HashSet<ContextTermKind>
    {
        ContextTermKind.Fee,
        ContextTermKind.InterestRate,
    };

    // ── Owner resolution ─────────────────────────────────────────────────────────
    //
    // One resolver per owner. Everything that differs between an account and a contract is decided
    // HERE and handed to the one validator as data, so a third owner (issue #135's "Later") is a new
    // resolver rather than a second validator.

    /// <summary>
    /// Resolves an account to the facts the validator needs, or <c>null</c> when it does not exist.
    /// </summary>
    private async Task<TermOwnerFacts?> ResolveAccountOwner(Guid accountId, CancellationToken cancellationToken)
    {
        var account = await context.Accounts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.AccountId == accountId, cancellationToken);
        if (account is null)
            return null;

        var permitted = new HashSet<ContextTermKind> { ContextTermKind.Fee };
        if (InterestRateAccountTypes.Contains(account.AccountType))
            permitted.Add(ContextTermKind.InterestRate);
        if (ExpectedReturnAccountTypes.Contains(account.AccountType))
            permitted.Add(ContextTermKind.ExpectedReturn);

        return new TermOwnerFacts(
            TermOwnerKind.Account,
            account.AccountId,
            "account",
            permitted,
            $"accounts of type '{account.AccountType}'",
            // An amount term on an account defaults to the account's own currency, which is the
            // pre-#135 behaviour and stays unchanged.
            DefaultCurrencyCode: account.CurrencyCode,
            // No cap on account terms — the pre-existing gap is not widened here and is left to its
            // own issue (Non-Goal 4).
            IsTermCapped: false);
    }

    /// <summary>
    /// Resolves a contract to the facts the validator needs, or <c>null</c> when it does not exist.
    /// </summary>
    private async Task<TermOwnerFacts?> ResolveContractOwner(Guid contractId, CancellationToken cancellationToken)
    {
        var contract = await context.Contracts
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.ContractId == contractId, cancellationToken);
        if (contract is null)
            return null;

        return new TermOwnerFacts(
            TermOwnerKind.Contract,
            contract.ContractId,
            "contract",
            ContractTermKinds,
            "contracts",
            // A contract has no currency of its own, which is what makes an explicit code REQUIRED for
            // an amount term (issue #135 §8 rule 2) rather than merely recommended.
            DefaultCurrencyCode: null,
            IsTermCapped: true);
    }

    private async Task<int> ResolveTermCap(CancellationToken cancellationToken)
    {
        if (systemSettingsLookup is null)
            return SystemSettingsDefaults.ContractMaxTermsPerContract;

        return (await systemSettingsLookup.GetRequestCapsAsync(cancellationToken)).MaxTermsPerContract;
    }

    /// <summary>
    /// The owner predicate, written per owner kind rather than as one expression over two nullable
    /// columns: a comparison against a <c>Guid?</c> variable that happens to be null is a shape whose
    /// SQL depends on the provider, and this filter decides which owner's rows a caller can see.
    /// </summary>
    private static Expression<Func<Term, bool>> OwnedBy(TermOwnerKind kind, Guid id) => kind switch
    {
        TermOwnerKind.Contract => term => term.ContractId == id,
        _ => term => term.AccountId == id,
    };

    private static Expression<Func<Term, bool>> OwnedBy(TermOwnerFacts owner) => OwnedBy(owner.Kind, owner.Id);

    // ── Reads ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the full term history for an account (newest <c>EffectiveFrom</c> first), or
    /// <c>null</c> if the account does not exist. Optionally filtered by kind and/or an as-of date.
    /// </summary>
    public async Task<IList<ExistingTerm>?> GetHistory(Guid accountId, DtoTermKind? kind = null, DateTime? asOf = null, CancellationToken cancellationToken = default)
    {
        var accountExists = await context.Accounts.AnyAsync(a => a.AccountId == accountId, cancellationToken);
        if (!accountExists)
            return null;

        return await GetHistoryFor(TermOwnerKind.Account, accountId, kind, asOf, cancellationToken);
    }

    /// <summary>
    /// Returns the full term history for a contract (newest <c>EffectiveFrom</c> first), or
    /// <c>null</c> if the contract does not exist. An ARCHIVED contract's history stays readable —
    /// only writes are refused (issue #135 §8 rule 3).
    /// </summary>
    public async Task<IList<ExistingTerm>?> GetContractHistory(Guid contractId, DtoTermKind? kind = null, DateTime? asOf = null, CancellationToken cancellationToken = default)
    {
        var contractExists = await context.Contracts.AnyAsync(c => c.ContractId == contractId, cancellationToken);
        if (!contractExists)
            return null;

        return await GetHistoryFor(TermOwnerKind.Contract, contractId, kind, asOf, cancellationToken);
    }

    private async Task<IList<ExistingTerm>> GetHistoryFor(
        TermOwnerKind ownerKind, Guid ownerId, DtoTermKind? kind, DateTime? asOf, CancellationToken cancellationToken)
    {
        var query = context.Terms.AsNoTracking().Where(OwnedBy(ownerKind, ownerId));

        if (kind is not null)
        {
            var contextKind = kind.Value.Adapt<ContextTermKind>();
            query = query.Where(term => term.TermKind == contextKind);
        }

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
    /// <paramref name="asOf"/> (default now), or <c>null</c> if the account does not exist. One kind
    /// contributes one entry per label, so a card charging four named fees returns four.
    /// </summary>
    public async Task<IList<CurrentTerm>?> GetCurrent(Guid accountId, DateTime? asOf = null, CancellationToken cancellationToken = default)
    {
        var accountExists = await context.Accounts.AnyAsync(a => a.AccountId == accountId, cancellationToken);
        if (!accountExists)
            return null;

        return await GetCurrentFor(TermOwnerKind.Account, accountId, asOf, cancellationToken);
    }

    /// <summary>
    /// The contract mirror of <see cref="GetCurrent"/>. An empty list is a healthy response — a
    /// contract with no recorded price is not a defect.
    /// </summary>
    public async Task<IList<CurrentTerm>?> GetContractCurrent(Guid contractId, DateTime? asOf = null, CancellationToken cancellationToken = default)
    {
        var contractExists = await context.Contracts.AnyAsync(c => c.ContractId == contractId, cancellationToken);
        if (!contractExists)
            return null;

        return await GetCurrentFor(TermOwnerKind.Contract, contractId, asOf, cancellationToken);
    }

    private async Task<IList<CurrentTerm>> GetCurrentFor(
        TermOwnerKind ownerKind, Guid ownerId, DateTime? asOf, CancellationToken cancellationToken)
    {
        var cutoff = NormalizeToUtc(asOf ?? timeProvider.GetUtcNow().UtcDateTime);

        var terms = await context.Terms
            .AsNoTracking()
            .Where(OwnedBy(ownerKind, ownerId))
            .Where(term => term.EffectiveFrom <= cutoff)
            .ToListAsync(cancellationToken);

        return TermSeries.Current(terms).Adapt<List<CurrentTerm>>();
    }

    // ── Writes ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new term entry on an account.
    /// </summary>
    /// <exception cref="DomainNotFoundException">The account does not exist.</exception>
    /// <exception cref="DomainValidationException">Validation or eligibility failed.</exception>
    /// <exception cref="DomainValidationException">The currency for an amount is unsupported.</exception>
    /// <exception cref="DomainConflictException">A term in the same series with that effective date exists.</exception>
    public async Task<ExistingTerm> Create(Guid accountId, NewTerm newTerm, CancellationToken cancellationToken = default)
    {
        var owner = await ResolveAccountOwner(accountId, cancellationToken)
            ?? throw new DomainNotFoundException($"Account with ID {accountId} was not found.");

        return await CreateFor(owner, newTerm, cancellationToken);
    }

    /// <summary>
    /// Creates a new term entry on a contract (issue #135). The owner comes from the route and from
    /// nowhere else — <see cref="NewTerm"/> carries no owner field — so a <c>contracts.update</c>
    /// holder cannot write a term onto an account.
    /// </summary>
    /// <exception cref="DomainNotFoundException">The contract does not exist.</exception>
    /// <exception cref="DomainValidationException">Validation or eligibility failed.</exception>
    /// <exception cref="DomainConflictException">A term in the same series with that effective date exists.</exception>
    /// <exception cref="DomainUnprocessableException">The per-contract term cap is reached.</exception>
    public async Task<ExistingTerm> CreateForContract(
        Guid contractId, NewTerm newTerm, string? userId, CancellationToken cancellationToken = default)
    {
        var owner = await ResolveContractOwner(contractId, cancellationToken)
            ?? throw new DomainNotFoundException($"Contract ID {contractId} not found.");

        TermSnapshot? after = null;
        var created = await CreateFor(owner, newTerm, cancellationToken, term =>
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            ContractEventRecorder.Stage(
                context, contractId, ContractEventCatalogue.Term(TermWriteAction.Added, term, now), userId, now);
            after = TermSnapshot.Of(term);
        });

        // Post-commit: a log line describes a committed fact. A create has no "before" half, and that
        // (none) is HARDCODED rather than derived — an entity in the Added state has no prior row, so
        // OriginalValues is meaningless there and would silently read back the new values (§8.7).
        LogTermWrite("added", contractId, before: null, after, userId);

        return created;
    }

    private async Task<ExistingTerm> CreateFor(
        TermOwnerFacts owner, NewTerm newTerm, CancellationToken cancellationToken, Action<Term>? stage = null)
    {
        var term = new Term
        {
            AccountId = owner.Kind == TermOwnerKind.Account ? owner.Id : null,
            ContractId = owner.Kind == TermOwnerKind.Contract ? owner.Id : null,
            CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
        };

        await ApplyAndValidate(term, newTerm, owner, excludeTermId: null, cancellationToken);

        // Create only: an update replaces a row rather than adding one, so it is row-count-neutral and
        // is never refused by a cap — including on an owner already at or above one lowered later.
        // The cap's VALUE is read here rather than during owner resolution, so the four routes that
        // never consult it do not pay for a settings lookup.
        if (owner.IsTermCapped)
        {
            var cap = await ResolveTermCap(cancellationToken);
            var count = await context.Terms.CountAsync(OwnedBy(owner), cancellationToken);
            if (count >= cap)
                throw new DomainUnprocessableException(
                    $"This {owner.Noun} already has the maximum of {cap} terms.");
        }

        context.Terms.Add(term);

        // The staging seam (issue #154 §8.7). Invoked after the mutation is applied and BEFORE the save,
        // with the Term as it will be persisted, so whatever it stages rides this very SaveChangesAsync
        // — which is the atomicity §8.6 requires. The three ACCOUNT wrappers pass nothing and keep their
        // behaviour by OMISSION: an account-owned term write cannot emit a ContractEvent because no
        // delegate was supplied, not because a branch decided not to. There is no code path to get it
        // wrong, and ApplyAndValidate — the one validation path — is untouched.
        //
        // It is reached only after the cap check above, which throws before context.Terms.Add: a refused
        // create therefore writes no event and logs no line, structurally rather than by a guard.
        stage?.Invoke(term);

        await context.SaveChangesAsync(cancellationToken);

        return term.Adapt<ExistingTerm>();
    }

    /// <summary>
    /// Updates an existing term entry. Returns <c>false</c> if the term is not attached to the
    /// given account; otherwise applies the same validation as <see cref="Create"/>.
    /// </summary>
    public async Task<bool> Update(Guid accountId, Guid termId, NewTerm putTerm, CancellationToken cancellationToken = default)
    {
        var owner = await ResolveAccountOwner(accountId, cancellationToken);
        return owner is not null && await UpdateFor(owner, termId, putTerm, cancellationToken);
    }

    /// <summary>
    /// The contract mirror of <see cref="Update"/>. Returns <c>false</c> when the contract does not
    /// exist, or when that term is not attached to <i>this</i> contract — including when it belongs to
    /// an account or to a different contract, which is what keeps the endpoint from being an existence
    /// oracle across owners. The owner itself is never changeable through this endpoint.
    /// </summary>
    public async Task<bool> UpdateForContract(
        Guid contractId, Guid termId, NewTerm putTerm, string? userId, CancellationToken cancellationToken = default)
    {
        var owner = await ResolveContractOwner(contractId, cancellationToken);
        if (owner is null)
            return false;

        // Both halves are captured by this closure, which also tells the wrapper whether the delegate
        // ran at all — it does not when the term id matches no row on this owner.
        TermSnapshot? before = null;
        TermSnapshot? after = null;
        var updated = await UpdateFor(owner, termId, putTerm, cancellationToken, term =>
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
        TermOwnerFacts owner, Guid termId, NewTerm putTerm, CancellationToken cancellationToken,
        Action<Term>? stage = null)
    {
        var term = await context.Terms
            .Where(OwnedBy(owner))
            .FirstOrDefaultAsync(t => t.TermId == termId, cancellationToken);
        if (term is null)
            return false;

        await ApplyAndValidate(term, putTerm, owner, excludeTermId: termId, cancellationToken);

        // After ApplyAndValidate, so the entity already holds the new values; the PREVIOUS ones still
        // come off the change tracker's untouched original snapshot, at no extra query.
        stage?.Invoke(term);

        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Deletes a term entry. Returns <c>false</c> if the term is not attached to the given account.
    /// </summary>
    public async Task<bool> Delete(Guid accountId, Guid termId, CancellationToken cancellationToken = default)
    {
        var owner = await ResolveAccountOwner(accountId, cancellationToken);
        return owner is not null && await DeleteFor(owner, termId, cancellationToken);
    }

    /// <summary>
    /// The contract mirror of <see cref="Delete"/>. The contract itself is untouched.
    /// </summary>
    public async Task<bool> DeleteForContract(
        Guid contractId, Guid termId, string? userId, CancellationToken cancellationToken = default)
    {
        var owner = await ResolveContractOwner(contractId, cancellationToken);
        if (owner is null)
            return false;

        TermSnapshot? before = null;
        var deleted = await DeleteFor(owner, termId, cancellationToken, term =>
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
        TermOwnerFacts owner, Guid termId, CancellationToken cancellationToken, Action<Term>? stage = null)
    {
        var term = await context.Terms
            .Where(OwnedBy(owner))
            .FirstOrDefaultAsync(t => t.TermId == termId, cancellationToken);
        if (term is null)
            return false;

        // Before Remove, for readability rather than correctness: Remove() only flips the tracked state
        // and does not clear the entity's properties, so either order would in fact work. Stated so
        // nobody has to re-derive it.
        stage?.Invoke(term);

        context.Terms.Remove(term);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    // ── The structured-log safety net for term writes (issue #154 §8.8) ──────────

    /// <summary>
    /// The four values a term log line names, on one side of a write. Money amounts, a currency code, a
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
        Guid TermId, ContextTermKind Kind, decimal Value, string? CurrencyCode, DateTime EffectiveFrom)
    {
        /// <summary>The term as it stands right now.</summary>
        public static TermSnapshot Of(Term term) =>
            new(term.TermId, term.TermKind, term.Value, term.CurrencyCode, term.EffectiveFrom);

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
                original.GetValue<ContextTermKind>(nameof(Term.TermKind)),
                original.GetValue<decimal>(nameof(Term.Value)),
                original.GetValue<string?>(nameof(Term.CurrencyCode)),
                original.GetValue<DateTime>(nameof(Term.EffectiveFrom)));
        }
    }

    /// <summary>What a log slot reads when there is no term on that side of the write.</summary>
    private const string NoTermValue = "(none)";

    /// <summary>
    /// One structured <c>Information</c> line per <b>contract</b> term write, emitted <b>after</b> the
    /// commit. The three account wrappers supply no staging delegate, so they reach this with both
    /// snapshots null and log nothing.
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
            "Contract term {Action}: contract {ContractId}, term {TermId}, kind {TermKind}, " +
            "{BeforeValue} {BeforeCurrency} from {BeforeEffectiveFrom} -> " +
            "{AfterValue} {AfterCurrency} from {AfterEffectiveFrom}, by user {UserId}.",
            action,
            contractId,
            subject.TermId,
            subject.Kind,
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
    private static string LogCurrency(string? code) =>
        code is { Length: 3 } && code.All(char.IsAsciiLetter) ? code : NoTermValue;

    /// <summary>
    /// The single validation, normalization and supersession path both owners run through. Everything
    /// that varies between an account and a contract arrives in <paramref name="owner"/>: which kinds
    /// are eligible, whether an amount may fall back to an owner currency, and nothing else. The owner
    /// ids on <paramref name="term"/> are set by the caller from the route and are never read from
    /// <paramref name="source"/>, which carries no owner field at all.
    /// </summary>
    private async Task ApplyAndValidate(Term term, NewTerm source, TermOwnerFacts owner, Guid? excludeTermId, CancellationToken cancellationToken = default)
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

        var kind = source.TermKind.Adapt<ContextTermKind>();
        if (kind == ContextTermKind.Unknown)
            throw new DomainValidationException("TermKind must be a recognised value.");

        if (!owner.PermittedKinds.Contains(kind))
            throw new DomainValidationException(
                $"Term kind '{source.TermKind}' is not permitted for {owner.EligibilityScope}.");

        // The label carries a fee's taxonomy, so it is required there and refused on a rate: two
        // labelled interest rates would both be in force, and the account header, the record card and
        // the history chart each headline exactly one, with no non-arbitrary way to choose.
        var label = TermLabel.Normalize(source.Label);
        if (label is not null && label.Length > TermLabel.MaxLength)
            throw new DomainValidationException(
                $"A term label must be {TermLabel.MaxLength} characters or fewer.");

        switch (TermLabel.RuleFor(source.TermKind))
        {
            case TermLabelRule.Refused when label is not null:
                throw new DomainValidationException(
                    $"Term kind '{source.TermKind}' does not take a label.");
            case TermLabelRule.Required when label is null:
                throw new DomainValidationException(
                    $"Term kind '{source.TermKind}' requires a label naming what is charged.");
        }

        // Derived here and only here — LabelKey is on no request DTO and is never bound from one.
        var labelKey = TermLabel.Key(label);

        var unit = source.ValueUnit.Adapt<ContextTermValueUnit>();

        // Rate kinds are percentages by definition; an amount unit would store a rate as a currency
        // value, which is semantically invalid history.
        if (unit != ContextTermValueUnit.Percentage
            && (kind == ContextTermKind.InterestRate || kind == ContextTermKind.ExpectedReturn))
            throw new DomainValidationException(
                $"Term kind '{source.TermKind}' must be expressed as a percentage, not an amount.");

        var isRateKind = kind == ContextTermKind.InterestRate || kind == ContextTermKind.ExpectedReturn;

        if (source.Interval is not null && isRateKind)
            throw new DomainValidationException(
                $"Interval is not allowed for term kind '{source.TermKind}'.");

        // A rate is not billed, so a rate row must not be able to carry half a billing description.
        // The rule is KIND-based, not interval-based: a one-time fee charged on a known date is
        // precisely a case worth recording.
        if (source.AnchorDate is not null && isRateKind)
            throw new DomainValidationException(
                $"AnchorDate is not allowed for term kind '{source.TermKind}'.");

        var direction = source.Direction.Adapt<ContextTermDirection>();

        // V1 (issue #159) — direction is a FEE-only field. A rate is a percentage, is already excluded
        // from the roll-up, and belongs to the account-side question direction on account terms is
        // deferred to. Stored as Outgoing there, where it carries no meaning.
        if (direction != ContextTermDirection.Outgoing && isRateKind)
            throw new DomainValidationException(
                $"Direction applies to a fee term only, not to term kind '{source.TermKind}'.",
                code: null,
                field: nameof(NewTerm.Direction));

        // V4 (issue #159) — an account-owned term may not carry a non-default direction. A savings
        // account's interest is incoming and a loan's is outgoing, but no account surface READS a
        // direction, so accepting one would let a user record a fact the product then contradicts.
        // Deliberately deferred to its own issue rather than half-implemented here.
        if (direction != ContextTermDirection.Outgoing && owner.Kind == TermOwnerKind.Account)
            throw new DomainValidationException(
                "Direction applies to contract terms only.",
                code: null,
                field: nameof(NewTerm.Direction));

        // V2 is the ABSENCE of a rule: every other fee accepts a direction, including the ones the
        // roll-up ignores — a percentage-unit fee, a OneTime/PerOccurrence/PerUnit fee, and a fee with
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

            // An owner with a currency of its own defaults from it; one without — a contract — requires
            // an explicit code. The two rejected alternatives for a contract (its first account
            // party's currency, an instance-wide base currency) both assign a meaning nobody chose,
            // and the first changes retroactively when parties are detached or re-ordered.
            var requested = source.CurrencyCode;
            if (string.IsNullOrWhiteSpace(requested))
            {
                requested = owner.DefaultCurrencyCode
                    ?? throw new DomainValidationException(
                        $"A money-valued term on a {owner.Noun} must name its currency — a {owner.Noun} has no currency of its own to fall back to.",
                        code: null,
                        field: nameof(NewTerm.CurrencyCode));
            }

            var normalized = CurrencyValidationService.Normalize(requested);
            await CurrencyValidationService.EnsureSupportedAndActive(context, normalized, nameof(source.CurrencyCode));
            currencyCode = normalized;
        }

        var effectiveFrom = NormalizeToUtc(source.EffectiveFrom);

        // The guard is over the SERIES key, on the folded form, so "ATM abroad", "atm abroad" and
        // "  ATM   abroad  " collide while two differently-named fees on one date do not.
        var duplicateExists = await context.Terms.Where(OwnedBy(owner)).AnyAsync(existing =>
            existing.TermKind == kind
            && existing.LabelKey == labelKey
            && existing.EffectiveFrom == effectiveFrom
            && (excludeTermId == null || existing.TermId != excludeTermId), cancellationToken);
        if (duplicateExists)
            throw new DomainConflictException(label is null
                ? $"A '{source.TermKind}' term effective from {effectiveFrom:yyyy-MM-dd} already exists for this {owner.Noun}."
                : $"'{label}' already has an entry effective from {effectiveFrom:yyyy-MM-dd} on this {owner.Noun}.");

        term.TermKind = kind;
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
