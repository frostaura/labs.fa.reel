using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FrostAura.Reel.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Phase 0 item 0.3 — the evaluation instrument.
    /// <para><c>UserRatings.FirstRatedAt</c> is the immutable split axis for <c>EvalHarness</c> and
    /// the drift axis for the nightly retrain gate. It is added NULLABLE, backfilled from the
    /// current <c>RatedAt</c>, and only then made NOT NULL — the default the scaffolder wanted
    /// (<c>0001-01-01</c>) would have put ~2,000 historical ratings at the dawn of time, collapsing
    /// the 80% split index into a single tie block and zeroing the holdout. Backfilling from
    /// <c>RatedAt</c> preserves the real chronology, so iteration 13's train/holdout counts stay
    /// comparable with iteration 12's.</para>
    /// <para>The <c>EvaluationResults</c> columns are all nullable because rows written before this
    /// migration genuinely do not have those measurements, and a zero would read as one.
    /// <c>PassedLegacyGate</c> is the exception — a bool, defaulting false, carrying the retired
    /// precision@10 verdict forward so the twelve historical ledger rows stay readable.</para>
    /// </summary>
    public partial class Phase0EvalInstrument : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FirstRatedAt",
                table: "UserRatings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql(@"UPDATE ""UserRatings"" SET ""FirstRatedAt"" = ""RatedAt"" WHERE ""FirstRatedAt"" IS NULL;");

            migrationBuilder.AlterColumn<DateTime>(
                name: "FirstRatedAt",
                table: "UserRatings",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CalibrationBias",
                table: "EvaluationResults",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Ece",
                table: "EvaluationResults",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EligibleHoldoutCount",
                table: "EvaluationResults",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "NdcgAt10Baseline",
                table: "EvaluationResults",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "NdcgAt10Model",
                table: "EvaluationResults",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "NdcgLiftCiHigh",
                table: "EvaluationResults",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "NdcgLiftCiLow",
                table: "EvaluationResults",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginsJson",
                table: "EvaluationResults",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PassedLegacyGate",
                table: "EvaluationResults",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "SpearmanSe",
                table: "EvaluationResults",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UnfilteredHoldoutCount",
                table: "EvaluationResults",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserRatings_AccountId_FirstRatedAt",
                table: "UserRatings",
                columns: new[] { "AccountId", "FirstRatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserRatings_AccountId_FirstRatedAt",
                table: "UserRatings");

            migrationBuilder.DropColumn(
                name: "FirstRatedAt",
                table: "UserRatings");

            migrationBuilder.DropColumn(
                name: "CalibrationBias",
                table: "EvaluationResults");

            migrationBuilder.DropColumn(
                name: "Ece",
                table: "EvaluationResults");

            migrationBuilder.DropColumn(
                name: "EligibleHoldoutCount",
                table: "EvaluationResults");

            migrationBuilder.DropColumn(
                name: "NdcgAt10Baseline",
                table: "EvaluationResults");

            migrationBuilder.DropColumn(
                name: "NdcgAt10Model",
                table: "EvaluationResults");

            migrationBuilder.DropColumn(
                name: "NdcgLiftCiHigh",
                table: "EvaluationResults");

            migrationBuilder.DropColumn(
                name: "NdcgLiftCiLow",
                table: "EvaluationResults");

            migrationBuilder.DropColumn(
                name: "OriginsJson",
                table: "EvaluationResults");

            migrationBuilder.DropColumn(
                name: "PassedLegacyGate",
                table: "EvaluationResults");

            migrationBuilder.DropColumn(
                name: "SpearmanSe",
                table: "EvaluationResults");

            migrationBuilder.DropColumn(
                name: "UnfilteredHoldoutCount",
                table: "EvaluationResults");
        }
    }
}
