using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FrostAura.Reel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase0Instrumentation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExternalApiUsages_Provider_Day",
                table: "ExternalApiUsages");

            migrationBuilder.AddColumn<Guid>(
                name: "AccountId",
                table: "ExternalApiUsages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CompletionTokens",
                table: "ExternalApiUsages",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Model",
                table: "ExternalApiUsages",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PromptTokens",
                table: "ExternalApiUsages",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Purpose",
                table: "ExternalApiUsages",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FeedBuildStats",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    FeedSnapshotId = table.Column<Guid>(type: "uuid", nullable: true),
                    ModelArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    BuiltAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CandidateCount = table.Column<int>(type: "integer", nullable: false),
                    EligiblePoolCount = table.Column<int>(type: "integer", nullable: false),
                    ShortlistCount = table.Column<int>(type: "integer", nullable: false),
                    HeroEligibleCount = table.Column<int>(type: "integer", nullable: false),
                    ShortlistP50 = table.Column<decimal>(type: "numeric(4,2)", precision: 4, scale: 2, nullable: true),
                    ShortlistP90 = table.Column<decimal>(type: "numeric(4,2)", precision: 4, scale: 2, nullable: true),
                    NewCandidatesUpserted = table.Column<int>(type: "integer", nullable: false),
                    RailsDropped = table.Column<int>(type: "integer", nullable: false),
                    DegradedReason = table.Column<string>(type: "text", nullable: true),
                    DurationMs = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeedBuildStats", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalApiUsages_Provider_Day_AccountId_Model_Purpose",
                table: "ExternalApiUsages",
                columns: new[] { "Provider", "Day", "AccountId", "Model", "Purpose" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_FeedBuildStats_AccountId_BuiltAt",
                table: "FeedBuildStats",
                columns: new[] { "AccountId", "BuiltAt" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FeedBuildStats");

            migrationBuilder.DropIndex(
                name: "IX_ExternalApiUsages_Provider_Day_AccountId_Model_Purpose",
                table: "ExternalApiUsages");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "ExternalApiUsages");

            migrationBuilder.DropColumn(
                name: "CompletionTokens",
                table: "ExternalApiUsages");

            migrationBuilder.DropColumn(
                name: "Model",
                table: "ExternalApiUsages");

            migrationBuilder.DropColumn(
                name: "PromptTokens",
                table: "ExternalApiUsages");

            migrationBuilder.DropColumn(
                name: "Purpose",
                table: "ExternalApiUsages");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalApiUsages_Provider_Day",
                table: "ExternalApiUsages",
                columns: new[] { "Provider", "Day" },
                unique: true);
        }
    }
}
