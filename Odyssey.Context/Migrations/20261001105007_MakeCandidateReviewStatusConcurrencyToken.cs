using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// Marks <c>FileAnalysisCandidateTransactions.ReviewStatus</c> as a concurrency token (issue #237) so
    /// two racing import requests cannot both turn one candidate into a ledger row.
    /// </summary>
    /// <remarks>
    /// Model-only: a concurrency token changes the <c>WHERE</c> clause EF emits on <c>UPDATE</c>, not
    /// the schema, so both directions are intentionally empty. The migration exists to carry the
    /// snapshot change.
    /// </remarks>
    public partial class MakeCandidateReviewStatusConcurrencyToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
