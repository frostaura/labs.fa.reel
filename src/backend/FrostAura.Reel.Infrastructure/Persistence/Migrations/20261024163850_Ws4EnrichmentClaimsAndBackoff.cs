using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FrostAura.Reel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Ws4EnrichmentClaimsAndBackoff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ClaimedBy",
                table: "TitleAttributes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClaimedUntil",
                table: "TitleAttributes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastAttemptAt",
                table: "TitleAttributes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RetryAfter",
                table: "TitleAttributes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TransportFailures",
                table: "TitleAttributes",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClaimedBy",
                table: "TitleAttributes");

            migrationBuilder.DropColumn(
                name: "ClaimedUntil",
                table: "TitleAttributes");

            migrationBuilder.DropColumn(
                name: "LastAttemptAt",
                table: "TitleAttributes");

            migrationBuilder.DropColumn(
                name: "RetryAfter",
                table: "TitleAttributes");

            migrationBuilder.DropColumn(
                name: "TransportFailures",
                table: "TitleAttributes");
        }
    }
}
