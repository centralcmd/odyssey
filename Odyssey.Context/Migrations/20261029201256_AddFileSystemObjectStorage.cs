using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddFileSystemObjectStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FileStorageMaintenanceLeases",
                columns: table => new
                {
                    Id = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    HolderId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    LeaseUntilUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Version = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    LastCompletedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileStorageMaintenanceLeases", x => x.Id);
                    table.CheckConstraint("CK_FileStorageMaintenanceLeases_Singleton", "`Id` = 1");
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "FileStorageVolumes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    InitializedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LastSeenAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ReconciliationPass = table.Column<int>(type: "int", nullable: false),
                    ReconciliationCursor = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    LastPassOrphanCount = table.Column<int>(type: "int", nullable: true),
                    ReconciliationHaltedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    QuarantinedFileCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileStorageVolumes", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "FileStorageOrphanSightings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    VolumeId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    RelativePath = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    FirstSeenAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    LastSeenPass = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileStorageOrphanSightings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FileStorageOrphanSightings_FileStorageVolumes_VolumeId",
                        column: x => x.VolumeId,
                        principalTable: "FileStorageVolumes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "StoredObjects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    VolumeId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FeatureKey = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RelativePath = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256Hash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    State = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    IntegrityFaultAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    DeleteRequestedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    DeleteImmediately = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    LastVerifiedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoredObjects", x => x.Id);
                    table.CheckConstraint("CK_StoredObjects_DeleteRequestedWhenPendingDelete", "`State` <> 2 OR `DeleteRequestedAtUtc` IS NOT NULL");
                    table.CheckConstraint("CK_StoredObjects_HashUnlessPending", "`State` = 0 OR `Sha256Hash` IS NOT NULL");
                    table.CheckConstraint("CK_StoredObjects_SizeNonNegative", "`SizeBytes` >= 0");
                    table.CheckConstraint("CK_StoredObjects_StateRange", "`State` IN (0, 1, 2)");
                    table.ForeignKey(
                        name: "FK_StoredObjects_FileStorageVolumes_VolumeId",
                        column: x => x.VolumeId,
                        principalTable: "FileStorageVolumes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.InsertData(
                table: "FileStorageMaintenanceLeases",
                columns: new[] { "Id", "HolderId", "LastCompletedAtUtc", "LeaseUntilUtc", "Version" },
                values: new object[] { (byte)1, null, null, null, new Guid("3c8f0d4e-5a8b-4f6e-9a51-2b7d1e0c9f34") });

            migrationBuilder.InsertData(
                table: "SystemSettings",
                columns: new[] { "Key", "UpdatedAt", "UpdatedBy", "Value" },
                values: new object[] { "FileStorageDeletionGraceHours", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "24" });

            migrationBuilder.CreateIndex(
                name: "IX_FileStorageOrphanSightings_VolumeId_RelativePath",
                table: "FileStorageOrphanSightings",
                columns: new[] { "VolumeId", "RelativePath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FileStorageVolumes_Name",
                table: "FileStorageVolumes",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StoredObjects_State_CreatedAtUtc",
                table: "StoredObjects",
                columns: new[] { "State", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_StoredObjects_State_DeleteRequestedAtUtc",
                table: "StoredObjects",
                columns: new[] { "State", "DeleteRequestedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_StoredObjects_State_LastVerifiedAtUtc",
                table: "StoredObjects",
                columns: new[] { "State", "LastVerifiedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_StoredObjects_VolumeId_FeatureKey_State_SizeBytes",
                table: "StoredObjects",
                columns: new[] { "VolumeId", "FeatureKey", "State", "SizeBytes" });

            migrationBuilder.CreateIndex(
                name: "IX_StoredObjects_VolumeId_IntegrityFaultAtUtc",
                table: "StoredObjects",
                columns: new[] { "VolumeId", "IntegrityFaultAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_StoredObjects_VolumeId_RelativePath",
                table: "StoredObjects",
                columns: new[] { "VolumeId", "RelativePath" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FileStorageMaintenanceLeases");

            migrationBuilder.DropTable(
                name: "FileStorageOrphanSightings");

            migrationBuilder.DropTable(
                name: "StoredObjects");

            migrationBuilder.DropTable(
                name: "FileStorageVolumes");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "FileStorageDeletionGraceHours");
        }
    }
}
