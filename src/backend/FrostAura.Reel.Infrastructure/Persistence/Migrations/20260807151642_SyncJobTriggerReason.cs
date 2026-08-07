using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FrostAura.Reel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SyncJobTriggerReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TriggerReason",
                table: "SyncJobs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TriggerReason",
                table: "SyncJobs");
        }
    }
}
