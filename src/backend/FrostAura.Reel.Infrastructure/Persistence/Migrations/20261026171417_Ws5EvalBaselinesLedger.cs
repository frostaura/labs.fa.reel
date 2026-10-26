using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FrostAura.Reel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Ws5EvalBaselinesLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BaselinesJson",
                table: "EvaluationResults",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FeatureSetHash",
                table: "EvaluationResults",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BaselinesJson",
                table: "EvaluationResults");

            migrationBuilder.DropColumn(
                name: "FeatureSetHash",
                table: "EvaluationResults");
        }
    }
}
