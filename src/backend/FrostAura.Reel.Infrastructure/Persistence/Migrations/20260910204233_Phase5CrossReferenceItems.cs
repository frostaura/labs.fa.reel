using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FrostAura.Reel.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The instrument gets a memory: one row per judged holdout title, and twelve nullable
    /// discrimination columns on the evaluation rows that summarise them.
    ///
    /// <para><b>What forced it.</b> The 2026-09-10 live run — prompt v1, gpt-5.4-mini, five origins,
    /// 1,305 judged titles, 25 calls — reported a false-rule-out rate of 12.0% against a
    /// true-rule-out rate of 10.1%. Those two numbers being equal is the finding: the round-1
    /// <c>out</c> verdict carried no information about the outcome on this account's history. The
    /// gate could not have caught that, because each of its three clauses reads ONE rate in
    /// isolation and "12% is under the 15% ceiling" is a true sentence about a coin. And the run
    /// persisted only aggregates, so the immediate follow-up — does <c>keep</c> carry signal even
    /// though <c>out</c> does not? — could not be answered without repeating the whole paid run.
    /// </para>
    ///
    /// <para><b><c>CrossReferenceEvaluationItems</c> is evidence, not state.</b> Nothing on a
    /// serving path reads it; it is written once per backtest origin and never updated. Its index is
    /// (AccountId, EvaluationId, TitleId) because the only read is "one origin's items, paged, in a
    /// stable order" and TitleId in the index is what stops a page boundary moving between two
    /// requests for the same page. It is deliberately NOT keyed on the taste fingerprint — that is
    /// <c>TitleCrossReferenceVerdicts</c>' business, and these rows describe a past instant that no
    /// fingerprint move can invalidate.</para>
    ///
    /// <para><b>The twelve new columns are nullable, and every one of them is null on a row written
    /// before today.</b> A zero is a claim: a row from before these existed did not measure an AUC
    /// of 0.5, it measured no AUC. <c>SpearmanConfidenceVsRating</c> is the one that is signed
    /// (numeric(7,4)), because a negative rank correlation means the judge orders titles backwards,
    /// which is a finding rather than an error.</para>
    ///
    /// <para><c>Down()</c> drops the table and all twelve columns.</para>
    /// </summary>
    public partial class Phase5CrossReferenceItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AucKeepVsOut",
                table: "CrossReferenceEvaluations",
                type: "numeric(6,4)",
                precision: 6,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "KeepCount",
                table: "CrossReferenceEvaluations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "KeepDislikedRate",
                table: "CrossReferenceEvaluations",
                type: "numeric(6,4)",
                precision: 6,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "KeepLovedRate",
                table: "CrossReferenceEvaluations",
                type: "numeric(6,4)",
                precision: 6,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MeanLaterRatingKeep",
                table: "CrossReferenceEvaluations",
                type: "numeric(6,4)",
                precision: 6,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MeanLaterRatingOut",
                table: "CrossReferenceEvaluations",
                type: "numeric(6,4)",
                precision: 6,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MeanLaterRatingUnsure",
                table: "CrossReferenceEvaluations",
                type: "numeric(6,4)",
                precision: 6,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OutCount",
                table: "CrossReferenceEvaluations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OutDislikedRate",
                table: "CrossReferenceEvaluations",
                type: "numeric(6,4)",
                precision: 6,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OutLovedRate",
                table: "CrossReferenceEvaluations",
                type: "numeric(6,4)",
                precision: 6,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SpearmanConfidenceVsRating",
                table: "CrossReferenceEvaluations",
                type: "numeric(7,4)",
                precision: 7,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UnsureCount",
                table: "CrossReferenceEvaluations",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CrossReferenceEvaluationItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    EvaluationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TitleId = table.Column<Guid>(type: "uuid", nullable: false),
                    SplitFraction = table.Column<double>(type: "double precision", nullable: false),
                    Verdict = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ReasonCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Confidence = table.Column<decimal>(type: "numeric(4,3)", precision: 4, scale: 3, nullable: false),
                    LaterRating = table.Column<short>(type: "smallint", nullable: false),
                    NeighbourIdsJson = table.Column<string>(type: "jsonb", nullable: false),
                    BatchIndex = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrossReferenceEvaluationItems", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CrossReferenceEvaluationItems_AccountId_EvaluationId_TitleId",
                table: "CrossReferenceEvaluationItems",
                columns: new[] { "AccountId", "EvaluationId", "TitleId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CrossReferenceEvaluationItems");

            migrationBuilder.DropColumn(
                name: "AucKeepVsOut",
                table: "CrossReferenceEvaluations");

            migrationBuilder.DropColumn(
                name: "KeepCount",
                table: "CrossReferenceEvaluations");

            migrationBuilder.DropColumn(
                name: "KeepDislikedRate",
                table: "CrossReferenceEvaluations");

            migrationBuilder.DropColumn(
                name: "KeepLovedRate",
                table: "CrossReferenceEvaluations");

            migrationBuilder.DropColumn(
                name: "MeanLaterRatingKeep",
                table: "CrossReferenceEvaluations");

            migrationBuilder.DropColumn(
                name: "MeanLaterRatingOut",
                table: "CrossReferenceEvaluations");

            migrationBuilder.DropColumn(
                name: "MeanLaterRatingUnsure",
                table: "CrossReferenceEvaluations");

            migrationBuilder.DropColumn(
                name: "OutCount",
                table: "CrossReferenceEvaluations");

            migrationBuilder.DropColumn(
                name: "OutDislikedRate",
                table: "CrossReferenceEvaluations");

            migrationBuilder.DropColumn(
                name: "OutLovedRate",
                table: "CrossReferenceEvaluations");

            migrationBuilder.DropColumn(
                name: "SpearmanConfidenceVsRating",
                table: "CrossReferenceEvaluations");

            migrationBuilder.DropColumn(
                name: "UnsureCount",
                table: "CrossReferenceEvaluations");
        }
    }
}
