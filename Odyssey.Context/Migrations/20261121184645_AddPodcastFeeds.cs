using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Odyssey.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddPodcastFeeds : Migration
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

            migrationBuilder.AddColumn<long>(
                name: "ArtworkBytes",
                table: "NewsStorageTotals",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "AudioBytes",
                table: "NewsStorageTotals",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "AudioPendingReleaseBytes",
                table: "NewsStorageTotals",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "AudioReservedBytes",
                table: "NewsStorageTotals",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "AutoDeleteAfterDays",
                table: "NewsFeeds",
                type: "int",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<DateTime>(
                name: "AutoDeleteChangedAtUtc",
                table: "NewsFeeds",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AutoDeleteEnabled",
                table: "NewsFeeds",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AutoDownload",
                table: "NewsFeeds",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<byte>(
                name: "Kind",
                table: "NewsFeeds",
                type: "tinyint unsigned",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<DateTime>(
                name: "KindChangedAtUtc",
                table: "NewsFeeds",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FeedKindRewrittenAtUtc",
                table: "NewsFeedFetchStates",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "FeedKind",
                table: "NewsArticles",
                type: "tinyint unsigned",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<int>(
                name: "ArtworkFailed",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ArtworkStored",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AudioBytesReleased",
                table: "JobRuns",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AudioBytesStored",
                table: "JobRuns",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AudioReleased",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ChaptersFetched",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DownloadsDeferred",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DownloadsFailed",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DownloadsRecovered",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DownloadsStored",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReservationsRefused",
                table: "JobRuns",
                type: "int",
                nullable: true);

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

            migrationBuilder.UpdateData(
                table: "NewsStorageTotals",
                keyColumn: "Id",
                keyValue: (byte)1,
                columns: new[] { "ArtworkBytes", "AudioBytes", "AudioPendingReleaseBytes", "AudioReservedBytes" },
                values: new object[] { 0L, 0L, 0L, 0L });

            migrationBuilder.InsertData(
                table: "SystemSettings",
                columns: new[] { "Key", "UpdatedAt", "UpdatedBy", "Value" },
                values: new object[,]
                {
                    { "PodcastAudioMaxEpisodeMb", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "1024" },
                    { "PodcastAudioStoreMaxGiB", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "50" },
                    { "PodcastAutoDownloadMaxUnfetchedPerFeed", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "5" },
                    { "PodcastAutoDownloadsPerFeedPerDay", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "5" }
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_NewsStorageTotals_PodcastBytesNonNegative",
                table: "NewsStorageTotals",
                sql: "`AudioBytes` >= 0 AND `AudioPendingReleaseBytes` >= 0 AND `AudioReservedBytes` >= 0 AND `ArtworkBytes` >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_NewsFeeds_AutoDeleteDays",
                table: "NewsFeeds",
                sql: "`AutoDeleteAfterDays` BETWEEN 1 AND 3650");

            migrationBuilder.AddCheckConstraint(
                name: "CK_NewsFeeds_Kind",
                table: "NewsFeeds",
                sql: "`Kind` IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "IX_NewsArticles_Kind_Sort",
                table: "NewsArticles",
                columns: new[] { "FeedKind", "SortAtUtc", "NewsArticleId" },
                descending: new[] { false, true, false });

            migrationBuilder.AddCheckConstraint(
                name: "CK_NewsArticles_FeedKind",
                table: "NewsArticles",
                sql: "`FeedKind` IN (0, 1)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobRuns_CountersNonNegative",
                table: "JobRuns",
                sql: "COALESCE(`ItemsProcessed`, 0) >= 0 AND COALESCE(`ItemsFailed`, 0) >= 0 AND COALESCE(`DurationMs`, 0) >= 0 AND COALESCE(`RowsInserted`, 0) >= 0 AND COALESCE(`RowsSkipped`, 0) >= 0 AND COALESCE(`RowsRejected`, 0) >= 0 AND COALESCE(`RowsDeleted`, 0) >= 0 AND COALESCE(`PointsAttempted`, 0) >= 0 AND COALESCE(`PointsSkippedNotExpired`, 0) >= 0 AND COALESCE(`PointsFailed`, 0) >= 0 AND COALESCE(`ForecastsStored`, 0) >= 0 AND COALESCE(`ObservationDaysStored`, 0) >= 0 AND COALESCE(`FilesErased`, 0) >= 0 AND COALESCE(`AbandonedWritesSwept`, 0) >= 0 AND COALESCE(`ObjectsScrubbed`, 0) >= 0 AND COALESCE(`IntegrityFailures`, 0) >= 0 AND COALESCE(`OrphansFound`, 0) >= 0 AND COALESCE(`PhotosRendered`, 0) >= 0 AND COALESCE(`PhotosFailed`, 0) >= 0 AND COALESCE(`PhotosRequeued`, 0) >= 0 AND COALESCE(`Crashes`, 0) >= 0 AND COALESCE(`Hangs`, 0) >= 0 AND COALESCE(`AnalysesCompleted`, 0) >= 0 AND COALESCE(`AnalysesFailed`, 0) >= 0 AND COALESCE(`AnalysesRefused`, 0) >= 0 AND COALESCE(`InstrumentsAttempted`, 0) >= 0 AND COALESCE(`InstrumentsSucceeded`, 0) >= 0 AND COALESCE(`InstrumentsFailed`, 0) >= 0 AND COALESCE(`PricesInserted`, 0) >= 0 AND COALESCE(`FallbacksUsed`, 0) >= 0 AND COALESCE(`RequestsMade`, 0) >= 0 AND COALESCE(`PricesDeleted`, 0) >= 0 AND COALESCE(`FeedsFetched`, 0) >= 0 AND COALESCE(`ItemsStored`, 0) >= 0 AND COALESCE(`ItemsRevised`, 0) >= 0 AND COALESCE(`ImagesStored`, 0) >= 0 AND COALESCE(`FeedsQuarantined`, 0) >= 0 AND COALESCE(`FeedsMarked`, 0) >= 0 AND COALESCE(`SeriesKeyed`, 0) >= 0 AND COALESCE(`SeriesKeyFailures`, 0) >= 0 AND COALESCE(`KeyRowsDetached`, 0) >= 0 AND COALESCE(`ArtworkStored`, 0) >= 0 AND COALESCE(`ArtworkFailed`, 0) >= 0 AND COALESCE(`ChaptersFetched`, 0) >= 0 AND COALESCE(`DownloadsStored`, 0) >= 0 AND COALESCE(`DownloadsFailed`, 0) >= 0 AND COALESCE(`DownloadsDeferred`, 0) >= 0 AND COALESCE(`DownloadsRecovered`, 0) >= 0 AND COALESCE(`ReservationsRefused`, 0) >= 0 AND COALESCE(`AudioBytesStored`, 0) >= 0 AND COALESCE(`AudioReleased`, 0) >= 0 AND COALESCE(`AudioBytesReleased`, 0) >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobRuns_JobType",
                table: "JobRuns",
                sql: "`JobType` IN (1, 2, 3, 4, 6, 8, 9, 10, 11, 12)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobHeartbeats_JobType",
                table: "JobHeartbeats",
                sql: "`JobType` IN (1, 2, 3, 4, 6, 8, 9, 10, 11, 12)");

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The JobType CHECKs re-added below refuse 11 and 12, so the podcast jobs' run-log rows go first (the news
            // migration's precedent). Down() restores schema only: the podcast data it drops is not restored.
            migrationBuilder.Sql("DELETE FROM `JobRuns` WHERE `JobType` IN (11, 12);");
            migrationBuilder.Sql("DELETE FROM `JobHeartbeats` WHERE `JobType` IN (11, 12);");

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
                name: "PodcastEpisodes");

            migrationBuilder.DropTable(
                name: "PodcastArtwork");

            migrationBuilder.DropCheckConstraint(
                name: "CK_NewsStorageTotals_PodcastBytesNonNegative",
                table: "NewsStorageTotals");

            migrationBuilder.DropCheckConstraint(
                name: "CK_NewsFeeds_AutoDeleteDays",
                table: "NewsFeeds");

            migrationBuilder.DropCheckConstraint(
                name: "CK_NewsFeeds_Kind",
                table: "NewsFeeds");

            migrationBuilder.DropIndex(
                name: "IX_NewsArticles_Kind_Sort",
                table: "NewsArticles");

            migrationBuilder.DropCheckConstraint(
                name: "CK_NewsArticles_FeedKind",
                table: "NewsArticles");

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
                keyValue: "PodcastAudioMaxEpisodeMb");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "PodcastAudioStoreMaxGiB");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "PodcastAutoDownloadMaxUnfetchedPerFeed");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "PodcastAutoDownloadsPerFeedPerDay");

            migrationBuilder.DropColumn(
                name: "ArtworkBytes",
                table: "NewsStorageTotals");

            migrationBuilder.DropColumn(
                name: "AudioBytes",
                table: "NewsStorageTotals");

            migrationBuilder.DropColumn(
                name: "AudioPendingReleaseBytes",
                table: "NewsStorageTotals");

            migrationBuilder.DropColumn(
                name: "AudioReservedBytes",
                table: "NewsStorageTotals");

            migrationBuilder.DropColumn(
                name: "AutoDeleteAfterDays",
                table: "NewsFeeds");

            migrationBuilder.DropColumn(
                name: "AutoDeleteChangedAtUtc",
                table: "NewsFeeds");

            migrationBuilder.DropColumn(
                name: "AutoDeleteEnabled",
                table: "NewsFeeds");

            migrationBuilder.DropColumn(
                name: "AutoDownload",
                table: "NewsFeeds");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "NewsFeeds");

            migrationBuilder.DropColumn(
                name: "KindChangedAtUtc",
                table: "NewsFeeds");

            migrationBuilder.DropColumn(
                name: "FeedKindRewrittenAtUtc",
                table: "NewsFeedFetchStates");

            migrationBuilder.DropColumn(
                name: "FeedKind",
                table: "NewsArticles");

            migrationBuilder.DropColumn(
                name: "ArtworkFailed",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "ArtworkStored",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "AudioBytesReleased",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "AudioBytesStored",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "AudioReleased",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "ChaptersFetched",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "DownloadsDeferred",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "DownloadsFailed",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "DownloadsRecovered",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "DownloadsStored",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "ReservationsRefused",
                table: "JobRuns");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobRuns_CountersNonNegative",
                table: "JobRuns",
                sql: "COALESCE(`ItemsProcessed`, 0) >= 0 AND COALESCE(`ItemsFailed`, 0) >= 0 AND COALESCE(`DurationMs`, 0) >= 0 AND COALESCE(`RowsInserted`, 0) >= 0 AND COALESCE(`RowsSkipped`, 0) >= 0 AND COALESCE(`RowsRejected`, 0) >= 0 AND COALESCE(`RowsDeleted`, 0) >= 0 AND COALESCE(`PointsAttempted`, 0) >= 0 AND COALESCE(`PointsSkippedNotExpired`, 0) >= 0 AND COALESCE(`PointsFailed`, 0) >= 0 AND COALESCE(`ForecastsStored`, 0) >= 0 AND COALESCE(`ObservationDaysStored`, 0) >= 0 AND COALESCE(`FilesErased`, 0) >= 0 AND COALESCE(`AbandonedWritesSwept`, 0) >= 0 AND COALESCE(`ObjectsScrubbed`, 0) >= 0 AND COALESCE(`IntegrityFailures`, 0) >= 0 AND COALESCE(`OrphansFound`, 0) >= 0 AND COALESCE(`PhotosRendered`, 0) >= 0 AND COALESCE(`PhotosFailed`, 0) >= 0 AND COALESCE(`PhotosRequeued`, 0) >= 0 AND COALESCE(`Crashes`, 0) >= 0 AND COALESCE(`Hangs`, 0) >= 0 AND COALESCE(`AnalysesCompleted`, 0) >= 0 AND COALESCE(`AnalysesFailed`, 0) >= 0 AND COALESCE(`AnalysesRefused`, 0) >= 0 AND COALESCE(`InstrumentsAttempted`, 0) >= 0 AND COALESCE(`InstrumentsSucceeded`, 0) >= 0 AND COALESCE(`InstrumentsFailed`, 0) >= 0 AND COALESCE(`PricesInserted`, 0) >= 0 AND COALESCE(`FallbacksUsed`, 0) >= 0 AND COALESCE(`RequestsMade`, 0) >= 0 AND COALESCE(`PricesDeleted`, 0) >= 0 AND COALESCE(`FeedsFetched`, 0) >= 0 AND COALESCE(`ItemsStored`, 0) >= 0 AND COALESCE(`ItemsRevised`, 0) >= 0 AND COALESCE(`ImagesStored`, 0) >= 0 AND COALESCE(`FeedsQuarantined`, 0) >= 0 AND COALESCE(`FeedsMarked`, 0) >= 0 AND COALESCE(`SeriesKeyed`, 0) >= 0 AND COALESCE(`SeriesKeyFailures`, 0) >= 0 AND COALESCE(`KeyRowsDetached`, 0) >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobRuns_JobType",
                table: "JobRuns",
                sql: "`JobType` IN (1, 2, 3, 4, 6, 8, 9, 10)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobHeartbeats_JobType",
                table: "JobHeartbeats",
                sql: "`JobType` IN (1, 2, 3, 4, 6, 8, 9, 10)");
        }
    }
}
