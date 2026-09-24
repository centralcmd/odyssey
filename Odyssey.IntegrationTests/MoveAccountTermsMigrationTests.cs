// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a Guid, a
// number or a literal this test wrote itself — there is no external input — and the pre-migration
// rows carry an account owner the current model no longer has, which is what makes raw SQL the only
// way to build them.
#pragma warning disable EF1002

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// <c>MoveAccountTermsToContracts</c> (issue #190): every account-owned term moves onto a contract —
/// reused when exactly one fits, otherwise created from the account — each moved account gets one
/// attention event, and the account owner is dropped from the schema. Only observable here: the EF
/// InMemory provider runs no migrations.
/// </summary>
/// <remarks>
/// Every seed and every read is raw SQL at the schema the step is at, so the test keeps compiling and
/// running once a later migration changes the entities it touches.
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class MoveAccountTermsMigrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_move_account_terms";

    /// <summary>The migration immediately before the one under test.</summary>
    private const string Baseline = "_RemoveTermKind";

    private const string Subject = "_MoveAccountTermsToContracts";

    // Enum ordinals, spelled out so a later reorder of an entity enum cannot change what was seeded.
    private const int Percentage = 0, Amount = 1;
    private const int Outgoing = 0, Incoming = 1;
    private const int Monthly = 3;
    private const int ContractOther = 3, Loan = 8, Deposit = 9;
    private const int RoleOther = 6, Lender = 13, ObjectRole = 17, Depositor = 20, Custodian = 21;
    private const int SavingsAccount = 3, CarLoan = 13, UnknownAccount = 0, CheckingAccount = 2;

    private static readonly DateTime Opened = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// AC 1–6, 9, 10, 12 — the three expected types, created from the account's own data, and the
    /// schema afterwards.
    /// </summary>
    [SkippableFact]
    public async Task Accounts_without_a_matching_contract_get_one_created_from_their_own_data()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var bank = Guid.NewGuid();
        var savings = Guid.NewGuid();
        var carLoan = Guid.NewGuid();
        var unknown = Guid.NewGuid();
        var reversed = Guid.NewGuid();
        var untouched = Guid.NewGuid();

        var savingsRate = Guid.NewGuid();
        var savingsFee = Guid.NewGuid();
        var loanRate = Guid.NewGuid();
        var loanFee = Guid.NewGuid();
        var unknownRate = Guid.NewGuid();
        var reversedFee = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                await BaselineContacts.AddPersonAsync(context, bank, "Nordic", "Bank");

                await InsertAccountAsync(context, savings, "Savings", SavingsAccount, "NOK",
                    custodian: bank, accountNumber: "1234.56.78901", description: "Rainy-day money");
                await InsertAccountAsync(context, carLoan, "Car loan", CarLoan, "EUR");
                await InsertAccountAsync(context, unknown, "Mystery", UnknownAccount, "USD", custodian: bank);
                await InsertAccountAsync(context, reversed, "Reversed dates", CheckingAccount, "USD",
                    closed: Opened.AddYears(-1));
                await InsertAccountAsync(context, untouched, "No terms", SavingsAccount, "USD");

                await InsertTermAsync(context, savingsRate, savings, Percentage, 0.0325m, "Interest rate");
                await InsertTermAsync(context, savingsFee, savings, Amount, 5m, "Monthly fee", interval: Monthly,
                    note: "Waived for students");
                await InsertTermAsync(context, loanRate, carLoan, Percentage, 0.049m, "Interest rate");
                await InsertTermAsync(context, loanFee, carLoan, Amount, 50m, "Setup fee", currency: "EUR");
                await InsertTermAsync(context, unknownRate, unknown, Percentage, 0.01m, "Rate");
                await InsertTermAsync(context, reversedFee, reversed, Amount, 1m, "Card fee", currency: "USD");
            }

            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Subject);

                // AC 1 — the schema.
                Assert.False(await ColumnExistsAsync(context, "Terms", "AccountId"));
                Assert.Equal("NO", await ScalarAsync<string>(context,
                    "SELECT IS_NULLABLE AS `Value` FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() " +
                    "AND TABLE_NAME = 'Terms' AND COLUMN_NAME = 'ContractId'"));
                Assert.False(await TableExistsAsync(context, "__AccountTermMigration"));

                var terms = await ReadTermsAsync(context);

                // AC 3 — asset → Deposit, Object + Custodian, fields from the account, Draft.
                var deposit = terms[savingsRate].ContractId;
                Assert.Equal(deposit, terms[savingsFee].ContractId);
                var depositRow = await ReadContractAsync(context, deposit);
                Assert.Equal(("Savings", Deposit, "Rainy-day money", "1234.56.78901"),
                    (depositRow.Name, depositRow.Type, depositRow.Description, depositRow.ReferenceNumber));
                Assert.Equal(Opened, depositRow.StartDate);
                Assert.Null(depositRow.EndDate);
                Assert.Null(depositRow.CompletionDate);
                Assert.Null(depositRow.Archived);
                Assert.Equal((null, null, null), (depositRow.Paused, depositRow.Ready, depositRow.Signed));
                Assert.Equal(
                    [(savings, (Guid?)null, ObjectRole), (null, bank, Custodian)],
                    (await ReadPartiesAsync(context, deposit)).OrderBy(p => p.Role).ToList());

                // AC 2, 9, 10 — same ids and values; currency frozen; the Deposit percentage flips.
                Assert.Equal((Percentage, Incoming, 0.0325m, (string?)null),
                    (terms[savingsRate].ValueUnit, terms[savingsRate].Direction, terms[savingsRate].Value, terms[savingsRate].CurrencyCode));
                Assert.Equal((Amount, Outgoing, 5m, "NOK", "Monthly fee", "monthly fee", "Waived for students"),
                    (terms[savingsFee].ValueUnit, terms[savingsFee].Direction, terms[savingsFee].Value,
                     terms[savingsFee].CurrencyCode, terms[savingsFee].Label, terms[savingsFee].LabelKey, terms[savingsFee].Note));
                Assert.Equal((Monthly, 1), (terms[savingsFee].Interval, terms[savingsFee].IntervalCount));
                Assert.Equal(Opened, terms[savingsFee].EffectiveFrom);
                Assert.Equal(Opened, terms[savingsFee].CreatedAtUtc);

                Assert.Equal(
                    ["A1 (1): Savings", "A6 (1): 1234.56.78901", "A8 (1): Interest rate", "A14 (1): Monthly fee"],
                    await ReadEventLinesAsync(context, deposit, "Savings"));

                // AC 4, 6 — liability → Loan, Object only; percentages stay Outgoing on a Loan.
                var loan = terms[loanRate].ContractId;
                Assert.Equal(Loan, (await ReadContractAsync(context, loan)).Type);
                Assert.Equal([(carLoan, (Guid?)null, ObjectRole)], await ReadPartiesAsync(context, loan));
                Assert.Equal(Outgoing, terms[loanRate].Direction);
                Assert.Equal(["A1 (1): Car loan", "A5 (1)"], await ReadEventLinesAsync(context, loan, "Car loan"));

                // AC 5 — unclassified → Other, Object + Other; A4; percentage stays Outgoing.
                var other = terms[unknownRate].ContractId;
                Assert.Equal(ContractOther, (await ReadContractAsync(context, other)).Type);
                Assert.Equal(
                    [(null, bank, RoleOther), (unknown, (Guid?)null, ObjectRole)],
                    (await ReadPartiesAsync(context, other)).OrderBy(p => p.Role).ToList());
                Assert.Equal(Outgoing, terms[unknownRate].Direction);
                Assert.Contains("A4 (1)", await ReadEventLinesAsync(context, other, "Mystery"));

                // A7 — Closed before Opened leaves no end date and says so.
                var reversedContract = terms[reversedFee].ContractId;
                Assert.Null((await ReadContractAsync(context, reversedContract)).EndDate);
                Assert.Contains("A7 (1)", await ReadEventLinesAsync(context, reversedContract, "Reversed dates"));

                // AC 12 — an account without terms is not touched.
                Assert.Equal(0, await ScalarAsync<int>(context,
                    $"SELECT COUNT(*) AS `Value` FROM `ContractParties` WHERE `AccountId` = '{untouched}'"));
                Assert.Equal(4, await ScalarAsync<int>(context, "SELECT COUNT(*) AS `Value` FROM `Contracts`"));
                Assert.Equal(4, await ScalarAsync<int>(context, "SELECT COUNT(*) AS `Value` FROM `ContractEvents`"));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// AC 7, 7a, 7b, 8, 11 — which pre-existing contract, if any, is reused.
    /// </summary>
    [SkippableFact]
    public async Task Exactly_one_matching_contract_without_a_series_collision_is_reused_untouched()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var single = Guid.NewGuid();
        var twoRoles = Guid.NewGuid();
        var ambiguous = Guid.NewGuid();
        var colliding = Guid.NewGuid();
        var archived = Guid.NewGuid();
        var capped = Guid.NewGuid();

        var singleContract = Guid.NewGuid();
        var loanContract = Guid.NewGuid();
        var twoRolesContract = Guid.NewGuid();
        var ambiguousA = Guid.NewGuid();
        var ambiguousB = Guid.NewGuid();
        var collidingContract = Guid.NewGuid();
        var archivedContract = Guid.NewGuid();
        var cappedContract = Guid.NewGuid();

        var singleTerm = Guid.NewGuid();
        var twoRolesTerm = Guid.NewGuid();
        var ambiguousTerm = Guid.NewGuid();
        var collidingTerm = Guid.NewGuid();
        var collidingExisting = Guid.NewGuid();
        var archivedTerm = Guid.NewGuid();
        var cappedTerm = Guid.NewGuid();
        var cappedExisting = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                await context.Database.ExecuteSqlRawAsync(
                    "UPDATE `SystemSettings` SET `Value` = '1' WHERE `Key` = 'ContractMaxTermsPerContract'");

                foreach (var (id, name) in new[]
                         {
                             (single, "Single"), (twoRoles, "Two roles"), (ambiguous, "Ambiguous"),
                             (colliding, "Colliding"), (archived, "Archived"), (capped, "Capped"),
                         })
                {
                    await InsertAccountAsync(context, id, name, SavingsAccount, "USD");
                }

                await InsertContractAsync(context, singleContract, "Savings agreement", Deposit, reference: "REF-1");
                await InsertPartyAsync(context, singleContract, single, null, Depositor);
                // A contract of ANOTHER type naming the account is ignored for matching — and flagged.
                await InsertContractAsync(context, loanContract, "Overdraft", Loan);
                await InsertPartyAsync(context, loanContract, single, null, Lender);

                await InsertContractAsync(context, twoRolesContract, "Two-role agreement", Deposit);
                await InsertPartyAsync(context, twoRolesContract, twoRoles, null, Depositor);
                await InsertPartyAsync(context, twoRolesContract, twoRoles, null, ObjectRole);

                await InsertContractAsync(context, ambiguousA, "First", Deposit);
                await InsertPartyAsync(context, ambiguousA, ambiguous, null, Depositor);
                await InsertContractAsync(context, ambiguousB, "Second", Deposit);
                await InsertPartyAsync(context, ambiguousB, ambiguous, null, ObjectRole);

                await InsertContractAsync(context, collidingContract, "Colliding agreement", Deposit);
                await InsertPartyAsync(context, collidingContract, colliding, null, Depositor);
                await InsertTermAsync(context, collidingExisting, null, Percentage, 0.02m, "Interest rate",
                    contractId: collidingContract);

                await InsertContractAsync(context, archivedContract, "Old agreement", Deposit, archived: Opened);
                await InsertPartyAsync(context, archivedContract, archived, null, Depositor);

                await InsertContractAsync(context, cappedContract, "Full agreement", Deposit);
                await InsertPartyAsync(context, cappedContract, capped, null, Depositor);
                await InsertTermAsync(context, cappedExisting, null, Amount, 3m, "Existing fee",
                    contractId: cappedContract, currency: "USD");

                await InsertTermAsync(context, singleTerm, single, Amount, 2m, "Service fee", currency: "USD");
                await InsertTermAsync(context, twoRolesTerm, twoRoles, Amount, 2m, "Service fee", currency: "USD");
                await InsertTermAsync(context, ambiguousTerm, ambiguous, Amount, 2m, "Service fee", currency: "USD");
                await InsertTermAsync(context, collidingTerm, colliding, Percentage, 0.03m, "Interest rate");
                await InsertTermAsync(context, archivedTerm, archived, Amount, 2m, "Service fee", currency: "USD");
                await InsertTermAsync(context, cappedTerm, capped, Amount, 2m, "Service fee", currency: "USD");
            }

            var before = await SnapshotContractsAsync(singleContract, twoRolesContract, collidingContract);

            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Subject);
                var terms = await ReadTermsAsync(context);

                // AC 7 — reused, its fields and parties unchanged.
                Assert.Equal(singleContract, terms[singleTerm].ContractId);
                Assert.Equal(
                    ["A3 (1)", "A15 (1): Single"],
                    await ReadEventLinesAsync(context, singleContract, "Single"));

                // AC 7a — two roles on one contract are one candidate.
                Assert.Equal(twoRolesContract, terms[twoRolesTerm].ContractId);

                // AC 8 — two candidates: a third contract is created and the ambiguity flagged.
                var created = terms[ambiguousTerm].ContractId;
                Assert.NotEqual(ambiguousA, created);
                Assert.NotEqual(ambiguousB, created);
                Assert.Equal("Ambiguous", (await ReadContractAsync(context, created)).Name);
                Assert.Equal(["A1 (1): Ambiguous", "A2 (2)", "A5 (1)"], await ReadEventLinesAsync(context, created, "Ambiguous"));

                // AC 7b — a colliding series is never merged into the candidate.
                var split = terms[collidingTerm].ContractId;
                Assert.NotEqual(collidingContract, split);
                Assert.Equal(collidingContract, terms[collidingExisting].ContractId);
                Assert.Contains("A9 (1)", await ReadEventLinesAsync(context, split, "Colliding"));

                // AC 11 — A10 and A11 on fixtures built to trigger them.
                Assert.Equal(archivedContract, terms[archivedTerm].ContractId);
                Assert.Contains("A11 (1)", await ReadEventLinesAsync(context, archivedContract, "Archived"));
                Assert.Equal(cappedContract, terms[cappedTerm].ContractId);
                Assert.Contains("A10 (1)", await ReadEventLinesAsync(context, cappedContract, "Capped"));
            }

            // AC 7 — byte-identical fields and parties on every reused (and every passed-over) contract.
            Assert.Equal(before, await SnapshotContractsAsync(singleContract, twoRolesContract, collidingContract));
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// AC 11a plus A12/A13 — the event text stays within its column even when one account carries
    /// 200 flipped terms, by listing labels only while they fit and counting the rest.
    /// </summary>
    [SkippableFact]
    public async Task The_attention_text_is_bounded_and_counts_what_it_cannot_list()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var many = Guid.NewGuid();
        var inactive = Guid.NewGuid();
        var inactiveFee = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                await InsertAccountAsync(context, many, "Many rates", SavingsAccount, "USD");
                for (var i = 0; i < 200; i++)
                {
                    await InsertTermAsync(context, Guid.NewGuid(), many, Percentage, 0.01m,
                        $"Promotional interest rate number {i:D3}");
                }

                // A12 — the account currency is not an active currency, so the frozen code cannot be
                // re-saved as is. A13 — the account also carries an estimate, which stays behind.
                await InsertAccountAsync(context, inactive, "Old currency", CheckingAccount, "ZZZ");
                await InsertTermAsync(context, inactiveFee, inactive, Amount, 4m, "Card fee");
                await context.Database.ExecuteSqlRawAsync($"""
                    INSERT INTO `AccountEstimates` (`AccountEstimateId`, `AccountId`, `Value`, `CurrencyCode`, `EffectiveFrom`, `CreatedAtUtc`)
                    VALUES ('{Guid.NewGuid()}', '{inactive}', 100, 'ZZZ', '2024-01-01 00:00:00', '2024-01-01 00:00:00');
                    """);
            }

            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Subject);

                var descriptions = await ReadAllDescriptionsAsync(context);
                Assert.All(descriptions, d => Assert.InRange(d.Length, 1, 1024));

                var manyContract = (await ReadTermsAsync(context)).Values.First(t => t.Label!.EndsWith("000")).ContractId;
                var a8 = Assert.Single(await ReadEventLinesAsync(context, manyContract, "Many rates"),
                    line => line.StartsWith("A8 ", StringComparison.Ordinal));
                Assert.StartsWith("A8 (200): Promotional interest rate number 000, ", a8);
                Assert.Matches(@", \+\d+ more$", a8);

                var shown = a8["A8 (200): ".Length..a8.LastIndexOf(", +", StringComparison.Ordinal)].Split(", ").Length;
                var more = int.Parse(a8[(a8.LastIndexOf('+') + 1)..a8.LastIndexOf(" more", StringComparison.Ordinal)],
                    CultureInfo.InvariantCulture);
                Assert.Equal(200, shown + more);

                var inactiveContract = (await ReadTermsAsync(context))[inactiveFee].ContractId;
                var lines = await ReadEventLinesAsync(context, inactiveContract, "Old currency");
                Assert.Contains("A12 (1): Card fee", lines);
                Assert.Contains("A13 (1)", lines);
                Assert.Equal("ZZZ", (await ReadTermsAsync(context))[inactiveFee].CurrencyCode);
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// AC 13 — interrupted after the contracts and parties exist (the event insert is made to fail),
    /// the re-run reuses the decisions already staged: no duplicate contract, party or event, the
    /// two-candidate path included, and no staging table left behind.
    /// </summary>
    [SkippableFact]
    public async Task A_rerun_after_an_interruption_reuses_every_staged_decision()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var none = Guid.NewGuid();
        var one = Guid.NewGuid();
        var two = Guid.NewGuid();
        var oneContract = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                await InsertAccountAsync(context, none, "None", SavingsAccount, "USD");
                await InsertAccountAsync(context, one, "One", SavingsAccount, "USD");
                await InsertAccountAsync(context, two, "Two", SavingsAccount, "USD");

                await InsertContractAsync(context, oneContract, "One agreement", Deposit);
                await InsertPartyAsync(context, oneContract, one, null, Depositor);
                foreach (var name in new[] { "Two A", "Two B" })
                {
                    var id = Guid.NewGuid();
                    await InsertContractAsync(context, id, name, Deposit);
                    await InsertPartyAsync(context, id, two, null, Depositor);
                }

                foreach (var account in new[] { none, one, two })
                    await InsertTermAsync(context, Guid.NewGuid(), account, Percentage, 0.01m, "Interest rate");

                await context.Database.ExecuteSqlRawAsync("""
                    CREATE TRIGGER `interrupt_event_insert` BEFORE INSERT ON `ContractEvents` FOR EACH ROW
                        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'simulated interruption';
                    """);
            }

            await using (var context = NewContext())
            {
                var failure = await Assert.ThrowsAnyAsync<Exception>(() => MigrationSeam.MigrateToAsync(context, Subject));
                Assert.Contains("simulated interruption", failure.ToString());

                // Step 2 ran: the two created contracts exist, the terms have not moved.
                Assert.Equal(5, await ScalarAsync<int>(context, "SELECT COUNT(*) AS `Value` FROM `Contracts`"));
                Assert.True(await TableExistsAsync(context, "__AccountTermMigration"));
                Assert.True(await ColumnExistsAsync(context, "Terms", "AccountId"));

                await context.Database.ExecuteSqlRawAsync("DROP TRIGGER `interrupt_event_insert`");
            }

            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Subject);

                Assert.Equal(5, await ScalarAsync<int>(context, "SELECT COUNT(*) AS `Value` FROM `Contracts`"));
                Assert.Equal(3, await ScalarAsync<int>(context, "SELECT COUNT(*) AS `Value` FROM `ContractEvents`"));
                // The three seeded Depositor parties plus one Object party per created contract.
                Assert.Equal(5, await ScalarAsync<int>(context, "SELECT COUNT(*) AS `Value` FROM `ContractParties`"));
                Assert.Equal(3, await ScalarAsync<int>(context,
                    "SELECT COUNT(DISTINCT `ContractId`) AS `Value` FROM `Terms`"));
                Assert.False(await TableExistsAsync(context, "__AccountTermMigration"));

                var terms = await ReadTermsAsync(context);
                Assert.Contains(terms.Values, t => t.ContractId == oneContract);
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// AC 14 — a term that is still account-owned after the data steps aborts the migration before
    /// any DDL. A trigger that undoes the move is the only way to leave one behind.
    /// </summary>
    [SkippableFact]
    public async Task A_remaining_account_term_aborts_before_any_schema_change()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var account = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                await InsertAccountAsync(context, account, "Stuck", SavingsAccount, "USD");
                await InsertTermAsync(context, Guid.NewGuid(), account, Percentage, 0.01m, "Interest rate");
                await context.Database.ExecuteSqlRawAsync("""
                    CREATE TRIGGER `keep_account_owner` BEFORE UPDATE ON `Terms` FOR EACH ROW
                        SET NEW.`AccountId` = OLD.`AccountId`, NEW.`ContractId` = OLD.`ContractId`;
                    """);
            }

            await using (var context = NewContext())
            {
                var failure = await Assert.ThrowsAnyAsync<Exception>(() => MigrationSeam.MigrateToAsync(context, Subject));
                Assert.Contains("1 term(s) still have an account owner", failure.ToString());

                Assert.True(await ColumnExistsAsync(context, "Terms", "AccountId"));
                Assert.Equal(0, await ScalarAsync<int>(context,
                    "SELECT COUNT(*) AS `Value` FROM `__EFMigrationsHistory` WHERE `MigrationId` LIKE '%_MoveAccountTermsToContracts'"));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// AC 15 and <c>Down()</c> — deleting a migrated account removes only its <c>Object</c> party;
    /// the revert restores the account owner column with every term still on its contract.
    /// </summary>
    [SkippableFact]
    public async Task Deleting_a_migrated_account_keeps_its_contract_and_the_revert_restores_the_schema_only()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var account = Guid.NewGuid();
        var term = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                await InsertAccountAsync(context, account, "Goes away", SavingsAccount, "USD");
                await InsertTermAsync(context, term, account, Percentage, 0.01m, "Interest rate");
            }

            Guid contract;
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Subject);
                contract = (await ReadTermsAsync(context))[term].ContractId;

                await context.Database.ExecuteSqlRawAsync($"DELETE FROM `Accounts` WHERE `AccountId` = '{account}'");

                Assert.Empty(await ReadPartiesAsync(context, contract));
                Assert.Equal(contract, (await ReadTermsAsync(context))[term].ContractId);
                Assert.Equal(1, await ScalarAsync<int>(context, "SELECT COUNT(*) AS `Value` FROM `ContractEvents`"));
            }

            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);

                Assert.True(await ColumnExistsAsync(context, "Terms", "AccountId"));
                Assert.Equal(1, await ScalarAsync<int>(context,
                    $"SELECT COUNT(*) AS `Value` FROM `Terms` WHERE `TermId` = '{term}' " +
                    $"AND `AccountId` IS NULL AND `ContractId` = '{contract}'"));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    // ── Seeding (raw SQL at the baseline schema) ────────────────────────────────────────────────

    private static Task InsertAccountAsync(
        OdysseyContext context, Guid id, string name, int type, string currency,
        Guid? custodian = null, string? accountNumber = null, string description = "",
        DateTime? closed = null) =>
        context.Database.ExecuteSqlRawAsync(
            "INSERT INTO `Accounts` (`AccountId`, `Name`, `Description`, `Opened`, `AccountNumber`, `AccountType`, " +
            "`Closed`, `Archived`, `CurrencyCode`, `CustodianId`) VALUES " +
            $"({L(id)}, {L(name)}, {L(description)}, {L(Opened)}, {L(accountNumber)}, {type}, {L(closed)}, NULL, " +
            $"{L(currency)}, {L(custodian)})");

    private static Task InsertContractAsync(
        OdysseyContext context, Guid id, string name, int type, string? reference = null, DateTime? archived = null) =>
        context.Database.ExecuteSqlRawAsync(
            "INSERT INTO `Contracts` (`ContractId`, `Name`, `Type`, `ReferenceNumber`, `StartDate`, `Archived`, `CreatedAtUtc`) " +
            $"VALUES ({L(id)}, {L(name)}, {type}, {L(reference)}, {L(Opened)}, {L(archived)}, {L(Opened)})");

    private static Task InsertPartyAsync(OdysseyContext context, Guid contract, Guid? account, Guid? contact, int role) =>
        context.Database.ExecuteSqlRawAsync(
            "INSERT INTO `ContractParties` (`ContractPartyId`, `ContractId`, `AccountId`, `ContactId`, `Role`) " +
            $"VALUES ({L(Guid.NewGuid())}, {L(contract)}, {L(account)}, {L(contact)}, {role})");

    private static Task InsertTermAsync(
        OdysseyContext context, Guid termId, Guid? accountId, int unit, decimal value, string label,
        Guid? contractId = null, string? currency = null, int? interval = null, string? note = null) =>
        context.Database.ExecuteSqlRawAsync(
            "INSERT INTO `Terms` (`TermId`, `AccountId`, `ContractId`, `Label`, `LabelKey`, `ValueUnit`, `Direction`, " +
            "`Value`, `CurrencyCode`, `Interval`, `IntervalCount`, `EffectiveFrom`, `Note`, `CreatedAtUtc`) VALUES " +
            $"({L(termId)}, {L(accountId)}, {L(contractId)}, {L(label)}, {L(label.ToLowerInvariant())}, {unit}, 0, " +
            $"{value.ToString(CultureInfo.InvariantCulture)}, {L(currency)}, {(interval is null ? "NULL" : interval)}, " +
            $"{(interval is null ? "NULL" : "1")}, {L(Opened)}, {L(note)}, {L(Opened)})");

    /// <summary>A SQL literal for a value this test wrote itself — never external input.</summary>
    private static string L(object? value) => value switch
    {
        null => "NULL",
        DateTime date => $"'{date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}'",
        string text => $"'{text.Replace("'", "''", StringComparison.Ordinal)}'",
        _ => $"'{value}'",
    };

    // ── Reading (raw SQL at the post-migration schema) ──────────────────────────────────────────

    private sealed record TermRow(
        Guid ContractId, string? Label, string? LabelKey, int ValueUnit, int Direction, decimal Value,
        string? CurrencyCode, int? Interval, int? IntervalCount, DateTime EffectiveFrom, string? Note,
        DateTime CreatedAtUtc);

    private static async Task<Dictionary<Guid, TermRow>> ReadTermsAsync(OdysseyContext context)
    {
        var rows = new Dictionary<Guid, TermRow>();
        await ReadAsync(context,
            "SELECT `TermId`, `ContractId`, `Label`, `LabelKey`, `ValueUnit`, `Direction`, `Value`, `CurrencyCode`, " +
            "`Interval`, `IntervalCount`, `EffectiveFrom`, `Note`, `CreatedAtUtc` FROM `Terms`",
            r => rows[r.GetGuid(0)] = new TermRow(
                r.GetGuid(1), NullableString(r, 2), NullableString(r, 3), r.GetInt32(4), r.GetInt32(5), r.GetDecimal(6),
                NullableString(r, 7), r.IsDBNull(8) ? null : r.GetInt32(8), r.IsDBNull(9) ? null : r.GetInt32(9),
                DateTime.SpecifyKind(r.GetDateTime(10), DateTimeKind.Utc), NullableString(r, 11),
                DateTime.SpecifyKind(r.GetDateTime(12), DateTimeKind.Utc)));
        return rows;
    }

    private sealed record ContractRow(
        string Name, int Type, string? Description, string? ReferenceNumber, DateTime? StartDate,
        DateTime? EndDate, DateTime? CompletionDate, DateTime? Archived, DateTime? Paused, DateTime? Ready,
        DateTime? Signed);

    private static async Task<ContractRow> ReadContractAsync(OdysseyContext context, Guid id)
    {
        ContractRow? row = null;
        await ReadAsync(context,
            "SELECT `Name`, `Type`, `Description`, `ReferenceNumber`, `StartDate`, `EndDate`, `CompletionDate`, " +
            $"`Archived`, `Paused`, `Ready`, `Signed` FROM `Contracts` WHERE `ContractId` = '{id}'",
            r => row = new ContractRow(
                r.GetString(0), r.GetInt32(1), NullableString(r, 2), NullableString(r, 3), NullableDate(r, 4),
                NullableDate(r, 5), NullableDate(r, 6), NullableDate(r, 7), NullableDate(r, 8), NullableDate(r, 9),
                NullableDate(r, 10)));
        return row ?? throw new InvalidOperationException($"Contract {id} not found.");
    }

    private static async Task<List<(Guid? Account, Guid? Contact, int Role)>> ReadPartiesAsync(OdysseyContext context, Guid contract)
    {
        var rows = new List<(Guid?, Guid?, int)>();
        await ReadAsync(context,
            $"SELECT `AccountId`, `ContactId`, `Role` FROM `ContractParties` WHERE `ContractId` = '{contract}'",
            r => rows.Add((r.IsDBNull(0) ? null : r.GetGuid(0), r.IsDBNull(1) ? null : r.GetGuid(1), r.GetInt32(2))));
        return rows;
    }

    /// <summary>
    /// The description lines of the one system event naming <paramref name="accountName"/> on the
    /// contract, after asserting its fixed shape (Other, System, no author).
    /// </summary>
    private static async Task<List<string>> ReadEventLinesAsync(OdysseyContext context, Guid contract, string accountName)
    {
        var events = new List<(int Type, int Source, string Title, string? Description, string? Author)>();
        await ReadAsync(context,
            "SELECT `Type`, `Source`, `Title`, `Description`, `CreatedByUserId` FROM `ContractEvents` " +
            $"WHERE `ContractId` = '{contract}'",
            r => events.Add((r.GetInt32(0), r.GetInt32(1), r.GetString(2), NullableString(r, 3), NullableString(r, 4))));

        var mine = Assert.Single(events, e => e.Title == $"Terms migrated from account \"{accountName}\"");
        Assert.Equal((8, 1, (string?)null), (mine.Type, mine.Source, mine.Author));
        return [.. (mine.Description ?? string.Empty).Split('\n')];
    }

    private static async Task<List<string>> ReadAllDescriptionsAsync(OdysseyContext context)
    {
        var rows = new List<string>();
        await ReadAsync(context, "SELECT `Description` FROM `ContractEvents`", r => rows.Add(r.GetString(0)));
        return rows;
    }

    /// <summary>Every column of the named contracts and their parties, as one comparable string.</summary>
    private async Task<string> SnapshotContractsAsync(params Guid[] ids)
    {
        await using var context = NewContext();
        var list = string.Join(", ", ids.Select(id => $"'{id}'"));
        var parts = new List<string>();
        await ReadAsync(context,
            "SELECT CONCAT_WS('|', c.`ContractId`, c.`Name`, c.`Type`, c.`Description`, c.`ReferenceNumber`, c.`StartDate`, " +
            "c.`EndDate`, c.`CompletionDate`, c.`Archived`, c.`Paused`, c.`Ready`, c.`Signed`, c.`CreatedAtUtc`) " +
            $"FROM `Contracts` c WHERE c.`ContractId` IN ({list}) ORDER BY c.`ContractId`",
            r => parts.Add(r.GetString(0)));
        await ReadAsync(context,
            "SELECT CONCAT_WS('|', p.`ContractPartyId`, p.`ContractId`, p.`AccountId`, p.`ContactId`, p.`Role`, p.`FromDate`, p.`ToDate`) " +
            $"FROM `ContractParties` p WHERE p.`ContractId` IN ({list}) ORDER BY p.`ContractPartyId`",
            r => parts.Add(r.GetString(0)));
        return string.Join('\n', parts);
    }

    private static string? NullableString(System.Data.Common.DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static DateTime? NullableDate(System.Data.Common.DbDataReader r, int i) =>
        r.IsDBNull(i) ? null : DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Utc);

    private static async Task ReadAsync(OdysseyContext context, string sql, Action<System.Data.Common.DbDataReader> row)
    {
        await context.Database.OpenConnectionAsync();
        try
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                row(reader);
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    private static Task<T> ScalarAsync<T>(OdysseyContext context, string sql) =>
        context.Database.SqlQueryRaw<T>(sql).SingleAsync();

    private static async Task<bool> ColumnExistsAsync(OdysseyContext context, string table, string column) =>
        await ScalarAsync<int>(context,
            "SELECT COUNT(*) AS `Value` FROM information_schema.COLUMNS " +
            $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}' AND COLUMN_NAME = '{column}'") > 0;

    private static async Task<bool> TableExistsAsync(OdysseyContext context, string table) =>
        await ScalarAsync<int>(context,
            "SELECT COUNT(*) AS `Value` FROM information_schema.TABLES " +
            $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}'") > 0;

    private OdysseyContext NewContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);

    private async Task RecreateAsync()
    {
        await DropAsync();
        await using var server = ServerContext();
        await server.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
    }

    private async Task DropAsync()
    {
        await using var server = ServerContext();
        await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
    }

    private OdysseyContext ServerContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);
}

#pragma warning restore EF1002
