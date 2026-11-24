using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// Drops <c>RecipeIngredients.Preparation</c> and <c>AmountMax</c> (issue #595). A <b>data</b> migration: before the
    /// columns go, each line's preparation is appended to its name (", sifted") and a range is noted after it
    /// (" (4–6)", numbers without trailing zeros) while <c>Amount</c> keeps the lower value, so "garlic" 4–6 cloves,
    /// "unpeeled" becomes 4 cloves "garlic, unpeeled (4–6)". A result longer than the name column is cut to it, ending
    /// with "…". <see cref="Down"/> restores the columns only, empty: the rewrite is undone only by restoring a backup.
    /// </summary>
    public partial class RemoveIngredientPreparationAndRange : Migration
    {
        /// <summary>The name column's length, <c>RecipeLimits.MaxIngredientNameLength</c> when this was written.</summary>
        private const int NameLength = 200;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One assignment, so the untruncated name never reaches the varchar(200) column (strict mode would refuse it).
            // A range is only noted with its lower amount; the service never allowed an upper end without one.
            var rewritten =
                "CONCAT(`Name`, "
                + "CASE WHEN `Preparation` REGEXP '[^[:space:]]' THEN CONCAT(', ', TRIM(`Preparation`)) ELSE '' END, "
                + "CASE WHEN `AmountMax` IS NOT NULL AND `Amount` IS NOT NULL "
                + "THEN CONCAT(' (', " + Plain("`Amount`") + ", '–', " + Plain("`AmountMax`") + ", ')') ELSE '' END)";

            migrationBuilder.Sql(
                "UPDATE `RecipeIngredients` SET `Name` = "
                + $"CASE WHEN CHAR_LENGTH({rewritten}) > {NameLength} THEN CONCAT(LEFT({rewritten}, {NameLength - 1}), '…') ELSE {rewritten} END "
                + "WHERE `Preparation` REGEXP '[^[:space:]]' OR (`AmountMax` IS NOT NULL AND `Amount` IS NOT NULL);");

            migrationBuilder.DropColumn(
                name: "AmountMax",
                table: "RecipeIngredients");

            migrationBuilder.DropColumn(
                name: "Preparation",
                table: "RecipeIngredients");
        }

        /// <summary>A <c>decimal(12,3)</c> as text without trailing zeros: 4.000 → "4", 0.500 → "0.5".</summary>
        private static string Plain(string column) =>
            $"TRIM(TRAILING '.' FROM TRIM(TRAILING '0' FROM CAST({column} AS CHAR)))";

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AmountMax",
                table: "RecipeIngredients",
                type: "decimal(12,3)",
                precision: 12,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Preparation",
                table: "RecipeIngredients",
                type: "varchar(200)",
                maxLength: 200,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }
    }
}
