using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// Relocates every <c>InsurancePolicyFiles</c> row onto a <c>PolicyRenewals</c> period, so a period
    /// becomes the only home for an insurance document (issue #26).
    ///
    /// <para>
    /// <strong>This migration contains no DDL at all</strong>, and that is what makes its assertion
    /// meaningful. MariaDB commits DDL implicitly, so a single <c>CREATE TABLE</c> here would commit
    /// the placeholder periods and relocated rows before the assertion could reject them. The ledger's
    /// table is created by the preceding migration and the source table is dropped by the following
    /// one; what is left here is pure DML plus a <c>TEMPORARY</c> table, which is the documented
    /// exception to the implicit-commit rule. EF's transaction is therefore still open when the
    /// assertion raises, and everything this migration wrote rolls back with it.
    /// </para>
    ///
    /// <para>
    /// <strong>The assertion is three-way and value-checking, in BOTH directions.</strong> An
    /// existence check could not distinguish a correct copy from a mis-copied one, because the ledger
    /// is written by the same statement that resolves the destination. At assertion time the originals
    /// are still there to compare against — the last moment in the design where original and copy
    /// coexist — so for every source row it requires a ledger row, a live <c>PolicyRenewalFiles</c>
    /// row for the resolved <c>(period, file)</c> pair (for <c>SkippedDuplicate</c> as well as
    /// <c>Relocated</c>, or the document is gone), <c>source == ledger</c> field equality, and — for a
    /// <c>Relocated</c> row — <c>ledger == destination</c> field equality too.
    /// </para>
    ///
    /// <para>
    /// That second direction is easy to leave out and was: checking the destination for existence
    /// alone verifies that the resolution join found the right period, not that step 4 wrote the right
    /// values into it. A swapped column alias there would have produced a destination row that the
    /// ledger described correctly and that did not match it, and nothing would have fired.
    /// Comparisons use MariaDB's NULL-safe <c>&lt;=&gt;</c>, never <c>=</c>: <c>EffectiveDate</c> and
    /// <c>AttachedByUserId</c> are nullable and a null <c>EffectiveDate</c> is common, so <c>=</c>
    /// would yield <c>NULL</c> and silently pass every row the check exists to catch.
    /// </para>
    ///
    /// <para>
    /// <strong>Replay safety is structural, not guarded.</strong> Every write carries its own
    /// <c>NOT EXISTS</c>, so a re-entry after a transient failure is idempotent; the ledger insert is
    /// guarded on <c>SourceId</c> specifically, or a re-entry would see its own prior inserts and
    /// reclassify an already-<c>Relocated</c> row as <c>SkippedDuplicate</c>, stranding it past
    /// <c>Down</c>. Each anti-join is wrapped in a derived table so the target of the insert is
    /// materialised before the insert reads it.
    /// </para>
    ///
    /// <para>
    /// <strong>The clock is pinned to a scaffold-time literal</strong> rather than
    /// <c>UTC_TIMESTAMP()</c>, so <c>Up</c> is reproducible, an acceptance criterion can assert an exact
    /// value, and a placeholder period is dated near when the data was last coherent rather than near
    /// whenever a given environment happens to deploy.
    /// </para>
    ///
    /// <para>
    /// The API is down while migrations run — <c>docker-compose.yml</c>'s
    /// <c>service_completed_successfully</c> and Aspire's <c>.WaitForCompletion(migrations)</c> — so no
    /// request can attach a policy-level document between the relocation and the drop. A deployment
    /// topology that starts the API in parallel would break this migration.
    /// </para>
    /// </summary>
    public partial class MoveInsurancePolicyFilesToRenewals : Migration
    {
        /// <summary>
        /// The pinned scaffold-time instant: one day before this migration was authored. It dates the
        /// placeholder periods and is asserted verbatim by the acceptance criteria, so it must never be
        /// swapped for an apply-time clock.
        /// </summary>
        private const string PinnedInstant = "2026-08-30 00:00:00.000000";

        /// <summary>
        /// The placeholder period's <c>Notes</c>. It is user-editable, so it is documentation rather
        /// than an identifier — structural identity comes from the ledger's
        /// <c>PlaceholderPeriodCreated</c> flag, which is what <c>Down</c> keys on.
        /// </summary>
        private const string PlaceholderNotesPrefix = "Auto-created during migration to preserve ";

        private const string PlaceholderNotesSuffix =
            " document(s) that were attached to the policy rather than to a period. The dates, " +
            "premium (0) and coverage (0) are placeholders — please correct them or move the " +
            "documents to a real period.";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. A session temporary table can outlive a failed attempt on a pooled connection, and
            //    would then mask the real fault with ERROR 1050 on the next run.
            migrationBuilder.Sql("DROP TEMPORARY TABLE IF EXISTS `_assert`;");

            // 2. A policy holding documents but no period gets one placeholder, or its documents would
            //    have nowhere to go. Any period at all makes EvaluateCoverage report Lapsed rather than
            //    NoCoverage — forced, not chosen, and better than discarding the paperwork.
            migrationBuilder.Sql($"""
                INSERT INTO `PolicyRenewals`
                    (`PolicyRenewalId`, `InsurancePolicyId`, `FromDate`, `ToDate`, `Premium`,
                     `PremiumCurrencyCode`, `CoverageAmount`, `CoverageCurrencyCode`, `Notes`, `CreatedAtUtc`)
                SELECT UUID(), orphan.`InsurancePolicyId`,
                       '{PinnedInstant}', '{PinnedInstant}', 0, 'USD', 0, 'USD',
                       CONCAT('{PlaceholderNotesPrefix}', orphan.`DocumentCount`, '{PlaceholderNotesSuffix}'),
                       '{PinnedInstant}'
                FROM (
                    SELECT f.`InsurancePolicyId` AS `InsurancePolicyId`, COUNT(*) AS `DocumentCount`
                    FROM `InsurancePolicyFiles` f
                    WHERE NOT EXISTS (
                        SELECT 1 FROM `PolicyRenewals` r
                        WHERE r.`InsurancePolicyId` = f.`InsurancePolicyId`)
                    GROUP BY f.`InsurancePolicyId`
                ) orphan;
                """);

            // 3. The ledger, written from the same join that resolves the destination period. That
            //    coupling is what keeps the assertion in step 5 non-tautological: a source row the
            //    resolution fails to place gets no ledger row, so the anti-join finds it. A refactor
            //    that ledgered every source row unconditionally would restore the tautology.
            //
            //    "The first period" is FromDate, then CreatedAtUtc, then the primary key — a total
            //    order, so the result is deterministic across re-runs. Earliest rather than latest: a
            //    document predating any period belongs to the start of cover. (The row-menu attach in
            //    the client targets the LATEST period; both are deliberate and neither should be
            //    "fixed" to match the other.)
            //
            //    DestinationPolicyRenewalFileId is null for a skipped duplicate — nothing was inserted,
            //    and Down deletes precisely the rows this migration created, never the pre-existing one
            //    the user chose deliberately.
            migrationBuilder.Sql($"""
                INSERT INTO `_InsurancePolicyFileRelocation`
                    (`SourceId`, `InsurancePolicyId`, `FileMetadataId`, `FileType`, `EffectiveDate`,
                     `AttachedByUserId`, `AttachedAtUtc`, `DestinationPolicyRenewalId`,
                     `DestinationPolicyRenewalFileId`, `Outcome`, `PlaceholderPeriodCreated`, `MigratedAtUtc`)
                SELECT `SourceId`, `InsurancePolicyId`, `FileMetadataId`, `FileType`, `EffectiveDate`,
                       `AttachedByUserId`, `AttachedAtUtc`, `DestinationPolicyRenewalId`,
                       `DestinationPolicyRenewalFileId`, `Outcome`, `PlaceholderPeriodCreated`, `MigratedAtUtc`
                FROM (
                    SELECT s.`Id`               AS `SourceId`,
                           s.`InsurancePolicyId` AS `InsurancePolicyId`,
                           s.`FileMetadataId`   AS `FileMetadataId`,
                           s.`FileType`         AS `FileType`,
                           s.`EffectiveDate`    AS `EffectiveDate`,
                           s.`AttachedByUserId` AS `AttachedByUserId`,
                           s.`AttachedAtUtc`    AS `AttachedAtUtc`,
                           first_period.`PolicyRenewalId` AS `DestinationPolicyRenewalId`,
                           CASE WHEN existing.`Id` IS NULL THEN UUID() END AS `DestinationPolicyRenewalFileId`,
                           CASE WHEN existing.`Id` IS NULL THEN 'Relocated' ELSE 'SkippedDuplicate' END AS `Outcome`,
                           CASE WHEN first_period.`FromDate` = '{PinnedInstant}'
                                 AND first_period.`ToDate` = '{PinnedInstant}'
                                 AND first_period.`CreatedAtUtc` = '{PinnedInstant}'
                                THEN 1 ELSE 0 END AS `PlaceholderPeriodCreated`,
                           UTC_TIMESTAMP(6) AS `MigratedAtUtc`
                    FROM `InsurancePolicyFiles` s
                    JOIN (
                        SELECT r.`PolicyRenewalId`, r.`InsurancePolicyId`, r.`FromDate`, r.`ToDate`,
                               r.`CreatedAtUtc`,
                               ROW_NUMBER() OVER (
                                   PARTITION BY r.`InsurancePolicyId`
                                   ORDER BY r.`FromDate`, r.`CreatedAtUtc`, r.`PolicyRenewalId`) AS `rn`
                        FROM `PolicyRenewals` r
                    ) first_period
                      ON first_period.`InsurancePolicyId` = s.`InsurancePolicyId`
                     AND first_period.`rn` = 1
                    LEFT JOIN `PolicyRenewalFiles` existing
                      ON existing.`PolicyRenewalId` = first_period.`PolicyRenewalId`
                     AND existing.`FileMetadataId` = s.`FileMetadataId`
                    WHERE NOT EXISTS (
                        SELECT 1 FROM `_InsurancePolicyFileRelocation` ledger
                        WHERE ledger.`SourceId` = s.`Id`)
                ) resolved;
                """);

            // 4. The relocated rows themselves, from the ledger the previous statement wrote — so the
            //    id in the ledger is the id of the row that exists, not a second guess at it. An
            //    explicit NOT EXISTS, never INSERT IGNORE: IGNORE would also swallow foreign-key
            //    violations, NOT NULL breaches and truncations, turning a data fault into silent row
            //    loss. Intra-batch collision is impossible because the source table's unique index on
            //    (InsurancePolicyId, FileMetadataId) allows at most one source row per (policy, file)
            //    and all of a policy's rows resolve to the same destination.
            migrationBuilder.Sql("""
                INSERT INTO `PolicyRenewalFiles`
                    (`Id`, `PolicyRenewalId`, `FileMetadataId`, `FileType`, `EffectiveDate`,
                     `AttachedByUserId`, `AttachedAtUtc`)
                SELECT `Id`, `PolicyRenewalId`, `FileMetadataId`, `FileType`, `EffectiveDate`,
                       `AttachedByUserId`, `AttachedAtUtc`
                FROM (
                    SELECT l.`DestinationPolicyRenewalFileId` AS `Id`,
                           l.`DestinationPolicyRenewalId`     AS `PolicyRenewalId`,
                           l.`FileMetadataId`                 AS `FileMetadataId`,
                           l.`FileType`                       AS `FileType`,
                           l.`EffectiveDate`                  AS `EffectiveDate`,
                           l.`AttachedByUserId`               AS `AttachedByUserId`,
                           l.`AttachedAtUtc`                  AS `AttachedAtUtc`
                    FROM `_InsurancePolicyFileRelocation` l
                    WHERE l.`Outcome` = 'Relocated'
                      AND l.`DestinationPolicyRenewalFileId` IS NOT NULL
                      AND NOT EXISTS (
                          SELECT 1 FROM `PolicyRenewalFiles` d
                          WHERE d.`PolicyRenewalId` = l.`DestinationPolicyRenewalId`
                            AND d.`FileMetadataId` = l.`FileMetadataId`)
                ) relocated;
                """);

            // 5. The assertion. A CHECK on a TEMPORARY table is enforced by MariaDB and raises
            //    ERROR 4025 (23000), which is absent from Pomelo's transient allow-list — so
            //    MigrateAsync's execution strategy will not retry it. A stored procedure with SIGNAL
            //    would have been DDL, and DDL commits.
            migrationBuilder.Sql("CREATE TEMPORARY TABLE `_assert` (`n` INT NOT NULL CHECK (`n` = 0));");

            migrationBuilder.Sql("""
                INSERT INTO `_assert` (`n`)
                SELECT COUNT(*) FROM `InsurancePolicyFiles` s
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM `_InsurancePolicyFileRelocation` l
                    JOIN `PolicyRenewalFiles` d
                      ON d.`PolicyRenewalId` = l.`DestinationPolicyRenewalId`
                     AND d.`FileMetadataId` = l.`FileMetadataId`
                    WHERE l.`SourceId` = s.`Id`
                      -- source <-> ledger: the copy is faithful to the original.
                      AND l.`InsurancePolicyId` <=> s.`InsurancePolicyId`
                      AND l.`FileMetadataId`   <=> s.`FileMetadataId`
                      AND l.`FileType`         <=> s.`FileType`
                      AND l.`EffectiveDate`    <=> s.`EffectiveDate`
                      AND l.`AttachedByUserId` <=> s.`AttachedByUserId`
                      AND l.`AttachedAtUtc`    <=> s.`AttachedAtUtc`
                      -- ledger <-> destination: and what was actually WRITTEN is that copy. Without
                      -- this the destination is only ever checked for existence, so a fault in the
                      -- insert of step 4 — a swapped column alias, say — would write wrong data and
                      -- still pass. Scoped to Relocated: a SkippedDuplicate names no destination row
                      -- of its own, and the row that is there is the user's, whose type, effective
                      -- date and attribution are legitimately their own.
                      AND (l.`Outcome` <> 'Relocated' OR (
                               d.`Id`               <=> l.`DestinationPolicyRenewalFileId`
                           AND d.`FileType`         <=> l.`FileType`
                           AND d.`EffectiveDate`    <=> l.`EffectiveDate`
                           AND d.`AttachedByUserId` <=> l.`AttachedByUserId`
                           AND d.`AttachedAtUtc`    <=> l.`AttachedAtUtc`)));
                """);

            migrationBuilder.Sql("DROP TEMPORARY TABLE IF EXISTS `_assert`;");
        }

        /// <summary>
        /// Reconstructs <c>InsurancePolicyFiles</c> from the ledger's full payload and undoes exactly
        /// what <c>Up</c> did — nothing it did not create.
        ///
        /// <para>
        /// The relocated rows are deleted <em>by row id</em>, never by <c>(period, file)</c> pair, so a
        /// later detach-and-reattach of the same file is not mistaken for this migration's own row. The
        /// placeholder periods are found through the ledger's flag rather than a <c>Notes</c> substring,
        /// which a user editing the note as it asks would have broken, and only where they hold nothing
        /// any more.
        /// </para>
        ///
        /// <para>
        /// It cannot fail on a foreign key: the ledger carries the same three keys the source table did,
        /// so a policy or file deleted since <c>Up</c> has already cascaded its ledger row away and a
        /// deleted user's row correctly restores <c>NULL</c>.
        /// </para>
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO `InsurancePolicyFiles`
                    (`Id`, `InsurancePolicyId`, `FileMetadataId`, `FileType`, `EffectiveDate`,
                     `AttachedByUserId`, `AttachedAtUtc`)
                SELECT `Id`, `InsurancePolicyId`, `FileMetadataId`, `FileType`, `EffectiveDate`,
                       `AttachedByUserId`, `AttachedAtUtc`
                FROM (
                    SELECT l.`SourceId`          AS `Id`,
                           l.`InsurancePolicyId` AS `InsurancePolicyId`,
                           l.`FileMetadataId`    AS `FileMetadataId`,
                           l.`FileType`          AS `FileType`,
                           l.`EffectiveDate`     AS `EffectiveDate`,
                           l.`AttachedByUserId`  AS `AttachedByUserId`,
                           l.`AttachedAtUtc`     AS `AttachedAtUtc`
                    FROM `_InsurancePolicyFileRelocation` l
                    WHERE NOT EXISTS (
                        SELECT 1 FROM `InsurancePolicyFiles` s WHERE s.`Id` = l.`SourceId`)
                ) restored;
                """);

            migrationBuilder.Sql("""
                DELETE d FROM `PolicyRenewalFiles` d
                JOIN `_InsurancePolicyFileRelocation` l
                  ON l.`DestinationPolicyRenewalFileId` = d.`Id`
                WHERE l.`Outcome` = 'Relocated';
                """);

            migrationBuilder.Sql("""
                DELETE r FROM `PolicyRenewals` r
                JOIN (
                    SELECT DISTINCT l.`DestinationPolicyRenewalId` AS `PolicyRenewalId`
                    FROM `_InsurancePolicyFileRelocation` l
                    WHERE l.`PlaceholderPeriodCreated` = 1
                ) placeholder ON placeholder.`PolicyRenewalId` = r.`PolicyRenewalId`
                WHERE NOT EXISTS (
                    SELECT 1 FROM `PolicyRenewalFiles` f
                    WHERE f.`PolicyRenewalId` = r.`PolicyRenewalId`);
                """);
        }
    }
}
