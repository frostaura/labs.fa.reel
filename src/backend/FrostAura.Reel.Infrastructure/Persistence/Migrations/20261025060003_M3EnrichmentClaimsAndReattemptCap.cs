using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FrostAura.Reel.Infrastructure.Persistence.Migrations
{
    /// <summary>M3 (WS-4): enrichment claims, back-off and re-attempt cap — TitleAttributes.{ClaimedBy,ClaimedUntil,LastAttemptAt,RetryAfter,TransportFailures,ReattemptFailures,ReattemptQuestion}. Additive (expand-only); integrator re-scaffold of the lane migrations 20261024163850_Ws4EnrichmentClaimsAndBackoff, 20261024191837_Ws4AnsweredRowReattemptCap.</summary>
    public partial class M3EnrichmentClaimsAndReattemptCap : Migration
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

            migrationBuilder.AddColumn<int>(
                name: "ReattemptFailures",
                table: "TitleAttributes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ReattemptQuestion",
                table: "TitleAttributes",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReattemptFailures",
                table: "TitleAttributes");

            migrationBuilder.DropColumn(
                name: "ReattemptQuestion",
                table: "TitleAttributes");

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
