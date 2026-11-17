using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Odyssey.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddNewsModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_JobRuns_CountersNonNegative",
                table: "JobRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_JobRuns_JobType",
                table: "JobRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_JobHeartbeats_JobType",
                table: "JobHeartbeats");

            migrationBuilder.AddColumn<int>(
                name: "FeedsFetched",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FeedsMarked",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FeedsQuarantined",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ImagesStored",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ItemsRevised",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ItemsStored",
                table: "JobRuns",
                type: "int",
                nullable: true);

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
                    SettingsVersion = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsFeeds", x => x.NewsFeedId);
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
                    TextAndSourceBytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsStorageTotals", x => x.Id);
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
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsArticles", x => x.NewsArticleId);
                    table.UniqueConstraint("AK_NewsArticles_NewsArticleId_NewsFeedId", x => new { x.NewsArticleId, x.NewsFeedId });
                    table.CheckConstraint("CK_NewsArticles_BodyOmittedReason", "`BodyOmittedReason` IS NULL OR `BodyOmittedReason` IN (1, 2)");
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

            migrationBuilder.InsertData(
                table: "NewsStorageTotals",
                columns: new[] { "Id", "ImageBytes", "TextAndSourceBytes" },
                values: new object[] { (byte)1, 0L, 0L });

            migrationBuilder.InsertData(
                table: "SystemSettings",
                columns: new[] { "Key", "UpdatedAt", "UpdatedBy", "Value" },
                values: new object[,]
                {
                    { "NewsDefaultFetchIntervalMinutes", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "60" },
                    { "NewsFeedMaxArticlesPerDay", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "500" },
                    { "NewsFeedMaxImageMbPerDay", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "200" },
                    { "NewsImageMaxPerArticle", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "5" },
                    { "NewsImageMaxSizeKb", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "5120" },
                    { "NewsImageStoreMaxGiB", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "50" },
                    { "NewsMaxFeeds", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "200" },
                    { "NewsPrivateHostAllowList", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "" },
                    { "NewsTextStoreMaxGiB", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "20" }
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobRuns_CountersNonNegative",
                table: "JobRuns",
                sql: "COALESCE(`ItemsProcessed`, 0) >= 0 AND COALESCE(`ItemsFailed`, 0) >= 0 AND COALESCE(`DurationMs`, 0) >= 0 AND COALESCE(`RowsInserted`, 0) >= 0 AND COALESCE(`RowsSkipped`, 0) >= 0 AND COALESCE(`RowsRejected`, 0) >= 0 AND COALESCE(`RowsDeleted`, 0) >= 0 AND COALESCE(`PointsAttempted`, 0) >= 0 AND COALESCE(`PointsSkippedNotExpired`, 0) >= 0 AND COALESCE(`PointsFailed`, 0) >= 0 AND COALESCE(`ForecastsStored`, 0) >= 0 AND COALESCE(`ObservationDaysStored`, 0) >= 0 AND COALESCE(`FilesErased`, 0) >= 0 AND COALESCE(`AbandonedWritesSwept`, 0) >= 0 AND COALESCE(`ObjectsScrubbed`, 0) >= 0 AND COALESCE(`IntegrityFailures`, 0) >= 0 AND COALESCE(`OrphansFound`, 0) >= 0 AND COALESCE(`PhotosRendered`, 0) >= 0 AND COALESCE(`PhotosFailed`, 0) >= 0 AND COALESCE(`PhotosRequeued`, 0) >= 0 AND COALESCE(`Crashes`, 0) >= 0 AND COALESCE(`Hangs`, 0) >= 0 AND COALESCE(`PhotosMoved`, 0) >= 0 AND COALESCE(`PhotosCopiedShared`, 0) >= 0 AND COALESCE(`PhotosParked`, 0) >= 0 AND COALESCE(`Interruptions`, 0) >= 0 AND COALESCE(`AnalysesCompleted`, 0) >= 0 AND COALESCE(`AnalysesFailed`, 0) >= 0 AND COALESCE(`AnalysesRefused`, 0) >= 0 AND COALESCE(`FinanceFilesMoved`, 0) >= 0 AND COALESCE(`JournalFilesMoved`, 0) >= 0 AND COALESCE(`ContactAvatarsMoved`, 0) >= 0 AND COALESCE(`FilesParked`, 0) >= 0 AND COALESCE(`InstrumentsAttempted`, 0) >= 0 AND COALESCE(`InstrumentsSucceeded`, 0) >= 0 AND COALESCE(`InstrumentsFailed`, 0) >= 0 AND COALESCE(`PricesInserted`, 0) >= 0 AND COALESCE(`FallbacksUsed`, 0) >= 0 AND COALESCE(`RequestsMade`, 0) >= 0 AND COALESCE(`PricesDeleted`, 0) >= 0 AND COALESCE(`FeedsFetched`, 0) >= 0 AND COALESCE(`ItemsStored`, 0) >= 0 AND COALESCE(`ItemsRevised`, 0) >= 0 AND COALESCE(`ImagesStored`, 0) >= 0 AND COALESCE(`FeedsQuarantined`, 0) >= 0 AND COALESCE(`FeedsMarked`, 0) >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobRuns_JobType",
                table: "JobRuns",
                sql: "`JobType` IN (1, 2, 3, 4, 5, 6, 7, 8, 9)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobHeartbeats_JobType",
                table: "JobHeartbeats",
                sql: "`JobType` IN (1, 2, 3, 4, 5, 6, 7, 8, 9)");

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Issue #470 §11.2: the run-log rows of job 9 go first, because the narrowed CHECKs below would refuse them;
            // then the CHECKs narrow; then the tables go, and any news data with them. The news StoredObjects rows
            // remain and raise startup check B4's Critical log ("a live row of an unknown feature"); the release notes
            // say to delete every feed and wait out the deletion grace before rolling back.
            migrationBuilder.Sql("DELETE FROM `JobRuns` WHERE `JobType` = 9;");
            migrationBuilder.Sql("DELETE FROM `JobHeartbeats` WHERE `JobType` = 9;");

            migrationBuilder.DropCheckConstraint(
                name: "CK_JobRuns_CountersNonNegative",
                table: "JobRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_JobRuns_JobType",
                table: "JobRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_JobHeartbeats_JobType",
                table: "JobHeartbeats");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "NewsDefaultFetchIntervalMinutes");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "NewsFeedMaxArticlesPerDay");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "NewsFeedMaxImageMbPerDay");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "NewsImageMaxPerArticle");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "NewsImageMaxSizeKb");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "NewsImageStoreMaxGiB");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "NewsMaxFeeds");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "NewsPrivateHostAllowList");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "NewsTextStoreMaxGiB");

            migrationBuilder.DropColumn(
                name: "FeedsFetched",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "FeedsMarked",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "FeedsQuarantined",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "ImagesStored",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "ItemsRevised",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "ItemsStored",
                table: "JobRuns");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobRuns_CountersNonNegative",
                table: "JobRuns",
                sql: "COALESCE(`ItemsProcessed`, 0) >= 0 AND COALESCE(`ItemsFailed`, 0) >= 0 AND COALESCE(`DurationMs`, 0) >= 0 AND COALESCE(`RowsInserted`, 0) >= 0 AND COALESCE(`RowsSkipped`, 0) >= 0 AND COALESCE(`RowsRejected`, 0) >= 0 AND COALESCE(`RowsDeleted`, 0) >= 0 AND COALESCE(`PointsAttempted`, 0) >= 0 AND COALESCE(`PointsSkippedNotExpired`, 0) >= 0 AND COALESCE(`PointsFailed`, 0) >= 0 AND COALESCE(`ForecastsStored`, 0) >= 0 AND COALESCE(`ObservationDaysStored`, 0) >= 0 AND COALESCE(`FilesErased`, 0) >= 0 AND COALESCE(`AbandonedWritesSwept`, 0) >= 0 AND COALESCE(`ObjectsScrubbed`, 0) >= 0 AND COALESCE(`IntegrityFailures`, 0) >= 0 AND COALESCE(`OrphansFound`, 0) >= 0 AND COALESCE(`PhotosRendered`, 0) >= 0 AND COALESCE(`PhotosFailed`, 0) >= 0 AND COALESCE(`PhotosRequeued`, 0) >= 0 AND COALESCE(`Crashes`, 0) >= 0 AND COALESCE(`Hangs`, 0) >= 0 AND COALESCE(`PhotosMoved`, 0) >= 0 AND COALESCE(`PhotosCopiedShared`, 0) >= 0 AND COALESCE(`PhotosParked`, 0) >= 0 AND COALESCE(`Interruptions`, 0) >= 0 AND COALESCE(`AnalysesCompleted`, 0) >= 0 AND COALESCE(`AnalysesFailed`, 0) >= 0 AND COALESCE(`AnalysesRefused`, 0) >= 0 AND COALESCE(`FinanceFilesMoved`, 0) >= 0 AND COALESCE(`JournalFilesMoved`, 0) >= 0 AND COALESCE(`ContactAvatarsMoved`, 0) >= 0 AND COALESCE(`FilesParked`, 0) >= 0 AND COALESCE(`InstrumentsAttempted`, 0) >= 0 AND COALESCE(`InstrumentsSucceeded`, 0) >= 0 AND COALESCE(`InstrumentsFailed`, 0) >= 0 AND COALESCE(`PricesInserted`, 0) >= 0 AND COALESCE(`FallbacksUsed`, 0) >= 0 AND COALESCE(`RequestsMade`, 0) >= 0 AND COALESCE(`PricesDeleted`, 0) >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobRuns_JobType",
                table: "JobRuns",
                sql: "`JobType` IN (1, 2, 3, 4, 5, 6, 7, 8)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobHeartbeats_JobType",
                table: "JobHeartbeats",
                sql: "`JobType` IN (1, 2, 3, 4, 5, 6, 7, 8)");

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
                name: "NewsArticleFiles");

            migrationBuilder.DropTable(
                name: "NewsTags");

            migrationBuilder.DropTable(
                name: "NewsArticleRevisions");

            migrationBuilder.DropTable(
                name: "NewsArticles");

            migrationBuilder.DropTable(
                name: "NewsFeeds");
        }
    }
}
