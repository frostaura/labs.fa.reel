using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FrostAura.Reel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FeedDegradationAndResidualAlgorithmVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AlgorithmVersion",
                table: "OnlineModelStates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DegradedReason",
                table: "FeedSnapshots",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AlgorithmVersion",
                table: "OnlineModelStates");

            migrationBuilder.DropColumn(
                name: "DegradedReason",
                table: "FeedSnapshots");
        }
    }
}
