using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// Data-only migration for issue #157 §11. <b>No schema change</b> — <c>ContractParties.Role</c> and
    /// <c>Contracts.Type</c> are already <c>int</c> columns; what changes is what the stored values mean.
    /// </summary>
    /// <remarks>
    /// <b>Every ordinal here is a literal, never a C# enum member.</b> <c>Unspecified</c> and
    /// <c>ServiceProvider</c> no longer exist in the assembly this migration compiles against, and the
    /// legal-role lists in step 3 must keep describing the matrix <em>as it was when this migration was
    /// written</em> — binding them to <c>ContractPartyRoleMatrix</c> would silently rewrite history the
    /// first time a later issue widens a cell.
    /// </remarks>
    public partial class RetireUnspecifiedAndServiceProviderPartyRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Step 1 — Unspecified (0) becomes Other (6). The distinction the two used to carry
            // ("nobody has said" vs. "somebody looked and none of these fit") stops being expressible
            // because a role is required on every write from here on, so it could only ever have
            // described history.
            migrationBuilder.Sql("UPDATE ContractParties SET Role = 6 WHERE Role = 0;");

            // Step 2 — ServiceProvider (5) becomes Seller (4), whose documented meaning — the party
            // disposing under this agreement — already covers supplying a service.
            migrationBuilder.Sql("UPDATE ContractParties SET Role = 4 WHERE Role = 5;");

            // Step 3 — every (type, role) pair the new matrix rejects becomes Other (6),
            // UNCONDITIONALLY. Not "the type's nearest legal role": there is no distance metric on the
            // matrix, so "nearest" is undefined and each implementer would resolve it differently. The
            // worked example is a Home Insurance contract whose ServiceProvider party step 2 has just
            // moved to Seller — Seller is rejected on Insurance, where Insurer, Policyholder, Insured
            // and Beneficiary are all equally near. Other is legal on every type, is honest that the
            // original value did not survive, and matches the precedent set by
            // RemapContactMethodLabelsForOrganizations. Correcting such a row to a MEANINGFUL role is
            // the seeder's job, not this migration's.
            //
            // Written per contract type, with each type's legal roles as a literal ordinal list, rather
            // than as 83 individual pairs: same result, and a list a reader can check against §4.7's
            // table. The NOT IN form also sweeps up any ordinal that is not a live member at all.
            Remap(migrationBuilder, contractType: 0, legalRoles: "1, 2, 6, 16");            // Employment
            Remap(migrationBuilder, contractType: 1, legalRoles: "3, 4, 6, 16");            // Service
            Remap(migrationBuilder, contractType: 2, legalRoles: "6, 7, 8, 15, 16");        // Rental
            Remap(migrationBuilder, contractType: 3,                                        // Other
                legalRoles: "1, 2, 3, 4, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16");
            Remap(migrationBuilder, contractType: 4, legalRoles: "6, 9, 10, 11, 12, 16");   // Insurance
            Remap(migrationBuilder, contractType: 5, legalRoles: "3, 4, 6, 16");            // Subscription
            Remap(migrationBuilder, contractType: 6, legalRoles: "3, 4, 6, 15, 16");        // Purchase
            Remap(migrationBuilder, contractType: 7, legalRoles: "3, 4, 6, 16");            // Membership
            Remap(migrationBuilder, contractType: 8, legalRoles: "6, 13, 14, 15, 16");      // Loan
        }

        /// <summary>
        /// Moves every party on a contract of <paramref name="contractType"/> whose role is not in
        /// <paramref name="legalRoles"/> to <c>Other</c> (6). Re-running it is a no-op, since
        /// <c>Other</c> is legal on every type — the migration is idempotent in effect, which matters
        /// on MariaDB, where DDL commits implicitly and an interrupted run can be replayed.
        /// </summary>
        private static void Remap(MigrationBuilder migrationBuilder, int contractType, string legalRoles) =>
            migrationBuilder.Sql(
                $"""
                UPDATE ContractParties AS p
                JOIN Contracts AS c ON c.ContractId = p.ContractId
                SET p.Role = 6
                WHERE c.Type = {contractType} AND p.Role NOT IN ({legalRoles});
                """);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately a no-op. All three steps above are LOSSY: after them a row reading Other is
            // indistinguishable from one that always read Other, so no inverse exists and fabricating
            // one would invent data. This is acceptable under the same precondition that licenses
            // Odyssey's migration squashes — no deployed database holds data anyone needs to keep — and
            // that precondition was re-checked for this change rather than inherited. The first real
            // deployment retires it permanently.
        }
    }
}
