using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace FrostAura.Reel.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Phase 2.1 — the closed loop's schema, in one migration.
    /// <para><b>Added:</b> <c>ExposureEvents</c> (append-only, one row per card actually served,
    /// idempotent per minute bucket) and <c>UserRatingEvents</c> (append-only rating history), plus
    /// the two derived views <c>ExposureOutcomes</c> and <c>TitleExposureRollups</c>.</para>
    /// <para><b>Dropped:</b> <c>AccountTasteProfiles</c>, <c>Episodes</c> and
    /// <c>ProviderLinkPatterns</c> — three tables no application code reads or writes. A fourth
    /// candidate, <c>PersonAffinities</c>, was deliberately KEPT: it is read live by
    /// <c>CandidateGenerator.TopPeopleTmdbIdsAsync</c> for the person-discover arm, so dropping it
    /// would have removed a working retrieval arm and broken its integration test.</para>
    /// <para><b>No backfill of <c>UserRatingEvents</c>.</b> <c>ObservedAt</c> means "when Reel saw
    /// this change"; stamping ~2,000 historical ratings with a migration timestamp, or with their
    /// own <c>FirstRatedAt</c>, would be a fabricated observation. The ledger starts empty and is
    /// honest from its first row.</para>
    /// </summary>
    public partial class Phase2ExposureLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountTasteProfiles");

            migrationBuilder.DropTable(
                name: "Episodes");

            migrationBuilder.DropTable(
                name: "ProviderLinkPatterns");

            migrationBuilder.CreateTable(
                name: "ExposureEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    TitleId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Surface = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RowKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Rank = table.Column<int>(type: "integer", nullable: false),
                    PageIndex = table.Column<int>(type: "integer", nullable: false),
                    FeedSnapshotId = table.Column<Guid>(type: "uuid", nullable: true),
                    ModelArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    PredictedRating = table.Column<decimal>(type: "numeric(4,2)", precision: 4, scale: 2, nullable: false),
                    BaseScore = table.Column<decimal>(type: "numeric(6,3)", precision: 6, scale: 3, nullable: false),
                    CalibrationDelta = table.Column<decimal>(type: "numeric(6,3)", precision: 6, scale: 3, nullable: false),
                    ResidualApplied = table.Column<decimal>(type: "numeric(6,3)", precision: 6, scale: 3, nullable: false),
                    DemotionApplied = table.Column<decimal>(type: "numeric(6,3)", precision: 6, scale: 3, nullable: false),
                    LlmFit = table.Column<decimal>(type: "numeric(4,3)", precision: 4, scale: 3, nullable: true),
                    WhySentence = table.Column<string>(type: "text", nullable: true),
                    WhySource = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    MinuteBucket = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, computedColumnSql: "date_trunc('minute', \"OccurredAt\", 'UTC')", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExposureEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserRatingEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    TitleId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Season = table.Column<int>(type: "integer", nullable: false),
                    Episode = table.Column<int>(type: "integer", nullable: false),
                    Rating = table.Column<short>(type: "smallint", nullable: false),
                    PreviousRating = table.Column<short>(type: "smallint", nullable: true),
                    RatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ObservedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ChangeKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRatingEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExposureEvents_AccountId_ModelArtifactId",
                table: "ExposureEvents",
                columns: new[] { "AccountId", "ModelArtifactId" });

            migrationBuilder.CreateIndex(
                name: "IX_ExposureEvents_AccountId_OccurredAt",
                table: "ExposureEvents",
                columns: new[] { "AccountId", "OccurredAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_ExposureEvents_AccountId_TitleId_OccurredAt",
                table: "ExposureEvents",
                columns: new[] { "AccountId", "TitleId", "OccurredAt" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_ExposureEvents_AccountId_TitleId_Surface_FeedSnapshotId_Min~",
                table: "ExposureEvents",
                columns: new[] { "AccountId", "TitleId", "Surface", "FeedSnapshotId", "MinuteBucket" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_UserRatingEvents_AccountId_ObservedAt",
                table: "UserRatingEvents",
                columns: new[] { "AccountId", "ObservedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_UserRatingEvents_AccountId_TitleId_ObservedAt",
                table: "UserRatingEvents",
                columns: new[] { "AccountId", "TitleId", "ObservedAt" },
                descending: new[] { false, false, true });

            // ── The two derived views ──────────────────────────────────────────────────────
            // Both are read-only projections over tables that already exist, so they are created
            // here as raw SQL and mapped keyless with ToView(). Nothing is materialised: the volume
            // is hundreds of rows per account, and a materialised copy would introduce exactly the
            // staleness class the live refill in FeedEndpoints already exists to compensate for.
            //
            // EVERY join leg names "AccountId" explicitly. EF's global query filters would supply
            // tenancy for an application read, but a view is also readable from a background job
            // whose account context is not yet pinned, and a cross-tenant join there would have no
            // structural guard at all.
            //
            // Every outcome leg is a LATERAL with LIMIT 1, not a plain LEFT JOIN: a title can carry
            // several revoked reactions and several closed managed-list rows, and a plain join would
            // silently multiply one exposure into several rows — inflating every denominator on the
            // track-record page.
            migrationBuilder.Sql("""
                CREATE VIEW "ExposureOutcomes" AS
                SELECT
                    e."Id"              AS "ExposureId",
                    e."AccountId"       AS "AccountId",
                    e."TitleId"         AS "TitleId",
                    e."Surface"         AS "Surface",
                    e."Rank"            AS "Rank",
                    e."OccurredAt"      AS "OccurredAt",
                    e."PredictedRating" AS "PredictedRating",
                    e."ModelArtifactId" AS "ModelArtifactId",
                    r."Rating"          AS "ActualRating",
                    r."FirstRatedAt"    AS "RatedAt",
                    w."LastWatchedAt"   AS "WatchedAt",
                    sv."CreatedAt"      AS "SavedAt",
                    ni."CreatedAt"      AS "DismissedAt",
                    ml."RemovedAt"      AS "ListClosedAt",
                    ml."RemovalReason"  AS "ListCloseReason",
                    CASE
                        WHEN r."FirstRatedAt" IS NOT NULL OR w."LastWatchedAt" IS NOT NULL THEN 'converted'
                        WHEN sv."CreatedAt"   IS NOT NULL                                  THEN 'saved'
                        WHEN ni."CreatedAt"   IS NOT NULL                                  THEN 'dismissed'
                        ELSE 'pending'
                    END                 AS "Outcome"
                FROM "ExposureEvents" e
                LEFT JOIN LATERAL (
                    SELECT r2."Rating", r2."FirstRatedAt"
                    FROM "UserRatings" r2
                    WHERE r2."AccountId" = e."AccountId"
                      AND r2."TitleId"   = e."TitleId"
                      AND r2."SubjectType" IN ('Movie', 'Show')
                      AND r2."SeasonNumber" = 0
                      AND r2."EpisodeNumber" = 0
                      -- The causal guard. A rating that PREDATES the exposure is not a conversion,
                      -- and FirstRatedAt is used rather than RatedAt because RatedAt is rewritten
                      -- by every Trakt ratings walk and would let an old opinion drift forward.
                      AND r2."FirstRatedAt" > e."OccurredAt"
                    ORDER BY r2."FirstRatedAt"
                    LIMIT 1
                ) r ON TRUE
                LEFT JOIN LATERAL (
                    SELECT w2."LastWatchedAt"
                    FROM "WatchedTitles" w2
                    WHERE w2."AccountId" = e."AccountId"
                      AND w2."TitleId"   = e."TitleId"
                      AND w2."LastWatchedAt" > e."OccurredAt"
                    LIMIT 1
                ) w ON TRUE
                LEFT JOIN LATERAL (
                    SELECT s2."CreatedAt"
                    FROM "UserTitleReactions" s2
                    WHERE s2."AccountId" = e."AccountId"
                      AND s2."TitleId"   = e."TitleId"
                      AND s2."Kind"      = 'SaveForLater'
                      AND s2."RevokedAt" IS NULL
                      AND s2."CreatedAt" > e."OccurredAt"
                    ORDER BY s2."CreatedAt"
                    LIMIT 1
                ) sv ON TRUE
                LEFT JOIN LATERAL (
                    SELECT n2."CreatedAt"
                    FROM "UserTitleReactions" n2
                    WHERE n2."AccountId" = e."AccountId"
                      AND n2."TitleId"   = e."TitleId"
                      AND n2."Kind"      = 'NotInterested'
                      AND n2."RevokedAt" IS NULL
                      AND n2."CreatedAt" > e."OccurredAt"
                    ORDER BY n2."CreatedAt"
                    LIMIT 1
                ) ni ON TRUE
                LEFT JOIN LATERAL (
                    SELECT m2."RemovedAt", m2."RemovalReason"
                    FROM "ManagedListItems" m2
                    WHERE m2."AccountId" = e."AccountId"
                      AND m2."TitleId"   = e."TitleId"
                      AND m2."AddedAt"   > e."OccurredAt"
                    ORDER BY m2."AddedAt"
                    LIMIT 1
                ) ml ON TRUE;
                """);

            // The cheap per-(account, title, surface) shape, correctly derived from the raw events
            // rather than stored as a counter. ShowCount counts DISTINCT minute buckets, not rows,
            // so a page refetch storm cannot inflate it.
            migrationBuilder.Sql("""
                CREATE VIEW "TitleExposureRollups" AS
                SELECT
                    "AccountId"                          AS "AccountId",
                    "TitleId"                            AS "TitleId",
                    "Surface"                            AS "Surface",
                    min("OccurredAt")                    AS "FirstShownAt",
                    max("OccurredAt")                    AS "LastShownAt",
                    count(DISTINCT "MinuteBucket")::int  AS "ShowCount",
                    min("Rank")                          AS "MinRank",
                    min("PredictedRating")               AS "MinPredicted",
                    max("PredictedRating")               AS "MaxPredicted"
                FROM "ExposureEvents"
                GROUP BY "AccountId", "TitleId", "Surface";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Views first — they depend on ExposureEvents.
            migrationBuilder.Sql("DROP VIEW IF EXISTS \"TitleExposureRollups\";");
            migrationBuilder.Sql("DROP VIEW IF EXISTS \"ExposureOutcomes\";");

            migrationBuilder.DropTable(
                name: "ExposureEvents");

            migrationBuilder.DropTable(
                name: "UserRatingEvents");

            migrationBuilder.CreateTable(
                name: "AccountTasteProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LovedCentroid = table.Column<Vector>(type: "vector(1536)", nullable: true),
                    ProfileJson = table.Column<string>(type: "jsonb", nullable: false),
                    RecentCentroid = table.Column<Vector>(type: "vector(1536)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountTasteProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Episodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AiredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EpisodeNumber = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: true),
                    RuntimeMinutes = table.Column<int>(type: "integer", nullable: true),
                    SeasonNumber = table.Column<int>(type: "integer", nullable: false),
                    TitleId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Episodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProviderLinkPatterns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CanaryTitleId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    IsHealthy = table.Column<bool>(type: "boolean", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LastCheckedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastHttpStatus = table.Column<int>(type: "integer", nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    ProviderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Region = table.Column<string>(type: "text", nullable: true),
                    UrlTemplate = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderLinkPatterns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountTasteProfiles_AccountId",
                table: "AccountTasteProfiles",
                column: "AccountId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Episodes_TitleId_SeasonNumber_EpisodeNumber",
                table: "Episodes",
                columns: new[] { "TitleId", "SeasonNumber", "EpisodeNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProviderLinkPatterns_ProviderId_Region",
                table: "ProviderLinkPatterns",
                columns: new[] { "ProviderId", "Region" });
        }
    }
}
