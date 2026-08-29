using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Odyssey.Context.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Name = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    NormalizedName = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ConcurrencyStamp = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    MustChangePassword = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    UserName = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    NormalizedUserName = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Email = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    NormalizedEmail = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    EmailConfirmed = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    PasswordHash = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SecurityStamp = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ConcurrencyStamp = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PhoneNumber = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PhoneNumberConfirmed = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Budgets",
                columns: table => new
                {
                    BudgetId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    StartDate = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    EndDate = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Archived = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    BaseCurrencyCode = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Budgets", x => x.BudgetId);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Contacts",
                columns: table => new
                {
                    ContactId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ExternalUid = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DisplayName = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    NormalizedName = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Type = table.Column<int>(type: "int", nullable: false),
                    OrganizationNumber = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Notes = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Archived = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    LegacyType = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contacts", x => x.ContactId);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Contracts",
                columns: table => new
                {
                    ContractId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Name = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Type = table.Column<int>(type: "int", nullable: false, defaultValue: 3),
                    Description = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    StartDate = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    EndDate = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CompletionDate = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Archived = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contracts", x => x.ContractId);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Currencies",
                columns: table => new
                {
                    CurrencyCode = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    MinorUnits = table.Column<int>(type: "int", nullable: false),
                    Symbol = table.Column<string>(type: "varchar(8)", maxLength: 8, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Archived = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Currencies", x => x.CurrencyCode);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "FileBlob",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Content = table.Column<byte[]>(type: "longblob", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileBlob", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "JournalTags",
                columns: table => new
                {
                    JournalTagId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Archived = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalTags", x => x.JournalTagId);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "JournalTaskTags",
                columns: table => new
                {
                    JournalTaskTagId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Archived = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalTaskTags", x => x.JournalTaskTagId);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "LicenseAcceptances",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    UserId = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    LicenseHash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Accepted = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    RespondedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LicenseAcceptances", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PhotoTags",
                columns: table => new
                {
                    PhotoTagId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Archived = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoTags", x => x.PhotoTagId);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "SystemSettings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Value = table.Column<string>(type: "varchar(4000)", maxLength: 4000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedBy = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemSettings", x => x.Key);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "SystemSettingSecrets",
                columns: table => new
                {
                    Key = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Ciphertext = table.Column<string>(type: "varchar(6000)", maxLength: 6000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ProtectionScheme = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedBy = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemSettingSecrets", x => x.Key);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "TaxStatements",
                columns: table => new
                {
                    TaxStatementId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    FiscalYear = table.Column<int>(type: "int", nullable: false),
                    StartDate = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    EndDate = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    BaseCurrencyCode = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DeclaredTotalAssets = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    DeclaredTotalLiabilities = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    DeclaredNetWorth = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    DeclaredTotalIncome = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    AssessedTax = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    SettlementAmount = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    SettledAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    FiledAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    TaxOfficeApprovedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    StatusComment = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    StatusChangedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Notes = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Archived = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxStatements", x => x.TaxStatementId);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "TransactionTags",
                columns: table => new
                {
                    TransactionTagId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Archived = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransactionTags", x => x.TransactionTagId);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    RoleId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ClaimType = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ClaimValue = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    UserId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ClaimType = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ClaimValue = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ProviderKey = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ProviderDisplayName = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UserId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RoleId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    LoginProvider = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Name = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Value = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Calendars",
                columns: table => new
                {
                    CalendarId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Name = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Color = table.Column<string>(type: "varchar(7)", maxLength: 7, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UpdatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Calendars", x => x.CalendarId);
                    table.ForeignKey(
                        name: "FK_Calendars_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Calendars_AspNetUsers_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "JournalEntries",
                columns: table => new
                {
                    JournalEntryId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ExternalUid = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Title = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Content = table.Column<string>(type: "varchar(4096)", maxLength: 4096, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    EntryDate = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Location = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UpdatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Archived = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalEntries", x => x.JournalEntryId);
                    table.ForeignKey(
                        name: "FK_JournalEntries_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_JournalEntries_AspNetUsers_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "JournalTasks",
                columns: table => new
                {
                    JournalTaskId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ExternalUid = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Title = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Content = table.Column<string>(type: "varchar(4096)", maxLength: 4096, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Deadline = table.Column<DateOnly>(type: "date", nullable: true),
                    Position = table.Column<int>(type: "int", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UpdatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Archived = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalTasks", x => x.JournalTaskId);
                    table.ForeignKey(
                        name: "FK_JournalTasks_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_JournalTasks_AspNetUsers_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "TermsOfServiceVersions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Content = table.Column<string>(type: "longtext", maxLength: 50000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PublishedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    PublishedByUserId = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TermsOfServiceVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TermsOfServiceVersions_AspNetUsers_PublishedByUserId",
                        column: x => x.PublishedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "UserPreferences",
                columns: table => new
                {
                    UserPreferenceId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    UserId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Key = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PreferencesJson = table.Column<string>(type: "varchar(4096)", maxLength: 4096, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPreferences", x => x.UserPreferenceId);
                    table.ForeignKey(
                        name: "FK_UserPreferences_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "UserProfiles",
                columns: table => new
                {
                    UserProfileId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    UserId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    FirstName = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    MiddleName = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    LastName = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DisplayName = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Title = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    BirthDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Sex = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserProfiles", x => x.UserProfileId);
                    table.ForeignKey(
                        name: "FK_UserProfiles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Accounts",
                columns: table => new
                {
                    AccountId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Name = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Opened = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    AccountNumber = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AccountType = table.Column<int>(type: "int", nullable: false),
                    Closed = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Archived = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CurrencyCode = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CustodianId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => x.AccountId);
                    table.ForeignKey(
                        name: "FK_Accounts_Contacts_CustodianId",
                        column: x => x.CustodianId,
                        principalTable: "Contacts",
                        principalColumn: "ContactId",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Addresses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ContactId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Label = table.Column<int>(type: "int", nullable: false),
                    IsPrimary = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Line1 = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Line2 = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    City = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PostalCode = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Region = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CountryCode = table.Column<string>(type: "varchar(2)", maxLength: 2, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Addresses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Addresses_Contacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contacts",
                        principalColumn: "ContactId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "EmailAddresses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ContactId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Label = table.Column<int>(type: "int", nullable: false),
                    IsPrimary = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Value = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailAddresses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmailAddresses_Contacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contacts",
                        principalColumn: "ContactId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "OrganizationDetails",
                columns: table => new
                {
                    ContactId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    LegalName = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    OrganizationNumber = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Website = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationDetails", x => x.ContactId);
                    table.ForeignKey(
                        name: "FK_OrganizationDetails_Contacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contacts",
                        principalColumn: "ContactId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PersonDetails",
                columns: table => new
                {
                    ContactId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FirstName = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    LastName = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DateOfBirth = table.Column<DateOnly>(type: "date", nullable: true),
                    RelationshipType = table.Column<int>(type: "int", nullable: true),
                    Sex = table.Column<int>(type: "int", nullable: true),
                    Title = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Company = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersonDetails", x => x.ContactId);
                    table.ForeignKey(
                        name: "FK_PersonDetails_Contacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contacts",
                        principalColumn: "ContactId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PhoneNumbers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ContactId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Label = table.Column<int>(type: "int", nullable: false),
                    IsPrimary = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Value = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhoneNumbers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PhoneNumbers_Contacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contacts",
                        principalColumn: "ContactId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Subscriptions",
                columns: table => new
                {
                    SubscriptionId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ExternalId = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ContactId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    CurrencyCode = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Interval = table.Column<int>(type: "int", nullable: false, defaultValue: 2),
                    IntervalCount = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                    FirstBillingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Notes = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Paused = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Archived = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subscriptions", x => x.SubscriptionId);
                    table.ForeignKey(
                        name: "FK_Subscriptions_Contacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contacts",
                        principalColumn: "ContactId",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ExchangeRates",
                columns: table => new
                {
                    ExchangeRateId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FromCurrencyCode = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ToCurrencyCode = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Rate = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    AsOf = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExchangeRates", x => x.ExchangeRateId);
                    table.ForeignKey(
                        name: "FK_ExchangeRates_Currencies_FromCurrencyCode",
                        column: x => x.FromCurrencyCode,
                        principalTable: "Currencies",
                        principalColumn: "CurrencyCode",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExchangeRates_Currencies_ToCurrencyCode",
                        column: x => x.ToCurrencyCode,
                        principalTable: "Currencies",
                        principalColumn: "CurrencyCode",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "FileMetadata",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    UploadedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    FileName = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ContentType = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256Hash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    FileBlobId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Description = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UploadedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileMetadata", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FileMetadata_AspNetUsers_UploadedByUserId",
                        column: x => x.UploadedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_FileMetadata_FileBlob_FileBlobId",
                        column: x => x.FileBlobId,
                        principalTable: "FileBlob",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "BudgetItems",
                columns: table => new
                {
                    BudgetItemId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    BudgetId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CategoryType = table.Column<int>(type: "int", nullable: false),
                    PlannedAmount = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    TransactionTagId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BudgetItems", x => x.BudgetItemId);
                    table.ForeignKey(
                        name: "FK_BudgetItems_Budgets_BudgetId",
                        column: x => x.BudgetId,
                        principalTable: "Budgets",
                        principalColumn: "BudgetId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BudgetItems_TransactionTags_TransactionTagId",
                        column: x => x.TransactionTagId,
                        principalTable: "TransactionTags",
                        principalColumn: "TransactionTagId",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "TaxStatementTags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    TaxStatementId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    TransactionTagId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Role = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxStatementTags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaxStatementTags_TaxStatements_TaxStatementId",
                        column: x => x.TaxStatementId,
                        principalTable: "TaxStatements",
                        principalColumn: "TaxStatementId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TaxStatementTags_TransactionTags_TransactionTagId",
                        column: x => x.TransactionTagId,
                        principalTable: "TransactionTags",
                        principalColumn: "TransactionTagId",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RecurrencePatterns",
                columns: table => new
                {
                    RecurrencePatternId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CalendarId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Title = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Location = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ExternalUid = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    StartDateTime = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    EndDateTime = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    IsAllDay = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Frequency = table.Column<int>(type: "int", nullable: false),
                    Interval = table.Column<int>(type: "int", nullable: false),
                    DaysOfWeek = table.Column<int>(type: "int", nullable: true),
                    DayOfMonth = table.Column<int>(type: "int", nullable: true),
                    MonthOfYear = table.Column<int>(type: "int", nullable: true),
                    RecurrenceEndDate = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    OccurrenceCount = table.Column<int>(type: "int", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UpdatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurrencePatterns", x => x.RecurrencePatternId);
                    table.ForeignKey(
                        name: "FK_RecurrencePatterns_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RecurrencePatterns_AspNetUsers_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RecurrencePatterns_Calendars_CalendarId",
                        column: x => x.CalendarId,
                        principalTable: "Calendars",
                        principalColumn: "CalendarId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "JournalEntryContacts",
                columns: table => new
                {
                    JournalEntryContactId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    JournalEntryId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ContactId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalEntryContacts", x => x.JournalEntryContactId);
                    table.ForeignKey(
                        name: "FK_JournalEntryContacts_Contacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contacts",
                        principalColumn: "ContactId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_JournalEntryContacts_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "JournalEntryId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "JournalEntryTags",
                columns: table => new
                {
                    JournalEntryTagId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    JournalEntryId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    JournalTagId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalEntryTags", x => x.JournalEntryTagId);
                    table.ForeignKey(
                        name: "FK_JournalEntryTags_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "JournalEntryId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_JournalEntryTags_JournalTags_JournalTagId",
                        column: x => x.JournalTagId,
                        principalTable: "JournalTags",
                        principalColumn: "JournalTagId",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "JournalTaskTagLinks",
                columns: table => new
                {
                    JournalTaskTagLinkId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    JournalTaskId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    JournalTaskTagId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalTaskTagLinks", x => x.JournalTaskTagLinkId);
                    table.ForeignKey(
                        name: "FK_JournalTaskTagLinks_JournalTaskTags_JournalTaskTagId",
                        column: x => x.JournalTaskTagId,
                        principalTable: "JournalTaskTags",
                        principalColumn: "JournalTaskTagId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JournalTaskTagLinks_JournalTasks_JournalTaskId",
                        column: x => x.JournalTaskId,
                        principalTable: "JournalTasks",
                        principalColumn: "JournalTaskId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "TermsOfServiceAcceptances",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    UserId = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    TermsOfServiceVersionId = table.Column<int>(type: "int", nullable: false),
                    Accepted = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    RespondedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TermsOfServiceAcceptances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TermsOfServiceAcceptances_TermsOfServiceVersions_TermsOfServ~",
                        column: x => x.TermsOfServiceVersionId,
                        principalTable: "TermsOfServiceVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AccountEstimates",
                columns: table => new
                {
                    AccountEstimateId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    AccountId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Value = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    CurrencyCode = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    EffectiveFrom = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Note = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountEstimates", x => x.AccountEstimateId);
                    table.ForeignKey(
                        name: "FK_AccountEstimates_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "AccountId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AccountSmartTags",
                columns: table => new
                {
                    AccountId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    TransactionTagId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    AddedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountSmartTags", x => new { x.AccountId, x.TransactionTagId });
                    table.ForeignKey(
                        name: "FK_AccountSmartTags_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "AccountId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AccountSmartTags_TransactionTags_TransactionTagId",
                        column: x => x.TransactionTagId,
                        principalTable: "TransactionTags",
                        principalColumn: "TransactionTagId",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AccountTerms",
                columns: table => new
                {
                    AccountTermId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    AccountId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    TermKind = table.Column<int>(type: "int", nullable: false),
                    ValueUnit = table.Column<int>(type: "int", nullable: false),
                    Value = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    CurrencyCode = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    BillingPeriod = table.Column<int>(type: "int", nullable: true),
                    EffectiveFrom = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Note = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountTerms", x => x.AccountTermId);
                    table.ForeignKey(
                        name: "FK_AccountTerms_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "AccountId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "InsurancePolicies",
                columns: table => new
                {
                    InsurancePolicyId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PolicyNumber = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Type = table.Column<int>(type: "int", nullable: false, defaultValue: 11),
                    InsurerId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    InsuredAccountId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    Notes = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Archived = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InsurancePolicies", x => x.InsurancePolicyId);
                    table.ForeignKey(
                        name: "FK_InsurancePolicies_Accounts_InsuredAccountId",
                        column: x => x.InsuredAccountId,
                        principalTable: "Accounts",
                        principalColumn: "AccountId",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_InsurancePolicies_Contacts_InsurerId",
                        column: x => x.InsurerId,
                        principalTable: "Contacts",
                        principalColumn: "ContactId",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Transactions",
                columns: table => new
                {
                    TransactionId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Description = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Amount = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    TimeStamp = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    AccountId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ContactId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    ExternalId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    InternalId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ExtraData = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Status = table.Column<int>(type: "int", nullable: false),
                    StatusComment = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    StatusChangedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CurrencyCode = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Transactions", x => x.TransactionId);
                    table.ForeignKey(
                        name: "FK_Transactions_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "AccountId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Transactions_Contacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contacts",
                        principalColumn: "ContactId",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AccountFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    AccountId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FileMetadataId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    AttachedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AttachedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    FileType = table.Column<int>(type: "int", nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ValidTo = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    IssuedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    IssuedBy = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountFiles_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "AccountId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AccountFiles_AspNetUsers_AttachedByUserId",
                        column: x => x.AttachedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AccountFiles_Contacts_IssuedBy",
                        column: x => x.IssuedBy,
                        principalTable: "Contacts",
                        principalColumn: "ContactId",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AccountFiles_FileMetadata_FileMetadataId",
                        column: x => x.FileMetadataId,
                        principalTable: "FileMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ContractFiles",
                columns: table => new
                {
                    ContractFileId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ContractId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FileMetadataId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FileType = table.Column<int>(type: "int", nullable: false, defaultValue: 3),
                    AttachedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AttachedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractFiles", x => x.ContractFileId);
                    table.ForeignKey(
                        name: "FK_ContractFiles_AspNetUsers_AttachedByUserId",
                        column: x => x.AttachedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ContractFiles_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contracts",
                        principalColumn: "ContractId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ContractFiles_FileMetadata_FileMetadataId",
                        column: x => x.FileMetadataId,
                        principalTable: "FileMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "JournalEntryAttachments",
                columns: table => new
                {
                    JournalEntryAttachmentId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    JournalEntryId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FileId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalEntryAttachments", x => x.JournalEntryAttachmentId);
                    table.ForeignKey(
                        name: "FK_JournalEntryAttachments_FileMetadata_FileId",
                        column: x => x.FileId,
                        principalTable: "FileMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_JournalEntryAttachments_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "JournalEntryId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "JournalTaskAttachments",
                columns: table => new
                {
                    JournalTaskAttachmentId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    JournalTaskId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FileId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalTaskAttachments", x => x.JournalTaskAttachmentId);
                    table.ForeignKey(
                        name: "FK_JournalTaskAttachments_FileMetadata_FileId",
                        column: x => x.FileId,
                        principalTable: "FileMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_JournalTaskAttachments_JournalTasks_JournalTaskId",
                        column: x => x.JournalTaskId,
                        principalTable: "JournalTasks",
                        principalColumn: "JournalTaskId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Photos",
                columns: table => new
                {
                    PhotoId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FileId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Title = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Caption = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    TakenAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CapturedLatitude = table.Column<double>(type: "double", nullable: true),
                    CapturedLongitude = table.Column<double>(type: "double", nullable: true),
                    LocationName = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PixelWidth = table.Column<int>(type: "int", nullable: true),
                    PixelHeight = table.Column<int>(type: "int", nullable: true),
                    Archived = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Favourited = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UpdatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Photos", x => x.PhotoId);
                    table.ForeignKey(
                        name: "FK_Photos_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Photos_AspNetUsers_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Photos_FileMetadata_FileId",
                        column: x => x.FileId,
                        principalTable: "FileMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "TaxStatementFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    TaxStatementId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FileMetadataId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    AttachedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AttachedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    FileType = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxStatementFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaxStatementFiles_AspNetUsers_AttachedByUserId",
                        column: x => x.AttachedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TaxStatementFiles_FileMetadata_FileMetadataId",
                        column: x => x.FileMetadataId,
                        principalTable: "FileMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TaxStatementFiles_TaxStatements_TaxStatementId",
                        column: x => x.TaxStatementId,
                        principalTable: "TaxStatements",
                        principalColumn: "TaxStatementId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "CalendarEvents",
                columns: table => new
                {
                    CalendarEventId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CalendarId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Title = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Location = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ExternalUid = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    StartDateTime = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    EndDateTime = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    IsAllDay = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    RecurrencePatternId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    CreatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UpdatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarEvents", x => x.CalendarEventId);
                    table.ForeignKey(
                        name: "FK_CalendarEvents_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CalendarEvents_AspNetUsers_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CalendarEvents_Calendars_CalendarId",
                        column: x => x.CalendarId,
                        principalTable: "Calendars",
                        principalColumn: "CalendarId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CalendarEvents_RecurrencePatterns_RecurrencePatternId",
                        column: x => x.RecurrencePatternId,
                        principalTable: "RecurrencePatterns",
                        principalColumn: "RecurrencePatternId",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ContractParties",
                columns: table => new
                {
                    ContractPartyId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ContractId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    AccountId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    ContactId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    InsurancePolicyId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractParties", x => x.ContractPartyId);
                    table.CheckConstraint("CK_ContractParties_ExactlyOneTarget", "((`AccountId` IS NOT NULL) + (`ContactId` IS NOT NULL) + (`InsurancePolicyId` IS NOT NULL)) = 1");
                    table.ForeignKey(
                        name: "FK_ContractParties_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "AccountId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ContractParties_Contacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contacts",
                        principalColumn: "ContactId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ContractParties_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contracts",
                        principalColumn: "ContractId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ContractParties_InsurancePolicies_InsurancePolicyId",
                        column: x => x.InsurancePolicyId,
                        principalTable: "InsurancePolicies",
                        principalColumn: "InsurancePolicyId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "InsurancePolicyFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    InsurancePolicyId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FileMetadataId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FileType = table.Column<int>(type: "int", nullable: false, defaultValue: 5),
                    EffectiveDate = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    AttachedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AttachedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InsurancePolicyFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InsurancePolicyFiles_AspNetUsers_AttachedByUserId",
                        column: x => x.AttachedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_InsurancePolicyFiles_FileMetadata_FileMetadataId",
                        column: x => x.FileMetadataId,
                        principalTable: "FileMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InsurancePolicyFiles_InsurancePolicies_InsurancePolicyId",
                        column: x => x.InsurancePolicyId,
                        principalTable: "InsurancePolicies",
                        principalColumn: "InsurancePolicyId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PolicyRenewals",
                columns: table => new
                {
                    PolicyRenewalId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    InsurancePolicyId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FromDate = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ToDate = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Premium = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    PremiumCurrencyCode = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CoverageAmount = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    CoverageCurrencyCode = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Notes = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicyRenewals", x => x.PolicyRenewalId);
                    table.ForeignKey(
                        name: "FK_PolicyRenewals_InsurancePolicies_InsurancePolicyId",
                        column: x => x.InsurancePolicyId,
                        principalTable: "InsurancePolicies",
                        principalColumn: "InsurancePolicyId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "TransactionFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    TransactionId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FileMetadataId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    AttachedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AttachedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false, defaultValue: 2)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransactionFiles", x => x.Id);
                    table.CheckConstraint("CK_TransactionFiles_Type_AllowedValues", "`Type` IN (0, 1, 2, 3, 4, 5, 6)");
                    table.ForeignKey(
                        name: "FK_TransactionFiles_AspNetUsers_AttachedByUserId",
                        column: x => x.AttachedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TransactionFiles_FileMetadata_FileMetadataId",
                        column: x => x.FileMetadataId,
                        principalTable: "FileMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TransactionFiles_Transactions_TransactionId",
                        column: x => x.TransactionId,
                        principalTable: "Transactions",
                        principalColumn: "TransactionId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "TransactionTagLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    TransactionId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    TransactionTagId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransactionTagLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TransactionTagLinks_TransactionTags_TransactionTagId",
                        column: x => x.TransactionTagId,
                        principalTable: "TransactionTags",
                        principalColumn: "TransactionTagId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TransactionTagLinks_Transactions_TransactionId",
                        column: x => x.TransactionId,
                        principalTable: "Transactions",
                        principalColumn: "TransactionId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "FileAnalysisJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    AccountFileId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Status = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    FileTypeDetected = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    StartedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    FailureCode = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    FailureMessage = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AnalyzerProvider = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    AnalyzerModel = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PromptVersion = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RequestedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ConsentRecorded = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    ConsentMethod = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ConsentText = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    LawfulBasis = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    MatchStatus = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    MatchFailureMessage = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    VocabularyCount = table.Column<int>(type: "int", nullable: true),
                    AutoLinkThresholdInForce = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: true),
                    MaxTokensInForce = table.Column<int>(type: "int", nullable: true),
                    MatchTimeoutSecondsInForce = table.Column<int>(type: "int", nullable: true),
                    AnalyzerBaseUrlHost = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ProcessorInForce = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ProcessorRegionInForce = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileAnalysisJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FileAnalysisJobs_AccountFiles_AccountFileId",
                        column: x => x.AccountFileId,
                        principalTable: "AccountFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FileAnalysisJobs_AspNetUsers_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "JournalEntryPhotos",
                columns: table => new
                {
                    JournalEntryPhotoId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    JournalEntryId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    PhotoId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Position = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalEntryPhotos", x => x.JournalEntryPhotoId);
                    table.ForeignKey(
                        name: "FK_JournalEntryPhotos_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "JournalEntryId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_JournalEntryPhotos_Photos_PhotoId",
                        column: x => x.PhotoId,
                        principalTable: "Photos",
                        principalColumn: "PhotoId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PhotoAlbums",
                columns: table => new
                {
                    PhotoAlbumId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CoverPhotoId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    Archived = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UpdatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoAlbums", x => x.PhotoAlbumId);
                    table.ForeignKey(
                        name: "FK_PhotoAlbums_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PhotoAlbums_AspNetUsers_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PhotoAlbums_Photos_CoverPhotoId",
                        column: x => x.CoverPhotoId,
                        principalTable: "Photos",
                        principalColumn: "PhotoId",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PhotoPeople",
                columns: table => new
                {
                    PhotoPersonId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    PhotoId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ContactId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoPeople", x => x.PhotoPersonId);
                    table.ForeignKey(
                        name: "FK_PhotoPeople_Contacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contacts",
                        principalColumn: "ContactId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PhotoPeople_Photos_PhotoId",
                        column: x => x.PhotoId,
                        principalTable: "Photos",
                        principalColumn: "PhotoId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PhotoTagLinks",
                columns: table => new
                {
                    PhotoTagLinkId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    PhotoId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    PhotoTagId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoTagLinks", x => x.PhotoTagLinkId);
                    table.ForeignKey(
                        name: "FK_PhotoTagLinks_PhotoTags_PhotoTagId",
                        column: x => x.PhotoTagId,
                        principalTable: "PhotoTags",
                        principalColumn: "PhotoTagId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PhotoTagLinks_Photos_PhotoId",
                        column: x => x.PhotoId,
                        principalTable: "Photos",
                        principalColumn: "PhotoId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PolicyRenewalFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    PolicyRenewalId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FileMetadataId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FileType = table.Column<int>(type: "int", nullable: false, defaultValue: 5),
                    EffectiveDate = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    AttachedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AttachedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicyRenewalFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PolicyRenewalFiles_AspNetUsers_AttachedByUserId",
                        column: x => x.AttachedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PolicyRenewalFiles_FileMetadata_FileMetadataId",
                        column: x => x.FileMetadataId,
                        principalTable: "FileMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PolicyRenewalFiles_PolicyRenewals_PolicyRenewalId",
                        column: x => x.PolicyRenewalId,
                        principalTable: "PolicyRenewals",
                        principalColumn: "PolicyRenewalId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "FileAnalysisCandidateTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    AnalysisJobId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    TransactionDate = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    BookingDate = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Description = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Merchant = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CategoryHint = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Amount = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    Currency = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ExternalId = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    InternalId = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ReferenceNumber = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SourceLineNumber = table.Column<int>(type: "int", nullable: true),
                    SourcePageNumber = table.Column<int>(type: "int", nullable: true),
                    LlmConfidence = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: true),
                    LlmModel = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    LlmProviderResponseId = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    LlmRawJson = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ReviewStatus = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    ReviewedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ReviewedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    MatchedContactId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    MerchantMatchConfidence = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: true),
                    CategoryMatchConfidence = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: true),
                    MatchMethod = table.Column<int>(type: "int", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileAnalysisCandidateTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FileAnalysisCandidateTransactions_AspNetUsers_ReviewedByUser~",
                        column: x => x.ReviewedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_FileAnalysisCandidateTransactions_Contacts_MatchedContactId",
                        column: x => x.MatchedContactId,
                        principalTable: "Contacts",
                        principalColumn: "ContactId",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_FileAnalysisCandidateTransactions_FileAnalysisJobs_AnalysisJ~",
                        column: x => x.AnalysisJobId,
                        principalTable: "FileAnalysisJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PhotoAlbumItems",
                columns: table => new
                {
                    PhotoAlbumItemId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    PhotoAlbumId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    PhotoId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Position = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoAlbumItems", x => x.PhotoAlbumItemId);
                    table.ForeignKey(
                        name: "FK_PhotoAlbumItems_PhotoAlbums_PhotoAlbumId",
                        column: x => x.PhotoAlbumId,
                        principalTable: "PhotoAlbums",
                        principalColumn: "PhotoAlbumId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PhotoAlbumItems_Photos_PhotoId",
                        column: x => x.PhotoId,
                        principalTable: "Photos",
                        principalColumn: "PhotoId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "FileAnalysisCandidateTags",
                columns: table => new
                {
                    CandidateTransactionId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    TransactionTagId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileAnalysisCandidateTags", x => new { x.CandidateTransactionId, x.TransactionTagId });
                    table.ForeignKey(
                        name: "FK_FileAnalysisCandidateTags_FileAnalysisCandidateTransactions_~",
                        column: x => x.CandidateTransactionId,
                        principalTable: "FileAnalysisCandidateTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FileAnalysisCandidateTags_TransactionTags_TransactionTagId",
                        column: x => x.TransactionTagId,
                        principalTable: "TransactionTags",
                        principalColumn: "TransactionTagId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.InsertData(
                table: "AspNetRoles",
                columns: new[] { "Id", "ConcurrencyStamp", "Name", "NormalizedName" },
                values: new object[,]
                {
                    { "019ebf36-3a6a-43b2-aa58-7e022c3b9cf3", "1d63d0dc-6c9f-4ec4-90c5-2a7f8a0b2f2c", "User", "user" },
                    { "6c17017f-8072-44a8-8ed1-a03b71ef85a6", "c6c5b1d6-6c4a-4a5e-8c8b-7736ff6a8f27", "Admin", "admin" },
                    { "c9a82815-d9f8-4f3f-8b34-9f7272b71c7c", "27f1b3e3-82ff-4e4a-9e14-0c6a3c0f1f9e", "Guest", "guest" },
                    { "e444d6c0-1a33-4b2c-9f20-ef7a4ad2770b", "5e6b9878-3b89-474f-a2db-0e3d1e38859d", "Owner", "owner" }
                });

            migrationBuilder.InsertData(
                table: "Currencies",
                columns: new[] { "CurrencyCode", "Archived", "MinorUnits", "Name", "Symbol" },
                values: new object[,]
                {
                    { "AED", null, 2, "UAE Dirham", null },
                    { "AFN", null, 2, "Afghani", null },
                    { "ALL", null, 2, "Albanian Lek", "L" },
                    { "AMD", null, 2, "Armenian Dram", null },
                    { "AOA", null, 2, "Kwanza", null },
                    { "ARS", null, 2, "Argentine Peso", null },
                    { "AUD", null, 2, "Australian Dollar", "$" },
                    { "AWG", null, 2, "Aruban Florin", null },
                    { "AZN", null, 2, "Azerbaijan Manat", "₼" },
                    { "BAM", null, 2, "Convertible Mark", null },
                    { "BBD", null, 2, "Barbados Dollar", "$" },
                    { "BDT", null, 2, "Taka", "৳" },
                    { "BGN", null, 2, "Bulgarian Lev", "лв" },
                    { "BHD", null, 3, "Bahraini Dinar", null },
                    { "BIF", null, 0, "Burundi Franc", null },
                    { "BMD", null, 2, "Bermudian Dollar", "$" },
                    { "BND", null, 2, "Brunei Dollar", "$" },
                    { "BOB", null, 2, "Boliviano", "Bs." },
                    { "BOV", null, 2, "Mvdol", null },
                    { "BRL", null, 2, "Brazilian Real", "R$" },
                    { "BSD", null, 2, "Bahamian Dollar", "$" },
                    { "BTN", null, 2, "Ngultrum", null },
                    { "BWP", null, 2, "Pula", null },
                    { "BYN", null, 2, "Belarusian Ruble", null },
                    { "BZD", null, 2, "Belize Dollar", "$" },
                    { "CAD", null, 2, "Canadian Dollar", "$" },
                    { "CDF", null, 2, "Congolese Franc", null },
                    { "CHE", null, 2, "WIR Euro", null },
                    { "CHF", null, 2, "Swiss Franc", "CHF" },
                    { "CHW", null, 2, "WIR Franc", null },
                    { "CLF", null, 4, "Unidad de Fomento", null },
                    { "CLP", null, 0, "Chilean Peso", "$" },
                    { "CNY", null, 2, "Yuan Renminbi", "¥" },
                    { "COP", null, 2, "Colombian Peso", "$" },
                    { "COU", null, 2, "Unidad de Valor Real", null },
                    { "CRC", null, 2, "Costa Rican Colon", "₡" },
                    { "CUP", null, 2, "Cuban Peso", "₱" },
                    { "CVE", null, 2, "Cape Verde Escudo", null },
                    { "CZK", null, 2, "Czech Koruna", "Kč" },
                    { "DJF", null, 0, "Djibouti Franc", null },
                    { "DKK", null, 2, "Danish Krone", "kr" },
                    { "DOP", null, 2, "Dominican Peso", "RD$" },
                    { "DZD", null, 2, "Algerian Dinar", null },
                    { "EGP", null, 2, "Egyptian Pound", "£" },
                    { "ERN", null, 2, "Nakfa", null },
                    { "ETB", null, 2, "Ethiopian Birr", null },
                    { "EUR", null, 2, "Euro", "€" },
                    { "FJD", null, 2, "Fiji Dollar", "$" },
                    { "FKP", null, 2, "Falkland Islands Pound", "£" },
                    { "GBP", null, 2, "Pound Sterling", "£" },
                    { "GEL", null, 2, "Lari", "₾" },
                    { "GHS", null, 2, "Ghana Cedi", "₵" },
                    { "GIP", null, 2, "Gibraltar Pound", "£" },
                    { "GMD", null, 2, "Dalasi", null },
                    { "GNF", null, 0, "Guinean Franc", null },
                    { "GTQ", null, 2, "Quetzal", "Q" },
                    { "GYD", null, 2, "Guyana Dollar", "$" },
                    { "HKD", null, 2, "Hong Kong Dollar", "$" },
                    { "HNL", null, 2, "Lempira", "L" },
                    { "HTG", null, 2, "Gourde", null },
                    { "HUF", null, 2, "Forint", "Ft" },
                    { "IDR", null, 2, "Rupiah", "Rp" },
                    { "ILS", null, 2, "New Israeli Sheqel", "₪" },
                    { "INR", null, 2, "Indian Rupee", "₹" },
                    { "IQD", null, 3, "Iraqi Dinar", null },
                    { "IRR", null, 2, "Iranian Rial", null },
                    { "ISK", null, 0, "Iceland Krona", "kr" },
                    { "JMD", null, 2, "Jamaican Dollar", "$" },
                    { "JOD", null, 3, "Jordanian Dinar", null },
                    { "JPY", null, 0, "Yen", "¥" },
                    { "KES", null, 2, "Kenyan Shilling", null },
                    { "KGS", null, 2, "Som", null },
                    { "KHR", null, 2, "Riel", "៛" },
                    { "KMF", null, 0, "Comorian Franc", null },
                    { "KPW", null, 0, "North Korean Won", "₩" },
                    { "KRW", null, 0, "Won", "₩" },
                    { "KWD", null, 3, "Kuwaiti Dinar", null },
                    { "KYD", null, 2, "Cayman Islands Dollar", "$" },
                    { "KZT", null, 2, "Tenge", "₸" },
                    { "LAK", null, 0, "Lao Kip", "₭" },
                    { "LBP", null, 0, "Lebanese Pound", "£" },
                    { "LKR", null, 2, "Sri Lanka Rupee", "₨" },
                    { "LRD", null, 2, "Liberian Dollar", "$" },
                    { "LSL", null, 2, "Loti", null },
                    { "LYD", null, 3, "Libyan Dinar", null },
                    { "MAD", null, 2, "Moroccan Dirham", null },
                    { "MDL", null, 2, "Moldovan Leu", null },
                    { "MGA", null, 2, "Malagasy Ariary", null },
                    { "MKD", null, 2, "Macedonian Denar", "ден" },
                    { "MMK", null, 2, "Kyat", "Ks" },
                    { "MNT", null, 2, "Tugrik", "₮" },
                    { "MOP", null, 2, "Pataca", "MOP$" },
                    { "MRU", null, 2, "Ouguiya", null },
                    { "MUR", null, 2, "Mauritius Rupee", "₨" },
                    { "MVR", null, 2, "Rufiyaa", null },
                    { "MWK", null, 2, "Malawi Kwacha", "MK" },
                    { "MXN", null, 2, "Mexican Peso", "$" },
                    { "MXV", null, 2, "Mexican Unidad de Inversion (UDI)", null },
                    { "MYR", null, 2, "Malaysian Ringgit", "RM" },
                    { "MZN", null, 2, "Mozambique Metical", null },
                    { "NAD", null, 2, "Namibia Dollar", "$" },
                    { "NGN", null, 2, "Naira", "₦" },
                    { "NIO", null, 2, "Cordoba Oro", "C$" },
                    { "NOK", null, 2, "Norwegian Krone", "kr" },
                    { "NPR", null, 2, "Nepalese Rupee", "₨" },
                    { "NZD", null, 2, "New Zealand Dollar", "$" },
                    { "OMR", null, 3, "Rial Omani", null },
                    { "PAB", null, 2, "Balboa", "B/." },
                    { "PEN", null, 2, "Sol", "S/" },
                    { "PGK", null, 2, "Kina", null },
                    { "PHP", null, 2, "Philippine Peso", "₱" },
                    { "PKR", null, 2, "Pakistan Rupee", "₨" },
                    { "PLN", null, 2, "Zloty", "zł" },
                    { "PYG", null, 0, "Guarani", "₲" },
                    { "QAR", null, 2, "Qatari Rial", null },
                    { "RON", null, 2, "Romanian Leu", "lei" },
                    { "RSD", null, 2, "Serbian Dinar", null },
                    { "RUB", null, 2, "Russian Ruble", "₽" },
                    { "RWF", null, 0, "Rwanda Franc", null },
                    { "SAR", null, 2, "Saudi Riyal", null },
                    { "SBD", null, 2, "Solomon Islands Dollar", "$" },
                    { "SCR", null, 2, "Seychelles Rupee", "₨" },
                    { "SDG", null, 2, "Sudanese Pound", null },
                    { "SEK", null, 2, "Swedish Krona", "kr" },
                    { "SGD", null, 2, "Singapore Dollar", "$" },
                    { "SHP", null, 2, "Saint Helena Pound", "£" },
                    { "SLE", null, 2, "Leone", null },
                    { "SOS", null, 2, "Somali Shilling", null },
                    { "SRD", null, 2, "Surinam Dollar", "$" },
                    { "SSP", null, 2, "South Sudanese Pound", "£" },
                    { "STN", null, 2, "Dobra", "Db" },
                    { "SVC", null, 2, "El Salvador Colon", "₡" },
                    { "SYP", null, 2, "Syrian Pound", "£" },
                    { "SZL", null, 2, "Lilangeni", null },
                    { "THB", null, 2, "Baht", "฿" },
                    { "TJS", null, 2, "Somoni", null },
                    { "TMT", null, 2, "Turkmenistan New Manat", null },
                    { "TND", null, 3, "Tunisian Dinar", null },
                    { "TOP", null, 2, "Pa'anga", null },
                    { "TRY", null, 2, "Turkish Lira", "₺" },
                    { "TTD", null, 2, "Trinidad and Tobago Dollar", "$" },
                    { "TWD", null, 2, "New Taiwan Dollar", "$" },
                    { "TZS", null, 2, "Tanzanian Shilling", null },
                    { "UAH", null, 2, "Ukrainian Hryvnia", "₴" },
                    { "UGX", null, 0, "Uganda Shilling", null },
                    { "USD", null, 2, "US Dollar", "$" },
                    { "USN", null, 2, "US Dollar (Next Day)", null },
                    { "UYI", null, 0, "Uruguay Peso en Unidades Indexadas (UI)", null },
                    { "UYU", null, 2, "Peso Uruguayo", "$U" },
                    { "UYW", null, 4, "Unidad Previsional", null },
                    { "UZS", null, 2, "Uzbekistan Sum", null },
                    { "VED", null, 2, "Bolivar Digital", null },
                    { "VND", null, 0, "Dong", "₫" },
                    { "VUV", null, 0, "Vatu", null },
                    { "WST", null, 2, "Tala", null },
                    { "XAF", null, 0, "CFA Franc BEAC", null },
                    { "XCD", null, 2, "East Caribbean Dollar", "$" },
                    { "XCG", null, 2, "Caribbean Guilder", null },
                    { "XOF", null, 0, "CFA Franc BCEAO", null },
                    { "XPF", null, 0, "CFP Franc", null },
                    { "YER", null, 2, "Yemeni Rial", null },
                    { "ZAR", null, 2, "Rand", "R" },
                    { "ZMW", null, 2, "Zambian Kwacha", "ZK" },
                    { "ZWG", null, 2, "Zimbabwe Gold", null }
                });

            migrationBuilder.InsertData(
                table: "SystemSettings",
                columns: new[] { "Key", "UpdatedAt", "UpdatedBy", "Value" },
                values: new object[,]
                {
                    { "AccountMaxSmartTagsPerAccount", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "20" },
                    { "CalendarIcsMaxAggregateExportRows", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "20000" },
                    { "CalendarIcsMaxAggregateExportWindowDays", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "92" },
                    { "CalendarIcsMaxAggregateOccurrences", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "5000" },
                    { "CalendarIcsMaxExportEvents", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "2000" },
                    { "CalendarIcsMaxExportMegabytes", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "64" },
                    { "CalendarIcsMaxImportEvents", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "2000" },
                    { "CalendarIcsMaxImportMegabytes", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "64" },
                    { "CalendarMaxEventDurationDays", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "366" },
                    { "CalendarMaxWindowDays", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "92" },
                    { "ContactVCardMaxExportMegabytes", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "64" },
                    { "ContactVCardMaxExportRows", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "unlimited" },
                    { "ContactVCardMaxImportEntries", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "unlimited" },
                    { "ContactVCardMaxImportMegabytes", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "64" },
                    { "ContactVCardMaxRepeatablePropertiesPerEntry", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "200" },
                    { "ContractMaxFilesPerContract", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "50" },
                    { "ContractMaxPartiesPerContract", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "25" },
                    { "ContractMaxSummaryContracts", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "1000" },
                    { "EmailFromAddress", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "no-reply@odyssey.local" },
                    { "EmailFromName", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Odyssey" },
                    { "EmailMaxTrackedRecipients", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "20000" },
                    { "EmailPerRecipientLimit", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "3" },
                    { "EmailPerRecipientWindowMinutes", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "60" },
                    { "EmailRequireConfirmation", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "true" },
                    { "FileAnalysisBaseUrl", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "https://api.anthropic.com" },
                    { "FileAnalysisEnabled", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "false" },
                    { "FileAnalysisLawfulBasis", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Consent · GDPR Art. 6(1)(a)" },
                    { "FileAnalysisMatchAutoLinkThreshold", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "0.6" },
                    { "FileAnalysisMatchMaxVocabulary", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "500" },
                    { "FileAnalysisMatchTimeoutSeconds", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "60" },
                    { "FileAnalysisMaxFutureTransactionDays", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "90" },
                    { "FileAnalysisMaxTokens", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "8096" },
                    { "FileAnalysisModel", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "claude-sonnet-5" },
                    { "FileAnalysisPrivacyNoticeUrl", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "https://www.anthropic.com/legal/privacy" },
                    { "FileAnalysisProcessor", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Anthropic" },
                    { "FileAnalysisProcessorRegion", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "United States" },
                    { "FileStorageMaxUploadMegabytes", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "64" },
                    { "ImportMaxSamplesPerSkipReason", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "100" },
                    { "InsuranceExpiringSoonWindowDays", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "30" },
                    { "InsuranceMaxFilesPerParent", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "50" },
                    { "InsuranceMaxRenewalsPerPolicy", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "100" },
                    { "InsuranceMaxSummaryPolicies", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "1000" },
                    { "JournalEntryMaxLinksPerKind", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "50" },
                    { "JournalIcsMaxExportMegabytes", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "64" },
                    { "JournalIcsMaxExportRows", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "2000" },
                    { "JournalIcsMaxImportEntries", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "2000" },
                    { "JournalIcsMaxImportMegabytes", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "64" },
                    { "JournalTaskMaxLinksPerKind", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "50" },
                    { "PhotoMaxAlbumMembers", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "1000" },
                    { "PhotoMaxLinksPerKind", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "50" },
                    { "PhotoMetadataExtractionTimeoutSeconds", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "5" },
                    { "PhotoMetadataReadMegabytes", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "8" },
                    { "RecurrenceMaxGeneratedOccurrences", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "1000" },
                    { "RegistrationRequireAdminApproval", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "true" },
                    { "RequireTwoFactor", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "false" },
                    { "SubscriptionMaxSummaryRenewals", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "6" },
                    { "SubscriptionMaxSummarySubscriptions", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "1000" },
                    { "SubscriptionRenewalWindowDays", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "45" },
                    { "TaskIcsMaxExportMegabytes", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "64" },
                    { "TaskIcsMaxExportTasks", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "2000" },
                    { "TaskIcsMaxImportMegabytes", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "64" },
                    { "TaskIcsMaxImportTasks", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "2000" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountEstimates_AccountId_EffectiveFrom",
                table: "AccountEstimates",
                columns: new[] { "AccountId", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountFiles_AccountId_FileMetadataId",
                table: "AccountFiles",
                columns: new[] { "AccountId", "FileMetadataId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountFiles_AttachedAtUtc",
                table: "AccountFiles",
                column: "AttachedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AccountFiles_AttachedByUserId",
                table: "AccountFiles",
                column: "AttachedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountFiles_FileMetadataId",
                table: "AccountFiles",
                column: "FileMetadataId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountFiles_IssuedBy",
                table: "AccountFiles",
                column: "IssuedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_Archived",
                table: "Accounts",
                column: "Archived");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_CustodianId",
                table: "Accounts",
                column: "CustodianId");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_Name",
                table: "Accounts",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_AccountSmartTags_AddedAt",
                table: "AccountSmartTags",
                column: "AddedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AccountSmartTags_TransactionTagId",
                table: "AccountSmartTags",
                column: "TransactionTagId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountTerms_AccountId_TermKind_EffectiveFrom",
                table: "AccountTerms",
                columns: new[] { "AccountId", "TermKind", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_Addresses_ContactId",
                table: "Addresses",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                table: "AspNetRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AspNetRoles",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_RoleId",
                table: "AspNetUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BudgetItems_BudgetId_Name",
                table: "BudgetItems",
                columns: new[] { "BudgetId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BudgetItems_TransactionTagId",
                table: "BudgetItems",
                column: "TransactionTagId");

            migrationBuilder.CreateIndex(
                name: "IX_Budgets_Archived",
                table: "Budgets",
                column: "Archived");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_CalendarId_ExternalUid",
                table: "CalendarEvents",
                columns: new[] { "CalendarId", "ExternalUid" });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_CalendarId_StartDateTime",
                table: "CalendarEvents",
                columns: new[] { "CalendarId", "StartDateTime" });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_CreatedByUserId",
                table: "CalendarEvents",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_RecurrencePatternId",
                table: "CalendarEvents",
                column: "RecurrencePatternId");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_UpdatedByUserId",
                table: "CalendarEvents",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Calendars_CreatedByUserId",
                table: "Calendars",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Calendars_Name",
                table: "Calendars",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Calendars_UpdatedByUserId",
                table: "Calendars",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_ExternalUid",
                table: "Contacts",
                column: "ExternalUid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_NormalizedName",
                table: "Contacts",
                column: "NormalizedName");

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_Type_Archived",
                table: "Contacts",
                columns: new[] { "Type", "Archived" });

            migrationBuilder.CreateIndex(
                name: "IX_ContractFiles_AttachedAtUtc",
                table: "ContractFiles",
                column: "AttachedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ContractFiles_AttachedByUserId",
                table: "ContractFiles",
                column: "AttachedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ContractFiles_ContractId_FileMetadataId",
                table: "ContractFiles",
                columns: new[] { "ContractId", "FileMetadataId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContractFiles_FileMetadataId",
                table: "ContractFiles",
                column: "FileMetadataId");

            migrationBuilder.CreateIndex(
                name: "IX_ContractParties_AccountId",
                table: "ContractParties",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ContractParties_ContactId",
                table: "ContractParties",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_ContractParties_ContractId",
                table: "ContractParties",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_ContractParties_InsurancePolicyId",
                table: "ContractParties",
                column: "InsurancePolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_Archived",
                table: "Contracts",
                column: "Archived");

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_Type_Archived",
                table: "Contracts",
                columns: new[] { "Type", "Archived" });

            migrationBuilder.CreateIndex(
                name: "IX_Currencies_Archived",
                table: "Currencies",
                column: "Archived");

            migrationBuilder.CreateIndex(
                name: "IX_EmailAddresses_ContactId",
                table: "EmailAddresses",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRates_FromCurrencyCode_ToCurrencyCode_AsOf",
                table: "ExchangeRates",
                columns: new[] { "FromCurrencyCode", "ToCurrencyCode", "AsOf" });

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRates_ToCurrencyCode",
                table: "ExchangeRates",
                column: "ToCurrencyCode");

            migrationBuilder.CreateIndex(
                name: "IX_FileAnalysisCandidateTags_TransactionTagId",
                table: "FileAnalysisCandidateTags",
                column: "TransactionTagId");

            migrationBuilder.CreateIndex(
                name: "IX_FileAnalysisCandidateTransactions_AnalysisJobId",
                table: "FileAnalysisCandidateTransactions",
                column: "AnalysisJobId");

            migrationBuilder.CreateIndex(
                name: "IX_FileAnalysisCandidateTransactions_MatchedContactId",
                table: "FileAnalysisCandidateTransactions",
                column: "MatchedContactId");

            migrationBuilder.CreateIndex(
                name: "IX_FileAnalysisCandidateTransactions_ReviewedByUserId",
                table: "FileAnalysisCandidateTransactions",
                column: "ReviewedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_FileAnalysisCandidateTransactions_ReviewStatus",
                table: "FileAnalysisCandidateTransactions",
                column: "ReviewStatus");

            migrationBuilder.CreateIndex(
                name: "IX_FileAnalysisJobs_AccountFileId",
                table: "FileAnalysisJobs",
                column: "AccountFileId");

            migrationBuilder.CreateIndex(
                name: "IX_FileAnalysisJobs_RequestedByUserId",
                table: "FileAnalysisJobs",
                column: "RequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_FileAnalysisJobs_Status",
                table: "FileAnalysisJobs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_FileMetadata_FileBlobId",
                table: "FileMetadata",
                column: "FileBlobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FileMetadata_Sha256Hash",
                table: "FileMetadata",
                column: "Sha256Hash");

            migrationBuilder.CreateIndex(
                name: "IX_FileMetadata_UploadedAtUtc",
                table: "FileMetadata",
                column: "UploadedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_FileMetadata_UploadedByUserId",
                table: "FileMetadata",
                column: "UploadedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_FileMetadata_UploadedByUserId_Sha256Hash_SizeBytes",
                table: "FileMetadata",
                columns: new[] { "UploadedByUserId", "Sha256Hash", "SizeBytes" });

            migrationBuilder.CreateIndex(
                name: "IX_InsurancePolicies_Archived",
                table: "InsurancePolicies",
                column: "Archived");

            migrationBuilder.CreateIndex(
                name: "IX_InsurancePolicies_InsuredAccountId",
                table: "InsurancePolicies",
                column: "InsuredAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_InsurancePolicies_InsurerId",
                table: "InsurancePolicies",
                column: "InsurerId");

            migrationBuilder.CreateIndex(
                name: "IX_InsurancePolicies_Type_Archived",
                table: "InsurancePolicies",
                columns: new[] { "Type", "Archived" });

            migrationBuilder.CreateIndex(
                name: "IX_InsurancePolicyFiles_AttachedAtUtc",
                table: "InsurancePolicyFiles",
                column: "AttachedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_InsurancePolicyFiles_AttachedByUserId",
                table: "InsurancePolicyFiles",
                column: "AttachedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_InsurancePolicyFiles_FileMetadataId",
                table: "InsurancePolicyFiles",
                column: "FileMetadataId");

            migrationBuilder.CreateIndex(
                name: "IX_InsurancePolicyFiles_InsurancePolicyId_FileMetadataId",
                table: "InsurancePolicyFiles",
                columns: new[] { "InsurancePolicyId", "FileMetadataId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_Archived_EntryDate",
                table: "JournalEntries",
                columns: new[] { "Archived", "EntryDate" });

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_CreatedByUserId",
                table: "JournalEntries",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_ExternalUid",
                table: "JournalEntries",
                column: "ExternalUid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_UpdatedByUserId",
                table: "JournalEntries",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntryAttachments_FileId",
                table: "JournalEntryAttachments",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntryAttachments_JournalEntryId_FileId",
                table: "JournalEntryAttachments",
                columns: new[] { "JournalEntryId", "FileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntryContacts_ContactId",
                table: "JournalEntryContacts",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntryContacts_JournalEntryId_ContactId",
                table: "JournalEntryContacts",
                columns: new[] { "JournalEntryId", "ContactId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntryPhotos_JournalEntryId_PhotoId",
                table: "JournalEntryPhotos",
                columns: new[] { "JournalEntryId", "PhotoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntryPhotos_PhotoId",
                table: "JournalEntryPhotos",
                column: "PhotoId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntryTags_JournalEntryId_JournalTagId",
                table: "JournalEntryTags",
                columns: new[] { "JournalEntryId", "JournalTagId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntryTags_JournalTagId",
                table: "JournalEntryTags",
                column: "JournalTagId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalTags_Archived",
                table: "JournalTags",
                column: "Archived");

            migrationBuilder.CreateIndex(
                name: "IX_JournalTags_Name",
                table: "JournalTags",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_JournalTaskAttachments_FileId",
                table: "JournalTaskAttachments",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalTaskAttachments_JournalTaskId_FileId",
                table: "JournalTaskAttachments",
                columns: new[] { "JournalTaskId", "FileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JournalTasks_Archived_Position",
                table: "JournalTasks",
                columns: new[] { "Archived", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_JournalTasks_CreatedByUserId",
                table: "JournalTasks",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalTasks_ExternalUid",
                table: "JournalTasks",
                column: "ExternalUid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JournalTasks_UpdatedByUserId",
                table: "JournalTasks",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalTaskTagLinks_JournalTaskId_JournalTaskTagId",
                table: "JournalTaskTagLinks",
                columns: new[] { "JournalTaskId", "JournalTaskTagId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JournalTaskTagLinks_JournalTaskTagId",
                table: "JournalTaskTagLinks",
                column: "JournalTaskTagId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalTaskTags_Archived",
                table: "JournalTaskTags",
                column: "Archived");

            migrationBuilder.CreateIndex(
                name: "IX_JournalTaskTags_Name",
                table: "JournalTaskTags",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_LicenseAcceptances_UserId_LicenseHash_RespondedAt",
                table: "LicenseAcceptances",
                columns: new[] { "UserId", "LicenseHash", "RespondedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PhoneNumbers_ContactId",
                table: "PhoneNumbers",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_PhotoAlbumItems_PhotoAlbumId_PhotoId",
                table: "PhotoAlbumItems",
                columns: new[] { "PhotoAlbumId", "PhotoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PhotoAlbumItems_PhotoId",
                table: "PhotoAlbumItems",
                column: "PhotoId");

            migrationBuilder.CreateIndex(
                name: "IX_PhotoAlbums_Archived",
                table: "PhotoAlbums",
                column: "Archived");

            migrationBuilder.CreateIndex(
                name: "IX_PhotoAlbums_CoverPhotoId",
                table: "PhotoAlbums",
                column: "CoverPhotoId");

            migrationBuilder.CreateIndex(
                name: "IX_PhotoAlbums_CreatedByUserId",
                table: "PhotoAlbums",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PhotoAlbums_Name",
                table: "PhotoAlbums",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_PhotoAlbums_UpdatedByUserId",
                table: "PhotoAlbums",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PhotoPeople_ContactId",
                table: "PhotoPeople",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_PhotoPeople_PhotoId_ContactId",
                table: "PhotoPeople",
                columns: new[] { "PhotoId", "ContactId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Photos_Archived",
                table: "Photos",
                column: "Archived");

            migrationBuilder.CreateIndex(
                name: "IX_Photos_CreatedByUserId",
                table: "Photos",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Photos_Favourited",
                table: "Photos",
                column: "Favourited");

            migrationBuilder.CreateIndex(
                name: "IX_Photos_FileId",
                table: "Photos",
                column: "FileId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Photos_TakenAt",
                table: "Photos",
                column: "TakenAt");

            migrationBuilder.CreateIndex(
                name: "IX_Photos_UpdatedByUserId",
                table: "Photos",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PhotoTagLinks_PhotoId_PhotoTagId",
                table: "PhotoTagLinks",
                columns: new[] { "PhotoId", "PhotoTagId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PhotoTagLinks_PhotoTagId",
                table: "PhotoTagLinks",
                column: "PhotoTagId");

            migrationBuilder.CreateIndex(
                name: "IX_PhotoTags_Archived",
                table: "PhotoTags",
                column: "Archived");

            migrationBuilder.CreateIndex(
                name: "IX_PhotoTags_Name",
                table: "PhotoTags",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PolicyRenewalFiles_AttachedAtUtc",
                table: "PolicyRenewalFiles",
                column: "AttachedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyRenewalFiles_AttachedByUserId",
                table: "PolicyRenewalFiles",
                column: "AttachedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyRenewalFiles_FileMetadataId",
                table: "PolicyRenewalFiles",
                column: "FileMetadataId");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyRenewalFiles_PolicyRenewalId_FileMetadataId",
                table: "PolicyRenewalFiles",
                columns: new[] { "PolicyRenewalId", "FileMetadataId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PolicyRenewals_InsurancePolicyId_ToDate",
                table: "PolicyRenewals",
                columns: new[] { "InsurancePolicyId", "ToDate" });

            migrationBuilder.CreateIndex(
                name: "IX_RecurrencePatterns_CalendarId",
                table: "RecurrencePatterns",
                column: "CalendarId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurrencePatterns_CalendarId_ExternalUid",
                table: "RecurrencePatterns",
                columns: new[] { "CalendarId", "ExternalUid" });

            migrationBuilder.CreateIndex(
                name: "IX_RecurrencePatterns_CreatedByUserId",
                table: "RecurrencePatterns",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurrencePatterns_UpdatedByUserId",
                table: "RecurrencePatterns",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_Archived",
                table: "Subscriptions",
                column: "Archived");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_ContactId",
                table: "Subscriptions",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_Interval_Archived",
                table: "Subscriptions",
                columns: new[] { "Interval", "Archived" });

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_Paused",
                table: "Subscriptions",
                column: "Paused");

            migrationBuilder.CreateIndex(
                name: "IX_TaxStatementFiles_AttachedAtUtc",
                table: "TaxStatementFiles",
                column: "AttachedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_TaxStatementFiles_AttachedByUserId",
                table: "TaxStatementFiles",
                column: "AttachedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TaxStatementFiles_FileMetadataId",
                table: "TaxStatementFiles",
                column: "FileMetadataId");

            migrationBuilder.CreateIndex(
                name: "IX_TaxStatementFiles_TaxStatementId_FileMetadataId",
                table: "TaxStatementFiles",
                columns: new[] { "TaxStatementId", "FileMetadataId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaxStatements_Archived",
                table: "TaxStatements",
                column: "Archived");

            migrationBuilder.CreateIndex(
                name: "IX_TaxStatements_FiscalYear",
                table: "TaxStatements",
                column: "FiscalYear");

            migrationBuilder.CreateIndex(
                name: "IX_TaxStatementTags_TaxStatementId_TransactionTagId_Role",
                table: "TaxStatementTags",
                columns: new[] { "TaxStatementId", "TransactionTagId", "Role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaxStatementTags_TransactionTagId",
                table: "TaxStatementTags",
                column: "TransactionTagId");

            migrationBuilder.CreateIndex(
                name: "IX_TermsOfServiceAcceptances_TermsOfServiceVersionId",
                table: "TermsOfServiceAcceptances",
                column: "TermsOfServiceVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_TermsOfServiceAcceptances_UserId_TermsOfServiceVersionId_Res~",
                table: "TermsOfServiceAcceptances",
                columns: new[] { "UserId", "TermsOfServiceVersionId", "RespondedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TermsOfServiceVersions_PublishedByUserId",
                table: "TermsOfServiceVersions",
                column: "PublishedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TransactionFiles_AttachedAtUtc",
                table: "TransactionFiles",
                column: "AttachedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_TransactionFiles_AttachedByUserId",
                table: "TransactionFiles",
                column: "AttachedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TransactionFiles_FileMetadataId",
                table: "TransactionFiles",
                column: "FileMetadataId");

            migrationBuilder.CreateIndex(
                name: "IX_TransactionFiles_TransactionId_FileMetadataId",
                table: "TransactionFiles",
                columns: new[] { "TransactionId", "FileMetadataId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_AccountId",
                table: "Transactions",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_ContactId",
                table: "Transactions",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_CurrencyCode",
                table: "Transactions",
                column: "CurrencyCode");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_TimeStamp",
                table: "Transactions",
                column: "TimeStamp");

            migrationBuilder.CreateIndex(
                name: "IX_TransactionTagLinks_TransactionId_TransactionTagId",
                table: "TransactionTagLinks",
                columns: new[] { "TransactionId", "TransactionTagId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TransactionTagLinks_TransactionTagId",
                table: "TransactionTagLinks",
                column: "TransactionTagId");

            migrationBuilder.CreateIndex(
                name: "IX_TransactionTags_Archived",
                table: "TransactionTags",
                column: "Archived");

            migrationBuilder.CreateIndex(
                name: "IX_UserPreferences_UserId_Key",
                table: "UserPreferences",
                columns: new[] { "UserId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_UserId",
                table: "UserProfiles",
                column: "UserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountEstimates");

            migrationBuilder.DropTable(
                name: "AccountSmartTags");

            migrationBuilder.DropTable(
                name: "AccountTerms");

            migrationBuilder.DropTable(
                name: "Addresses");

            migrationBuilder.DropTable(
                name: "AspNetRoleClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserRoles");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "BudgetItems");

            migrationBuilder.DropTable(
                name: "CalendarEvents");

            migrationBuilder.DropTable(
                name: "ContractFiles");

            migrationBuilder.DropTable(
                name: "ContractParties");

            migrationBuilder.DropTable(
                name: "EmailAddresses");

            migrationBuilder.DropTable(
                name: "ExchangeRates");

            migrationBuilder.DropTable(
                name: "FileAnalysisCandidateTags");

            migrationBuilder.DropTable(
                name: "InsurancePolicyFiles");

            migrationBuilder.DropTable(
                name: "JournalEntryAttachments");

            migrationBuilder.DropTable(
                name: "JournalEntryContacts");

            migrationBuilder.DropTable(
                name: "JournalEntryPhotos");

            migrationBuilder.DropTable(
                name: "JournalEntryTags");

            migrationBuilder.DropTable(
                name: "JournalTaskAttachments");

            migrationBuilder.DropTable(
                name: "JournalTaskTagLinks");

            migrationBuilder.DropTable(
                name: "LicenseAcceptances");

            migrationBuilder.DropTable(
                name: "OrganizationDetails");

            migrationBuilder.DropTable(
                name: "PersonDetails");

            migrationBuilder.DropTable(
                name: "PhoneNumbers");

            migrationBuilder.DropTable(
                name: "PhotoAlbumItems");

            migrationBuilder.DropTable(
                name: "PhotoPeople");

            migrationBuilder.DropTable(
                name: "PhotoTagLinks");

            migrationBuilder.DropTable(
                name: "PolicyRenewalFiles");

            migrationBuilder.DropTable(
                name: "Subscriptions");

            migrationBuilder.DropTable(
                name: "SystemSettings");

            migrationBuilder.DropTable(
                name: "SystemSettingSecrets");

            migrationBuilder.DropTable(
                name: "TaxStatementFiles");

            migrationBuilder.DropTable(
                name: "TaxStatementTags");

            migrationBuilder.DropTable(
                name: "TermsOfServiceAcceptances");

            migrationBuilder.DropTable(
                name: "TransactionFiles");

            migrationBuilder.DropTable(
                name: "TransactionTagLinks");

            migrationBuilder.DropTable(
                name: "UserPreferences");

            migrationBuilder.DropTable(
                name: "UserProfiles");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "Budgets");

            migrationBuilder.DropTable(
                name: "RecurrencePatterns");

            migrationBuilder.DropTable(
                name: "Contracts");

            migrationBuilder.DropTable(
                name: "Currencies");

            migrationBuilder.DropTable(
                name: "FileAnalysisCandidateTransactions");

            migrationBuilder.DropTable(
                name: "JournalEntries");

            migrationBuilder.DropTable(
                name: "JournalTags");

            migrationBuilder.DropTable(
                name: "JournalTaskTags");

            migrationBuilder.DropTable(
                name: "JournalTasks");

            migrationBuilder.DropTable(
                name: "PhotoAlbums");

            migrationBuilder.DropTable(
                name: "PhotoTags");

            migrationBuilder.DropTable(
                name: "PolicyRenewals");

            migrationBuilder.DropTable(
                name: "TaxStatements");

            migrationBuilder.DropTable(
                name: "TermsOfServiceVersions");

            migrationBuilder.DropTable(
                name: "TransactionTags");

            migrationBuilder.DropTable(
                name: "Transactions");

            migrationBuilder.DropTable(
                name: "Calendars");

            migrationBuilder.DropTable(
                name: "FileAnalysisJobs");

            migrationBuilder.DropTable(
                name: "Photos");

            migrationBuilder.DropTable(
                name: "InsurancePolicies");

            migrationBuilder.DropTable(
                name: "AccountFiles");

            migrationBuilder.DropTable(
                name: "Accounts");

            migrationBuilder.DropTable(
                name: "FileMetadata");

            migrationBuilder.DropTable(
                name: "Contacts");

            migrationBuilder.DropTable(
                name: "AspNetUsers");

            migrationBuilder.DropTable(
                name: "FileBlob");
        }
    }
}
