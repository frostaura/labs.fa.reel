using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FrostAura.Reel.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The tray joins the record: a card the account's rating floor pushed under the fence becomes
    /// a stored <c>SearchTurnCard</c>, flagged.
    ///
    /// <para><b>Why.</b> The founder-locked rule is <i>never show things I've already seen</i>, and
    /// the no-repeat set is <c>SELECT DISTINCT "TmdbId"</c> over the conversation's cards. Below-floor
    /// cards were emitted on the wire and then forgotten, so that query could not contain them — and
    /// an end-to-end smoke run watched 13 of one turn's 14 tray titles come straight back as the
    /// NEXT turn's top picks, visible twice on a single screen. The fix is storing what was shown,
    /// not a second exclusion list.</para>
    ///
    /// <para><b>Additive, and the only kind of change this can be.</b> One column, non-nullable
    /// with a database default of <c>false</c>, so every existing row is a pick — which is what
    /// every existing row is, since the tray was never written. Nothing is dropped, nothing is
    /// back-filled, and <c>Down</c> is a clean <c>DropColumn</c>: the column carries no data that
    /// exists nowhere else, because a tray card removed from this table is simply a tray card the
    /// no-repeat set stops knowing about.</para>
    ///
    /// <para><b>No new index.</b> The read that matters — the no-repeat set — deliberately does NOT
    /// filter on this column, since a tray card was on the glass and counts as shown; the existing
    /// <c>(ConversationId, TmdbId)</c> index still serves it whole. The replay partitions in memory
    /// over rows it has already loaded by <c>(TurnId, Rank)</c>, so it wants no index either. The
    /// rank counter continues across both blocks rather than restarting, which is what keeps the
    /// unique <c>(TurnId, Rank)</c> from colliding on every turn that has a tray at all.</para>
    /// </summary>
    public partial class AskReelTrayCards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "BelowFloor",
                table: "SearchTurnCards",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BelowFloor",
                table: "SearchTurnCards");
        }
    }
}
