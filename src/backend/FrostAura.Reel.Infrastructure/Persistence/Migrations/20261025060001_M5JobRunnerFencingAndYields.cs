using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FrostAura.Reel.Infrastructure.Persistence.Migrations
{
    /// <summary>M5 (WS-6): job-runner fencing — SyncJobs.DeadlineYields, Interruptions, RequeuedAt. AttemptCount and Status became concurrency tokens (model only, no DDL). Additive (expand-only); integrator re-scaffold of the lane migrations 20261024163819_JobRunnerDeadlineYields, 20261024191832_JobRunnerInterruptionsAndRequeue.</summary>
    public partial class M5JobRunnerFencingAndYields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DeadlineYields",
                table: "SyncJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Interruptions",
                table: "SyncJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "RequeuedAt",
                table: "SyncJobs",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Interruptions",
                table: "SyncJobs");

            migrationBuilder.DropColumn(
                name: "RequeuedAt",
                table: "SyncJobs");

            migrationBuilder.DropColumn(
                name: "DeadlineYields",
                table: "SyncJobs");
        }
    }
}
