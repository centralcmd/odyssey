using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// Retires <c>AccountType.Property</c> (6) and <c>AccountType.Vehicle</c> (7) (issue #218). Every
    /// such account becomes a <c>Property</c> under <b>the same GUID</b> — 6 → <c>RealEstate</c>,
    /// 7 → <c>Vehicle</c>, with a detail row of kind <c>Other</c> — carrying its estimates, smart tags,
    /// file links and contract-party links; then <c>CK_Accounts_AccountTypeNotRetired</c> forbids both
    /// ordinals for good.
    ///
    /// <para>
    /// <b>An account with transactions is kept</b>, because a property holds none: it is re-typed to
    /// <c>OtherAsset</c> (8) and archived if it was not already, and keeps its transactions, custodian,
    /// account number and smart tags (which are <em>copied</em>, since they filter its transactions).
    /// Its <b>file links are copied too, not moved</b>: an account file may carry a
    /// <c>FileAnalysisJob</c> — the consent record and the provenance of imported transactions — which
    /// cascades with the link, and nothing on a retained account is meant to be lost. An account
    /// without transactions is deleted once emptied; its custodian goes with it (a property has no
    /// custodian, and copying the contact's name would move <c>contacts.read</c> data under
    /// <c>properties.read</c>), and so do any analysis jobs on its file links.
    /// </para>
    ///
    /// <para>
    /// <b>Set-based SQL with constant text only.</b> No row is read into C# and no stored value is ever
    /// interpolated into a statement — <c>Notes</c> is composed by <c>CONCAT</c> inside the engine.
    /// <c>AccountNumber</c> is free text written through <c>POST /api/accounts</c>; composing it into SQL
    /// here would be a second-order injection at migration-runner privilege (CWE-89).
    /// </para>
    ///
    /// <para>
    /// <b>Atomicity and replay.</b> Every DML statement runs inside the migration's transaction, and the
    /// <c>CHECK</c> (DDL, which MariaDB commits implicitly) runs last. Each statement selects only rows
    /// still typed 6/7, so an interruption before the <c>CHECK</c> leaves either nothing (rolled back) or
    /// a fully migrated database a replay passes over untouched. An interruption after the <c>CHECK</c>
    /// but before the history row is caught by <c>MigrationRunner</c>'s drift guard, which recognises
    /// check constraints for this reason.
    /// </para>
    ///
    /// <para>
    /// <b>The counts are logged by the migrations job, not here</b> — a <c>migrationBuilder.Sql</c> step
    /// cannot reach the application log. See <c>AccountTypeRetirementReport</c>.
    /// </para>
    ///
    /// <para>
    /// <b><c>Down()</c> drops the constraint only.</b> The data move is not reversible: no property is
    /// turned back into an account.
    /// </para>
    /// </summary>
    public partial class RetirePropertyAndVehicleAccountTypes : Migration
    {
        /// <summary>The rows this migration moves; every statement is scoped by it.</summary>
        private const string Retired = "a.`AccountType` IN (6, 7)";

        private const string HasTransactions =
            "EXISTS (SELECT 1 FROM `Transactions` t WHERE t.`AccountId` = a.`AccountId`)";

        /// <summary>
        /// <c>AccountFileType</c> → <c>PropertyFileType</c>. Seven account values have no property
        /// counterpart and land on <c>Other</c>; the file and the link survive either way.
        /// </summary>
        /// <remarks>
        /// Tax 4 → 10, InsurancePolicy 6 → Insurance 6, PurchaseAgreement 9 → 2, Valuation 10 → 3,
        /// Warranty 11 → 7, Registration 12 → 5; Other, Message, Statement, Contract, Documentation,
        /// LoanAgreement, RepaymentSchedule and Prospectus → Other 0.
        /// </remarks>
        private const string FileTypeMap =
            "CASE f.`FileType` WHEN 4 THEN 10 WHEN 6 THEN 6 WHEN 9 THEN 2 WHEN 10 THEN 3 WHEN 11 THEN 7 WHEN 12 THEN 5 ELSE 0 END";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1 — One property per account, same GUID. A Closed before Opened cannot be a DisposedDate
            // (disposal may not precede acquisition), so it is dropped there and kept in Notes instead.
            migrationBuilder.Sql($"""
                INSERT INTO `Properties`
                    (`PropertyId`, `Name`, `Description`, `Type`, `CurrencyCode`, `AcquiredDate`, `DisposedDate`,
                     `Notes`, `Archived`, `CreatedAt`, `UpdatedAt`)
                SELECT a.`AccountId`, a.`Name`, a.`Description`, IF(a.`AccountType` = 6, 0, 1), a.`CurrencyCode`,
                       a.`Opened`,
                       CASE WHEN a.`Closed` >= a.`Opened` THEN a.`Closed` ELSE NULL END,
                       CONCAT('Migrated from account on ', DATE_FORMAT(UTC_TIMESTAMP(), '%Y-%m-%d'), '.',
                              IF(a.`AccountNumber` IS NOT NULL AND a.`AccountNumber` <> '',
                                 CONCAT(' Account number: ', a.`AccountNumber`, '.'), ''),
                              IF(a.`Closed` < a.`Opened`,
                                 CONCAT(' Original closed date: ', DATE_FORMAT(a.`Closed`, '%Y-%m-%d'), '.'), '')),
                       a.`Archived`, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)
                  FROM `Accounts` a
                 WHERE {Retired};
                """);

            // 2 — The 1:1 detail row, kind Other; nothing is inferred from free text.
            migrationBuilder.Sql("""
                INSERT INTO `RealEstateDetails` (`PropertyId`, `Kind`)
                SELECT a.`AccountId`, 5 FROM `Accounts` a WHERE a.`AccountType` = 6;
                """);
            migrationBuilder.Sql("""
                INSERT INTO `VehicleDetails` (`PropertyId`, `Kind`)
                SELECT a.`AccountId`, 4 FROM `Accounts` a WHERE a.`AccountType` = 7;
                """);

            // 3 — Estimates move with their ids and values; smart tags and file links are copied, and the
            // account delete in step 6 removes the originals wherever the account is not kept.
            migrationBuilder.Sql($"""
                INSERT INTO `PropertyEstimates`
                    (`PropertyEstimateId`, `PropertyId`, `Value`, `CurrencyCode`, `EffectiveFrom`, `Note`, `CreatedAtUtc`)
                SELECT e.`AccountEstimateId`, e.`AccountId`, e.`Value`, e.`CurrencyCode`, e.`EffectiveFrom`, e.`Note`,
                       e.`CreatedAtUtc`
                  FROM `AccountEstimates` e
                  JOIN `Accounts` a ON a.`AccountId` = e.`AccountId`
                 WHERE {Retired};
                """);
            migrationBuilder.Sql($"""
                DELETE e FROM `AccountEstimates` e
                  JOIN `Accounts` a ON a.`AccountId` = e.`AccountId`
                 WHERE {Retired};
                """);

            migrationBuilder.Sql($"""
                INSERT INTO `PropertySmartTags` (`PropertyId`, `TransactionTagId`, `AddedAt`)
                SELECT s.`AccountId`, s.`TransactionTagId`, s.`AddedAt`
                  FROM `AccountSmartTags` s
                  JOIN `Accounts` a ON a.`AccountId` = s.`AccountId`
                 WHERE {Retired};
                """);

            migrationBuilder.Sql($"""
                INSERT INTO `PropertyFiles`
                    (`PropertyFileId`, `PropertyId`, `FileMetadataId`, `FileType`, `AttachedAtUtc`, `AttachedByUserId`,
                     `IssuedAt`, `IssuedBy`, `ValidFrom`, `ValidTo`)
                SELECT UUID(), f.`AccountId`, f.`FileMetadataId`, {FileTypeMap}, f.`AttachedAtUtc`, f.`AttachedByUserId`,
                       f.`IssuedAt`, f.`IssuedBy`, f.`ValidFrom`, f.`ValidTo`
                  FROM `AccountFiles` f
                  JOIN `Accounts` a ON a.`AccountId` = f.`AccountId`
                 WHERE {Retired};
                """);

            // 4 — Contract parties are re-pointed in place: same row, id and role. A single-table UPDATE,
            // because only that form evaluates its assignments left to right — PropertyId must read the
            // AccountId before it is cleared — and each row then satisfies CK_ContractParties_ExactlyOneTarget.
            migrationBuilder.Sql($"""
                UPDATE `ContractParties`
                   SET `PropertyId` = `AccountId`, `AccountId` = NULL
                 WHERE `AccountId` IN (SELECT a.`AccountId` FROM `Accounts` a WHERE {Retired});
                """);

            // 5 — Accounts holding transactions are kept as archived Other-asset accounts.
            migrationBuilder.Sql($"""
                UPDATE `Accounts` a
                   SET a.`AccountType` = 8, a.`Archived` = COALESCE(a.`Archived`, UTC_TIMESTAMP(6))
                 WHERE {Retired} AND {HasTransactions};
                """);

            // 6 — The rest are deleted; their smart-tag and file links (and any analysis jobs on those
            // links) cascade, everything else having already moved.
            migrationBuilder.Sql($"""
                DELETE a FROM `Accounts` a WHERE {Retired};
                """);

            // 7 — Last, after all DML: MariaDB commits DDL implicitly.
            migrationBuilder.AddCheckConstraint(
                name: "CK_Accounts_AccountTypeNotRetired",
                table: "Accounts",
                sql: "`AccountType` NOT IN (6, 7)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Accounts_AccountTypeNotRetired",
                table: "Accounts");
        }
    }
}
