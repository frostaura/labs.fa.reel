using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FrostAura.Reel.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Two nullable columns on the append-only exposure log, and nothing else.
    ///
    /// <para>Nullable is the whole design. The interleaving experiment is off by default, so almost
    /// every row ever written will carry NULL in both — and that NULL means something exact: no
    /// ranker drafted this card. The live serve paths (the feed refill, pagination, the digest, Ask
    /// Reel) have no slate to belong to and correctly write nothing here, so a NOT NULL column with
    /// a sentinel would have manufactured attribution that never existed.</para>
    ///
    /// <para>No backfill, deliberately. Every row that predates this migration was served before
    /// any policy existed; stamping them with the calibrated model's id would be inventing an
    /// experiment retroactively, and the sequential test would count them as evidence.</para>
    ///
    /// <para>The index is partial. On a log whose overwhelming majority carries NULL, a full index
    /// on (AccountId, InterleaveGroup) would pay for rows the interleaving read never visits.</para>
    /// </summary>
    public partial class Phase4Interleaving : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "InterleaveGroup",
                table: "ExposureEvents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PolicyId",
                table: "ExposureEvents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExposureEvents_AccountId_InterleaveGroup",
                table: "ExposureEvents",
                columns: new[] { "AccountId", "InterleaveGroup" },
                filter: "\"InterleaveGroup\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExposureEvents_AccountId_InterleaveGroup",
                table: "ExposureEvents");

            migrationBuilder.DropColumn(
                name: "InterleaveGroup",
                table: "ExposureEvents");

            migrationBuilder.DropColumn(
                name: "PolicyId",
                table: "ExposureEvents");
        }
    }
}
