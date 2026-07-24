using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FrostAura.Reel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOnlineModelState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OnlineModelStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    BaseArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    Weights = table.Column<byte[]>(type: "bytea", nullable: false),
                    Bias = table.Column<double>(type: "double precision", nullable: false),
                    UpdateCount = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnlineModelStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OnlineModelStates_AccountId",
                table: "OnlineModelStates",
                column: "AccountId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OnlineModelStates");
        }
    }
}
