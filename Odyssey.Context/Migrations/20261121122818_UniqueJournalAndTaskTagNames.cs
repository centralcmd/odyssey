using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// Journal and task tag names become unique, archived tags included, the way transaction and photo tag names
    /// already are: the services refuse a new or renamed tag that takes an archived tag's name, and this index is
    /// the backstop for two writes racing past that check. Before the index can be created, any names that already
    /// collide are made distinct — a database from before the rule can hold an active and an archived tag with one
    /// name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This rewrites stored rows.</b> In each group of names the unique index would treat as equal — grouped by
    /// the column's own collation, so exactly the groups the index would refuse, case and accent folds included —
    /// one tag keeps its name: an active tag first, else the most recently archived, then the lowest id. Every other
    /// tag in the group gets a numbered suffix, <c>"Bills (2)"</c>, <c>"Bills (3)"</c>, with the name shortened to fit
    /// the 64-character column. A suffixed name that collides with a tag already called that is suffixed again on
    /// the next pass; the passes stop when no group remains, and are capped so a pathological table fails at the
    /// index rather than looping. Only the name changes: links, icons, colours and archive state are untouched.
    /// </para>
    /// <para>
    /// The rename is safe to replay — on a table with no duplicates it changes nothing — and it runs before the index
    /// swap, so an interruption at any point is repaired by running the migration again. <c>Down()</c> restores the
    /// non-unique index only; renamed tags keep their new names, and undoing that takes a backup.
    /// </para>
    /// </remarks>
    public partial class UniqueJournalAndTaskTagNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            DeduplicateNames(migrationBuilder, "JournalTags", "JournalTagId");
            DeduplicateNames(migrationBuilder, "JournalTaskTags", "JournalTaskTagId");

            migrationBuilder.DropIndex(
                name: "IX_JournalTaskTags_Name",
                table: "JournalTaskTags");

            migrationBuilder.DropIndex(
                name: "IX_JournalTags_Name",
                table: "JournalTags");

            migrationBuilder.CreateIndex(
                name: "IX_JournalTaskTags_Name",
                table: "JournalTaskTags",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JournalTags_Name",
                table: "JournalTags",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_JournalTaskTags_Name",
                table: "JournalTaskTags");

            migrationBuilder.DropIndex(
                name: "IX_JournalTags_Name",
                table: "JournalTags");

            migrationBuilder.CreateIndex(
                name: "IX_JournalTaskTags_Name",
                table: "JournalTaskTags",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_JournalTags_Name",
                table: "JournalTags",
                column: "Name");
        }

        // One pass renames every tag but the keeper in each colliding group; a pass can only create a new collision
        // with a tag already carrying the suffixed name, which the next pass resolves. Twenty passes is far beyond
        // any real table — if they run out, the CreateIndex that follows fails and names the remaining duplicate.
        private static void DeduplicateNames(MigrationBuilder migrationBuilder, string table, string idColumn)
        {
            var procedure = $"odyssey_dedupe_{table}_names";
            migrationBuilder.Sql(
                $"""
                CREATE OR REPLACE PROCEDURE `{procedure}`()
                BEGIN
                  DECLARE pass INT DEFAULT 0;
                  WHILE pass < 20 AND EXISTS (SELECT 1 FROM `{table}` GROUP BY `Name` HAVING COUNT(*) > 1) DO
                    UPDATE `{table}` t
                    JOIN (
                      SELECT `{idColumn}`,
                             ROW_NUMBER() OVER (
                               PARTITION BY `Name`
                               ORDER BY `Archived` IS NOT NULL, `Archived` DESC, `{idColumn}`) AS rn
                      FROM `{table}`
                    ) d ON d.`{idColumn}` = t.`{idColumn}`
                    SET t.`Name` = CONCAT(
                          LEFT(TRIM(t.`Name`), 64 - CHAR_LENGTH(CONCAT(' (', d.rn + pass, ')'))),
                          ' (', d.rn + pass, ')')
                    WHERE d.rn > 1;
                    SET pass = pass + 1;
                  END WHILE;
                END
                """);
            migrationBuilder.Sql($"CALL `{procedure}`();");
            migrationBuilder.Sql($"DROP PROCEDURE IF EXISTS `{procedure}`;");
        }
    }
}
