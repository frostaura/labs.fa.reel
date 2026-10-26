using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FrostAura.Reel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M4bEvalLedgerAndCalibrationPin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "ProbeBaseMae",
                table: "OnlineModelStates",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ProbeMae",
                table: "OnlineModelStates",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RejectedUpdateCount",
                table: "OnlineModelStates",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "CalibratorJson",
                table: "ModelArtifacts",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CalibratorPinnedAt",
                table: "ModelArtifacts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OffsetsJson",
                table: "FeedBuildStats",
                type: "jsonb",
                nullable: true);

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
                name: "ProbeBaseMae",
                table: "OnlineModelStates");

            migrationBuilder.DropColumn(
                name: "ProbeMae",
                table: "OnlineModelStates");

            migrationBuilder.DropColumn(
                name: "RejectedUpdateCount",
                table: "OnlineModelStates");

            migrationBuilder.DropColumn(
                name: "CalibratorJson",
                table: "ModelArtifacts");

            migrationBuilder.DropColumn(
                name: "CalibratorPinnedAt",
                table: "ModelArtifacts");

            migrationBuilder.DropColumn(
                name: "OffsetsJson",
                table: "FeedBuildStats");

            migrationBuilder.DropColumn(
                name: "BaselinesJson",
                table: "EvaluationResults");

            migrationBuilder.DropColumn(
                name: "FeatureSetHash",
                table: "EvaluationResults");
        }
    }
}
