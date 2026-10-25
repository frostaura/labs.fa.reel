using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FrostAura.Reel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExposureBreakdownNullable : Migration
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
