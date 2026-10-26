using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FrostAura.Reel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M5AskReelV2State : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Act",
                table: "SearchTurns",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlanSource",
                table: "SearchTurns",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TruncationCount",
                table: "SearchTurns",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SatisfiesEvidence",
                table: "SearchTurnCards",
                type: "character varying(280)",
                maxLength: 280,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StateJson",
                table: "SearchConversations",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StateVersion",
                table: "SearchConversations",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Act",
                table: "SearchTurns");

            migrationBuilder.DropColumn(
                name: "PlanSource",
                table: "SearchTurns");

            migrationBuilder.DropColumn(
                name: "TruncationCount",
                table: "SearchTurns");

            migrationBuilder.DropColumn(
                name: "SatisfiesEvidence",
                table: "SearchTurnCards");

            migrationBuilder.DropColumn(
                name: "StateJson",
                table: "SearchConversations");

            migrationBuilder.DropColumn(
                name: "StateVersion",
                table: "SearchConversations");
        }
    }
}
