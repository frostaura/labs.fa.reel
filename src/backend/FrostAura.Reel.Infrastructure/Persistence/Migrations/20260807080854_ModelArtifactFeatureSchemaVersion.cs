using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FrostAura.Reel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ModelArtifactFeatureSchemaVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FeatureSchemaVersion",
                table: "ModelArtifacts",
                type: "integer",
                nullable: true,
                computedColumnSql: "((\"FeatureSchemaJson\" ->> 'version'))::integer",
                stored: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FeatureSchemaVersion",
                table: "ModelArtifacts");
        }
    }
}
