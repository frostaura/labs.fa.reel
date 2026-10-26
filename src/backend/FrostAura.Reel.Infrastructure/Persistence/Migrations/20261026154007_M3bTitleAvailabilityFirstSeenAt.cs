using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FrostAura.Reel.Infrastructure.Persistence.Migrations
{
    /// <summary>M3b (WS-4, IR2S-02): TitleAvailabilities.FirstSeenAt, when Reel first saw an offer absent from the previous snapshot. Nullable with no default and no backfill: null means no arrival observed, so existing rows never read as new. Additive (expand-only); integrator scaffold of WS-4 CR-1, round 3.</summary>
    public partial class M3bTitleAvailabilityFirstSeenAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FirstSeenAt",
                table: "TitleAvailabilities",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FirstSeenAt",
                table: "TitleAvailabilities");
        }
    }
}
