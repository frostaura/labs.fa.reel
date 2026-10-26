using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace FrostAura.Reel.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// M2 (WS-3, branch-local for testing; the schema steward re-scaffolds it): the retrieval schema
    /// behind Ask Reel's hybrid retriever (program §4.7). Additive only.
    /// <list type="bullet">
    /// <item><c>KeywordLabels</c>: the keyword vocabulary's precomputed label embeddings (HNSW), for the
    /// concept resolver's nearest-label fallback.</item>
    /// <item>A GIN index on <c>Titles.Keywords</c>, so a concept resolved to exact vocabulary labels is an
    /// indexed array overlap (<c>&amp;&amp;</c>) instead of a regex over every title's labels.</item>
    /// <item><c>Titles.SearchDocument</c>: a STORED generated tsvector (name and keywords weighted A,
    /// overview C, the <c>english</c> configuration) with a GIN index, and
    /// <c>TitleAttributes.ThemesDocument</c> (themes weighted B) with its own — the full-text arm.
    /// Generated columns need immutable expressions and <c>array_to_string</c> is only stable, so a
    /// one-line immutable wrapper (<c>reel_labels_text</c>) joins the label arrays.</item>
    /// </list>
    /// Neither generated column is mapped in the EF model: the full-text arm reads them through raw SQL,
    /// so no entity, insert or update statement changes. Adding a STORED generated column rewrites the
    /// table once (38k titles: about a second).
    /// </summary>
    public partial class M2RetrievalVocabularyAndFullText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "KeywordLabels",
                columns: table => new
                {
                    Label = table.Column<string>(type: "text", nullable: false),
                    TitleCount = table.Column<int>(type: "integer", nullable: false),
                    Embedding = table.Column<Vector>(type: "vector(1536)", nullable: true),
                    EmbeddingModel = table.Column<string>(type: "text", nullable: true),
                    CountedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EmbeddedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KeywordLabels", x => x.Label);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KeywordLabels_Embedding",
                table: "KeywordLabels",
                column: "Embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });

            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION reel_labels_text(labels text[]) RETURNS text
                    LANGUAGE sql IMMUTABLE PARALLEL SAFE
                    AS $$ SELECT coalesce(array_to_string(labels, ' '), '') $$;
                """);

            migrationBuilder.Sql("""CREATE INDEX "IX_Titles_Keywords" ON "Titles" USING gin ("Keywords");""");

            migrationBuilder.Sql(
                """
                ALTER TABLE "Titles" ADD COLUMN "SearchDocument" tsvector GENERATED ALWAYS AS (
                    setweight(to_tsvector('english'::regconfig, coalesce("Name", '')), 'A')
                    || setweight(to_tsvector('english'::regconfig, reel_labels_text("Keywords")), 'A')
                    || setweight(to_tsvector('english'::regconfig, coalesce("Overview", '')), 'C')
                ) STORED;
                """);
            migrationBuilder.Sql("""CREATE INDEX "IX_Titles_SearchDocument" ON "Titles" USING gin ("SearchDocument");""");

            migrationBuilder.Sql(
                """
                ALTER TABLE "TitleAttributes" ADD COLUMN "ThemesDocument" tsvector GENERATED ALWAYS AS (
                    setweight(to_tsvector('english'::regconfig, reel_labels_text("Themes")), 'B')
                ) STORED;
                """);
            migrationBuilder.Sql("""CREATE INDEX "IX_TitleAttributes_ThemesDocument" ON "TitleAttributes" USING gin ("ThemesDocument");""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_TitleAttributes_ThemesDocument";""");
            migrationBuilder.Sql("""ALTER TABLE "TitleAttributes" DROP COLUMN IF EXISTS "ThemesDocument";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Titles_SearchDocument";""");
            migrationBuilder.Sql("""ALTER TABLE "Titles" DROP COLUMN IF EXISTS "SearchDocument";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Titles_Keywords";""");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS reel_labels_text(text[]);");

            migrationBuilder.DropTable(
                name: "KeywordLabels");
        }
    }
}
