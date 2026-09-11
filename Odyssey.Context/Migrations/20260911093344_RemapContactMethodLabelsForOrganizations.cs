using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// Establishes issue #47's invariant — <b>a contact method's label is valid for its contact's
    /// type</b> — over rows written before the organization vocabulary existed.
    ///
    /// <para><b>No schema change.</b> All three <c>Label</c> columns are already <c>int</c>
    /// (<c>HasConversion&lt;int&gt;()</c>), so widening the enums produces no model-snapshot
    /// difference and the scaffold came out empty. That is expected, not a tooling failure; the body
    /// below is hand-written.</para>
    ///
    /// <para><b><c>Up</c> targets the complement of the valid set</b>, not a list of known-bad values:
    /// six statements, one per (table, contact type), so a failure names where it failed and the
    /// predicate is complete by construction — an <c>IN (1,2)</c> form would miss a row holding an
    /// out-of-range ordinal, which is exactly what hand-entered dev data produces. Literal ordinals
    /// rather than enum names, so a later rename cannot break them; the lists mirror issue #47 §6.
    /// <c>Label</c> and <c>Type</c> are non-nullable, so <c>NOT IN</c> cannot go three-valued and skip
    /// rows, and idempotence is provable rather than observed: the post-state is exactly the
    /// predicate's complement.</para>
    ///
    /// <para>Rows whose <c>Contacts.Type</c> is outside <c>{1, 2}</c> are untouched by all six
    /// statements. There is no CHECK constraint, so a hand edit can create one, but such a row has no
    /// valid label set to be measured against — data corruption rather than a labelling problem
    /// (issue #47 Non-Goal 7).</para>
    /// </summary>
    public partial class RemapContactMethodLabelsForOrganizations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Addresses — Person valid {1,2,3,4,5}; Organization valid {3,4,5,20,21,22}; Other = 4
            migrationBuilder.Sql(@"UPDATE Addresses a JOIN Contacts c ON c.ContactId = a.ContactId
                                      SET a.Label = 4 WHERE c.Type = 1 AND a.Label NOT IN (1,2,3,4,5);");
            migrationBuilder.Sql(@"UPDATE Addresses a JOIN Contacts c ON c.ContactId = a.ContactId
                                      SET a.Label = 4 WHERE c.Type = 2 AND a.Label NOT IN (3,4,5,20,21,22);");

            // EmailAddresses — Person valid {1,2,3}; Organization valid {3,20,21,22,23,24}; Other = 3
            migrationBuilder.Sql(@"UPDATE EmailAddresses e JOIN Contacts c ON c.ContactId = e.ContactId
                                      SET e.Label = 3 WHERE c.Type = 1 AND e.Label NOT IN (1,2,3);");
            migrationBuilder.Sql(@"UPDATE EmailAddresses e JOIN Contacts c ON c.ContactId = e.ContactId
                                      SET e.Label = 3 WHERE c.Type = 2 AND e.Label NOT IN (3,20,21,22,23,24);");

            // PhoneNumbers — Person valid {1,2,3,4}; Organization valid {3,4,20,21,22,23,24,25,26}; Other = 4
            migrationBuilder.Sql(@"UPDATE PhoneNumbers p JOIN Contacts c ON c.ContactId = p.ContactId
                                      SET p.Label = 4 WHERE c.Type = 1 AND p.Label NOT IN (1,2,3,4);");
            migrationBuilder.Sql(@"UPDATE PhoneNumbers p JOIN Contacts c ON c.ContactId = p.ContactId
                                      SET p.Label = 4 WHERE c.Type = 2 AND p.Label NOT IN (3,4,20,21,22,23,24,25,26);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately a no-op. The pre-migration label is not recoverable and inventing one would
            // fabricate data. The real rollback hazard is the new ordinals 20–26: rows written under
            // this build are meaningless to an older one. The landing is benign — an older build
            // renders them as Other — but it is a one-way door and belongs in the release notes.
        }
    }
}
