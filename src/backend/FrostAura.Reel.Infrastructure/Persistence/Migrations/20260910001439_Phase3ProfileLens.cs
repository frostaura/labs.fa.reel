using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FrostAura.Reel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase3ProfileLens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountTasteNarratives",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    ProfileHash = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ProfileFingerprint = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    EditedText = table.Column<string>(type: "text", nullable: true),
                    FactsJson = table.Column<string>(type: "jsonb", nullable: false),
                    RatingsCount = table.Column<int>(type: "integer", nullable: false),
                    ModelId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PromptVersion = table.Column<int>(type: "integer", nullable: false),
                    PromptTokens = table.Column<long>(type: "bigint", nullable: true),
                    CompletionTokens = table.Column<long>(type: "bigint", nullable: true),
                    GeneratedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EditedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountTasteNarratives", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TitleLensCopies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    TitleId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileHash = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PromptVersion = table.Column<int>(type: "integer", nullable: false),
                    LensJson = table.Column<string>(type: "jsonb", nullable: false),
                    ModelId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PromptTokens = table.Column<long>(type: "bigint", nullable: true),
                    CompletionTokens = table.Column<long>(type: "bigint", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RejectionReason = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    GeneratedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TitleLensCopies", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountTasteNarratives_AccountId_Version",
                table: "AccountTasteNarratives",
                columns: new[] { "AccountId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TitleLensCopies_AccountId_TitleId_GeneratedAt",
                table: "TitleLensCopies",
                columns: new[] { "AccountId", "TitleId", "GeneratedAt" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_TitleLensCopies_AccountId_TitleId_ProfileHash_PromptVersion",
                table: "TitleLensCopies",
                columns: new[] { "AccountId", "TitleId", "ProfileHash", "PromptVersion" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountTasteNarratives");

            migrationBuilder.DropTable(
                name: "TitleLensCopies");
        }
    }
}
