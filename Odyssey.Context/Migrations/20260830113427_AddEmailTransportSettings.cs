using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// The four remaining <c>Email:*</c> transport settings move out of deploy-time configuration and
    /// into the database-backed store (issue #8).
    ///
    /// <para>
    /// Data only — no tables, columns, indexes or foreign keys. <c>SystemSettings</c> is a key-value
    /// table whose <c>Key</c> is the natural primary key, so a new setting is four inserts and the
    /// matching <c>HasData</c> entries on <c>OdysseyContext</c>.
    /// </para>
    ///
    /// <para>
    /// <strong>The two empty values are deliberate and are NOT a placeholder for an adoption step.</strong>
    /// There is no path from <c>appsettings.json</c> or the environment into this store: the variables
    /// were deleted rather than migrated, on the same precondition that retired
    /// <c>SystemSettingsConfigAdoption</c> — no deployment has ever run a release these keys could be
    /// carried over from. A compile-time <c>InsertData</c> cannot see an operator's environment
    /// variable anyway, and would silently replace their value with the shipped default if it tried.
    /// The consequence is that mail is off on a fresh deployment until an administrator sets a relay
    /// at <c>/settings</c>; see <c>docs/deployment.md</c>.
    /// </para>
    /// </summary>
    public partial class AddEmailTransportSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "SystemSettings",
                columns: new[] { "Key", "UpdatedAt", "UpdatedBy", "Value" },
                values: new object[,]
                {
                    { "EmailClientBaseUrl", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "" },
                    { "EmailSmtpHost", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "" },
                    { "EmailSmtpPort", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "587" },
                    { "EmailUseStartTls", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "true" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "EmailClientBaseUrl");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "EmailSmtpHost");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "EmailSmtpPort");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "EmailUseStartTls");
        }
    }
}
