using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FrostAura.Reel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DigestPushBlend : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BlendCode",
                table: "Accounts",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AccountDigests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    WeekStart = table.Column<DateOnly>(type: "date", nullable: false),
                    GeneratedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ItemsJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountDigests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AppSecrets",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ValueProtected = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSecrets", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "PushSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Endpoint = table.Column<string>(type: "text", nullable: false),
                    P256dh = table.Column<string>(type: "text", nullable: false),
                    Auth = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSuccessAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushSubscriptions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_BlendCode",
                table: "Accounts",
                column: "BlendCode",
                unique: true,
                filter: "\"BlendCode\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AccountDigests_AccountId_WeekStart",
                table: "AccountDigests",
                columns: new[] { "AccountId", "WeekStart" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PushSubscriptions_AccountId_Endpoint",
                table: "PushSubscriptions",
                columns: new[] { "AccountId", "Endpoint" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountDigests");

            migrationBuilder.DropTable(
                name: "AppSecrets");

            migrationBuilder.DropTable(
                name: "PushSubscriptions");

            migrationBuilder.DropIndex(
                name: "IX_Accounts_BlendCode",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "BlendCode",
                table: "Accounts");
        }
    }
}
