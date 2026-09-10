using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FrostAura.Reel.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The cross-reference instrument: two tables and two nullable columns. Nothing user-visible
    /// ships with it — no job, no feed integration, no UI — because the whole point of the ordering
    /// is that a negative result is cheap and attributable.
    ///
    /// <para><b><c>TitleCrossReferenceVerdicts</c>.</b> Unique on
    /// (AccountId, TitleId, ProfileHash, PromptVersion, Round). <c>Round</c> is IN the key because
    /// the aggregation rule counts observations — "two keeps and no out" — and a round-agnostic key
    /// would make the second pass an overwrite of the first, leaving nothing to count.
    /// <c>ModelArtifactId</c> is a plain nullable column and deliberately NOT part of the key, for
    /// the reason <c>TasteProfileHash</c> already records for the lens cache: the founder retrains
    /// most weeks, and an artifact in the key empties the whole store every retrain night.</para>
    ///
    /// <para><b><c>IX_…_Sweep</c> is the job's resume state, and there is no cursor.</b> "What is
    /// still unjudged at the current key" is an anti-join over that index: correct after a crash,
    /// after a budget stop, after a partial batch and after a concurrent run — none of which a
    /// stored cursor survives, and all of which it would have to be invalidated for. The third index
    /// (AccountId, TitleId, GeneratedAt desc) is the stale ladder's read: a verdict at a superseded
    /// fingerprint is half-strength evidence and is re-judged first, never deleted.</para>
    ///
    /// <para><b><c>CrossReferenceEvaluations</c> is deliberately not <c>EvaluationResult</c>.</b>
    /// That table is the M2 modelling ledger; its iteration counter IS the kill criterion and its
    /// rows are comparable across sixteen iterations of one estimand. Writing a judge's rule-out
    /// rate into it would make the kill-criterion count meaningless. <c>Caveat</c> is persisted
    /// rather than rendered so the number and the reason to be careful with it travel together.</para>
    ///
    /// <para><b>The two <c>ExposureEvents</c> columns are nullable and are never back-filled.</b>
    /// NULL means "no verdict existed when this card was served", which is a different fact from
    /// <c>Unsure</c> — one is nothing had judged it, the other is something judged it and could not
    /// tell. Back-filling would make the append-only log claim the product knew something at a
    /// moment when it did not, which is the one thing that table is for.</para>
    /// </summary>
    public partial class Phase5CrossReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "XrefConfidence",
                table: "ExposureEvents",
                type: "numeric(4,3)",
                precision: 4,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "XrefVerdict",
                table: "ExposureEvents",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CrossReferenceEvaluations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    SplitFraction = table.Column<double>(type: "double precision", nullable: true),
                    SplitAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    HoldoutJudged = table.Column<int>(type: "integer", nullable: false),
                    LovedCount = table.Column<int>(type: "integer", nullable: false),
                    DislikedCount = table.Column<int>(type: "integer", nullable: false),
                    FalseRuleOuts = table.Column<int>(type: "integer", nullable: false),
                    TrueRuleOuts = table.Column<int>(type: "integer", nullable: false),
                    FalseRuleOutRate = table.Column<decimal>(type: "numeric(6,4)", precision: 6, scale: 4, nullable: false),
                    TrueRuleOutRate = table.Column<decimal>(type: "numeric(6,4)", precision: 6, scale: 4, nullable: false),
                    WilsonLow = table.Column<decimal>(type: "numeric(6,4)", precision: 6, scale: 4, nullable: false),
                    WilsonHigh = table.Column<decimal>(type: "numeric(6,4)", precision: 6, scale: 4, nullable: false),
                    UnsureRate = table.Column<decimal>(type: "numeric(6,4)", precision: 6, scale: 4, nullable: false),
                    NoEmbeddingCount = table.Column<int>(type: "integer", nullable: false),
                    GatingDecision = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Caveat = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    ModelId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PromptVersion = table.Column<int>(type: "integer", nullable: false),
                    PromptTokens = table.Column<long>(type: "bigint", nullable: false),
                    CompletionTokens = table.Column<long>(type: "bigint", nullable: false),
                    EvaluatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrossReferenceEvaluations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TitleCrossReferenceVerdicts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    TitleId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileHash = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PromptVersion = table.Column<int>(type: "integer", nullable: false),
                    Round = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Verdict = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ReasonCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Confidence = table.Column<decimal>(type: "numeric(4,3)", precision: 4, scale: 3, nullable: false),
                    NeighbourIdsJson = table.Column<string>(type: "jsonb", nullable: false),
                    ModelId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ModelArtifactId = table.Column<Guid>(type: "uuid", nullable: true),
                    BatchIndex = table.Column<int>(type: "integer", nullable: false),
                    GeneratedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TitleCrossReferenceVerdicts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CrossReferenceEvaluations_AccountId_EvaluatedAt",
                table: "CrossReferenceEvaluations",
                columns: new[] { "AccountId", "EvaluatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_CrossReferenceEvaluations_AccountId_RunId",
                table: "CrossReferenceEvaluations",
                columns: new[] { "AccountId", "RunId" });

            migrationBuilder.CreateIndex(
                name: "IX_TitleCrossReferenceVerdicts_AccountId_TitleId_GeneratedAt",
                table: "TitleCrossReferenceVerdicts",
                columns: new[] { "AccountId", "TitleId", "GeneratedAt" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_TitleCrossReferenceVerdicts_Key",
                table: "TitleCrossReferenceVerdicts",
                columns: new[] { "AccountId", "TitleId", "ProfileHash", "PromptVersion", "Round" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TitleCrossReferenceVerdicts_Sweep",
                table: "TitleCrossReferenceVerdicts",
                columns: new[] { "AccountId", "ProfileHash", "PromptVersion", "Round", "Verdict" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CrossReferenceEvaluations");

            migrationBuilder.DropTable(
                name: "TitleCrossReferenceVerdicts");

            migrationBuilder.DropColumn(
                name: "XrefConfidence",
                table: "ExposureEvents");

            migrationBuilder.DropColumn(
                name: "XrefVerdict",
                table: "ExposureEvents");
        }
    }
}
