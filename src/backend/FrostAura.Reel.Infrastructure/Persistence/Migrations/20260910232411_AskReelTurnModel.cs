using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FrostAura.Reel.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The Ask Reel turn record: the server, not the client, becomes the owner of what a
    /// conversation is.
    ///
    /// <para><b>Additive.</b> Nothing is dropped and no row is deleted. The two narrowing
    /// <c>AlterColumn</c>s (<c>SearchTurns.Role</c> to 16, <c>SearchConversations.Title</c> to 200)
    /// are wider than every value that has ever been written — the roles are "user" and
    /// "assistant", and the endpoint has always truncated a title at 80 — so the scaffolder's
    /// data-loss warning does not apply to any live row.</para>
    ///
    /// <para><b>The three backfills are load-bearing and must run before the indexes.</b>
    /// <c>Sequence</c> lands on the alternation the 90 live rows already have, ordered
    /// <c>("CreatedAt", "Id")</c>: the <c>"Id"</c> tiebreak is not decoration, because a user row
    /// and its assistant row written in one <c>SaveChanges</c> share a timestamp to the microsecond
    /// and an ordering without it is non-deterministic — which the unique
    /// <c>(ConversationId, Sequence)</c> index would then reject at random. That index is created
    /// AFTER the update for the same reason: every existing row arrives at the column default of
    /// 0, and a conversation with two turns would collide on it.</para>
    ///
    /// <para><c>Status</c> and <c>Kind</c> arrive as the empty string EF uses for a non-nullable
    /// text default, which is not a member of either vocabulary. Historical turns are, by
    /// definition, finished asks — a row exists only because a turn completed — so they are
    /// stamped <c>Completed</c> / <c>ask</c>. <c>LastCardsAt</c> is deliberately left NULL on every
    /// historical conversation: NULL is the "transcript only" signal the resume surface needs, and
    /// inventing a timestamp would make those threads render an empty grid instead of saying what
    /// happened.</para>
    ///
    /// <para><c>ExposureEvents.SourceTurnId</c> is forward-only and takes no index and no foreign
    /// key. Retention prunes conversations at 90 days and exposure rows outlive them, so a dangling
    /// id is the designed state, not a defect.</para>
    /// </summary>
    public partial class AskReelTurnModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Role",
                table: "SearchTurns",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<Guid>(
                name: "ClientTurnId",
                table: "SearchTurns",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CompletedAt",
                table: "SearchTurns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DiscoveredCount",
                table: "SearchTurns",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FailureCode",
                table: "SearchTurns",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "FloorRelaxed",
                table: "SearchTurns",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "IntentJson",
                table: "SearchTurns",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "SearchTurns",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "LatencyMs",
                table: "SearchTurns",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MediaFilter",
                table: "SearchTurns",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ModelArtifactId",
                table: "SearchTurns",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModelId",
                table: "SearchTurns",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PromptVersion",
                table: "SearchTurns",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Reason",
                table: "SearchTurns",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Relaxation",
                table: "SearchTurns",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Sequence",
                table: "SearchTurns",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "StageTimingsJson",
                table: "SearchTurns",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "SearchTurns",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AlterColumn<string>(
                name: "Title",
                table: "SearchConversations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<DateTime>(
                name: "LastCardsAt",
                table: "SearchConversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TurnCount",
                table: "SearchConversations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceTurnId",
                table: "ExposureEvents",
                type: "uuid",
                nullable: true);


            // ── Backfills, before the indexes that depend on them ───────────────────────────
            // The sequence the 90 live rows already imply. The ", \"Id\"" tiebreak is load-bearing:
            // a user row and its assistant row share a CreatedAt to the microsecond, and without it
            // the ordering — and therefore the unique index below — is a coin toss.
            migrationBuilder.Sql(
                """
                UPDATE "SearchTurns" AS t
                SET "Sequence" = s.seq
                FROM (
                    SELECT
                        "Id",
                        row_number() OVER (PARTITION BY "ConversationId" ORDER BY "CreatedAt", "Id") - 1 AS seq
                    FROM "SearchTurns"
                ) AS s
                WHERE t."Id" = s."Id";
                """);

            // A row exists only because a turn finished, so every historical turn is a completed ask.
            migrationBuilder.Sql("""UPDATE "SearchTurns" SET "Status" = 'Completed' WHERE "Status" = '';""");
            migrationBuilder.Sql("""UPDATE "SearchTurns" SET "Kind" = 'ask' WHERE "Kind" = '';""");

            // The denormalised count. Conversations with no turns keep the column default of 0.
            migrationBuilder.Sql(
                """
                UPDATE "SearchConversations" AS c
                SET "TurnCount" = s.n
                FROM (
                    SELECT "ConversationId", COUNT(*) AS n FROM "SearchTurns" GROUP BY "ConversationId"
                ) AS s
                WHERE c."Id" = s."ConversationId";
                """);

            migrationBuilder.CreateTable(
                name: "SearchTurnCards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TurnId = table.Column<Guid>(type: "uuid", nullable: false),
                    TitleId = table.Column<Guid>(type: "uuid", nullable: false),
                    TmdbId = table.Column<long>(type: "bigint", nullable: true),
                    Rank = table.Column<int>(type: "integer", nullable: false),
                    OutsideRequest = table.Column<bool>(type: "boolean", nullable: false),
                    Similarity = table.Column<double>(type: "double precision", nullable: true),
                    PredictedRating = table.Column<decimal>(type: "numeric(4,2)", precision: 4, scale: 2, nullable: true),
                    LlmFit = table.Column<decimal>(type: "numeric(4,3)", precision: 4, scale: 3, nullable: true),
                    Blend = table.Column<decimal>(type: "numeric(6,4)", precision: 6, scale: 4, nullable: false),
                    Why = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    WhySource = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SearchTurnCards", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SearchTurns_AccountId",
                table: "SearchTurns",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SearchTurns_ConversationId_ClientTurnId",
                table: "SearchTurns",
                columns: new[] { "ConversationId", "ClientTurnId" },
                unique: true,
                filter: "\"ClientTurnId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SearchTurns_ConversationId_Sequence",
                table: "SearchTurns",
                columns: new[] { "ConversationId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SearchTurnCards_AccountId",
                table: "SearchTurnCards",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SearchTurnCards_ConversationId_TmdbId",
                table: "SearchTurnCards",
                columns: new[] { "ConversationId", "TmdbId" });

            migrationBuilder.CreateIndex(
                name: "IX_SearchTurnCards_TurnId_Rank",
                table: "SearchTurnCards",
                columns: new[] { "TurnId", "Rank" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SearchTurnCards_TurnId_TitleId",
                table: "SearchTurnCards",
                columns: new[] { "TurnId", "TitleId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SearchTurnCards");

            migrationBuilder.DropIndex(
                name: "IX_SearchTurns_AccountId",
                table: "SearchTurns");

            migrationBuilder.DropIndex(
                name: "IX_SearchTurns_ConversationId_ClientTurnId",
                table: "SearchTurns");

            migrationBuilder.DropIndex(
                name: "IX_SearchTurns_ConversationId_Sequence",
                table: "SearchTurns");

            migrationBuilder.DropColumn(
                name: "ClientTurnId",
                table: "SearchTurns");

            migrationBuilder.DropColumn(
                name: "CompletedAt",
                table: "SearchTurns");

            migrationBuilder.DropColumn(
                name: "DiscoveredCount",
                table: "SearchTurns");

            migrationBuilder.DropColumn(
                name: "FailureCode",
                table: "SearchTurns");

            migrationBuilder.DropColumn(
                name: "FloorRelaxed",
                table: "SearchTurns");

            migrationBuilder.DropColumn(
                name: "IntentJson",
                table: "SearchTurns");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "SearchTurns");

            migrationBuilder.DropColumn(
                name: "LatencyMs",
                table: "SearchTurns");

            migrationBuilder.DropColumn(
                name: "MediaFilter",
                table: "SearchTurns");

            migrationBuilder.DropColumn(
                name: "ModelArtifactId",
                table: "SearchTurns");

            migrationBuilder.DropColumn(
                name: "ModelId",
                table: "SearchTurns");

            migrationBuilder.DropColumn(
                name: "PromptVersion",
                table: "SearchTurns");

            migrationBuilder.DropColumn(
                name: "Reason",
                table: "SearchTurns");

            migrationBuilder.DropColumn(
                name: "Relaxation",
                table: "SearchTurns");

            migrationBuilder.DropColumn(
                name: "Sequence",
                table: "SearchTurns");

            migrationBuilder.DropColumn(
                name: "StageTimingsJson",
                table: "SearchTurns");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "SearchTurns");

            migrationBuilder.DropColumn(
                name: "LastCardsAt",
                table: "SearchConversations");

            migrationBuilder.DropColumn(
                name: "TurnCount",
                table: "SearchConversations");

            migrationBuilder.DropColumn(
                name: "SourceTurnId",
                table: "ExposureEvents");

            migrationBuilder.AlterColumn<string>(
                name: "Role",
                table: "SearchTurns",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16);

            migrationBuilder.AlterColumn<string>(
                name: "Title",
                table: "SearchConversations",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);
        }
    }
}
