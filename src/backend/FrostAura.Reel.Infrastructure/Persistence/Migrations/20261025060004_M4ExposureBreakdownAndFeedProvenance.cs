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
            // Rollback is schema-only and never touches a row of the append-only exposure log
            // (program §5.0 rule 8). Once this build has recorded a breakdown as unknown (NULL),
            // the old NOT NULL cannot be restored without rewriting those rows into the false 0
            // this migration exists to stop; so the rollback refuses, before it changes anything,
            // and the way back to a pre-M4 image is the pre-migration dump. A database with no
            // NULL breakdowns yet (nothing written since the upgrade) still rolls back in place.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM "ExposureEvents"
                        WHERE "BaseScore" IS NULL OR "CalibrationDelta" IS NULL
                           OR "ResidualApplied" IS NULL OR "DemotionApplied" IS NULL) THEN
                        RAISE EXCEPTION USING
                            MESSAGE = 'M4 cannot be rolled back in place: ExposureEvents holds breakdowns recorded as unknown, and the exposure log is append-only.',
                            HINT = 'Restore the pre-migration dump instead (program section 5.0 rule 8).';
                    END IF;
                END
                $$;
                """);

            migrationBuilder.DropColumn(
                name: "EvidenceJson",
                table: "FeedItems");

            migrationBuilder.DropColumn(
                name: "WhySource",
                table: "FeedItems");

            // Back to exactly what Phase2ExposureLog created: NOT NULL with no default.
            foreach (var column in new[] { "ResidualApplied", "DemotionApplied", "CalibrationDelta", "BaseScore" })
            {
                migrationBuilder.AlterColumn<decimal>(
                    name: column,
                    table: "ExposureEvents",
                    type: "numeric(6,3)",
                    precision: 6,
                    scale: 3,
                    nullable: false,
                    oldClrType: typeof(decimal),
                    oldType: "numeric(6,3)",
                    oldPrecision: 6,
                    oldScale: 3,
                    oldNullable: true);
            }
        }
    }
}
