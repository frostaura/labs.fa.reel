using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace FrostAura.Reel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase5TasteClusters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AnnPerClusterJson",
                table: "FeedBuildStats",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ClustersUsed",
                table: "FeedBuildStats",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "NewlyEligiblePerArmJson",
                table: "FeedBuildStats",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SeedPoolSize",
                table: "FeedBuildStats",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SeedsUsed",
                table: "FeedBuildStats",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "TasteClusters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ComputedForDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    Centroid = table.Column<Vector>(type: "vector(1536)", nullable: false),
                    Weight = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    MemberCount = table.Column<int>(type: "integer", nullable: false),
                    ExemplarTitleIdsJson = table.Column<string>(type: "jsonb", nullable: false),
                    Label = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TasteClusters", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TasteClusters_AccountId_ComputedForDate",
                table: "TasteClusters",
                columns: new[] { "AccountId", "ComputedForDate" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_TasteClusters_AccountId_ComputedForDate_Ordinal",
                table: "TasteClusters",
                columns: new[] { "AccountId", "ComputedForDate", "Ordinal" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TasteClusters");

            migrationBuilder.DropColumn(
                name: "AnnPerClusterJson",
                table: "FeedBuildStats");

            migrationBuilder.DropColumn(
                name: "ClustersUsed",
                table: "FeedBuildStats");

            migrationBuilder.DropColumn(
                name: "NewlyEligiblePerArmJson",
                table: "FeedBuildStats");

            migrationBuilder.DropColumn(
                name: "SeedPoolSize",
                table: "FeedBuildStats");

            migrationBuilder.DropColumn(
                name: "SeedsUsed",
                table: "FeedBuildStats");
        }
    }
}
