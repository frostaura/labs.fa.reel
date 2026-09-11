using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FrostAura.Reel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AskReelExposureTurnKeyAndConflict : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExposureEvents_AccountId_TitleId_Surface_FeedSnapshotId_Min~",
                table: "ExposureEvents");

            migrationBuilder.AddColumn<string>(
                name: "ConflictJson",
                table: "SearchTurns",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExposureEvents_AccountId_TitleId_Surface_FeedSnapshotId_Min~",
                table: "ExposureEvents",
                columns: new[] { "AccountId", "TitleId", "Surface", "FeedSnapshotId", "MinuteBucket", "SourceTurnId" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExposureEvents_AccountId_TitleId_Surface_FeedSnapshotId_Min~",
                table: "ExposureEvents");

            migrationBuilder.DropColumn(
                name: "ConflictJson",
                table: "SearchTurns");

            migrationBuilder.CreateIndex(
                name: "IX_ExposureEvents_AccountId_TitleId_Surface_FeedSnapshotId_Min~",
                table: "ExposureEvents",
                columns: new[] { "AccountId", "TitleId", "Surface", "FeedSnapshotId", "MinuteBucket" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }
    }
}
