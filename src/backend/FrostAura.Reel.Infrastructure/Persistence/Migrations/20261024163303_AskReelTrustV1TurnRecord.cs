using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FrostAura.Reel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AskReelTrustV1TurnRecord : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DegradedFlags",
                table: "SearchTurns",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Mode",
                table: "SearchTurns",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModelIds",
                table: "SearchTurns",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReasoningEffort",
                table: "SearchTurns",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FitSource",
                table: "SearchTurnCards",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Satisfies",
                table: "SearchTurnCards",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tier",
                table: "SearchTurnCards",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SearchTurnCards_ConversationId_TitleId",
                table: "SearchTurnCards",
                columns: new[] { "ConversationId", "TitleId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SearchTurnCards_ConversationId_TitleId",
                table: "SearchTurnCards");

            migrationBuilder.DropColumn(
                name: "DegradedFlags",
                table: "SearchTurns");

            migrationBuilder.DropColumn(
                name: "Mode",
                table: "SearchTurns");

            migrationBuilder.DropColumn(
                name: "ModelIds",
                table: "SearchTurns");

            migrationBuilder.DropColumn(
                name: "ReasoningEffort",
                table: "SearchTurns");

            migrationBuilder.DropColumn(
                name: "FitSource",
                table: "SearchTurnCards");

            migrationBuilder.DropColumn(
                name: "Satisfies",
                table: "SearchTurnCards");

            migrationBuilder.DropColumn(
                name: "Tier",
                table: "SearchTurnCards");
        }
    }
}
