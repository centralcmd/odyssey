using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <inheritdoc />
    public partial class PodcastEpisodeListenedAndPlaybackBound : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Issue #593. The column first and the CHECK swap last: MariaDB commits each DDL on its own (issue #468), so
            // this order keeps the window without the CHECK as short as it can be. PlaybackPositionSeconds was unused
            // until now (always NULL), so the tighter bound cannot reject an existing row.
            migrationBuilder.AddColumn<DateTime>(
                name: "LastListenedAtUtc",
                table: "PodcastEpisodes",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.DropCheckConstraint(
                name: "CK_PodcastEpisodes_Ranges",
                table: "PodcastEpisodes");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PodcastEpisodes_Ranges",
                table: "PodcastEpisodes",
                sql: "(`DurationSeconds` IS NULL OR `DurationSeconds` BETWEEN 0 AND 604800) AND (`SeasonNumber` IS NULL OR `SeasonNumber` BETWEEN 0 AND 100000) AND (`EpisodeNumber` IS NULL OR `EpisodeNumber` BETWEEN 0 AND 100000) AND (`EnclosureDeclaredLength` IS NULL OR `EnclosureDeclaredLength` >= 0) AND (`PlaybackPositionSeconds` IS NULL OR `PlaybackPositionSeconds` BETWEEN 0 AND 604800)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drops LastListenedAtUtc and every listened stamp with it; auto-delete then counts from device downloads only.
            migrationBuilder.DropCheckConstraint(
                name: "CK_PodcastEpisodes_Ranges",
                table: "PodcastEpisodes");

            migrationBuilder.DropColumn(
                name: "LastListenedAtUtc",
                table: "PodcastEpisodes");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PodcastEpisodes_Ranges",
                table: "PodcastEpisodes",
                sql: "(`DurationSeconds` IS NULL OR `DurationSeconds` BETWEEN 0 AND 604800) AND (`SeasonNumber` IS NULL OR `SeasonNumber` BETWEEN 0 AND 100000) AND (`EpisodeNumber` IS NULL OR `EpisodeNumber` BETWEEN 0 AND 100000) AND (`EnclosureDeclaredLength` IS NULL OR `EnclosureDeclaredLength` >= 0) AND (`PlaybackPositionSeconds` IS NULL OR `PlaybackPositionSeconds` >= 0)");
        }
    }
}
