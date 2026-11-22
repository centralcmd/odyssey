using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// The baseline of the November 2026 rebaseline (issue #581): the 95 migrations up to and including v0.51.0's
    /// <c>20261122104734_AddRecipes</c>, collapsed into one. It carries that migration's id, so a database at v0.51.0
    /// sees it as applied and a new database builds the full schema from it. Its output is pinned by
    /// <c>Baseline/20261122104734_AddRecipes.fingerprint.txt</c>; see <c>docs/migration-rebaseline.md</c>.
    /// </summary>
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
                name: "BookTags",
                columns: table => new
                {
                    BookTagId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Archived = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Icon = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Color = table.Column<string>(type: "varchar(7)", maxLength: 7, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookTags", x => x.BookTagId);
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
                name: "ExchangeRateProviderSeries",
                columns: table => new
                {
                    Provider = table.Column<int>(type: "int", nullable: false),
                    CurrencyCode = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    FirstObservation = table.Column<DateOnly>(type: "date", nullable: true),
                    NewestObservation = table.Column<DateOnly>(type: "date", nullable: false),
                    FetchedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExchangeRateProviderSeries", x => new { x.Provider, x.CurrencyCode });
                })
                .Annotation("MySql:CharSet", "utf8mb4");

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
                name: "FileStorageWorkerVerdicts",
                columns: table => new
                {
                    WorkerId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FailedCheck = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    FailedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    LastReportedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileStorageWorkerVerdicts", x => x.WorkerId);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "InstrumentPriceSourceStates",
                columns: table => new
                {
                    SourceKey = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    BackOffUntilUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    BackOffReason = table.Column<byte>(type: "tinyint unsigned", nullable: true),
                    RequestDay = table.Column<DateOnly>(type: "date", nullable: true),
                    RequestCount = table.Column<int>(type: "int", nullable: false),
                    RequestHourUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    RequestHourCount = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    Version = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstrumentPriceSourceStates", x => x.SourceKey);
                    table.CheckConstraint("CK_InstrumentPriceSourceStates_RequestCount", "`RequestCount` >= 0");
                    table.CheckConstraint("CK_InstrumentPriceSourceStates_RequestHourCount", "`RequestHourCount` >= 0");
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "JobHeartbeats",
                columns: table => new
                {
                    JobType = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    WorkerId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    LastTickAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobHeartbeats", x => new { x.JobType, x.WorkerId });
                    table.CheckConstraint("CK_JobHeartbeats_JobType", "`JobType` IN (1, 2, 3, 4, 6, 8, 9, 10, 11, 12)");
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
                    Archived = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Icon = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Color = table.Column<string>(type: "varchar(7)", maxLength: 7, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
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
                    Archived = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Icon = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Color = table.Column<string>(type: "varchar(7)", maxLength: 7, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
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
                name: "NewsFeedCrashEvents",
                columns: table => new
                {
                    NewsFeedCrashEventId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    LeaseOwner = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    DetectedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    FeedsInFlight = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsFeedCrashEvents", x => x.NewsFeedCrashEventId);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "NewsFeeds",
                columns: table => new
                {
                    NewsFeedId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Url = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UrlKeyHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    CustomTitle = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    FetchIntervalMinutes = table.Column<int>(type: "int", nullable: true),
                    DownloadImages = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ArchivedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    DeletionRequestedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    SettingsVersion = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Kind = table.Column<byte>(type: "tinyint unsigned", nullable: false, defaultValue: (byte)0),
                    KindChangedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    AutoDownload = table.Column<bool>(type: "tinyint(1)", nullable: false, defaultValue: false),
                    AutoDeleteEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false, defaultValue: false),
                    AutoDeleteAfterDays = table.Column<int>(type: "int", nullable: false, defaultValue: 30),
                    AutoDeleteChangedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsFeeds", x => x.NewsFeedId);
                    table.CheckConstraint("CK_NewsFeeds_AutoDeleteDays", "`AutoDeleteAfterDays` BETWEEN 1 AND 3650");
                    table.CheckConstraint("CK_NewsFeeds_Kind", "`Kind` IN (0, 1)");
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "NewsHostThrottle",
                columns: table => new
                {
                    HostKeyHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    NextAllowedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsHostThrottle", x => x.HostKeyHash);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "NewsStorageTotals",
                columns: table => new
                {
                    Id = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    ImageBytes = table.Column<long>(type: "bigint", nullable: false),
                    TextAndSourceBytes = table.Column<long>(type: "bigint", nullable: false),
                    AudioBytes = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    AudioPendingReleaseBytes = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    AudioReservedBytes = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    ArtworkBytes = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsStorageTotals", x => x.Id);
                    table.CheckConstraint("CK_NewsStorageTotals_PodcastBytesNonNegative", "`AudioBytes` >= 0 AND `AudioPendingReleaseBytes` >= 0 AND `AudioReservedBytes` >= 0 AND `ArtworkBytes` >= 0");
                    table.CheckConstraint("CK_NewsStorageTotals_SingleRow", "`Id` = 1");
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "NewsTags",
                columns: table => new
                {
                    NewsTagId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    NameKey = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Icon = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Color = table.Column<string>(type: "varchar(7)", maxLength: 7, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ArchivedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsTags", x => x.NewsTagId);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PhotoOriginalBuckets",
                columns: table => new
                {
                    Year = table.Column<int>(type: "int", nullable: false),
                    CurrentBucket = table.Column<int>(type: "int", nullable: false),
                    PhotosWritten = table.Column<int>(type: "int", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoOriginalBuckets", x => x.Year);
                    table.CheckConstraint("CK_PhotoOriginalBuckets_CurrentBucketPositive", "`CurrentBucket` >= 1");
                    table.CheckConstraint("CK_PhotoOriginalBuckets_PhotosWrittenRange", "`PhotosWritten` BETWEEN 0 AND 10000");
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
                    Archived = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Icon = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Color = table.Column<string>(type: "varchar(7)", maxLength: 7, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoTags", x => x.PhotoTagId);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PhotoThumbnailBuckets",
                columns: table => new
                {
                    Id = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    CurrentBucket = table.Column<int>(type: "int", nullable: false),
                    PhotosWritten = table.Column<int>(type: "int", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoThumbnailBuckets", x => x.Id);
                    table.CheckConstraint("CK_PhotoThumbnailBuckets_CurrentBucketPositive", "`CurrentBucket` >= 1");
                    table.CheckConstraint("CK_PhotoThumbnailBuckets_PhotosWrittenRange", "`PhotosWritten` BETWEEN 0 AND 10000");
                    table.CheckConstraint("CK_PhotoThumbnailBuckets_Singleton", "`Id` = 1");
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PhotoThumbnailIncidents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Kind = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    WorkerName = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoThumbnailIncidents", x => x.Id);
                    table.CheckConstraint("CK_PhotoThumbnailIncidents_KindRange", "`Kind` IN (1, 2, 3)");
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PhotoThumbnailWorkerHeartbeats",
                columns: table => new
                {
                    WorkerName = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    State = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoThumbnailWorkerHeartbeats", x => x.WorkerName);
                    table.CheckConstraint("CK_PhotoThumbnailWorkerHeartbeats_StateRange", "`State` IN (0, 1, 2, 3, 4)");
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PodcastArtworkIncidents",
                columns: table => new
                {
                    PodcastArtworkIncidentId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Kind = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    DetectedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PodcastArtworkIncidents", x => x.PodcastArtworkIncidentId);
                    table.CheckConstraint("CK_PodcastArtworkIncidents_Kind", "`Kind` IN (1, 2)");
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RecipeTags",
                columns: table => new
                {
                    RecipeTagId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Archived = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Icon = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Color = table.Column<string>(type: "varchar(7)", maxLength: 7, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeTags", x => x.RecipeTagId);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "StoredFileBuckets",
                columns: table => new
                {
                    FeatureKey = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Year = table.Column<int>(type: "int", nullable: false),
                    CurrentBucket = table.Column<int>(type: "int", nullable: false),
                    ObjectsWritten = table.Column<int>(type: "int", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoredFileBuckets", x => new { x.FeatureKey, x.Year });
                    table.CheckConstraint("CK_StoredFileBuckets_CurrentBucketPositive", "`CurrentBucket` >= 1");
                    table.CheckConstraint("CK_StoredFileBuckets_ObjectsWrittenRange", "`ObjectsWritten` BETWEEN 0 AND 10000");
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
                name: "TransactionTags",
                columns: table => new
                {
                    TransactionTagId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Archived = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Icon = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Color = table.Column<string>(type: "varchar(7)", maxLength: 7, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
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
                    Icon = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
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
                name: "Contracts",
                columns: table => new
                {
                    ContractId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Name = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Type = table.Column<int>(type: "int", nullable: false, defaultValue: 3),
                    Description = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ReferenceNumber = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    StartDate = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    EndDate = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CompletionDate = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    SmartTagScopeStart = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    SmartTagScopeEnd = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Archived = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Paused = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Ready = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Signed = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contracts", x => x.ContractId);
                    table.CheckConstraint("CK_Contracts_SmartTagScopeOrder", "`SmartTagScopeStart` IS NULL OR `SmartTagScopeEnd` IS NULL OR `SmartTagScopeStart` <= `SmartTagScopeEnd`");
                    table.ForeignKey(
                        name: "FK_Contracts_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "JobRuns",
                columns: table => new
                {
                    JobRunId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    JobType = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    Trigger = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    Status = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    FailureCategory = table.Column<byte>(type: "tinyint unsigned", nullable: true),
                    HeldReason = table.Column<byte>(type: "tinyint unsigned", nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: true),
                    ClaimedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LastActivityAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Generation = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    WorkerId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    RequestedByUserId = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ItemsProcessed = table.Column<int>(type: "int", nullable: true),
                    ItemsFailed = table.Column<int>(type: "int", nullable: true),
                    OpenHeldGuard = table.Column<int>(type: "int", nullable: true, computedColumnSql: "IF(`Status` = 5 AND `FinishedAtUtc` IS NULL, 1, NULL)", stored: false),
                    ExclusiveGuard = table.Column<int>(type: "int", nullable: true, computedColumnSql: "IF(`Status` = 0 AND (`JobType` IN (1, 3, 8) OR `Trigger` = 1), `JobType`, NULL)", stored: false),
                    Provider = table.Column<int>(type: "int", nullable: true),
                    ErrorKind = table.Column<int>(type: "int", nullable: true),
                    SpanFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    SpanTo = table.Column<DateOnly>(type: "date", nullable: true),
                    CurrencyCodes = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RowsInserted = table.Column<int>(type: "int", nullable: true),
                    RowsSkipped = table.Column<int>(type: "int", nullable: true),
                    RowsRejected = table.Column<int>(type: "int", nullable: true),
                    RowsDeleted = table.Column<int>(type: "int", nullable: true),
                    AnalysesCompleted = table.Column<int>(type: "int", nullable: true),
                    AnalysesFailed = table.Column<int>(type: "int", nullable: true),
                    AnalysesRefused = table.Column<int>(type: "int", nullable: true),
                    FilesErased = table.Column<int>(type: "int", nullable: true),
                    AbandonedWritesSwept = table.Column<int>(type: "int", nullable: true),
                    ObjectsScrubbed = table.Column<int>(type: "int", nullable: true),
                    IntegrityFailures = table.Column<int>(type: "int", nullable: true),
                    OrphansFound = table.Column<int>(type: "int", nullable: true),
                    HeldByCheck = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    InstrumentsAttempted = table.Column<int>(type: "int", nullable: true),
                    InstrumentsSucceeded = table.Column<int>(type: "int", nullable: true),
                    InstrumentsFailed = table.Column<int>(type: "int", nullable: true),
                    PricesInserted = table.Column<int>(type: "int", nullable: true),
                    FallbacksUsed = table.Column<int>(type: "int", nullable: true),
                    RequestsMade = table.Column<int>(type: "int", nullable: true),
                    InstrumentsDeferred = table.Column<int>(type: "int", nullable: true),
                    PricesDeleted = table.Column<int>(type: "int", nullable: true),
                    FeedsFetched = table.Column<int>(type: "int", nullable: true),
                    ItemsStored = table.Column<int>(type: "int", nullable: true),
                    ItemsRevised = table.Column<int>(type: "int", nullable: true),
                    ImagesStored = table.Column<int>(type: "int", nullable: true),
                    FeedsQuarantined = table.Column<int>(type: "int", nullable: true),
                    FeedsMarked = table.Column<int>(type: "int", nullable: true),
                    ArtworkStored = table.Column<int>(type: "int", nullable: true),
                    ArtworkFailed = table.Column<int>(type: "int", nullable: true),
                    ChaptersFetched = table.Column<int>(type: "int", nullable: true),
                    PhotosRendered = table.Column<int>(type: "int", nullable: true),
                    PhotosFailed = table.Column<int>(type: "int", nullable: true),
                    PhotosRequeued = table.Column<int>(type: "int", nullable: true),
                    Crashes = table.Column<int>(type: "int", nullable: true),
                    Hangs = table.Column<int>(type: "int", nullable: true),
                    DownloadsStored = table.Column<int>(type: "int", nullable: true),
                    DownloadsFailed = table.Column<int>(type: "int", nullable: true),
                    DownloadsDeferred = table.Column<int>(type: "int", nullable: true),
                    DownloadsRecovered = table.Column<int>(type: "int", nullable: true),
                    ReservationsRefused = table.Column<int>(type: "int", nullable: true),
                    AudioBytesStored = table.Column<long>(type: "bigint", nullable: true),
                    AudioReleased = table.Column<int>(type: "int", nullable: true),
                    AudioBytesReleased = table.Column<long>(type: "bigint", nullable: true),
                    SeriesKeyed = table.Column<int>(type: "int", nullable: true),
                    SeriesKeyFailures = table.Column<int>(type: "int", nullable: true),
                    KeyRowsDetached = table.Column<int>(type: "int", nullable: true),
                    PointsAttempted = table.Column<int>(type: "int", nullable: true),
                    PointsSkippedNotExpired = table.Column<int>(type: "int", nullable: true),
                    PointsFailed = table.Column<int>(type: "int", nullable: true),
                    ForecastsStored = table.Column<int>(type: "int", nullable: true),
                    ObservationDaysStored = table.Column<int>(type: "int", nullable: true),
                    FailureKind = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobRuns", x => x.JobRunId);
                    table.CheckConstraint("CK_JobRuns_CountersNonNegative", "COALESCE(`ItemsProcessed`, 0) >= 0 AND COALESCE(`ItemsFailed`, 0) >= 0 AND COALESCE(`DurationMs`, 0) >= 0 AND COALESCE(`RowsInserted`, 0) >= 0 AND COALESCE(`RowsSkipped`, 0) >= 0 AND COALESCE(`RowsRejected`, 0) >= 0 AND COALESCE(`RowsDeleted`, 0) >= 0 AND COALESCE(`PointsAttempted`, 0) >= 0 AND COALESCE(`PointsSkippedNotExpired`, 0) >= 0 AND COALESCE(`PointsFailed`, 0) >= 0 AND COALESCE(`ForecastsStored`, 0) >= 0 AND COALESCE(`ObservationDaysStored`, 0) >= 0 AND COALESCE(`FilesErased`, 0) >= 0 AND COALESCE(`AbandonedWritesSwept`, 0) >= 0 AND COALESCE(`ObjectsScrubbed`, 0) >= 0 AND COALESCE(`IntegrityFailures`, 0) >= 0 AND COALESCE(`OrphansFound`, 0) >= 0 AND COALESCE(`PhotosRendered`, 0) >= 0 AND COALESCE(`PhotosFailed`, 0) >= 0 AND COALESCE(`PhotosRequeued`, 0) >= 0 AND COALESCE(`Crashes`, 0) >= 0 AND COALESCE(`Hangs`, 0) >= 0 AND COALESCE(`AnalysesCompleted`, 0) >= 0 AND COALESCE(`AnalysesFailed`, 0) >= 0 AND COALESCE(`AnalysesRefused`, 0) >= 0 AND COALESCE(`InstrumentsAttempted`, 0) >= 0 AND COALESCE(`InstrumentsSucceeded`, 0) >= 0 AND COALESCE(`InstrumentsFailed`, 0) >= 0 AND COALESCE(`PricesInserted`, 0) >= 0 AND COALESCE(`FallbacksUsed`, 0) >= 0 AND COALESCE(`RequestsMade`, 0) >= 0 AND COALESCE(`PricesDeleted`, 0) >= 0 AND COALESCE(`InstrumentsDeferred`, 0) >= 0 AND COALESCE(`FeedsFetched`, 0) >= 0 AND COALESCE(`ItemsStored`, 0) >= 0 AND COALESCE(`ItemsRevised`, 0) >= 0 AND COALESCE(`ImagesStored`, 0) >= 0 AND COALESCE(`FeedsQuarantined`, 0) >= 0 AND COALESCE(`FeedsMarked`, 0) >= 0 AND COALESCE(`SeriesKeyed`, 0) >= 0 AND COALESCE(`SeriesKeyFailures`, 0) >= 0 AND COALESCE(`KeyRowsDetached`, 0) >= 0 AND COALESCE(`ArtworkStored`, 0) >= 0 AND COALESCE(`ArtworkFailed`, 0) >= 0 AND COALESCE(`ChaptersFetched`, 0) >= 0 AND COALESCE(`DownloadsStored`, 0) >= 0 AND COALESCE(`DownloadsFailed`, 0) >= 0 AND COALESCE(`DownloadsDeferred`, 0) >= 0 AND COALESCE(`DownloadsRecovered`, 0) >= 0 AND COALESCE(`ReservationsRefused`, 0) >= 0 AND COALESCE(`AudioBytesStored`, 0) >= 0 AND COALESCE(`AudioReleased`, 0) >= 0 AND COALESCE(`AudioBytesReleased`, 0) >= 0");
                    table.CheckConstraint("CK_JobRuns_ErrorKind", "`ErrorKind` IS NULL OR `ErrorKind` BETWEEN 1 AND 9");
                    table.CheckConstraint("CK_JobRuns_FailureCategory", "`FailureCategory` IS NULL OR `FailureCategory` BETWEEN 1 AND 10");
                    table.CheckConstraint("CK_JobRuns_FinishedMatchesStatus", "(`Status` = 0 AND `FinishedAtUtc` IS NULL) OR `Status` = 5 OR (`Status` IN (1, 2, 3, 4) AND `FinishedAtUtc` IS NOT NULL)");
                    table.CheckConstraint("CK_JobRuns_HeldHasWorkerAndReason", "`Status` <> 5 OR (`WorkerId` IS NOT NULL AND `HeldReason` IS NOT NULL)");
                    table.CheckConstraint("CK_JobRuns_HeldReason", "`HeldReason` IS NULL OR `HeldReason` BETWEEN 1 AND 5");
                    table.CheckConstraint("CK_JobRuns_HeldReasonOnlyOnHeld", "`HeldReason` IS NULL OR `Status` = 5");
                    table.CheckConstraint("CK_JobRuns_JobType", "`JobType` IN (1, 2, 3, 4, 6, 8, 9, 10, 11, 12)");
                    table.CheckConstraint("CK_JobRuns_Provider", "`Provider` IS NULL OR `Provider` IN (1)");
                    table.CheckConstraint("CK_JobRuns_Status", "`Status` IN (0, 1, 2, 3, 4, 5)");
                    table.CheckConstraint("CK_JobRuns_Trigger", "`Trigger` IN (0, 1, 2)");
                    table.ForeignKey(
                        name: "FK_JobRuns_AspNetUsers_RequestedByUserId",
                        column: x => x.RequestedByUserId,
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
                name: "Recipes",
                columns: table => new
                {
                    RecipeId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Title = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Notes = table.Column<string>(type: "varchar(8000)", maxLength: 8000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Source = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Servings = table.Column<int>(type: "int", nullable: false),
                    ServingsLabel = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PrepMinutes = table.Column<int>(type: "int", nullable: true),
                    CookMinutes = table.Column<int>(type: "int", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
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
                    table.PrimaryKey("PK_Recipes", x => x.RecipeId);
                    table.ForeignKey(
                        name: "FK_Recipes_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Recipes_AspNetUsers_UpdatedByUserId",
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
                name: "UserProfileImages",
                columns: table => new
                {
                    UserProfileImageId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    UserId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ImageVersion = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ContentType = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256Hash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Width = table.Column<int>(type: "int", nullable: false),
                    Height = table.Column<int>(type: "int", nullable: false),
                    UploadedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserProfileImages", x => x.UserProfileImageId);
                    table.ForeignKey(
                        name: "FK_UserProfileImages_AspNetUsers_UserId",
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
                name: "WeatherPoints",
                columns: table => new
                {
                    WeatherPointId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Name = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ForecastEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    HistoryEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    IsTracked = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    EnabledAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CurrentSiteVersion = table.Column<int>(type: "int", nullable: false),
                    PurgeStartedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    PurgeMode = table.Column<int>(type: "int", nullable: true),
                    StationResolvedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    SettingsVersion = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CreatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UpdatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeatherPoints", x => x.WeatherPointId);
                    table.CheckConstraint("CK_WeatherPoints_CurrentSiteVersionPositive", "`CurrentSiteVersion` >= 1");
                    table.CheckConstraint("CK_WeatherPoints_PurgeModeWithFence", "(`PurgeStartedAt` IS NULL) = (`PurgeMode` IS NULL)");
                    table.ForeignKey(
                        name: "FK_WeatherPoints_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_WeatherPoints_AspNetUsers_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
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
                    table.ForeignKey(
                        name: "FK_Budgets_Currencies_BaseCurrencyCode",
                        column: x => x.BaseCurrencyCode,
                        principalTable: "Currencies",
                        principalColumn: "CurrencyCode",
                        onDelete: ReferentialAction.Restrict);
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
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Provider = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExchangeRates", x => x.ExchangeRateId);
                    table.CheckConstraint("CK_ExchangeRates_Provider", "`Provider` IS NULL OR `Provider` IN (1)");
                    table.CheckConstraint("CK_ExchangeRates_RatePositive", "`Rate` > 0");
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
                name: "ExchangeRateSyncCurrencies",
                columns: table => new
                {
                    CurrencyCode = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Enabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    HistoryStartDate = table.Column<DateOnly>(type: "date", nullable: true),
                    BackfilledFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExchangeRateSyncCurrencies", x => x.CurrencyCode);
                    table.ForeignKey(
                        name: "FK_ExchangeRateSyncCurrencies_AspNetUsers_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ExchangeRateSyncCurrencies_Currencies_CurrencyCode",
                        column: x => x.CurrencyCode,
                        principalTable: "Currencies",
                        principalColumn: "CurrencyCode",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Instruments",
                columns: table => new
                {
                    InstrumentId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Symbol = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    MicCode = table.Column<string>(type: "varchar(4)", maxLength: 4, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Isin = table.Column<string>(type: "varchar(12)", maxLength: 12, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Name = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Type = table.Column<int>(type: "int", nullable: false),
                    CurrencyCode = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ListingKey = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ShowOnDashboard = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Archived = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UpdatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Instruments", x => x.InstrumentId);
                    table.CheckConstraint("CK_Instruments_Type", "`Type` IN (1, 2, 3, 9)");
                    table.ForeignKey(
                        name: "FK_Instruments_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Instruments_AspNetUsers_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Instruments_Currencies_CurrencyCode",
                        column: x => x.CurrencyCode,
                        principalTable: "Currencies",
                        principalColumn: "CurrencyCode",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Properties",
                columns: table => new
                {
                    PropertyId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Name = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Type = table.Column<int>(type: "int", nullable: false),
                    CurrencyCode = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AcquiredDate = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    DisposedDate = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Notes = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Archived = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Properties", x => x.PropertyId);
                    table.ForeignKey(
                        name: "FK_Properties_Currencies_CurrencyCode",
                        column: x => x.CurrencyCode,
                        principalTable: "Currencies",
                        principalColumn: "CurrencyCode",
                        onDelete: ReferentialAction.Restrict);
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
                    SettlementStartDate = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    SettlementEndDate = table.Column<DateTime>(type: "datetime(6)", nullable: true),
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
                    table.ForeignKey(
                        name: "FK_TaxStatements_Currencies_BaseCurrencyCode",
                        column: x => x.BaseCurrencyCode,
                        principalTable: "Currencies",
                        principalColumn: "CurrencyCode",
                        onDelete: ReferentialAction.Restrict);
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

            migrationBuilder.CreateTable(
                name: "NewsArticles",
                columns: table => new
                {
                    NewsArticleId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    NewsFeedId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Bucket = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Author = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Excerpt = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    BodyOmittedReason = table.Column<byte>(type: "tinyint unsigned", nullable: true),
                    Link = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    LinkKeyHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: true),
                    ContentHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    IdentityHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    BodyLength = table.Column<int>(type: "int", nullable: false),
                    PublishedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    FeedUpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    FirstSeenAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    SortAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ContentChangedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    RevisionCount = table.Column<int>(type: "int", nullable: false),
                    IsRead = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    ReadAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    FeedVisible = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Language = table.Column<string>(type: "varchar(35)", maxLength: 35, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    FeedKind = table.Column<byte>(type: "tinyint unsigned", nullable: false, defaultValue: (byte)0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsArticles", x => x.NewsArticleId);
                    table.UniqueConstraint("AK_NewsArticles_NewsArticleId_NewsFeedId", x => new { x.NewsArticleId, x.NewsFeedId });
                    table.CheckConstraint("CK_NewsArticles_BodyOmittedReason", "`BodyOmittedReason` IS NULL OR `BodyOmittedReason` IN (1, 2)");
                    table.CheckConstraint("CK_NewsArticles_FeedKind", "`FeedKind` IN (0, 1)");
                    table.CheckConstraint("CK_NewsArticles_ReadState", "(`IsRead` = 0 AND `ReadAtUtc` IS NULL) OR (`IsRead` = 1 AND `ReadAtUtc` IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_NewsArticles_NewsFeeds_NewsFeedId",
                        column: x => x.NewsFeedId,
                        principalTable: "NewsFeeds",
                        principalColumn: "NewsFeedId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "NewsFeedBuckets",
                columns: table => new
                {
                    NewsFeedBucketId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    NewsFeedId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CurrentBucket = table.Column<int>(type: "int", nullable: false),
                    ArticlesInBucket = table.Column<int>(type: "int", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsFeedBuckets", x => x.NewsFeedBucketId);
                    table.ForeignKey(
                        name: "FK_NewsFeedBuckets_NewsFeeds_NewsFeedId",
                        column: x => x.NewsFeedId,
                        principalTable: "NewsFeeds",
                        principalColumn: "NewsFeedId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "NewsFeedCounters",
                columns: table => new
                {
                    NewsFeedId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ArticleCount = table.Column<int>(type: "int", nullable: false),
                    UnreadCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsFeedCounters", x => x.NewsFeedId);
                    table.ForeignKey(
                        name: "FK_NewsFeedCounters_NewsFeeds_NewsFeedId",
                        column: x => x.NewsFeedId,
                        principalTable: "NewsFeeds",
                        principalColumn: "NewsFeedId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "NewsFeedFetchStates",
                columns: table => new
                {
                    NewsFeedId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FetchFence = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Title = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SiteUrl = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Language = table.Column<string>(type: "varchar(35)", maxLength: 35, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Format = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    FeedTtlMinutes = table.Column<int>(type: "int", nullable: true),
                    NextFetchAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    RefreshRequestedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ValidatorsResetRequestedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    FeedKindRewrittenAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    SuggestedUrl = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    LeaseOwner = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    LeaseExpiresAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CrashSuspectCount = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    QuarantinedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    QuotaReachedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    QuotaReason = table.Column<byte>(type: "tinyint unsigned", nullable: true),
                    LastFetchAttemptAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LastSuccessAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LastErrorAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LastHttpStatus = table.Column<int>(type: "int", nullable: true),
                    LastErrorKind = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    LastErrorMessage = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ConsecutiveFailures = table.Column<int>(type: "int", nullable: false),
                    RedirectedFromHttps = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    ETag = table.Column<string>(type: "varchar(256)", unicode: false, maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    LastModified = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    LastBodySha256 = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: true),
                    DailyQuotaDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ArticlesToday = table.Column<int>(type: "int", nullable: false),
                    ImageBytesToday = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsFeedFetchStates", x => x.NewsFeedId);
                    table.CheckConstraint("CK_NewsFeedFetchStates_QuotaReason", "(`QuotaReachedAtUtc` IS NULL) = (`QuotaReason` IS NULL) AND (`QuotaReason` IS NULL OR `QuotaReason` IN (1, 2))");
                    table.ForeignKey(
                        name: "FK_NewsFeedFetchStates_NewsFeeds_NewsFeedId",
                        column: x => x.NewsFeedId,
                        principalTable: "NewsFeeds",
                        principalColumn: "NewsFeedId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PodcastArtwork",
                columns: table => new
                {
                    PodcastArtworkId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    NewsFeedId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    SourceUrl = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SourceUrlKeyHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    State = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    DeferredReason = table.Column<byte>(type: "tinyint unsigned", nullable: true),
                    RejectedReason = table.Column<byte>(type: "tinyint unsigned", nullable: true),
                    Width = table.Column<int>(type: "int", nullable: true),
                    Height = table.Column<int>(type: "int", nullable: true),
                    Attempts = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LeaseOwner = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    LeaseExpiresAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    RenderStartedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PodcastArtwork", x => x.PodcastArtworkId);
                    table.CheckConstraint("CK_PodcastArtwork_DeferredReason", "`DeferredReason` IS NULL OR `DeferredReason` IN (1)");
                    table.CheckConstraint("CK_PodcastArtwork_RejectedReason", "`RejectedReason` IS NULL OR `RejectedReason` IN (1, 2, 3)");
                    table.CheckConstraint("CK_PodcastArtwork_State", "`State` IN (0, 1, 2, 3, 4)");
                    table.ForeignKey(
                        name: "FK_PodcastArtwork_NewsFeeds_NewsFeedId",
                        column: x => x.NewsFeedId,
                        principalTable: "NewsFeeds",
                        principalColumn: "NewsFeedId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "NewsFeedTags",
                columns: table => new
                {
                    NewsFeedTagId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    NewsFeedId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    NewsTagId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsFeedTags", x => x.NewsFeedTagId);
                    table.ForeignKey(
                        name: "FK_NewsFeedTags_NewsFeeds_NewsFeedId",
                        column: x => x.NewsFeedId,
                        principalTable: "NewsFeeds",
                        principalColumn: "NewsFeedId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NewsFeedTags_NewsTags_NewsTagId",
                        column: x => x.NewsTagId,
                        principalTable: "NewsTags",
                        principalColumn: "NewsTagId",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ContractSmartTags",
                columns: table => new
                {
                    ContractId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    TransactionTagId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    AddedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractSmartTags", x => new { x.ContractId, x.TransactionTagId });
                    table.ForeignKey(
                        name: "FK_ContractSmartTags_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contracts",
                        principalColumn: "ContractId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ContractSmartTags_TransactionTags_TransactionTagId",
                        column: x => x.TransactionTagId,
                        principalTable: "TransactionTags",
                        principalColumn: "TransactionTagId",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Terms",
                columns: table => new
                {
                    TermId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ContractId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Label = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    LabelKey = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ValueUnit = table.Column<int>(type: "int", nullable: false),
                    Direction = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    Value = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    TextValue = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DateTimeValue = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CurrencyCode = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Interval = table.Column<int>(type: "int", nullable: true),
                    IntervalCount = table.Column<int>(type: "int", nullable: true),
                    AnchorDate = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    EffectiveFrom = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Note = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Terms", x => x.TermId);
                    table.CheckConstraint("CK_Terms_ValueMatchesUnit", "(`ValueUnit` IN (0, 1) AND `Value` IS NOT NULL AND `TextValue` IS NULL AND `DateTimeValue` IS NULL) OR (`ValueUnit` = 2 AND `Value` IS NULL AND `TextValue` IS NOT NULL AND `DateTimeValue` IS NULL) OR (`ValueUnit` = 3 AND `Value` IS NULL AND `TextValue` IS NULL AND `DateTimeValue` IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_Terms_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contracts",
                        principalColumn: "ContractId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Terms_Currencies_CurrencyCode",
                        column: x => x.CurrencyCode,
                        principalTable: "Currencies",
                        principalColumn: "CurrencyCode",
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
                name: "RecipeIngredients",
                columns: table => new
                {
                    RecipeIngredientId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    RecipeId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Position = table.Column<int>(type: "int", nullable: false),
                    Section = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Amount = table.Column<decimal>(type: "decimal(12,3)", precision: 12, scale: 3, nullable: true),
                    AmountMax = table.Column<decimal>(type: "decimal(12,3)", precision: 12, scale: 3, nullable: true),
                    Unit = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Preparation = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Optional = table.Column<bool>(type: "tinyint(1)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeIngredients", x => x.RecipeIngredientId);
                    table.ForeignKey(
                        name: "FK_RecipeIngredients_Recipes_RecipeId",
                        column: x => x.RecipeId,
                        principalTable: "Recipes",
                        principalColumn: "RecipeId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RecipeSteps",
                columns: table => new
                {
                    RecipeStepId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    RecipeId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Position = table.Column<int>(type: "int", nullable: false),
                    Section = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Text = table.Column<string>(type: "varchar(4000)", maxLength: 4000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeSteps", x => x.RecipeStepId);
                    table.ForeignKey(
                        name: "FK_RecipeSteps_Recipes_RecipeId",
                        column: x => x.RecipeId,
                        principalTable: "Recipes",
                        principalColumn: "RecipeId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RecipeTagLinks",
                columns: table => new
                {
                    RecipeTagLinkId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    RecipeId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    RecipeTagId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeTagLinks", x => x.RecipeTagLinkId);
                    table.ForeignKey(
                        name: "FK_RecipeTagLinks_RecipeTags_RecipeTagId",
                        column: x => x.RecipeTagId,
                        principalTable: "RecipeTags",
                        principalColumn: "RecipeTagId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecipeTagLinks_Recipes_RecipeId",
                        column: x => x.RecipeId,
                        principalTable: "Recipes",
                        principalColumn: "RecipeId",
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
                name: "UserProfileImageBlobs",
                columns: table => new
                {
                    UserProfileImageId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Content = table.Column<byte[]>(type: "longblob", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserProfileImageBlobs", x => x.UserProfileImageId);
                    table.ForeignKey(
                        name: "FK_UserProfileImageBlobs_UserProfileImages_UserProfileImageId",
                        column: x => x.UserProfileImageId,
                        principalTable: "UserProfileImages",
                        principalColumn: "UserProfileImageId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Locations",
                columns: table => new
                {
                    LocationId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AddressText = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Line1 = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Line2 = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PostalCode = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    City = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Region = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CountryCode = table.Column<string>(type: "char(2)", maxLength: 2, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AddressKey = table.Column<string>(type: "char(64)", maxLength: 64, nullable: true, collation: "ascii_bin")
                        .Annotation("MySql:CharSet", "ascii"),
                    MergedIntoLocationId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    WeatherPointId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    Latitude = table.Column<decimal>(type: "decimal(7,5)", precision: 7, scale: 5, nullable: true),
                    Longitude = table.Column<decimal>(type: "decimal(8,5)", precision: 8, scale: 5, nullable: true),
                    AltitudeMeters = table.Column<int>(type: "int", nullable: true),
                    TimeZoneId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Source = table.Column<int>(type: "int", nullable: false),
                    SourceReference = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
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
                    table.PrimaryKey("PK_Locations", x => x.LocationId);
                    table.CheckConstraint("CK_Locations_AltitudeRange", "`AltitudeMeters` IS NULL OR `AltitudeMeters` BETWEEN -500 AND 9000");
                    table.CheckConstraint("CK_Locations_CoordinatesAllOrNone", "(`Latitude` IS NULL AND `Longitude` IS NULL AND `TimeZoneId` IS NULL) OR (`Latitude` IS NOT NULL AND `Longitude` IS NOT NULL AND `TimeZoneId` IS NOT NULL)");
                    table.CheckConstraint("CK_Locations_LatitudeRange", "`Latitude` BETWEEN -90 AND 90");
                    table.CheckConstraint("CK_Locations_LongitudeRange", "`Longitude` BETWEEN -180 AND 180");
                    table.CheckConstraint("CK_Locations_ManualHasNoReference", "`Source` <> 1 OR `SourceReference` IS NULL");
                    table.CheckConstraint("CK_Locations_SourceKnown", "`Source` IN (1, 2, 3, 4, 5, 6, 7)");
                    table.ForeignKey(
                        name: "FK_Locations_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Locations_AspNetUsers_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Locations_Locations_MergedIntoLocationId",
                        column: x => x.MergedIntoLocationId,
                        principalTable: "Locations",
                        principalColumn: "LocationId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Locations_WeatherPoints_WeatherPointId",
                        column: x => x.WeatherPointId,
                        principalTable: "WeatherPoints",
                        principalColumn: "WeatherPointId",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "WeatherDays",
                columns: table => new
                {
                    WeatherPointId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    LocalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ForecastSymbolCode = table.Column<string>(type: "varchar(48)", maxLength: 48, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ForecastMinTempC = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    ForecastMaxTempC = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    ForecastMeanWindSpeedMps = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    ForecastMaxWindGustMps = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    ForecastPrecipitationMm = table.Column<decimal>(type: "decimal(5,1)", precision: 5, scale: 1, nullable: true),
                    ForecastCoverageHours = table.Column<int>(type: "int", nullable: true),
                    ForecastIssuedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ForecastSiteVersion = table.Column<int>(type: "int", nullable: true),
                    ObservedMinTempC = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    ObservedMaxTempC = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    ObservedMeanTempC = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    ObservedMeanWindSpeedMps = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    ObservedMaxWindGustMps = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    ObservedPrecipitationMm = table.Column<decimal>(type: "decimal(5,1)", precision: 5, scale: 1, nullable: true),
                    ObservedFetchedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ObservedSiteVersion = table.Column<int>(type: "int", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeatherDays", x => new { x.WeatherPointId, x.LocalDate });
                    table.CheckConstraint("CK_WeatherDays_HasData", "`ForecastCoverageHours` IS NOT NULL OR `ObservedFetchedAtUtc` IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_WeatherDays_WeatherPoints_WeatherPointId",
                        column: x => x.WeatherPointId,
                        principalTable: "WeatherPoints",
                        principalColumn: "WeatherPointId",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "WeatherForecastRuns",
                columns: table => new
                {
                    WeatherForecastRunId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    WeatherPointId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    SiteVersion = table.Column<int>(type: "int", nullable: false),
                    Provider = table.Column<int>(type: "int", nullable: false),
                    ProviderUpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    FetchedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Latitude = table.Column<decimal>(type: "decimal(6,3)", precision: 6, scale: 3, nullable: false),
                    Longitude = table.Column<decimal>(type: "decimal(7,3)", precision: 7, scale: 3, nullable: false),
                    AltitudeMeters = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeatherForecastRuns", x => x.WeatherForecastRunId);
                    table.CheckConstraint("CK_WeatherForecastRuns_ProviderKnown", "`Provider` IN (1)");
                    table.ForeignKey(
                        name: "FK_WeatherForecastRuns_WeatherPoints_WeatherPointId",
                        column: x => x.WeatherPointId,
                        principalTable: "WeatherPoints",
                        principalColumn: "WeatherPointId",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "WeatherObservationCoverages",
                columns: table => new
                {
                    WeatherPointId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    SiteVersion = table.Column<int>(type: "int", nullable: false),
                    FromDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ToDate = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeatherObservationCoverages", x => new { x.WeatherPointId, x.SiteVersion, x.FromDate });
                    table.CheckConstraint("CK_WeatherObservationCoverages_Order", "`ToDate` >= `FromDate`");
                    table.ForeignKey(
                        name: "FK_WeatherObservationCoverages_WeatherPoints_WeatherPointId",
                        column: x => x.WeatherPointId,
                        principalTable: "WeatherPoints",
                        principalColumn: "WeatherPointId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "WeatherObservations",
                columns: table => new
                {
                    WeatherObservationId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    WeatherPointId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    SiteVersion = table.Column<int>(type: "int", nullable: false),
                    Provider = table.Column<int>(type: "int", nullable: false),
                    TemperatureStationId = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PrecipitationStationId = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    WindStationId = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    LocalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    MeanTempC = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    MinTempC = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    MaxTempC = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    MeanWindSpeedMps = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    MaxWindGustMps = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    PrecipitationMm = table.Column<decimal>(type: "decimal(5,1)", precision: 5, scale: 1, nullable: true),
                    FetchedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeatherObservations", x => x.WeatherObservationId);
                    table.ForeignKey(
                        name: "FK_WeatherObservations_WeatherPoints_WeatherPointId",
                        column: x => x.WeatherPointId,
                        principalTable: "WeatherPoints",
                        principalColumn: "WeatherPointId",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "WeatherPointHistoryWindows",
                columns: table => new
                {
                    WeatherPointHistoryWindowId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    WeatherPointId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FromDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ToDate = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeatherPointHistoryWindows", x => x.WeatherPointHistoryWindowId);
                    table.CheckConstraint("CK_WeatherPointHistoryWindows_Order", "`ToDate` IS NULL OR `ToDate` >= `FromDate`");
                    table.ForeignKey(
                        name: "FK_WeatherPointHistoryWindows_WeatherPoints_WeatherPointId",
                        column: x => x.WeatherPointId,
                        principalTable: "WeatherPoints",
                        principalColumn: "WeatherPointId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "WeatherPointSchedules",
                columns: table => new
                {
                    WeatherPointId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Version = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    LeaseUntilUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    NextForecastFetchAfterUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ForecastLastModified = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ForecastExpiresAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LastForecastSuccessUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ForecastFailureKind = table.Column<int>(type: "int", nullable: true),
                    ForecastConsecutiveFailures = table.Column<int>(type: "int", nullable: false),
                    NextObservationFetchAfterUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LastObservationSuccessUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ObservationsCheckedThroughDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ObservationFailureKind = table.Column<int>(type: "int", nullable: true),
                    ObservationConsecutiveFailures = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeatherPointSchedules", x => x.WeatherPointId);
                    table.ForeignKey(
                        name: "FK_WeatherPointSchedules_WeatherPoints_WeatherPointId",
                        column: x => x.WeatherPointId,
                        principalTable: "WeatherPoints",
                        principalColumn: "WeatherPointId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "WeatherPointSites",
                columns: table => new
                {
                    WeatherPointId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    SiteVersion = table.Column<int>(type: "int", nullable: false),
                    Latitude = table.Column<decimal>(type: "decimal(6,3)", precision: 6, scale: 3, nullable: false),
                    Longitude = table.Column<decimal>(type: "decimal(7,3)", precision: 7, scale: 3, nullable: false),
                    AltitudeMeters = table.Column<int>(type: "int", nullable: true),
                    TimeZoneId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AppliedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    AppliedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeatherPointSites", x => new { x.WeatherPointId, x.SiteVersion });
                    table.CheckConstraint("CK_WeatherPointSites_VersionPositive", "`SiteVersion` >= 1");
                    table.ForeignKey(
                        name: "FK_WeatherPointSites_AspNetUsers_AppliedByUserId",
                        column: x => x.AppliedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_WeatherPointSites_WeatherPoints_WeatherPointId",
                        column: x => x.WeatherPointId,
                        principalTable: "WeatherPoints",
                        principalColumn: "WeatherPointId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "WeatherStations",
                columns: table => new
                {
                    WeatherPointId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Group = table.Column<int>(type: "int", nullable: false),
                    StationId = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DistanceKm = table.Column<decimal>(type: "decimal(5,1)", precision: 5, scale: 1, nullable: true),
                    AltitudeMeters = table.Column<int>(type: "int", nullable: true),
                    ResolvedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeatherStations", x => new { x.WeatherPointId, x.Group });
                    table.CheckConstraint("CK_WeatherStations_GroupKnown", "`Group` IN (0, 1, 2)");
                    table.ForeignKey(
                        name: "FK_WeatherStations_WeatherPoints_WeatherPointId",
                        column: x => x.WeatherPointId,
                        principalTable: "WeatherPoints",
                        principalColumn: "WeatherPointId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "BudgetItems",
                columns: table => new
                {
                    BudgetItemId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    BudgetId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CategoryType = table.Column<int>(type: "int", nullable: false),
                    PlannedAmount = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    TransactionTagId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci")
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
                name: "InstrumentPrices",
                columns: table => new
                {
                    InstrumentPriceId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    InstrumentId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    TradingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Close = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    SourceKey = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SourceKeyGuard = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false, computedColumnSql: "COALESCE(`SourceKey`, '')", stored: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstrumentPrices", x => x.InstrumentPriceId);
                    table.CheckConstraint("CK_InstrumentPrices_ClosePositive", "`Close` > 0");
                    table.CheckConstraint("CK_InstrumentPrices_SourceKeyFormat", "`SourceKey` IS NULL OR `SourceKey` REGEXP '^[a-z0-9-]{1,32}\\\\z'");
                    table.ForeignKey(
                        name: "FK_InstrumentPrices_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_InstrumentPrices_Instruments_InstrumentId",
                        column: x => x.InstrumentId,
                        principalTable: "Instruments",
                        principalColumn: "InstrumentId",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "InstrumentPriceSourceBindings",
                columns: table => new
                {
                    InstrumentId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    SourceKey = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SourceSymbol = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    Enabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    HistoryStartDate = table.Column<DateOnly>(type: "date", nullable: true),
                    NewestFetched = table.Column<DateOnly>(type: "date", nullable: true),
                    BackfilledFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    LastSuccessUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LastFailure = table.Column<byte>(type: "tinyint unsigned", nullable: true),
                    LastFailureUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "int", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstrumentPriceSourceBindings", x => new { x.InstrumentId, x.SourceKey });
                    table.CheckConstraint("CK_InstrumentPriceSourceBindings_ConsecutiveFailures", "`ConsecutiveFailures` >= 0");
                    table.CheckConstraint("CK_InstrumentPriceSourceBindings_Priority", "`Priority` BETWEEN 1 AND 9");
                    table.CheckConstraint("CK_InstrumentPriceSourceBindings_SourceKeyFormat", "`SourceKey` REGEXP '^[a-z0-9-]{1,32}\\\\z'");
                    table.ForeignKey(
                        name: "FK_InstrumentPriceSourceBindings_AspNetUsers_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_InstrumentPriceSourceBindings_Instruments_InstrumentId",
                        column: x => x.InstrumentId,
                        principalTable: "Instruments",
                        principalColumn: "InstrumentId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Events",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Source = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    Title = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Notes = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    OccurredAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    OwnerKind = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    ContractId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    Type = table.Column<int>(type: "int", nullable: true, defaultValue: 8),
                    PropertyId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.EventId);
                    table.CheckConstraint("CK_Events_ExactlyOneOwner", "(`OwnerKind` = 0 AND `ContractId` IS NOT NULL AND `PropertyId` IS NULL) OR (`OwnerKind` = 1 AND `PropertyId` IS NOT NULL AND `ContractId` IS NULL)");
                    table.CheckConstraint("CK_Events_TypeMatchesOwner", "`Type` IS NOT NULL AND ((`OwnerKind` = 0 AND `Type` BETWEEN 0 AND 99) OR (`OwnerKind` = 1 AND `Type` BETWEEN 100 AND 199))");
                    table.ForeignKey(
                        name: "FK_Events_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Events_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contracts",
                        principalColumn: "ContractId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Events_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "PropertyId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PropertyEstimates",
                columns: table => new
                {
                    PropertyEstimateId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    PropertyId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
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
                    table.PrimaryKey("PK_PropertyEstimates", x => x.PropertyEstimateId);
                    table.ForeignKey(
                        name: "FK_PropertyEstimates_Currencies_CurrencyCode",
                        column: x => x.CurrencyCode,
                        principalTable: "Currencies",
                        principalColumn: "CurrencyCode",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PropertyEstimates_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "PropertyId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PropertySmartTags",
                columns: table => new
                {
                    PropertyId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    TransactionTagId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    AddedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertySmartTags", x => new { x.PropertyId, x.TransactionTagId });
                    table.ForeignKey(
                        name: "FK_PropertySmartTags_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "PropertyId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PropertySmartTags_TransactionTags_TransactionTagId",
                        column: x => x.TransactionTagId,
                        principalTable: "TransactionTags",
                        principalColumn: "TransactionTagId",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "VehicleDetails",
                columns: table => new
                {
                    PropertyId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    RegistrationNumber = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Vin = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Make = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Model = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ModelYear = table.Column<int>(type: "int", nullable: true),
                    FirstRegisteredDate = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleDetails", x => x.PropertyId);
                    table.ForeignKey(
                        name: "FK_VehicleDetails_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "PropertyId",
                        onDelete: ReferentialAction.Cascade);
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
                    FinanceStoredObjectId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    JournalStoredObjectId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    ContactAvatarStoredObjectId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    LibraryStoredObjectId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    Description = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UploadedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileMetadata", x => x.Id);
                    table.CheckConstraint("CK_FileMetadata_ExactlyOneSource", "(`FinanceStoredObjectId` IS NOT NULL) + (`JournalStoredObjectId` IS NOT NULL) + (`ContactAvatarStoredObjectId` IS NOT NULL) + (`LibraryStoredObjectId` IS NOT NULL) = 1");
                    table.ForeignKey(
                        name: "FK_FileMetadata_AspNetUsers_UploadedByUserId",
                        column: x => x.UploadedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_FileMetadata_StoredObjects_ContactAvatarStoredObjectId",
                        column: x => x.ContactAvatarStoredObjectId,
                        principalTable: "StoredObjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FileMetadata_StoredObjects_FinanceStoredObjectId",
                        column: x => x.FinanceStoredObjectId,
                        principalTable: "StoredObjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FileMetadata_StoredObjects_JournalStoredObjectId",
                        column: x => x.JournalStoredObjectId,
                        principalTable: "StoredObjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FileMetadata_StoredObjects_LibraryStoredObjectId",
                        column: x => x.LibraryStoredObjectId,
                        principalTable: "StoredObjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Photos",
                columns: table => new
                {
                    PhotoId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    StoredObjectId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci", defaultValue: new Guid("00000000-0000-0000-0000-000000000000")),
                    FileName = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ContentType = table.Column<string>(type: "varchar(256)", unicode: false, maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    Sha256Hash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    LegacyFileId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
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
                        name: "FK_Photos_StoredObjects_StoredObjectId",
                        column: x => x.StoredObjectId,
                        principalTable: "StoredObjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "NewsArticleIdentities",
                columns: table => new
                {
                    NewsArticleIdentityId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    NewsArticleId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    NewsFeedId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Kind = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    IdentityKey = table.Column<string>(type: "varchar(2100)", maxLength: 2100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IdentityKeyHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    FirstSeenAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsArticleIdentities", x => x.NewsArticleIdentityId);
                    table.ForeignKey(
                        name: "FK_NewsArticleIdentities_NewsArticles_NewsArticleId_NewsFeedId",
                        columns: x => new { x.NewsArticleId, x.NewsFeedId },
                        principalTable: "NewsArticles",
                        principalColumns: new[] { "NewsArticleId", "NewsFeedId" },
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "NewsArticleRevisions",
                columns: table => new
                {
                    NewsArticleRevisionId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    NewsArticleId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    RevisionNumber = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Author = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Link = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ContentHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    IdentityHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    BodyLength = table.Column<int>(type: "int", nullable: false),
                    BodyOmittedReason = table.Column<byte>(type: "tinyint unsigned", nullable: true),
                    ValidFromUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ReplacedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsArticleRevisions", x => x.NewsArticleRevisionId);
                    table.CheckConstraint("CK_NewsArticleRevisions_BodyOmittedReason", "`BodyOmittedReason` IS NULL OR `BodyOmittedReason` IN (1, 2)");
                    table.ForeignKey(
                        name: "FK_NewsArticleRevisions_NewsArticles_NewsArticleId",
                        column: x => x.NewsArticleId,
                        principalTable: "NewsArticles",
                        principalColumn: "NewsArticleId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "NewsArticleTags",
                columns: table => new
                {
                    NewsArticleTagId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    NewsArticleId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    NewsTagId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    SortAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsArticleTags", x => x.NewsArticleTagId);
                    table.ForeignKey(
                        name: "FK_NewsArticleTags_NewsArticles_NewsArticleId",
                        column: x => x.NewsArticleId,
                        principalTable: "NewsArticles",
                        principalColumn: "NewsArticleId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NewsArticleTags_NewsTags_NewsTagId",
                        column: x => x.NewsTagId,
                        principalTable: "NewsTags",
                        principalColumn: "NewsTagId",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PodcastArtworkFiles",
                columns: table => new
                {
                    PodcastArtworkFileId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    PodcastArtworkId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Kind = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    StoredObjectId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ContentType = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PodcastArtworkFiles", x => x.PodcastArtworkFileId);
                    table.CheckConstraint("CK_PodcastArtworkFiles_Kind", "`Kind` IN (1, 2, 3)");
                    table.ForeignKey(
                        name: "FK_PodcastArtworkFiles_PodcastArtwork_PodcastArtworkId",
                        column: x => x.PodcastArtworkId,
                        principalTable: "PodcastArtwork",
                        principalColumn: "PodcastArtworkId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PodcastArtworkFiles_StoredObjects_StoredObjectId",
                        column: x => x.StoredObjectId,
                        principalTable: "StoredObjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PodcastEpisodes",
                columns: table => new
                {
                    NewsArticleId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    NewsFeedId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    EnclosureUrl = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    EnclosureDeclaredType = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    EnclosureDeclaredLength = table.Column<long>(type: "bigint", nullable: true),
                    Description = table.Column<string>(type: "varchar(4000)", maxLength: 4000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DurationSeconds = table.Column<int>(type: "int", nullable: true),
                    SeasonNumber = table.Column<int>(type: "int", nullable: true),
                    EpisodeNumber = table.Column<int>(type: "int", nullable: true),
                    EpisodeType = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    Explicit = table.Column<bool>(type: "tinyint(1)", nullable: true),
                    ArtworkId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    ChaptersUrl = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ChaptersUrlKeyHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: true),
                    ChaptersFetchState = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    ChaptersAttempts = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    ChaptersNextAttemptAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ChaptersLeaseOwner = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    ChaptersLeaseExpiresAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    EpisodeHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    SortAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    PlaybackPositionSeconds = table.Column<int>(type: "int", nullable: true),
                    LastPlayedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PodcastEpisodes", x => x.NewsArticleId);
                    table.CheckConstraint("CK_PodcastEpisodes_ChaptersFetchState", "`ChaptersFetchState` IN (0, 1, 2, 3)");
                    table.CheckConstraint("CK_PodcastEpisodes_EpisodeType", "`EpisodeType` IN (0, 1, 2)");
                    table.CheckConstraint("CK_PodcastEpisodes_Ranges", "(`DurationSeconds` IS NULL OR `DurationSeconds` BETWEEN 0 AND 604800) AND (`SeasonNumber` IS NULL OR `SeasonNumber` BETWEEN 0 AND 100000) AND (`EpisodeNumber` IS NULL OR `EpisodeNumber` BETWEEN 0 AND 100000) AND (`EnclosureDeclaredLength` IS NULL OR `EnclosureDeclaredLength` >= 0) AND (`PlaybackPositionSeconds` IS NULL OR `PlaybackPositionSeconds` >= 0)");
                    table.ForeignKey(
                        name: "FK_PodcastEpisodes_NewsArticles_NewsArticleId_NewsFeedId",
                        columns: x => new { x.NewsArticleId, x.NewsFeedId },
                        principalTable: "NewsArticles",
                        principalColumns: new[] { "NewsArticleId", "NewsFeedId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PodcastEpisodes_PodcastArtwork_ArtworkId",
                        column: x => x.ArtworkId,
                        principalTable: "PodcastArtwork",
                        principalColumn: "PodcastArtworkId",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PodcastFeedDetails",
                columns: table => new
                {
                    NewsFeedId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Author = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ArtworkId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    Explicit = table.Column<bool>(type: "tinyint(1)", nullable: true),
                    PodcastType = table.Column<byte>(type: "tinyint unsigned", nullable: true),
                    BaselineAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    AutoDownloadDate = table.Column<DateOnly>(type: "date", nullable: true),
                    AutoDownloadsToday = table.Column<int>(type: "int", nullable: false),
                    ArtworkIncidentCount = table.Column<int>(type: "int", nullable: false),
                    ArtworkIncidentAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PodcastFeedDetails", x => x.NewsFeedId);
                    table.CheckConstraint("CK_PodcastFeedDetails_CountersNonNegative", "`AutoDownloadsToday` >= 0 AND `ArtworkIncidentCount` >= 0");
                    table.CheckConstraint("CK_PodcastFeedDetails_PodcastType", "`PodcastType` IS NULL OR `PodcastType` IN (1, 2)");
                    table.ForeignKey(
                        name: "FK_PodcastFeedDetails_NewsFeeds_NewsFeedId",
                        column: x => x.NewsFeedId,
                        principalTable: "NewsFeeds",
                        principalColumn: "NewsFeedId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PodcastFeedDetails_PodcastArtwork_ArtworkId",
                        column: x => x.ArtworkId,
                        principalTable: "PodcastArtwork",
                        principalColumn: "PodcastArtworkId",
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
                    LocationId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
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
                    table.ForeignKey(
                        name: "FK_JournalEntries_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "LocationId",
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
                    LocationId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    ExternalUid = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ImportSourceUid = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    StartDateTime = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    EndDateTime = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    TimeZoneId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Version = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci", defaultValue: new Guid("00000000-0000-0000-0000-000000000000")),
                    KeysBackfilledAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
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
                    table.CheckConstraint("CK_RecurrencePatterns_AllDayHasNoZone", "`IsAllDay` = 0 OR `TimeZoneId` IS NULL");
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
                    table.ForeignKey(
                        name: "FK_RecurrencePatterns_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "LocationId",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "WeatherForecastSteps",
                columns: table => new
                {
                    WeatherForecastStepId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    WeatherForecastRunId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    WeatherPointId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    SiteVersion = table.Column<int>(type: "int", nullable: false),
                    RunProviderUpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    RunFetchedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    TimeUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    PeriodHours = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    AirTemperatureC = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    RelativeHumidityPct = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    CloudAreaFractionPct = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    AirPressureHpa = table.Column<decimal>(type: "decimal(5,1)", precision: 5, scale: 1, nullable: true),
                    WindSpeedMps = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    WindGustMps = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    WindFromDirectionDeg = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    UvIndexClearSky = table.Column<decimal>(type: "decimal(3,1)", precision: 3, scale: 1, nullable: true),
                    SymbolCode = table.Column<string>(type: "varchar(48)", maxLength: 48, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PrecipitationMm = table.Column<decimal>(type: "decimal(5,1)", precision: 5, scale: 1, nullable: true),
                    PrecipitationProbabilityPct = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    TempMinC = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    TempMaxC = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeatherForecastSteps", x => x.WeatherForecastStepId);
                    table.ForeignKey(
                        name: "FK_WeatherForecastSteps_WeatherForecastRuns_WeatherForecastRunId",
                        column: x => x.WeatherForecastRunId,
                        principalTable: "WeatherForecastRuns",
                        principalColumn: "WeatherForecastRunId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Contacts",
                columns: table => new
                {
                    ContactId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ExternalUid = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DisplayName = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    NormalizedName = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Notes = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AvatarFileId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    Archived = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contacts", x => x.ContactId);
                    table.ForeignKey(
                        name: "FK_Contacts_FileMetadata_AvatarFileId",
                        column: x => x.AvatarFileId,
                        principalTable: "FileMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
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
                name: "RecipeAttachments",
                columns: table => new
                {
                    RecipeAttachmentId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    RecipeId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FileId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeAttachments", x => x.RecipeAttachmentId);
                    table.ForeignKey(
                        name: "FK_RecipeAttachments_FileMetadata_FileId",
                        column: x => x.FileId,
                        principalTable: "FileMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RecipeAttachments_Recipes_RecipeId",
                        column: x => x.RecipeId,
                        principalTable: "Recipes",
                        principalColumn: "RecipeId",
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
                name: "PhotoAlbums",
                columns: table => new
                {
                    PhotoAlbumId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CoverPhotoId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    Icon = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Color = table.Column<string>(type: "varchar(7)", maxLength: 7, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
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
                name: "PhotoThumbnailJobs",
                columns: table => new
                {
                    PhotoId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    State = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    Generation = table.Column<int>(type: "int", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    ManualRequeues = table.Column<byte>(type: "tinyint unsigned", nullable: false, defaultValue: (byte)0),
                    LeaseExpiries = table.Column<int>(type: "int", nullable: false),
                    RunId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    ReclaimedRunId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    ReclaimIncidentId = table.Column<long>(type: "bigint", nullable: true),
                    RerunRequested = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Priority = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    LastClaimedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    AttemptedRecipeVersion = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    LeaseUntilUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    FailureCode = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoThumbnailJobs", x => x.PhotoId);
                    table.CheckConstraint("CK_PhotoThumbnailJobs_AttemptsNonNegative", "`Attempts` >= 0");
                    table.CheckConstraint("CK_PhotoThumbnailJobs_LeaseExpiriesNonNegative", "`LeaseExpiries` >= 0");
                    table.CheckConstraint("CK_PhotoThumbnailJobs_LeaseWhenRunning", "`State` <> 1 OR `LeaseUntilUtc` IS NOT NULL");
                    table.CheckConstraint("CK_PhotoThumbnailJobs_ManualRequeuesRange", "`ManualRequeues` IN (0, 1)");
                    table.CheckConstraint("CK_PhotoThumbnailJobs_PriorityRange", "`Priority` IN (0, 1)");
                    table.CheckConstraint("CK_PhotoThumbnailJobs_RunOnlyWhenRunning", "`State` = 1 OR (`RunId` IS NULL AND `RerunRequested` = 0)");
                    table.CheckConstraint("CK_PhotoThumbnailJobs_StateRange", "`State` IN (0, 1, 2, 3, 4)");
                    table.ForeignKey(
                        name: "FK_PhotoThumbnailJobs_Photos_PhotoId",
                        column: x => x.PhotoId,
                        principalTable: "Photos",
                        principalColumn: "PhotoId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PhotoThumbnails",
                columns: table => new
                {
                    PhotoThumbnailId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    PhotoId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Size = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    StoredObjectId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    PixelWidth = table.Column<int>(type: "int", nullable: false),
                    PixelHeight = table.Column<int>(type: "int", nullable: false),
                    RecipeVersion = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoThumbnails", x => x.PhotoThumbnailId);
                    table.CheckConstraint("CK_PhotoThumbnails_PixelsPositive", "`PixelWidth` > 0 AND `PixelHeight` > 0");
                    table.CheckConstraint("CK_PhotoThumbnails_SizeRange", "`Size` IN (1, 2)");
                    table.ForeignKey(
                        name: "FK_PhotoThumbnails_Photos_PhotoId",
                        column: x => x.PhotoId,
                        principalTable: "Photos",
                        principalColumn: "PhotoId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PhotoThumbnails_StoredObjects_StoredObjectId",
                        column: x => x.StoredObjectId,
                        principalTable: "StoredObjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RecipePhotos",
                columns: table => new
                {
                    RecipePhotoId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    RecipeId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    PhotoId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Position = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipePhotos", x => x.RecipePhotoId);
                    table.ForeignKey(
                        name: "FK_RecipePhotos_Photos_PhotoId",
                        column: x => x.PhotoId,
                        principalTable: "Photos",
                        principalColumn: "PhotoId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RecipePhotos_Recipes_RecipeId",
                        column: x => x.RecipeId,
                        principalTable: "Recipes",
                        principalColumn: "RecipeId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "NewsArticleFiles",
                columns: table => new
                {
                    NewsArticleFileId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    NewsArticleId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    NewsArticleRevisionId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    Kind = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    StoredObjectId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ContentType = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsArticleFiles", x => x.NewsArticleFileId);
                    table.CheckConstraint("CK_NewsArticleFiles_RevisionKind", "`NewsArticleRevisionId` IS NULL OR `Kind` IN (1, 2)");
                    table.ForeignKey(
                        name: "FK_NewsArticleFiles_NewsArticleRevisions_NewsArticleRevisionId",
                        column: x => x.NewsArticleRevisionId,
                        principalTable: "NewsArticleRevisions",
                        principalColumn: "NewsArticleRevisionId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NewsArticleFiles_NewsArticles_NewsArticleId",
                        column: x => x.NewsArticleId,
                        principalTable: "NewsArticles",
                        principalColumn: "NewsArticleId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NewsArticleFiles_StoredObjects_StoredObjectId",
                        column: x => x.StoredObjectId,
                        principalTable: "StoredObjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PodcastChapters",
                columns: table => new
                {
                    PodcastChapterId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    NewsArticleId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Source = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    StartMs = table.Column<long>(type: "bigint", nullable: false),
                    EndMs = table.Column<long>(type: "bigint", nullable: true),
                    Title = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Url = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PodcastChapters", x => x.PodcastChapterId);
                    table.CheckConstraint("CK_PodcastChapters_Source", "`Source` IN (1, 2, 3)");
                    table.CheckConstraint("CK_PodcastChapters_Times", "`StartMs` BETWEEN 0 AND 604800000 AND (`EndMs` IS NULL OR (`EndMs` >= `StartMs` AND `EndMs` <= 604800000))");
                    table.ForeignKey(
                        name: "FK_PodcastChapters_PodcastEpisodes_NewsArticleId",
                        column: x => x.NewsArticleId,
                        principalTable: "PodcastEpisodes",
                        principalColumn: "NewsArticleId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PodcastEpisodeAudio",
                columns: table => new
                {
                    NewsArticleId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    NewsFeedId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    EnclosureHostKeyHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    State = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    FailureKind = table.Column<byte>(type: "tinyint unsigned", nullable: true),
                    DeferredReason = table.Column<byte>(type: "tinyint unsigned", nullable: true),
                    RequestedBy = table.Column<byte>(type: "tinyint unsigned", nullable: true),
                    RequestedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LastUserRequestAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Attempts = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LeaseOwner = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    LeaseExpiresAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ReservedBytes = table.Column<long>(type: "bigint", nullable: false),
                    StoredObjectId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    ContentType = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    Sha256 = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: true),
                    StoredAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    AudioFence = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FeedDownloadGuard = table.Column<string>(type: "varchar(36)", nullable: true, computedColumnSql: "IF(`State` = 2, RTRIM(`NewsFeedId`), NULL)", stored: false, collation: "ascii_general_ci"),
                    HostDownloadGuard = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: true, computedColumnSql: "IF(`State` = 2, `EnclosureHostKeyHash`, NULL)", stored: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PodcastEpisodeAudio", x => x.NewsArticleId);
                    table.CheckConstraint("CK_PodcastEpisodeAudio_DeferredReason", "`DeferredReason` IS NULL OR `DeferredReason` IN (1, 2)");
                    table.CheckConstraint("CK_PodcastEpisodeAudio_FailureKind", "`FailureKind` IS NULL OR `FailureKind` BETWEEN 1 AND 6");
                    table.CheckConstraint("CK_PodcastEpisodeAudio_RequestedBy", "`RequestedBy` IS NULL OR `RequestedBy` IN (1, 2)");
                    table.CheckConstraint("CK_PodcastEpisodeAudio_Sizes", "`ReservedBytes` >= 0 AND (`SizeBytes` IS NULL OR `SizeBytes` >= 0)");
                    table.CheckConstraint("CK_PodcastEpisodeAudio_State", "`State` IN (0, 1, 2, 3, 4, 5)");
                    table.CheckConstraint("CK_PodcastEpisodeAudio_StoredHasObject", "(`State` = 3) = (`StoredObjectId` IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_PodcastEpisodeAudio_PodcastEpisodes_NewsArticleId",
                        column: x => x.NewsArticleId,
                        principalTable: "PodcastEpisodes",
                        principalColumn: "NewsArticleId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PodcastEpisodeAudio_StoredObjects_StoredObjectId",
                        column: x => x.StoredObjectId,
                        principalTable: "StoredObjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
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
                    LocationId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    ExternalUid = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ImportSourceUid = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    StartDateTime = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    EndDateTime = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    TimeZoneId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    OccurrenceKey = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    IsOverride = table.Column<bool>(type: "tinyint(1)", nullable: false, defaultValue: false),
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
                    table.CheckConstraint("CK_CalendarEvents_AllDayHasNoZone", "`IsAllDay` = 0 OR `TimeZoneId` IS NULL");
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
                        name: "FK_CalendarEvents_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "LocationId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CalendarEvents_RecurrencePatterns_RecurrencePatternId",
                        column: x => x.RecurrencePatternId,
                        principalTable: "RecurrencePatterns",
                        principalColumn: "RecurrencePatternId",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RecurrenceExclusions",
                columns: table => new
                {
                    RecurrenceExclusionId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    RecurrencePatternId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    OccurrenceKey = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurrenceExclusions", x => x.RecurrenceExclusionId);
                    table.ForeignKey(
                        name: "FK_RecurrenceExclusions_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RecurrenceExclusions_RecurrencePatterns_RecurrencePatternId",
                        column: x => x.RecurrencePatternId,
                        principalTable: "RecurrencePatterns",
                        principalColumn: "RecurrencePatternId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RecurrencePatternAttachments",
                columns: table => new
                {
                    RecurrencePatternAttachmentId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    RecurrencePatternId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FileId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurrencePatternAttachments", x => x.RecurrencePatternAttachmentId);
                    table.ForeignKey(
                        name: "FK_RecurrencePatternAttachments_FileMetadata_FileId",
                        column: x => x.FileId,
                        principalTable: "FileMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RecurrencePatternAttachments_RecurrencePatterns_RecurrencePa~",
                        column: x => x.RecurrencePatternId,
                        principalTable: "RecurrencePatterns",
                        principalColumn: "RecurrencePatternId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RecurrencePatternPhotos",
                columns: table => new
                {
                    RecurrencePatternPhotoId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    RecurrencePatternId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    PhotoId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Position = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurrencePatternPhotos", x => x.RecurrencePatternPhotoId);
                    table.ForeignKey(
                        name: "FK_RecurrencePatternPhotos_Photos_PhotoId",
                        column: x => x.PhotoId,
                        principalTable: "Photos",
                        principalColumn: "PhotoId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RecurrencePatternPhotos_RecurrencePatterns_RecurrencePattern~",
                        column: x => x.RecurrencePatternId,
                        principalTable: "RecurrencePatterns",
                        principalColumn: "RecurrencePatternId",
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
                    table.CheckConstraint("CK_Accounts_AccountTypeNotRetired", "`AccountType` NOT IN (6, 7)");
                    table.ForeignKey(
                        name: "FK_Accounts_Contacts_CustodianId",
                        column: x => x.CustodianId,
                        principalTable: "Contacts",
                        principalColumn: "ContactId",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Accounts_Currencies_CurrencyCode",
                        column: x => x.CurrencyCode,
                        principalTable: "Currencies",
                        principalColumn: "CurrencyCode",
                        onDelete: ReferentialAction.Restrict);
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
                    LocationId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci")
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
                    table.ForeignKey(
                        name: "FK_Addresses_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "LocationId",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Books",
                columns: table => new
                {
                    BookId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Title = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Publisher = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Year = table.Column<int>(type: "int", nullable: true),
                    Isbn = table.Column<string>(type: "varchar(13)", maxLength: 13, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Format = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Description = table.Column<string>(type: "varchar(4000)", maxLength: 4000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Notes = table.Column<string>(type: "varchar(8000)", maxLength: 8000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    MainAuthorContactId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UpdatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Books", x => x.BookId);
                    table.ForeignKey(
                        name: "FK_Books_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Books_AspNetUsers_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Books_Contacts_MainAuthorContactId",
                        column: x => x.MainAuthorContactId,
                        principalTable: "Contacts",
                        principalColumn: "ContactId",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ContactAliases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ContactId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Value = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Label = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContactAliases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContactAliases_Contacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contacts",
                        principalColumn: "ContactId",
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
                    AttachedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ValidTo = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    IssuedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    IssuedBy = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci")
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
                        name: "FK_ContractFiles_Contacts_IssuedBy",
                        column: x => x.IssuedBy,
                        principalTable: "Contacts",
                        principalColumn: "ContactId",
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
                name: "OrganizationDetails",
                columns: table => new
                {
                    ContactId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    LegalName = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    OrganizationNumber = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Website = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    EstablishedDate = table.Column<DateOnly>(type: "date", nullable: true),
                    DissolvedDate = table.Column<DateOnly>(type: "date", nullable: true)
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
                    MiddleName = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DateOfBirth = table.Column<DateOnly>(type: "date", nullable: true),
                    DateOfDeath = table.Column<DateOnly>(type: "date", nullable: true),
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
                name: "PropertyFiles",
                columns: table => new
                {
                    PropertyFileId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    PropertyId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FileMetadataId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FileType = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    AttachedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AttachedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ValidTo = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    IssuedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    IssuedBy = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertyFiles", x => x.PropertyFileId);
                    table.ForeignKey(
                        name: "FK_PropertyFiles_AspNetUsers_AttachedByUserId",
                        column: x => x.AttachedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PropertyFiles_Contacts_IssuedBy",
                        column: x => x.IssuedBy,
                        principalTable: "Contacts",
                        principalColumn: "ContactId",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PropertyFiles_FileMetadata_FileMetadataId",
                        column: x => x.FileMetadataId,
                        principalTable: "FileMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PropertyFiles_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "PropertyId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RealEstateDetails",
                columns: table => new
                {
                    PropertyId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    LocationId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    CadastralNumber = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    LivingAreaSqm = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    PlotAreaSqm = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    BuildYear = table.Column<int>(type: "int", nullable: true),
                    HomeownerAssociationId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RealEstateDetails", x => x.PropertyId);
                    table.ForeignKey(
                        name: "FK_RealEstateDetails_Contacts_HomeownerAssociationId",
                        column: x => x.HomeownerAssociationId,
                        principalTable: "Contacts",
                        principalColumn: "ContactId",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RealEstateDetails_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "LocationId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RealEstateDetails_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "PropertyId",
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
                name: "NewsArticleImages",
                columns: table => new
                {
                    NewsArticleImageId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    NewsArticleId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    NewsArticleFileId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    Position = table.Column<int>(type: "int", nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    Origin = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    SourceUrl = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AltText = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Width = table.Column<int>(type: "int", nullable: true),
                    Height = table.Column<int>(type: "int", nullable: true),
                    State = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    DeferredReason = table.Column<byte>(type: "tinyint unsigned", nullable: true),
                    Attempts = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LeaseOwner = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    LeaseExpiresAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    VerifiedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LastRequeuedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsArticleImages", x => x.NewsArticleImageId);
                    table.ForeignKey(
                        name: "FK_NewsArticleImages_NewsArticleFiles_NewsArticleFileId",
                        column: x => x.NewsArticleFileId,
                        principalTable: "NewsArticleFiles",
                        principalColumn: "NewsArticleFileId",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_NewsArticleImages_NewsArticles_NewsArticleId",
                        column: x => x.NewsArticleId,
                        principalTable: "NewsArticles",
                        principalColumn: "NewsArticleId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "CalendarEventAttachments",
                columns: table => new
                {
                    CalendarEventAttachmentId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CalendarEventId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FileId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarEventAttachments", x => x.CalendarEventAttachmentId);
                    table.ForeignKey(
                        name: "FK_CalendarEventAttachments_CalendarEvents_CalendarEventId",
                        column: x => x.CalendarEventId,
                        principalTable: "CalendarEvents",
                        principalColumn: "CalendarEventId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CalendarEventAttachments_FileMetadata_FileId",
                        column: x => x.FileId,
                        principalTable: "FileMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "CalendarEventPhotos",
                columns: table => new
                {
                    CalendarEventPhotoId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CalendarEventId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    PhotoId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Position = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarEventPhotos", x => x.CalendarEventPhotoId);
                    table.ForeignKey(
                        name: "FK_CalendarEventPhotos_CalendarEvents_CalendarEventId",
                        column: x => x.CalendarEventId,
                        principalTable: "CalendarEvents",
                        principalColumn: "CalendarEventId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CalendarEventPhotos_Photos_PhotoId",
                        column: x => x.PhotoId,
                        principalTable: "Photos",
                        principalColumn: "PhotoId",
                        onDelete: ReferentialAction.Cascade);
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
                    table.ForeignKey(
                        name: "FK_AccountEstimates_Currencies_CurrencyCode",
                        column: x => x.CurrencyCode,
                        principalTable: "Currencies",
                        principalColumn: "CurrencyCode",
                        onDelete: ReferentialAction.Restrict);
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
                name: "ContractParties",
                columns: table => new
                {
                    ContractPartyId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ContractId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    AccountId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    ContactId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    PropertyId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    Role = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    FromDate = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ToDate = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractParties", x => x.ContractPartyId);
                    table.CheckConstraint("CK_ContractParties_ExactlyOneTarget", "((`AccountId` IS NOT NULL) + (`ContactId` IS NOT NULL) + (`PropertyId` IS NOT NULL)) = 1");
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
                        name: "FK_ContractParties_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "PropertyId",
                        onDelete: ReferentialAction.Cascade);
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
                    table.ForeignKey(
                        name: "FK_Transactions_Currencies_CurrencyCode",
                        column: x => x.CurrencyCode,
                        principalTable: "Currencies",
                        principalColumn: "CurrencyCode",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "BookAttachments",
                columns: table => new
                {
                    BookAttachmentId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    BookId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FileId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookAttachments", x => x.BookAttachmentId);
                    table.ForeignKey(
                        name: "FK_BookAttachments_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "BookId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BookAttachments_FileMetadata_FileId",
                        column: x => x.FileId,
                        principalTable: "FileMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "BookContributors",
                columns: table => new
                {
                    BookContributorId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    BookId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ContactId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Role = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Position = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookContributors", x => x.BookContributorId);
                    table.ForeignKey(
                        name: "FK_BookContributors_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "BookId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BookContributors_Contacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contacts",
                        principalColumn: "ContactId",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "BookImages",
                columns: table => new
                {
                    BookImageId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    BookId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    PhotoId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    IsCover = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookImages", x => x.BookImageId);
                    table.ForeignKey(
                        name: "FK_BookImages_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "BookId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BookImages_Photos_PhotoId",
                        column: x => x.PhotoId,
                        principalTable: "Photos",
                        principalColumn: "PhotoId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "BookTagLinks",
                columns: table => new
                {
                    BookTagLinkId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    BookId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    BookTagId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookTagLinks", x => x.BookTagLinkId);
                    table.ForeignKey(
                        name: "FK_BookTagLinks_BookTags_BookTagId",
                        column: x => x.BookTagId,
                        principalTable: "BookTags",
                        principalColumn: "BookTagId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BookTagLinks_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "BookId",
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
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    MatchVocabularyScope = table.Column<int>(type: "int", nullable: true),
                    DisclosureVersionInForce = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    MatchDisclosureVersionInForce = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ExtractionPromptAdditionInForce = table.Column<string>(type: "text", maxLength: 4000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    MatchPromptAdditionInForce = table.Column<string>(type: "text", maxLength: 4000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    MatchPromptVersion = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ContactCount = table.Column<int>(type: "int", nullable: true),
                    TagCount = table.Column<int>(type: "int", nullable: true),
                    AliasCount = table.Column<int>(type: "int", nullable: true),
                    TagDescriptionCount = table.Column<int>(type: "int", nullable: true),
                    VocabularyReduction = table.Column<int>(type: "int", nullable: true),
                    MatchGeneration = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    QueuedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ClaimedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    TransferStartedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    RunId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    LeaseUntilUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ExtractionGeneration = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    PromptTemplateHash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    FileSha256 = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AnalyzerBaseUrlOrigin = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
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
                table: "FileStorageMaintenanceLeases",
                columns: new[] { "Id", "HolderId", "LastCompletedAtUtc", "LeaseUntilUtc", "Version" },
                values: new object[] { (byte)1, null, null, null, new Guid("3c8f0d4e-5a8b-4f6e-9a51-2b7d1e0c9f34") });

            migrationBuilder.InsertData(
                table: "NewsStorageTotals",
                columns: new[] { "Id", "ArtworkBytes", "AudioBytes", "AudioPendingReleaseBytes", "AudioReservedBytes", "ImageBytes", "TextAndSourceBytes" },
                values: new object[] { (byte)1, 0L, 0L, 0L, 0L, 0L, 0L });

            migrationBuilder.InsertData(
                table: "PhotoThumbnailBuckets",
                columns: new[] { "Id", "CurrentBucket", "PhotosWritten", "UpdatedAtUtc" },
                values: new object[] { (byte)1, 1, 0, new DateTime(2026, 10, 31, 0, 0, 0, 0, DateTimeKind.Utc) });

            migrationBuilder.InsertData(
                table: "SystemSettings",
                columns: new[] { "Key", "UpdatedAt", "UpdatedBy", "Value" },
                values: new object[,]
                {
                    { "AccountMaxSmartTagsPerAccount", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "20" },
                    { "CalendarEventMaxLinksPerKind", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "50" },
                    { "CalendarIcsMaxAggregateExportRows", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "20000" },
                    { "CalendarIcsMaxAggregateExportWindowDays", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "92" },
                    { "CalendarIcsMaxAggregateOccurrences", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "5000" },
                    { "CalendarIcsMaxExportEvents", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "unlimited" },
                    { "CalendarIcsMaxExportMegabytes", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "10240" },
                    { "CalendarIcsMaxImportEvents", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "unlimited" },
                    { "CalendarIcsMaxImportMegabytes", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "10240" },
                    { "CalendarMaxEventDurationDays", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "366" },
                    { "CalendarMaxWindowDays", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "92" },
                    { "ContactVCardMaxExportMegabytes", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "64" },
                    { "ContactVCardMaxExportRows", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "unlimited" },
                    { "ContactVCardMaxImportEntries", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "unlimited" },
                    { "ContactVCardMaxImportMegabytes", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "64" },
                    { "ContactVCardMaxRepeatablePropertiesPerEntry", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "200" },
                    { "ContractChargeWindowDays", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "45" },
                    { "ContractEndingWindowDays", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "45" },
                    { "ContractMaxFilesPerContract", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "50" },
                    { "ContractMaxPartiesPerContract", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "25" },
                    { "ContractMaxSmartTagsPerContract", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "20" },
                    { "ContractMaxSummaryCharges", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "6" },
                    { "ContractMaxSummaryContracts", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "1000" },
                    { "ContractMaxTermsPerContract", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "500" },
                    { "EmailClientBaseUrl", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "" },
                    { "EmailFromAddress", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "no-reply@odyssey.local" },
                    { "EmailFromName", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Odyssey" },
                    { "EmailMaxTrackedRecipients", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "20000" },
                    { "EmailPerRecipientLimit", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "3" },
                    { "EmailPerRecipientWindowMinutes", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "60" },
                    { "EmailRequireConfirmation", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "true" },
                    { "EmailSmtpHost", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "" },
                    { "EmailSmtpPort", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "587" },
                    { "EmailUseStartTls", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "true" },
                    { "ExchangeRateHistoryStartDate", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "" },
                    { "ExchangeRateSyncIntervalHours", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "24" },
                    { "FileAnalysisBaseUrl", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "https://api.anthropic.com" },
                    { "FileAnalysisEnabled", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "false" },
                    { "FileAnalysisExtractionPromptAddition", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "" },
                    { "FileAnalysisLawfulBasis", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Consent · GDPR Art. 6(1)(a)" },
                    { "FileAnalysisMatchAutoLinkThreshold", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "0.6" },
                    { "FileAnalysisMatchPromptAddition", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "" },
                    { "FileAnalysisMatchTimeoutSeconds", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "60" },
                    { "FileAnalysisMaxFutureTransactionDays", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "90" },
                    { "FileAnalysisMaxTokens", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "8096" },
                    { "FileAnalysisModel", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "claude-sonnet-5" },
                    { "FileAnalysisPrivacyNoticeUrl", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "https://www.anthropic.com/legal/privacy" },
                    { "FileAnalysisProcessor", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Anthropic" },
                    { "FileAnalysisProcessorRegion", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "United States" },
                    { "FileStorageDeletionGraceHours", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "24" },
                    { "FileStorageMaxUploadMegabytes", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "64" },
                    { "ImportMaxSamplesPerSkipReason", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "100" },
                    { "InstrumentPriceStaleAfterDays", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "5" },
                    { "InstrumentPriceSyncHistoryDays", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "365" },
                    { "InstrumentPriceSyncIntervalHours", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "24" },
                    { "JobRunRetentionDays", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "90" },
                    { "JournalEntryMaxLinksPerKind", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "50" },
                    { "JournalIcsMaxExportMegabytes", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "10240" },
                    { "JournalIcsMaxExportRows", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "unlimited" },
                    { "JournalIcsMaxImportEntries", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "unlimited" },
                    { "JournalIcsMaxImportMegabytes", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "10240" },
                    { "JournalTaskMaxLinksPerKind", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "50" },
                    { "NewsDefaultFetchIntervalMinutes", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "60" },
                    { "NewsFeedMaxArticlesPerDay", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "500" },
                    { "NewsFeedMaxImageMbPerDay", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "200" },
                    { "NewsImageMaxPerArticle", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "5" },
                    { "NewsImageMaxSizeKb", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "5120" },
                    { "NewsImageStoreMaxGiB", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "50" },
                    { "NewsMaxFeeds", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "200" },
                    { "NewsOpmlMaxImportEntries", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "100000" },
                    { "NewsOpmlMaxImportMegabytes", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "64" },
                    { "NewsPrivateHostAllowList", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "" },
                    { "NewsTextStoreMaxGiB", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "20" },
                    { "PhotoMaxAlbumMembers", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "1000" },
                    { "PhotoMaxLinksPerKind", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "50" },
                    { "PhotoMetadataExtractionTimeoutSeconds", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "5" },
                    { "PhotoMetadataReadMegabytes", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "8" },
                    { "PodcastAudioMaxEpisodeMb", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "1024" },
                    { "PodcastAudioStoreMaxGiB", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "50" },
                    { "PodcastAutoDownloadMaxUnfetchedPerFeed", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "5" },
                    { "PodcastAutoDownloadsPerFeedPerDay", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "5" },
                    { "PropertyMaxSmartTagsPerProperty", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "20" },
                    { "RecurrenceMaxGeneratedOccurrences", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "1000" },
                    { "RegistrationRequireAdminApproval", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "true" },
                    { "RequireTwoFactor", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "false" },
                    { "TaskIcsMaxExportMegabytes", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "10240" },
                    { "TaskIcsMaxExportTasks", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "unlimited" },
                    { "TaskIcsMaxImportMegabytes", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "10240" },
                    { "TaskIcsMaxImportTasks", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "unlimited" },
                    { "WeatherDefaultPointId", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "" },
                    { "WeatherMaxTrackedPoints", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "5" },
                    { "WeatherRefreshIntervalMinutes", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "1440" },
                    { "WeatherUserAgentContact", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountEstimates_AccountId_EffectiveFrom",
                table: "AccountEstimates",
                columns: new[] { "AccountId", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountEstimates_CurrencyCode",
                table: "AccountEstimates",
                column: "CurrencyCode");

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
                name: "IX_Accounts_CurrencyCode",
                table: "Accounts",
                column: "CurrencyCode");

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
                name: "IX_Addresses_ContactId_LocationId",
                table: "Addresses",
                columns: new[] { "ContactId", "LocationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Addresses_LocationId",
                table: "Addresses",
                column: "LocationId");

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
                name: "IX_BookAttachments_BookId_FileId",
                table: "BookAttachments",
                columns: new[] { "BookId", "FileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BookAttachments_FileId",
                table: "BookAttachments",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_BookContributors_BookId_ContactId",
                table: "BookContributors",
                columns: new[] { "BookId", "ContactId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BookContributors_ContactId",
                table: "BookContributors",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_BookImages_BookId_PhotoId",
                table: "BookImages",
                columns: new[] { "BookId", "PhotoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BookImages_PhotoId",
                table: "BookImages",
                column: "PhotoId");

            migrationBuilder.CreateIndex(
                name: "IX_Books_CreatedByUserId",
                table: "Books",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Books_Isbn",
                table: "Books",
                column: "Isbn");

            migrationBuilder.CreateIndex(
                name: "IX_Books_MainAuthorContactId",
                table: "Books",
                column: "MainAuthorContactId");

            migrationBuilder.CreateIndex(
                name: "IX_Books_Status",
                table: "Books",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Books_Title",
                table: "Books",
                column: "Title");

            migrationBuilder.CreateIndex(
                name: "IX_Books_UpdatedByUserId",
                table: "Books",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_BookTagLinks_BookId_BookTagId",
                table: "BookTagLinks",
                columns: new[] { "BookId", "BookTagId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BookTagLinks_BookTagId",
                table: "BookTagLinks",
                column: "BookTagId");

            migrationBuilder.CreateIndex(
                name: "IX_BookTags_Archived",
                table: "BookTags",
                column: "Archived");

            migrationBuilder.CreateIndex(
                name: "IX_BookTags_Name",
                table: "BookTags",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BudgetItems_BudgetId_TransactionTagId",
                table: "BudgetItems",
                columns: new[] { "BudgetId", "TransactionTagId" },
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
                name: "IX_Budgets_BaseCurrencyCode",
                table: "Budgets",
                column: "BaseCurrencyCode");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEventAttachments_CalendarEventId_FileId",
                table: "CalendarEventAttachments",
                columns: new[] { "CalendarEventId", "FileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEventAttachments_FileId",
                table: "CalendarEventAttachments",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEventPhotos_CalendarEventId_PhotoId",
                table: "CalendarEventPhotos",
                columns: new[] { "CalendarEventId", "PhotoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEventPhotos_PhotoId",
                table: "CalendarEventPhotos",
                column: "PhotoId");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_CalendarId_ExternalUid",
                table: "CalendarEvents",
                columns: new[] { "CalendarId", "ExternalUid" });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_CalendarId_ImportSourceUid",
                table: "CalendarEvents",
                columns: new[] { "CalendarId", "ImportSourceUid" });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_CalendarId_StartDateTime",
                table: "CalendarEvents",
                columns: new[] { "CalendarId", "StartDateTime" });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_CreatedByUserId",
                table: "CalendarEvents",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_LocationId",
                table: "CalendarEvents",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_RecurrencePatternId",
                table: "CalendarEvents",
                column: "RecurrencePatternId");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_RecurrencePatternId_OccurrenceKey",
                table: "CalendarEvents",
                columns: new[] { "RecurrencePatternId", "OccurrenceKey" },
                unique: true);

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
                name: "IX_ContactAliases_ContactId_Value",
                table: "ContactAliases",
                columns: new[] { "ContactId", "Value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_AvatarFileId",
                table: "Contacts",
                column: "AvatarFileId",
                unique: true);

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
                name: "IX_ContractFiles_IssuedBy",
                table: "ContractFiles",
                column: "IssuedBy");

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
                name: "IX_ContractParties_ContractId_AccountId_Role",
                table: "ContractParties",
                columns: new[] { "ContractId", "AccountId", "Role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContractParties_ContractId_ContactId_Role",
                table: "ContractParties",
                columns: new[] { "ContractId", "ContactId", "Role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContractParties_ContractId_PropertyId_Role",
                table: "ContractParties",
                columns: new[] { "ContractId", "PropertyId", "Role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContractParties_PropertyId",
                table: "ContractParties",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_Archived",
                table: "Contracts",
                column: "Archived");

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_CreatedByUserId",
                table: "Contracts",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_Type_Archived",
                table: "Contracts",
                columns: new[] { "Type", "Archived" });

            migrationBuilder.CreateIndex(
                name: "IX_ContractSmartTags_AddedAt",
                table: "ContractSmartTags",
                column: "AddedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ContractSmartTags_TransactionTagId",
                table: "ContractSmartTags",
                column: "TransactionTagId");

            migrationBuilder.CreateIndex(
                name: "IX_Currencies_Archived",
                table: "Currencies",
                column: "Archived");

            migrationBuilder.CreateIndex(
                name: "IX_EmailAddresses_ContactId",
                table: "EmailAddresses",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_Events_ContractId_OccurredAt",
                table: "Events",
                columns: new[] { "ContractId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Events_CreatedByUserId",
                table: "Events",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Events_PropertyId_OccurredAt",
                table: "Events",
                columns: new[] { "PropertyId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRates_AsOf",
                table: "ExchangeRates",
                column: "AsOf");

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRates_FromCurrencyCode_ToCurrencyCode_AsOf_Provider",
                table: "ExchangeRates",
                columns: new[] { "FromCurrencyCode", "ToCurrencyCode", "AsOf", "Provider" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRates_FromCurrencyCode_ToCurrencyCode_Provider_AsOf",
                table: "ExchangeRates",
                columns: new[] { "FromCurrencyCode", "ToCurrencyCode", "Provider", "AsOf" });

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRates_ToCurrencyCode",
                table: "ExchangeRates",
                column: "ToCurrencyCode");

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRateSyncCurrencies_UpdatedByUserId",
                table: "ExchangeRateSyncCurrencies",
                column: "UpdatedByUserId");

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
                name: "IX_FileAnalysisJobs_AccountFileId_Status",
                table: "FileAnalysisJobs",
                columns: new[] { "AccountFileId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_FileAnalysisJobs_RequestedByUserId",
                table: "FileAnalysisJobs",
                column: "RequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_FileAnalysisJobs_Status_QueuedAt",
                table: "FileAnalysisJobs",
                columns: new[] { "Status", "QueuedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FileMetadata_ContactAvatarStoredObjectId",
                table: "FileMetadata",
                column: "ContactAvatarStoredObjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FileMetadata_FinanceStoredObjectId",
                table: "FileMetadata",
                column: "FinanceStoredObjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FileMetadata_JournalStoredObjectId",
                table: "FileMetadata",
                column: "JournalStoredObjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FileMetadata_LibraryStoredObjectId",
                table: "FileMetadata",
                column: "LibraryStoredObjectId",
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
                name: "IX_FileStorageWorkerVerdicts_LastReportedAtUtc",
                table: "FileStorageWorkerVerdicts",
                column: "LastReportedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_InstrumentPrices_CreatedByUserId",
                table: "InstrumentPrices",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_InstrumentPrices_InstrumentId_TradingDate_SourceKeyGuard",
                table: "InstrumentPrices",
                columns: new[] { "InstrumentId", "TradingDate", "SourceKeyGuard" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InstrumentPrices_TradingDate",
                table: "InstrumentPrices",
                column: "TradingDate");

            migrationBuilder.CreateIndex(
                name: "IX_InstrumentPriceSourceBindings_InstrumentId_Priority",
                table: "InstrumentPriceSourceBindings",
                columns: new[] { "InstrumentId", "Priority" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InstrumentPriceSourceBindings_UpdatedByUserId",
                table: "InstrumentPriceSourceBindings",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Instruments_Archived",
                table: "Instruments",
                column: "Archived");

            migrationBuilder.CreateIndex(
                name: "IX_Instruments_CreatedByUserId",
                table: "Instruments",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Instruments_CurrencyCode",
                table: "Instruments",
                column: "CurrencyCode");

            migrationBuilder.CreateIndex(
                name: "IX_Instruments_ListingKey",
                table: "Instruments",
                column: "ListingKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Instruments_UpdatedByUserId",
                table: "Instruments",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_JobRuns_DurationMs",
                table: "JobRuns",
                column: "DurationMs");

            migrationBuilder.CreateIndex(
                name: "IX_JobRuns_ExclusiveGuard",
                table: "JobRuns",
                column: "ExclusiveGuard",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobRuns_JobType_StartedAtUtc",
                table: "JobRuns",
                columns: new[] { "JobType", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_JobRuns_JobType_WorkerId_OpenHeldGuard",
                table: "JobRuns",
                columns: new[] { "JobType", "WorkerId", "OpenHeldGuard" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobRuns_RequestedByUserId",
                table: "JobRuns",
                column: "RequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_JobRuns_StartedAtUtc",
                table: "JobRuns",
                column: "StartedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_JobRuns_Status_StartedAtUtc",
                table: "JobRuns",
                columns: new[] { "Status", "StartedAtUtc" });

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
                name: "IX_JournalEntries_LocationId",
                table: "JournalEntries",
                column: "LocationId");

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
                column: "Name",
                unique: true);

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
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LicenseAcceptances_UserId_LicenseHash_RespondedAt",
                table: "LicenseAcceptances",
                columns: new[] { "UserId", "LicenseHash", "RespondedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Locations_AddressKey",
                table: "Locations",
                column: "AddressKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Locations_Archived_Name",
                table: "Locations",
                columns: new[] { "Archived", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_Locations_CreatedByUserId",
                table: "Locations",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Locations_MergedIntoLocationId",
                table: "Locations",
                column: "MergedIntoLocationId");

            migrationBuilder.CreateIndex(
                name: "IX_Locations_Name",
                table: "Locations",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Locations_UpdatedByUserId",
                table: "Locations",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Locations_WeatherPointId",
                table: "Locations",
                column: "WeatherPointId");

            migrationBuilder.CreateIndex(
                name: "IX_NewsArticleFiles_NewsArticleId_NewsArticleRevisionId_Kind",
                table: "NewsArticleFiles",
                columns: new[] { "NewsArticleId", "NewsArticleRevisionId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_NewsArticleFiles_NewsArticleRevisionId",
                table: "NewsArticleFiles",
                column: "NewsArticleRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_NewsArticleFiles_StoredObjectId",
                table: "NewsArticleFiles",
                column: "StoredObjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NewsArticleIdentities_NewsArticleId_NewsFeedId",
                table: "NewsArticleIdentities",
                columns: new[] { "NewsArticleId", "NewsFeedId" });

            migrationBuilder.CreateIndex(
                name: "IX_NewsArticleIdentities_NewsFeedId_IdentityKeyHash",
                table: "NewsArticleIdentities",
                columns: new[] { "NewsFeedId", "IdentityKeyHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NewsArticleImages_NewsArticleFileId",
                table: "NewsArticleImages",
                column: "NewsArticleFileId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NewsArticleImages_NewsArticleId_DisplayOrder",
                table: "NewsArticleImages",
                columns: new[] { "NewsArticleId", "DisplayOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_NewsArticleImages_NewsArticleId_Position",
                table: "NewsArticleImages",
                columns: new[] { "NewsArticleId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NewsArticleImages_State_NextAttemptAtUtc",
                table: "NewsArticleImages",
                columns: new[] { "State", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_NewsArticleRevisions_NewsArticleId_RevisionNumber",
                table: "NewsArticleRevisions",
                columns: new[] { "NewsArticleId", "RevisionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "FT_NewsArticles_Title_Author_Excerpt",
                table: "NewsArticles",
                columns: new[] { "Title", "Author", "Excerpt" })
                .Annotation("MySql:FullTextIndex", true);

            migrationBuilder.CreateIndex(
                name: "IX_NewsArticles_Feed_Read_FirstSeen",
                table: "NewsArticles",
                columns: new[] { "NewsFeedId", "IsRead", "FirstSeenAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_NewsArticles_Feed_Read_Sort",
                table: "NewsArticles",
                columns: new[] { "NewsFeedId", "IsRead", "SortAtUtc", "NewsArticleId" },
                descending: new[] { false, false, true, false });

            migrationBuilder.CreateIndex(
                name: "IX_NewsArticles_Kind_Sort",
                table: "NewsArticles",
                columns: new[] { "FeedKind", "SortAtUtc", "NewsArticleId" },
                descending: new[] { false, true, false });

            migrationBuilder.CreateIndex(
                name: "IX_NewsArticles_Read_Visible_Sort",
                table: "NewsArticles",
                columns: new[] { "IsRead", "FeedVisible", "SortAtUtc", "NewsArticleId" },
                descending: new[] { false, false, true, false });

            migrationBuilder.CreateIndex(
                name: "IX_NewsArticles_Sort",
                table: "NewsArticles",
                columns: new[] { "SortAtUtc", "NewsArticleId" },
                descending: new[] { true, false });

            migrationBuilder.CreateIndex(
                name: "IX_NewsArticleTags_NewsArticleId_NewsTagId",
                table: "NewsArticleTags",
                columns: new[] { "NewsArticleId", "NewsTagId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NewsArticleTags_NewsTagId_NewsArticleId",
                table: "NewsArticleTags",
                columns: new[] { "NewsTagId", "NewsArticleId" });

            migrationBuilder.CreateIndex(
                name: "IX_NewsArticleTags_Tag_Sort",
                table: "NewsArticleTags",
                columns: new[] { "NewsTagId", "SortAtUtc", "NewsArticleId" },
                descending: new[] { false, true, false });

            migrationBuilder.CreateIndex(
                name: "IX_NewsFeedBuckets_NewsFeedId",
                table: "NewsFeedBuckets",
                column: "NewsFeedId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NewsFeedCrashEvents_DetectedAtUtc",
                table: "NewsFeedCrashEvents",
                column: "DetectedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_NewsFeedFetchStates_LeaseExpiresAtUtc",
                table: "NewsFeedFetchStates",
                column: "LeaseExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_NewsFeedFetchStates_QuarantinedAtUtc_NextFetchAtUtc",
                table: "NewsFeedFetchStates",
                columns: new[] { "QuarantinedAtUtc", "NextFetchAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_NewsFeedFetchStates_RefreshRequestedAtUtc",
                table: "NewsFeedFetchStates",
                column: "RefreshRequestedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_NewsFeeds_UrlKeyHash",
                table: "NewsFeeds",
                column: "UrlKeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NewsFeedTags_NewsFeedId_NewsTagId",
                table: "NewsFeedTags",
                columns: new[] { "NewsFeedId", "NewsTagId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NewsFeedTags_NewsTagId_NewsFeedId",
                table: "NewsFeedTags",
                columns: new[] { "NewsTagId", "NewsFeedId" });

            migrationBuilder.CreateIndex(
                name: "IX_NewsTags_ArchivedAtUtc",
                table: "NewsTags",
                column: "ArchivedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_NewsTags_NameKey",
                table: "NewsTags",
                column: "NameKey",
                unique: true);

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
                name: "IX_Photos_Archived_TakenAt",
                table: "Photos",
                columns: new[] { "Archived", "TakenAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Photos_CreatedByUserId",
                table: "Photos",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Photos_Favourited",
                table: "Photos",
                column: "Favourited");

            migrationBuilder.CreateIndex(
                name: "IX_Photos_LegacyFileId",
                table: "Photos",
                column: "LegacyFileId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Photos_Sha256Hash_SizeBytes",
                table: "Photos",
                columns: new[] { "Sha256Hash", "SizeBytes" });

            migrationBuilder.CreateIndex(
                name: "IX_Photos_StoredObjectId",
                table: "Photos",
                column: "StoredObjectId",
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
                name: "IX_PhotoThumbnailIncidents_OccurredAtUtc",
                table: "PhotoThumbnailIncidents",
                column: "OccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PhotoThumbnailJobs_State_AttemptedRecipeVersion",
                table: "PhotoThumbnailJobs",
                columns: new[] { "State", "AttemptedRecipeVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_PhotoThumbnailJobs_State_Priority_NextAttemptAtUtc",
                table: "PhotoThumbnailJobs",
                columns: new[] { "State", "Priority", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PhotoThumbnails_PhotoId_Size",
                table: "PhotoThumbnails",
                columns: new[] { "PhotoId", "Size" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PhotoThumbnails_StoredObjectId",
                table: "PhotoThumbnails",
                column: "StoredObjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PhotoThumbnailWorkerHeartbeats_UpdatedAtUtc",
                table: "PhotoThumbnailWorkerHeartbeats",
                column: "UpdatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PodcastArtwork_Queue",
                table: "PodcastArtwork",
                columns: new[] { "State", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_PodcastArtwork_Feed_Url",
                table: "PodcastArtwork",
                columns: new[] { "NewsFeedId", "SourceUrlKeyHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PodcastArtworkFiles_PodcastArtworkId_Kind",
                table: "PodcastArtworkFiles",
                columns: new[] { "PodcastArtworkId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PodcastArtworkFiles_StoredObjectId",
                table: "PodcastArtworkFiles",
                column: "StoredObjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PodcastArtworkIncidents_DetectedAtUtc",
                table: "PodcastArtworkIncidents",
                column: "DetectedAtUtc");

            migrationBuilder.CreateIndex(
                name: "UX_PodcastChapters_Episode_Source_Position",
                table: "PodcastChapters",
                columns: new[] { "NewsArticleId", "Source", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PodcastEpisodeAudio_Feed_State",
                table: "PodcastEpisodeAudio",
                columns: new[] { "NewsFeedId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_PodcastEpisodeAudio_Lease",
                table: "PodcastEpisodeAudio",
                column: "LeaseExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PodcastEpisodeAudio_Queue",
                table: "PodcastEpisodeAudio",
                columns: new[] { "State", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PodcastEpisodeAudio_Stored",
                table: "PodcastEpisodeAudio",
                columns: new[] { "State", "StoredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PodcastEpisodeAudio_StoredObjectId",
                table: "PodcastEpisodeAudio",
                column: "StoredObjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_PodcastEpisodeAudio_FeedDownloadGuard",
                table: "PodcastEpisodeAudio",
                column: "FeedDownloadGuard",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_PodcastEpisodeAudio_HostDownloadGuard",
                table: "PodcastEpisodeAudio",
                column: "HostDownloadGuard",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PodcastEpisodes_ArtworkId",
                table: "PodcastEpisodes",
                column: "ArtworkId");

            migrationBuilder.CreateIndex(
                name: "IX_PodcastEpisodes_ChaptersQueue",
                table: "PodcastEpisodes",
                columns: new[] { "ChaptersFetchState", "ChaptersNextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PodcastEpisodes_Feed_Sort",
                table: "PodcastEpisodes",
                columns: new[] { "NewsFeedId", "SortAtUtc", "NewsArticleId" },
                descending: new[] { false, true, false });

            migrationBuilder.CreateIndex(
                name: "IX_PodcastEpisodes_NewsArticleId_NewsFeedId",
                table: "PodcastEpisodes",
                columns: new[] { "NewsArticleId", "NewsFeedId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PodcastFeedDetails_ArtworkId",
                table: "PodcastFeedDetails",
                column: "ArtworkId");

            migrationBuilder.CreateIndex(
                name: "IX_Properties_Archived",
                table: "Properties",
                column: "Archived");

            migrationBuilder.CreateIndex(
                name: "IX_Properties_CurrencyCode",
                table: "Properties",
                column: "CurrencyCode");

            migrationBuilder.CreateIndex(
                name: "IX_Properties_Type_Archived",
                table: "Properties",
                columns: new[] { "Type", "Archived" });

            migrationBuilder.CreateIndex(
                name: "IX_PropertyEstimates_CurrencyCode",
                table: "PropertyEstimates",
                column: "CurrencyCode");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyEstimates_PropertyId_EffectiveFrom",
                table: "PropertyEstimates",
                columns: new[] { "PropertyId", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_PropertyFiles_AttachedAtUtc",
                table: "PropertyFiles",
                column: "AttachedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyFiles_AttachedByUserId",
                table: "PropertyFiles",
                column: "AttachedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyFiles_FileMetadataId",
                table: "PropertyFiles",
                column: "FileMetadataId");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyFiles_IssuedBy",
                table: "PropertyFiles",
                column: "IssuedBy");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyFiles_PropertyId_FileMetadataId",
                table: "PropertyFiles",
                columns: new[] { "PropertyId", "FileMetadataId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PropertySmartTags_AddedAt",
                table: "PropertySmartTags",
                column: "AddedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PropertySmartTags_TransactionTagId",
                table: "PropertySmartTags",
                column: "TransactionTagId");

            migrationBuilder.CreateIndex(
                name: "IX_RealEstateDetails_HomeownerAssociationId",
                table: "RealEstateDetails",
                column: "HomeownerAssociationId");

            migrationBuilder.CreateIndex(
                name: "IX_RealEstateDetails_LocationId",
                table: "RealEstateDetails",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_RecipeAttachments_FileId",
                table: "RecipeAttachments",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_RecipeAttachments_RecipeId_FileId",
                table: "RecipeAttachments",
                columns: new[] { "RecipeId", "FileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecipeIngredients_RecipeId_Position",
                table: "RecipeIngredients",
                columns: new[] { "RecipeId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_RecipePhotos_PhotoId",
                table: "RecipePhotos",
                column: "PhotoId");

            migrationBuilder.CreateIndex(
                name: "IX_RecipePhotos_RecipeId_PhotoId",
                table: "RecipePhotos",
                columns: new[] { "RecipeId", "PhotoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecipePhotos_RecipeId_Position",
                table: "RecipePhotos",
                columns: new[] { "RecipeId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_Recipes_Archived",
                table: "Recipes",
                column: "Archived");

            migrationBuilder.CreateIndex(
                name: "IX_Recipes_CreatedByUserId",
                table: "Recipes",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Recipes_Title",
                table: "Recipes",
                column: "Title");

            migrationBuilder.CreateIndex(
                name: "IX_Recipes_UpdatedByUserId",
                table: "Recipes",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_RecipeSteps_RecipeId_Position",
                table: "RecipeSteps",
                columns: new[] { "RecipeId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_RecipeTagLinks_RecipeId_RecipeTagId",
                table: "RecipeTagLinks",
                columns: new[] { "RecipeId", "RecipeTagId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecipeTagLinks_RecipeTagId",
                table: "RecipeTagLinks",
                column: "RecipeTagId");

            migrationBuilder.CreateIndex(
                name: "IX_RecipeTags_Archived",
                table: "RecipeTags",
                column: "Archived");

            migrationBuilder.CreateIndex(
                name: "IX_RecipeTags_Name",
                table: "RecipeTags",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecurrenceExclusions_CreatedByUserId",
                table: "RecurrenceExclusions",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurrenceExclusions_RecurrencePatternId_OccurrenceKey",
                table: "RecurrenceExclusions",
                columns: new[] { "RecurrencePatternId", "OccurrenceKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecurrencePatternAttachments_FileId",
                table: "RecurrencePatternAttachments",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurrencePatternAttachments_RecurrencePatternId_FileId",
                table: "RecurrencePatternAttachments",
                columns: new[] { "RecurrencePatternId", "FileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecurrencePatternPhotos_PhotoId",
                table: "RecurrencePatternPhotos",
                column: "PhotoId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurrencePatternPhotos_RecurrencePatternId_PhotoId",
                table: "RecurrencePatternPhotos",
                columns: new[] { "RecurrencePatternId", "PhotoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecurrencePatterns_CalendarId",
                table: "RecurrencePatterns",
                column: "CalendarId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurrencePatterns_CalendarId_ExternalUid",
                table: "RecurrencePatterns",
                columns: new[] { "CalendarId", "ExternalUid" });

            migrationBuilder.CreateIndex(
                name: "IX_RecurrencePatterns_CalendarId_ImportSourceUid",
                table: "RecurrencePatterns",
                columns: new[] { "CalendarId", "ImportSourceUid" });

            migrationBuilder.CreateIndex(
                name: "IX_RecurrencePatterns_CreatedByUserId",
                table: "RecurrencePatterns",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurrencePatterns_LocationId",
                table: "RecurrencePatterns",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurrencePatterns_UpdatedByUserId",
                table: "RecurrencePatterns",
                column: "UpdatedByUserId");

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
                name: "IX_TaxStatements_BaseCurrencyCode",
                table: "TaxStatements",
                column: "BaseCurrencyCode");

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
                name: "IX_Terms_ContractId_LabelKey_EffectiveFrom",
                table: "Terms",
                columns: new[] { "ContractId", "LabelKey", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_Terms_CurrencyCode",
                table: "Terms",
                column: "CurrencyCode");

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
                name: "IX_Transactions_AccountId_TimeStamp_Amount",
                table: "Transactions",
                columns: new[] { "AccountId", "TimeStamp", "Amount" });

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_ContactId_TimeStamp",
                table: "Transactions",
                columns: new[] { "ContactId", "TimeStamp" });

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_CurrencyCode",
                table: "Transactions",
                column: "CurrencyCode");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_Status",
                table: "Transactions",
                column: "Status");

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
                name: "IX_TransactionTags_Name",
                table: "TransactionTags",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserPreferences_UserId_Key",
                table: "UserPreferences",
                columns: new[] { "UserId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserProfileImages_UserId",
                table: "UserProfileImages",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_UserId",
                table: "UserProfiles",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WeatherForecastRuns_WeatherPointId_SiteVersion_ProviderUpdat~",
                table: "WeatherForecastRuns",
                columns: new[] { "WeatherPointId", "SiteVersion", "ProviderUpdatedAt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WeatherForecastSteps_WeatherForecastRunId_TimeUtc",
                table: "WeatherForecastSteps",
                columns: new[] { "WeatherForecastRunId", "TimeUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WeatherForecastSteps_WeatherPointId_TimeUtc_SiteVersion_RunP~",
                table: "WeatherForecastSteps",
                columns: new[] { "WeatherPointId", "TimeUtc", "SiteVersion", "RunProviderUpdatedAt", "RunFetchedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WeatherObservations_WeatherPointId_LocalDate_SiteVersion_Fet~",
                table: "WeatherObservations",
                columns: new[] { "WeatherPointId", "LocalDate", "SiteVersion", "FetchedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WeatherPointHistoryWindows_WeatherPointId_FromDate",
                table: "WeatherPointHistoryWindows",
                columns: new[] { "WeatherPointId", "FromDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WeatherPoints_CreatedByUserId",
                table: "WeatherPoints",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WeatherPoints_Name",
                table: "WeatherPoints",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WeatherPoints_UpdatedByUserId",
                table: "WeatherPoints",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WeatherPointSites_AppliedByUserId",
                table: "WeatherPointSites",
                column: "AppliedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountEstimates");

            migrationBuilder.DropTable(
                name: "AccountSmartTags");

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
                name: "BookAttachments");

            migrationBuilder.DropTable(
                name: "BookContributors");

            migrationBuilder.DropTable(
                name: "BookImages");

            migrationBuilder.DropTable(
                name: "BookTagLinks");

            migrationBuilder.DropTable(
                name: "BudgetItems");

            migrationBuilder.DropTable(
                name: "CalendarEventAttachments");

            migrationBuilder.DropTable(
                name: "CalendarEventPhotos");

            migrationBuilder.DropTable(
                name: "ContactAliases");

            migrationBuilder.DropTable(
                name: "ContractFiles");

            migrationBuilder.DropTable(
                name: "ContractParties");

            migrationBuilder.DropTable(
                name: "ContractSmartTags");

            migrationBuilder.DropTable(
                name: "EmailAddresses");

            migrationBuilder.DropTable(
                name: "Events");

            migrationBuilder.DropTable(
                name: "ExchangeRateProviderSeries");

            migrationBuilder.DropTable(
                name: "ExchangeRates");

            migrationBuilder.DropTable(
                name: "ExchangeRateSyncCurrencies");

            migrationBuilder.DropTable(
                name: "FileAnalysisCandidateTags");

            migrationBuilder.DropTable(
                name: "FileStorageMaintenanceLeases");

            migrationBuilder.DropTable(
                name: "FileStorageOrphanSightings");

            migrationBuilder.DropTable(
                name: "FileStorageWorkerVerdicts");

            migrationBuilder.DropTable(
                name: "InstrumentPrices");

            migrationBuilder.DropTable(
                name: "InstrumentPriceSourceBindings");

            migrationBuilder.DropTable(
                name: "InstrumentPriceSourceStates");

            migrationBuilder.DropTable(
                name: "JobHeartbeats");

            migrationBuilder.DropTable(
                name: "JobRuns");

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
                name: "NewsArticleIdentities");

            migrationBuilder.DropTable(
                name: "NewsArticleImages");

            migrationBuilder.DropTable(
                name: "NewsArticleTags");

            migrationBuilder.DropTable(
                name: "NewsFeedBuckets");

            migrationBuilder.DropTable(
                name: "NewsFeedCounters");

            migrationBuilder.DropTable(
                name: "NewsFeedCrashEvents");

            migrationBuilder.DropTable(
                name: "NewsFeedFetchStates");

            migrationBuilder.DropTable(
                name: "NewsFeedTags");

            migrationBuilder.DropTable(
                name: "NewsHostThrottle");

            migrationBuilder.DropTable(
                name: "NewsStorageTotals");

            migrationBuilder.DropTable(
                name: "OrganizationDetails");

            migrationBuilder.DropTable(
                name: "PersonDetails");

            migrationBuilder.DropTable(
                name: "PhoneNumbers");

            migrationBuilder.DropTable(
                name: "PhotoAlbumItems");

            migrationBuilder.DropTable(
                name: "PhotoOriginalBuckets");

            migrationBuilder.DropTable(
                name: "PhotoPeople");

            migrationBuilder.DropTable(
                name: "PhotoTagLinks");

            migrationBuilder.DropTable(
                name: "PhotoThumbnailBuckets");

            migrationBuilder.DropTable(
                name: "PhotoThumbnailIncidents");

            migrationBuilder.DropTable(
                name: "PhotoThumbnailJobs");

            migrationBuilder.DropTable(
                name: "PhotoThumbnails");

            migrationBuilder.DropTable(
                name: "PhotoThumbnailWorkerHeartbeats");

            migrationBuilder.DropTable(
                name: "PodcastArtworkFiles");

            migrationBuilder.DropTable(
                name: "PodcastArtworkIncidents");

            migrationBuilder.DropTable(
                name: "PodcastChapters");

            migrationBuilder.DropTable(
                name: "PodcastEpisodeAudio");

            migrationBuilder.DropTable(
                name: "PodcastFeedDetails");

            migrationBuilder.DropTable(
                name: "PropertyEstimates");

            migrationBuilder.DropTable(
                name: "PropertyFiles");

            migrationBuilder.DropTable(
                name: "PropertySmartTags");

            migrationBuilder.DropTable(
                name: "RealEstateDetails");

            migrationBuilder.DropTable(
                name: "RecipeAttachments");

            migrationBuilder.DropTable(
                name: "RecipeIngredients");

            migrationBuilder.DropTable(
                name: "RecipePhotos");

            migrationBuilder.DropTable(
                name: "RecipeSteps");

            migrationBuilder.DropTable(
                name: "RecipeTagLinks");

            migrationBuilder.DropTable(
                name: "RecurrenceExclusions");

            migrationBuilder.DropTable(
                name: "RecurrencePatternAttachments");

            migrationBuilder.DropTable(
                name: "RecurrencePatternPhotos");

            migrationBuilder.DropTable(
                name: "StoredFileBuckets");

            migrationBuilder.DropTable(
                name: "SystemSettings");

            migrationBuilder.DropTable(
                name: "SystemSettingSecrets");

            migrationBuilder.DropTable(
                name: "TaxStatementFiles");

            migrationBuilder.DropTable(
                name: "TaxStatementTags");

            migrationBuilder.DropTable(
                name: "Terms");

            migrationBuilder.DropTable(
                name: "TermsOfServiceAcceptances");

            migrationBuilder.DropTable(
                name: "TransactionFiles");

            migrationBuilder.DropTable(
                name: "TransactionTagLinks");

            migrationBuilder.DropTable(
                name: "UserPreferences");

            migrationBuilder.DropTable(
                name: "UserProfileImageBlobs");

            migrationBuilder.DropTable(
                name: "UserProfiles");

            migrationBuilder.DropTable(
                name: "VehicleDetails");

            migrationBuilder.DropTable(
                name: "WeatherDays");

            migrationBuilder.DropTable(
                name: "WeatherForecastSteps");

            migrationBuilder.DropTable(
                name: "WeatherObservationCoverages");

            migrationBuilder.DropTable(
                name: "WeatherObservations");

            migrationBuilder.DropTable(
                name: "WeatherPointHistoryWindows");

            migrationBuilder.DropTable(
                name: "WeatherPointSchedules");

            migrationBuilder.DropTable(
                name: "WeatherPointSites");

            migrationBuilder.DropTable(
                name: "WeatherStations");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "BookTags");

            migrationBuilder.DropTable(
                name: "Books");

            migrationBuilder.DropTable(
                name: "Budgets");

            migrationBuilder.DropTable(
                name: "CalendarEvents");

            migrationBuilder.DropTable(
                name: "FileAnalysisCandidateTransactions");

            migrationBuilder.DropTable(
                name: "Instruments");

            migrationBuilder.DropTable(
                name: "JournalEntries");

            migrationBuilder.DropTable(
                name: "JournalTags");

            migrationBuilder.DropTable(
                name: "JournalTaskTags");

            migrationBuilder.DropTable(
                name: "JournalTasks");

            migrationBuilder.DropTable(
                name: "NewsArticleFiles");

            migrationBuilder.DropTable(
                name: "NewsTags");

            migrationBuilder.DropTable(
                name: "PhotoAlbums");

            migrationBuilder.DropTable(
                name: "PhotoTags");

            migrationBuilder.DropTable(
                name: "PodcastEpisodes");

            migrationBuilder.DropTable(
                name: "RecipeTags");

            migrationBuilder.DropTable(
                name: "Recipes");

            migrationBuilder.DropTable(
                name: "TaxStatements");

            migrationBuilder.DropTable(
                name: "Contracts");

            migrationBuilder.DropTable(
                name: "TermsOfServiceVersions");

            migrationBuilder.DropTable(
                name: "TransactionTags");

            migrationBuilder.DropTable(
                name: "Transactions");

            migrationBuilder.DropTable(
                name: "UserProfileImages");

            migrationBuilder.DropTable(
                name: "Properties");

            migrationBuilder.DropTable(
                name: "WeatherForecastRuns");

            migrationBuilder.DropTable(
                name: "RecurrencePatterns");

            migrationBuilder.DropTable(
                name: "FileAnalysisJobs");

            migrationBuilder.DropTable(
                name: "NewsArticleRevisions");

            migrationBuilder.DropTable(
                name: "Photos");

            migrationBuilder.DropTable(
                name: "PodcastArtwork");

            migrationBuilder.DropTable(
                name: "Calendars");

            migrationBuilder.DropTable(
                name: "Locations");

            migrationBuilder.DropTable(
                name: "AccountFiles");

            migrationBuilder.DropTable(
                name: "NewsArticles");

            migrationBuilder.DropTable(
                name: "WeatherPoints");

            migrationBuilder.DropTable(
                name: "Accounts");

            migrationBuilder.DropTable(
                name: "NewsFeeds");

            migrationBuilder.DropTable(
                name: "Contacts");

            migrationBuilder.DropTable(
                name: "Currencies");

            migrationBuilder.DropTable(
                name: "FileMetadata");

            migrationBuilder.DropTable(
                name: "AspNetUsers");

            migrationBuilder.DropTable(
                name: "StoredObjects");

            migrationBuilder.DropTable(
                name: "FileStorageVolumes");
        }
    }
}
