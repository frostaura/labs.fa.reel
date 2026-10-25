using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FrostAura.Reel.Infrastructure.Persistence.Migrations
{
    /// <summary>M4 (WS-5): ExposureEvents breakdown columns become nullable ("unknown" instead of a false 0); FeedItems.{WhySource,EvidenceJson}. Additive (expand-only); integrator re-scaffold of the lane migrations 20261024164312_ExposureBreakdownNullable, 20261024201708_FeedItemProvenanceAndEvidence.</summary>
    public partial class M4ExposureBreakdownAndFeedProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "ResidualApplied",
                table: "ExposureEvents",
                type: "numeric(6,3)",
                precision: 6,
                scale: 3,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(6,3)",
                oldPrecision: 6,
                oldScale: 3);

            migrationBuilder.AlterColumn<decimal>(
                name: "DemotionApplied",
                table: "ExposureEvents",
                type: "numeric(6,3)",
                precision: 6,
                scale: 3,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(6,3)",
                oldPrecision: 6,
                oldScale: 3);

            migrationBuilder.AlterColumn<decimal>(
                name: "CalibrationDelta",
                table: "ExposureEvents",
                type: "numeric(6,3)",
                precision: 6,
                scale: 3,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(6,3)",
                oldPrecision: 6,
                oldScale: 3);

            migrationBuilder.AlterColumn<decimal>(
                name: "BaseScore",
                table: "ExposureEvents",
                type: "numeric(6,3)",
                precision: 6,
                scale: 3,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(6,3)",
                oldPrecision: 6,
                oldScale: 3);

            migrationBuilder.AddColumn<string>(
                name: "EvidenceJson",
                table: "FeedItems",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WhySource",
                table: "FeedItems",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EvidenceJson",
                table: "FeedItems");

            migrationBuilder.DropColumn(
                name: "WhySource",
                table: "FeedItems");

            // Rollback only. NOT NULL cannot be restored over the NULLs this migration's code writes
            // ("breakdown unknown"), so they go back to the 0 the older image used for the same
            // meaning — the one place the append-only log is rewritten, and only on the way down.
            migrationBuilder.Sql(
                """
                UPDATE "ExposureEvents"
                SET "BaseScore" = COALESCE("BaseScore", 0),
                    "CalibrationDelta" = COALESCE("CalibrationDelta", 0),
                    "ResidualApplied" = COALESCE("ResidualApplied", 0),
                    "DemotionApplied" = COALESCE("DemotionApplied", 0)
                WHERE "BaseScore" IS NULL OR "CalibrationDelta" IS NULL
                   OR "ResidualApplied" IS NULL OR "DemotionApplied" IS NULL;
                """);

            migrationBuilder.AlterColumn<decimal>(
                name: "ResidualApplied",
                table: "ExposureEvents",
                type: "numeric(6,3)",
                precision: 6,
                scale: 3,
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "numeric(6,3)",
                oldPrecision: 6,
                oldScale: 3,
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "DemotionApplied",
                table: "ExposureEvents",
                type: "numeric(6,3)",
                precision: 6,
                scale: 3,
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "numeric(6,3)",
                oldPrecision: 6,
                oldScale: 3,
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "CalibrationDelta",
                table: "ExposureEvents",
                type: "numeric(6,3)",
                precision: 6,
                scale: 3,
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "numeric(6,3)",
                oldPrecision: 6,
                oldScale: 3,
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "BaseScore",
                table: "ExposureEvents",
                type: "numeric(6,3)",
                precision: 6,
                scale: 3,
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "numeric(6,3)",
                oldPrecision: 6,
                oldScale: 3,
                oldNullable: true);
        }
    }
}
