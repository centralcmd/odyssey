using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// Moves every account-owned <c>Terms</c> row onto a contract the account takes part in, then drops
    /// the account owner from the schema (issue #190). A term keeps its <c>TermId</c>, value, label and
    /// dates; only its owner — and, where the issue says so, its currency and direction — change.
    ///
    /// <para>
    /// <b>Target resolution, per account holding at least one term.</b> The expected contract type is
    /// <c>Deposit</c> for an asset account (types 1–8), <c>Loan</c> for a liability (9–15) and
    /// <c>Other</c> for anything unclassified. A candidate is a contract of that type naming the
    /// account as a party in any role, archived or not, counted <c>DISTINCT</c> by contract because one
    /// account may hold two roles on one contract. Exactly one candidate with no series collision is
    /// reused untouched; anything else — none, several, or one that already holds a series with the same
    /// <c>(LabelKey, EffectiveFrom)</c> as a moving term (or as a term another account is moving onto
    /// it) — gets a new unsigned contract built from the account, with the account as <c>Object</c> and
    /// its custodian contact, if any, as <c>Custodian</c>/<c>Lender</c>/<c>Other</c>. Merging into a
    /// colliding series would leave both rows permanently <c>409</c> on edit, because the index is not
    /// unique and this migration bypasses <c>TermService</c>'s duplicate guard.
    /// </para>
    ///
    /// <para>
    /// <b>The attention record.</b> Each moved account gets one system <c>ContractEvent</c>
    /// (<c>Other</c>/<c>System</c>) on its target contract, titled after the account and listing the
    /// issue's flag codes A1–A15 — <c>A8 (3): Interest rate, +1 more</c> — bounded to the column's
    /// 1024 characters in SQL, so a long label list can never fail the insert. It is a to-do marker,
    /// editable and deletable like any system event. There is <b>no immutable record</b> of this
    /// migration: a <c>migrationBuilder.Sql</c> step cannot reach the application log, and the staging
    /// table is dropped at the end. The recovery path is a backup taken before upgrading.
    /// </para>
    ///
    /// <para>
    /// <b>Idempotency.</b> Every decision — the target contract id, whether it was created, the event
    /// id — is taken once and persisted in the staging table <c>__AccountTermMigration</c>, and every
    /// later step inserts with <c>WHERE NOT EXISTS</c> on that id. A re-run after an interruption in the
    /// data steps therefore reuses the decision it already made, and can never count its own created
    /// contract as a second candidate. Before any DDL a guard aborts (<c>SIGNAL SQLSTATE '45000'</c>) if
    /// any term still has an account owner, so no column is ever dropped over unmoved data.
    /// </para>
    ///
    /// <para>
    /// <b>An interruption in the final DDL is NOT covered.</b> MariaDB commits DDL implicitly, and
    /// <c>MigrationRunner</c>'s pre-check (issue #468) only detects a pending migration creating an
    /// object that already exists — not a half-finished drop sequence. A re-run then fails on whichever
    /// drop already succeeded; repair is manual, per <c>docs/migration-history-drift.md</c>.
    /// </para>
    ///
    /// <para>
    /// <b><c>Down()</c> restores the schema only.</b> The account owner column, its foreign key, index
    /// and check come back, but no term is moved back to an account and no created contract or event is
    /// removed — the same lossy precedent as <c>FoldRateTermKindsIntoFee</c>.
    /// </para>
    /// </summary>
    public partial class MoveAccountTermsToContracts : Migration
    {
        private const string Staging = "`__AccountTermMigration`";

        /// <summary>The column bound of <c>ContractEvent.Description</c>.</summary>
        private const int DescriptionLimit = 1024;

        /// <summary>
        /// Room kept per label-bearing line for its <c>": "</c> and <c>", +N more"</c> parts, so the
        /// labels themselves can never push the text past <see cref="DescriptionLimit"/>.
        /// </summary>
        private const int LabelLineReserve = 16;

        /// <summary>Only rows whose attention event has not been written yet are (re)computed.</summary>
        private const string EventPending =
            "NOT EXISTS (SELECT 1 FROM `ContractEvents` e WHERE e.`ContractEventId` = m.`EventId`)";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── 1. Staging: one row per account holding terms, decided once ──────────────────────────
            migrationBuilder.Sql($"""
                CREATE TABLE IF NOT EXISTS {Staging} (
                    `AccountId` char(36) CHARACTER SET ascii COLLATE ascii_general_ci NOT NULL,
                    `ExpectedType` int NOT NULL,
                    `CandidateCount` int NOT NULL,
                    `CandidateContractId` char(36) CHARACTER SET ascii COLLATE ascii_general_ci NULL,
                    `CollisionCount` int NOT NULL DEFAULT 0,
                    `Created` tinyint(1) NULL,
                    `TargetContractId` char(36) CHARACTER SET ascii COLLATE ascii_general_ci NULL,
                    `EventId` char(36) CHARACTER SET ascii COLLATE ascii_general_ci NULL,
                    `OtherTypeCount` int NOT NULL DEFAULT 0,
                    `CapExceeded` tinyint(1) NOT NULL DEFAULT 0,
                    `TargetArchived` tinyint(1) NOT NULL DEFAULT 0,
                    `EstimateCount` int NOT NULL DEFAULT 0,
                    `A8Count` int NOT NULL DEFAULT 0,
                    `A8Distinct` int NOT NULL DEFAULT 0,
                    `A8Shown` int NOT NULL DEFAULT 0,
                    `A8Labels` varchar({DescriptionLimit}) CHARACTER SET utf8mb4 NULL,
                    `A12Count` int NOT NULL DEFAULT 0,
                    `A12Distinct` int NOT NULL DEFAULT 0,
                    `A12Shown` int NOT NULL DEFAULT 0,
                    `A12Labels` varchar({DescriptionLimit}) CHARACTER SET utf8mb4 NULL,
                    `A14Count` int NOT NULL DEFAULT 0,
                    `A14Distinct` int NOT NULL DEFAULT 0,
                    `A14Shown` int NOT NULL DEFAULT 0,
                    `A14Labels` varchar({DescriptionLimit}) CHARACTER SET utf8mb4 NULL,
                    `Budget` int NOT NULL DEFAULT 0,
                    PRIMARY KEY (`AccountId`)
                ) CHARACTER SET utf8mb4;
                """);

            // 1a — Accounts not already staged. Candidates are counted DISTINCT by contract: the unique
            // index is (ContractId, AccountId, Role), so one account can hold two roles on one contract
            // and must still count as one candidate.
            migrationBuilder.Sql($"""
                INSERT INTO {Staging} (`AccountId`, `ExpectedType`, `CandidateCount`, `CandidateContractId`)
                SELECT x.`AccountId`, x.`ExpectedType`,
                       (SELECT COUNT(DISTINCT p.`ContractId`)
                          FROM `ContractParties` p
                          JOIN `Contracts` c ON c.`ContractId` = p.`ContractId`
                         WHERE p.`AccountId` = x.`AccountId` AND c.`Type` = x.`ExpectedType`),
                       (SELECT MIN(p.`ContractId`)
                          FROM `ContractParties` p
                          JOIN `Contracts` c ON c.`ContractId` = p.`ContractId`
                         WHERE p.`AccountId` = x.`AccountId` AND c.`Type` = x.`ExpectedType`)
                  FROM (SELECT a.`AccountId`,
                               CASE WHEN a.`AccountType` BETWEEN 1 AND 8 THEN 9
                                    WHEN a.`AccountType` BETWEEN 9 AND 15 THEN 8
                                    ELSE 3 END AS `ExpectedType`
                          FROM `Accounts` a
                         WHERE EXISTS (SELECT 1 FROM `Terms` t WHERE t.`AccountId` = a.`AccountId`)
                           AND NOT EXISTS (SELECT 1 FROM {Staging} s WHERE s.`AccountId` = a.`AccountId`)) x;
                """);

            // 1b — Series collisions for an undecided single candidate: a moving term sharing
            // (LabelKey, EffectiveFrom) with a term already on the candidate, or with a term another
            // account is about to move onto the same candidate. Either would create a duplicate the
            // service's guard then refuses to edit past.
            migrationBuilder.Sql($"""
                UPDATE {Staging} m
                   SET m.`CollisionCount` =
                       (SELECT COUNT(*) FROM `Terms` t
                         WHERE t.`AccountId` = m.`AccountId`
                           AND (EXISTS (SELECT 1 FROM `Terms` u
                                         WHERE u.`ContractId` = m.`CandidateContractId`
                                           AND u.`LabelKey` = t.`LabelKey`
                                           AND u.`EffectiveFrom` = t.`EffectiveFrom`)
                                OR EXISTS (SELECT 1 FROM `Terms` u
                                             JOIN {Staging} o ON o.`AccountId` = u.`AccountId`
                                            WHERE o.`AccountId` <> m.`AccountId`
                                              AND o.`TargetContractId` IS NULL
                                              AND o.`CandidateCount` = 1
                                              AND o.`CandidateContractId` = m.`CandidateContractId`
                                              AND u.`LabelKey` = t.`LabelKey`
                                              AND u.`EffectiveFrom` = t.`EffectiveFrom`)))
                 WHERE m.`TargetContractId` IS NULL AND m.`CandidateCount` = 1;
                """);

            // 1c — The decision, taken exactly once: the ids are assigned HERE and never recomputed.
            migrationBuilder.Sql($"""
                UPDATE {Staging}
                   SET `Created` = NOT (`CandidateCount` = 1 AND `CollisionCount` = 0),
                       `TargetContractId` = IF(`CandidateCount` = 1 AND `CollisionCount` = 0, `CandidateContractId`, UUID()),
                       `EventId` = UUID()
                 WHERE `TargetContractId` IS NULL;
                """);

            // 1d — Flag inputs, read from the PRE-MOVE state, for every row whose event is not written
            // yet. A10 counts every term that will sit on a reused contract, other accounts' included,
            // against the stored cap — only a plain number is read (a CAST of anything else warns, and a
            // warning in an UPDATE is an error under strict mode); otherwise the shipped default of 500.
            migrationBuilder.Sql($"""
                UPDATE {Staging} m
                  JOIN `Accounts` a ON a.`AccountId` = m.`AccountId`
                   SET m.`OtherTypeCount` =
                           (SELECT COUNT(DISTINCT p.`ContractId`)
                              FROM `ContractParties` p
                              JOIN `Contracts` c ON c.`ContractId` = p.`ContractId`
                             WHERE p.`AccountId` = m.`AccountId` AND c.`Type` <> m.`ExpectedType`),
                       m.`EstimateCount` =
                           (SELECT COUNT(*) FROM `AccountEstimates` e WHERE e.`AccountId` = m.`AccountId`),
                       m.`TargetArchived` =
                           m.`Created` = 0 AND EXISTS (SELECT 1 FROM `Contracts` c
                                                     WHERE c.`ContractId` = m.`TargetContractId`
                                                       AND c.`Archived` IS NOT NULL),
                       m.`CapExceeded` =
                           m.`Created` = 0
                           AND (SELECT COUNT(*) FROM `Terms` t WHERE t.`ContractId` = m.`TargetContractId`)
                             + (SELECT COUNT(*) FROM `Terms` t
                                  JOIN {Staging} o ON o.`AccountId` = t.`AccountId`
                                 WHERE o.`TargetContractId` = m.`TargetContractId`)
                             > COALESCE((SELECT CAST(s.`Value` AS UNSIGNED) FROM `SystemSettings` s
                                          WHERE s.`Key` = 'ContractMaxTermsPerContract'
                                            AND s.`Value` REGEXP '^[0-9]+$' AND CHAR_LENGTH(s.`Value`) <= 9), 500),
                       m.`A8Count` =
                           (SELECT COUNT(*) FROM `Terms` t WHERE t.`AccountId` = m.`AccountId` AND {A8Condition}),
                       m.`A8Distinct` =
                           (SELECT COUNT(DISTINCT COALESCE(t.`LabelKey`, '')) FROM `Terms` t
                             WHERE t.`AccountId` = m.`AccountId` AND {A8Condition}),
                       m.`A12Count` =
                           (SELECT COUNT(*) FROM `Terms` t WHERE t.`AccountId` = m.`AccountId` AND {A12Condition}),
                       m.`A12Distinct` =
                           (SELECT COUNT(DISTINCT COALESCE(t.`LabelKey`, '')) FROM `Terms` t
                             WHERE t.`AccountId` = m.`AccountId` AND {A12Condition}),
                       m.`A14Count` =
                           (SELECT COUNT(*) FROM `Terms` t WHERE t.`AccountId` = m.`AccountId` AND {A14Condition}),
                       m.`A14Distinct` =
                           (SELECT COUNT(DISTINCT COALESCE(t.`LabelKey`, '')) FROM `Terms` t
                             WHERE t.`AccountId` = m.`AccountId` AND {A14Condition}),
                       m.`A8Shown` = 0, m.`A8Labels` = NULL,
                       m.`A12Shown` = 0, m.`A12Labels` = NULL,
                       m.`A14Shown` = 0, m.`A14Labels` = NULL
                 WHERE {EventPending};
                """);

            // 1e — The label budget: what the description limit leaves once every code-and-count part,
            // every separator and each label line's reserve is paid for.
            migrationBuilder.Sql($"""
                UPDATE {Staging} m
                  JOIN `Accounts` a ON a.`AccountId` = m.`AccountId`
                   SET m.`Budget` = {DescriptionLimit}
                       - CHAR_LENGTH({Lines(withLabels: false)})
                       - {LabelLineReserve} * ((m.`A8Count` > 0) + (m.`A12Count` > 0) + (m.`A14Count` > 0))
                 WHERE {EventPending};
                """);

            // 1f — Labels, greedily and in code order, until the next one would overrun the budget.
            migrationBuilder.Sql(LabelStep("A8", A8Condition));
            migrationBuilder.Sql(LabelStep("A12", A12Condition));
            migrationBuilder.Sql(LabelStep("A14", A14Condition));

            // ── 2. Created contracts, then their parties ────────────────────────────────────────────
            // Left unsigned (Paused/Ready/Signed NULL), so the derived status is Draft — or Archived for
            // an archived account. Every role written is a legal cell of ContractPartyRoleMatrix:
            // Object on Deposit/Loan/Other, Custodian on Deposit, Lender on Loan, Other on Other.
            migrationBuilder.Sql($"""
                INSERT INTO `Contracts`
                    (`ContractId`, `Name`, `Type`, `Description`, `ReferenceNumber`, `StartDate`, `EndDate`,
                     `CompletionDate`, `Archived`, `Paused`, `Ready`, `Signed`, `CreatedAtUtc`)
                SELECT m.`TargetContractId`, a.`Name`, m.`ExpectedType`, NULLIF(a.`Description`, ''),
                       NULLIF(a.`AccountNumber`, ''), a.`Opened`,
                       CASE WHEN a.`Closed` >= a.`Opened` THEN a.`Closed` ELSE NULL END,
                       NULL, a.`Archived`, NULL, NULL, NULL, UTC_TIMESTAMP(6)
                  FROM {Staging} m
                  JOIN `Accounts` a ON a.`AccountId` = m.`AccountId`
                 WHERE m.`Created` = 1
                   AND NOT EXISTS (SELECT 1 FROM `Contracts` c WHERE c.`ContractId` = m.`TargetContractId`);
                """);

            migrationBuilder.Sql($"""
                INSERT INTO `ContractParties` (`ContractPartyId`, `ContractId`, `AccountId`, `ContactId`, `Role`, `FromDate`, `ToDate`)
                SELECT UUID(), m.`TargetContractId`, m.`AccountId`, NULL, 17, NULL, NULL
                  FROM {Staging} m
                 WHERE m.`Created` = 1
                   AND NOT EXISTS (SELECT 1 FROM `ContractParties` p
                                    WHERE p.`ContractId` = m.`TargetContractId`
                                      AND p.`AccountId` = m.`AccountId` AND p.`Role` = 17);
                """);

            migrationBuilder.Sql($"""
                INSERT INTO `ContractParties` (`ContractPartyId`, `ContractId`, `AccountId`, `ContactId`, `Role`, `FromDate`, `ToDate`)
                SELECT UUID(), m.`TargetContractId`, NULL, a.`CustodianId`,
                       CASE m.`ExpectedType` WHEN 9 THEN 21 WHEN 8 THEN 13 ELSE 6 END, NULL, NULL
                  FROM {Staging} m
                  JOIN `Accounts` a ON a.`AccountId` = m.`AccountId`
                 WHERE m.`Created` = 1
                   AND a.`CustodianId` IS NOT NULL
                   AND NOT EXISTS (SELECT 1 FROM `ContractParties` p
                                    WHERE p.`ContractId` = m.`TargetContractId` AND p.`ContactId` = a.`CustodianId`);
                """);

            // ── 3. One attention event per moved account ────────────────────────────────────────────
            // BEFORE the term update in step 4, and that order is load-bearing: steps 1d–1f read the
            // pre-move terms, and once the event exists a re-run never recomputes its text. Reordering
            // 3 and 4 for readability would silently empty every flag list.
            migrationBuilder.Sql($"""
                INSERT INTO `ContractEvents`
                    (`ContractEventId`, `ContractId`, `Type`, `Source`, `Title`, `Description`, `Notes`,
                     `OccurredAt`, `CreatedByUserId`, `CreatedAtUtc`)
                SELECT m.`EventId`, m.`TargetContractId`, 8, 1,
                       LEFT(CONCAT('Terms migrated from account "', a.`Name`, '"'), 256),
                       IF(CHAR_LENGTH(d.`Text`) > {DescriptionLimit}, CONCAT(LEFT(d.`Text`, 1010), '… (truncated)'), d.`Text`),
                       NULL, UTC_TIMESTAMP(6), NULL, UTC_TIMESTAMP(6)
                  FROM {Staging} m
                  JOIN `Accounts` a ON a.`AccountId` = m.`AccountId`
                  JOIN (SELECT m.`AccountId`, {Lines(withLabels: true)} AS `Text`
                          FROM {Staging} m
                          JOIN `Accounts` a ON a.`AccountId` = m.`AccountId`) d ON d.`AccountId` = m.`AccountId`
                 WHERE {EventPending};
                """);

            // ── 4. Move the terms in place (same TermId) ────────────────────────────────────────────
            // An Amount term with no currency freezes the account currency the API resolved on read; a
            // Percentage term on a Deposit becomes Incoming (A8). Both owner columns change in one
            // statement, so CK_Terms_ExactlyOneOwner holds for the row throughout.
            migrationBuilder.Sql($"""
                UPDATE `Terms` t
                  JOIN {Staging} m ON m.`AccountId` = t.`AccountId`
                  JOIN `Accounts` a ON a.`AccountId` = t.`AccountId`
                   SET t.`CurrencyCode` = IF(t.`ValueUnit` = 1 AND t.`CurrencyCode` IS NULL, a.`CurrencyCode`, t.`CurrencyCode`),
                       t.`Direction` = IF(m.`ExpectedType` = 9 AND t.`ValueUnit` = 0, 1, t.`Direction`),
                       t.`ContractId` = m.`TargetContractId`,
                       t.`AccountId` = NULL;
                """);

            // ── 5. Guard: no DDL over an unmoved term ───────────────────────────────────────────────
            // SIGNAL is only valid in a compound statement outside a stored program, and its
            // MESSAGE_TEXT takes a simple value — hence the local variable.
            migrationBuilder.Sql("""
                BEGIN NOT ATOMIC
                    DECLARE remaining INT DEFAULT 0;
                    DECLARE failure TEXT DEFAULT NULL;
                    SELECT COUNT(*) INTO remaining FROM `Terms` WHERE `AccountId` IS NOT NULL;
                    IF remaining > 0 THEN
                        SET failure = CONCAT('MoveAccountTermsToContracts: ', remaining,
                            ' term(s) still have an account owner; refusing to drop Terms.AccountId.');
                        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = failure;
                    END IF;
                END
                """);

            // ── 6. The staging table goes before any DDL ────────────────────────────────────────────
            migrationBuilder.Sql($"DROP TABLE IF EXISTS {Staging};");

            // ── 7. Drop the account owner ───────────────────────────────────────────────────────────
            migrationBuilder.DropForeignKey(
                name: "FK_Terms_Accounts_AccountId",
                table: "Terms");

            migrationBuilder.DropIndex(
                name: "IX_Terms_AccountId_LabelKey_EffectiveFrom",
                table: "Terms");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Terms_ExactlyOneOwner",
                table: "Terms");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "Terms");

            migrationBuilder.AlterColumn<Guid>(
                name: "ContractId",
                table: "Terms",
                type: "char(36)",
                nullable: false,
                collation: "ascii_general_ci",
                oldClrType: typeof(Guid),
                oldType: "char(36)",
                oldNullable: true)
                .OldAnnotation("Relational:Collation", "ascii_general_ci");
        }

        /// <summary>A8 — a Percentage term moving onto a Deposit, which step 4 flips to Incoming.</summary>
        private const string A8Condition = "m.`ExpectedType` = 9 AND t.`ValueUnit` = 0";

        /// <summary>
        /// A12 — an Amount term with no currency, which step 4 fills from the account, where that
        /// currency is inactive (archived or unknown) and the term therefore cannot be re-saved as is.
        /// </summary>
        private const string A12Condition =
            "t.`ValueUnit` = 1 AND t.`CurrencyCode` IS NULL AND NOT EXISTS (" +
            "SELECT 1 FROM `Currencies` cu WHERE cu.`CurrencyCode` = a.`CurrencyCode` AND cu.`Archived` IS NULL)";

        /// <summary>
        /// A14 — a periodic (Daily/Monthly/Annually/Weekly) Amount term landing on a Deposit or Loan,
        /// which the contracts run rate counts once the contract is signed.
        /// </summary>
        private const string A14Condition =
            "m.`ExpectedType` IN (8, 9) AND t.`ValueUnit` = 1 AND t.`Interval` IN (2, 3, 5, 7)";

        /// <summary>
        /// The description's lines in code order, one per applicable flag, joined by newlines.
        /// <c>CONCAT_WS</c> skips the <c>NULL</c> of an inapplicable flag. With
        /// <paramref name="withLabels"/> false it yields only the code-and-count parts the label
        /// budget is measured against.
        /// </summary>
        private static string Lines(bool withLabels) => $"""
            CONCAT_WS(CHAR(10 USING utf8mb4),
                IF(m.`Created` = 1, 'A1 (1)', NULL),
                IF(m.`CandidateCount` >= 2, CONCAT('A2 (', m.`CandidateCount`, ')'), NULL),
                IF(m.`OtherTypeCount` > 0, CONCAT('A3 (', m.`OtherTypeCount`, ')'), NULL),
                IF(m.`Created` = 1 AND m.`ExpectedType` = 3, 'A4 (1)', NULL),
                IF(m.`Created` = 1 AND a.`CustodianId` IS NULL, 'A5 (1)', NULL),
                IF(m.`Created` = 1 AND NULLIF(a.`AccountNumber`, '') IS NOT NULL, 'A6 (1)', NULL),
                IF(m.`Created` = 1 AND a.`Closed` < a.`Opened`, 'A7 (1)', NULL),
                {LabelLine("A8", withLabels)},
                IF(m.`CollisionCount` > 0, CONCAT('A9 (', m.`CollisionCount`, ')'), NULL),
                IF(m.`CapExceeded` = 1, 'A10 (1)', NULL),
                IF(m.`TargetArchived` = 1, 'A11 (1)', NULL),
                {LabelLine("A12", withLabels)},
                IF(m.`EstimateCount` > 0, CONCAT('A13 (', m.`EstimateCount`, ')'), NULL),
                {LabelLine("A14", withLabels)},
                IF(m.`Created` = 0, 'A15 (1)', NULL))
            """;

        /// <summary>
        /// <c>A8 (3): Interest rate, Expected return, +1 more</c> — the code, the count of affected
        /// rows, the labels that fitted the budget, and how many distinct labels did not.
        /// </summary>
        private static string LabelLine(string code, bool withLabels) => !withLabels
            ? $"IF(m.`{code}Count` > 0, CONCAT('{code} (', m.`{code}Count`, ')'), NULL)"
            : $"""
              IF(m.`{code}Count` > 0,
                 CONCAT('{code} (', m.`{code}Count`, ')',
                        IF(m.`{code}Shown` > 0, CONCAT(': ', m.`{code}Labels`), ''),
                        IF(m.`{code}Distinct` > m.`{code}Shown`,
                           CONCAT(IF(m.`{code}Shown` > 0, ', ', ': '), '+', m.`{code}Distinct` - m.`{code}Shown`, ' more'),
                           '')),
                 NULL)
              """;

        /// <summary>
        /// Fills one label-bearing line: the distinct labels (one per series key, in label order) whose
        /// running length — each label plus its <c>", "</c> — still fits the remaining budget, which is
        /// then reduced by what they used. Because the running length only grows, the labels that fit
        /// are always a prefix, so <c>+N more</c> names exactly the ones left out.
        /// </summary>
        private static string LabelStep(string code, string condition) => $"""
            UPDATE {Staging} m
              JOIN (SELECT r.`AccountId`,
                           GROUP_CONCAT(IF(r.`Run` <= r.`Budget`, r.`Label`, NULL) ORDER BY r.`Label`, r.`LabelKey` SEPARATOR ', ') AS `Shown`,
                           SUM(r.`Run` <= r.`Budget`) AS `ShownCount`,
                           MAX(IF(r.`Run` <= r.`Budget`, r.`Run`, 0)) AS `Used`
                      FROM (SELECT l.`AccountId`, l.`LabelKey`, l.`Label`, l.`Budget`,
                                   SUM(CHAR_LENGTH(l.`Label`) + 2)
                                       OVER (PARTITION BY l.`AccountId` ORDER BY l.`Label`, l.`LabelKey`
                                             ROWS UNBOUNDED PRECEDING) AS `Run`
                              FROM (SELECT m.`AccountId`, m.`Budget`, COALESCE(t.`LabelKey`, '') AS `LabelKey`,
                                           MIN(COALESCE(t.`Label`, '(unlabelled)')) AS `Label`
                                      FROM `Terms` t
                                      JOIN {Staging} m ON m.`AccountId` = t.`AccountId`
                                      JOIN `Accounts` a ON a.`AccountId` = t.`AccountId`
                                     WHERE {condition} AND {EventPending}
                                     GROUP BY m.`AccountId`, m.`Budget`, COALESCE(t.`LabelKey`, '')) l) r
                     GROUP BY r.`AccountId`) x ON x.`AccountId` = m.`AccountId`
               SET m.`{code}Labels` = x.`Shown`,
                   m.`{code}Shown` = x.`ShownCount`,
                   m.`Budget` = m.`Budget` - x.`Used`
             WHERE {EventPending};
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Schema only (see the summary): every term stays on its contract, so the restored
            // AccountId is NULL on every row, which is what the restored check requires.
            migrationBuilder.AlterColumn<Guid>(
                name: "ContractId",
                table: "Terms",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci",
                oldClrType: typeof(Guid),
                oldType: "char(36)")
                .OldAnnotation("Relational:Collation", "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "AccountId",
                table: "Terms",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.CreateIndex(
                name: "IX_Terms_AccountId_LabelKey_EffectiveFrom",
                table: "Terms",
                columns: new[] { "AccountId", "LabelKey", "EffectiveFrom" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Terms_ExactlyOneOwner",
                table: "Terms",
                sql: "((`AccountId` IS NOT NULL) + (`ContractId` IS NOT NULL)) = 1");

            migrationBuilder.AddForeignKey(
                name: "FK_Terms_Accounts_AccountId",
                table: "Terms",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "AccountId",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
