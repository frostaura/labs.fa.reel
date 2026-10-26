using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FrostAura.Reel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Ws4P1TraktIngestAndRatingsBackfill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HiddenItemsJson",
                table: "TraktConnections",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "HiddenItemsSyncedAt",
                table: "TraktConnections",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TraktRatingCheckedAt",
                table: "Titles",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HiddenItemsJson",
                table: "TraktConnections");

            migrationBuilder.DropColumn(
                name: "HiddenItemsSyncedAt",
                table: "TraktConnections");

            migrationBuilder.DropColumn(
                name: "TraktRatingCheckedAt",
                table: "Titles");
        }
    }
}
