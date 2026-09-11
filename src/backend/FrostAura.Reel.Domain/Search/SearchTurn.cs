using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Domain.Search;

/// <summary>
/// The closed vocabulary for <see cref="SearchTurn.Status"/>. Constant strings rather than an enum
/// for the same reason <c>ExposureWhySource</c> is: the value is read straight out of a row by
/// anyone debugging a stuck conversation, and it rides a partial index filter in SQL where a C#
/// enum name would have to be spelled by hand anyway.
///
/// <para>The three states are a lifecycle, not a taxonomy. A turn is written
/// <see cref="Pending"/> BEFORE the stream opens, so a crash mid-stream leaves a row that says so;
/// it becomes <see cref="Completed"/> only once the answer has been emitted and its cards stored,
/// and <see cref="Failed"/> when the pipeline said it failed or the client went away. There is no
/// fourth state and no "unknown": a row with no status is a row nothing wrote, which cannot
/// happen.</para>
/// </summary>
public static class SearchTurnStatus
{
    /// <summary>Written before the stream opens; the answer is still being produced.</summary>
    public const string Pending = "Pending";

    /// <summary>The turn answered and its cards are stored. The only status a replay serves.</summary>
    public const string Completed = "Completed";

    /// <summary>The turn ended without an answer. <see cref="SearchTurn.FailureCode"/> says how.</summary>
    public const string Failed = "Failed";
}

/// <summary>
/// The closed vocabulary for <see cref="SearchTurn.Kind"/>. Server-derived, never client-supplied:
/// the client cannot be trusted to say whether a request is a new question or another page of the
/// last one, because a filter toggle and an impatient re-tap look identical from its side.
/// </summary>
public static class SearchTurnKind
{
    /// <summary>A new question. Writes a user row and an assistant row.</summary>
    public const string Ask = "ask";

    /// <summary>Another page of the conversation's last question. Writes an assistant row only.</summary>
    public const string Page = "page";

    /// <summary>
    /// The derivation, stated once. A request whose question is the conversation's last question
    /// again is another PAGE of it; anything else is a new ask.
    ///
    /// <para>Trimmed and case-insensitive, because the same question re-sent by a filter toggle or
    /// an impatient tap is the same question — and ordinal rather than culture-aware, because two
    /// strings that are byte-equal must compare equal on every machine this runs on. A conversation
    /// with no user turn yet has nothing to be a page OF.</para>
    /// </summary>
    public static string Derive(string? lastUserText, string message) =>
        lastUserText is not null
        && string.Equals(lastUserText.Trim(), message.Trim(), StringComparison.OrdinalIgnoreCase)
            ? Page
            : Ask;
}

/// <summary>
/// One turn in a persisted <see cref="SearchConversation"/> — a user message or Reel's reply.
///
/// <para><b>Two rows per exchange, and the assistant row is the answer row.</b> It owns
/// <see cref="Status"/>, <see cref="ClientTurnId"/>, <see cref="IntentJson"/>,
/// <see cref="ModelArtifactId"/> and the cards; the user row is the question, verbatim, and
/// carries none of that. The split is what lets the 90 historical rows keep working — the
/// <see cref="Sequence"/> backfill lands on the alternation that is already there — and it is what
/// makes a <see cref="SearchTurnKind.Page"/> expressible at all: a page adds an assistant row to a
/// question that was already asked.</para>
///
/// <para><b><see cref="Sequence"/> is the order, not <see cref="CreatedAt"/>.</b> Two rows written
/// inside one <c>SaveChanges</c> share a timestamp to the microsecond and a resumed transcript
/// ordered by time can put the reply before the question. The unique
/// <c>(ConversationId, Sequence)</c> index is also the concurrency backstop: two turns racing for
/// one conversation collide there rather than both landing.</para>
/// </summary>
public class SearchTurn : IAccountScoped
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    public Guid ConversationId { get; set; }

    /// <summary>"user" or "assistant".</summary>
    public string Role { get; set; } = "user";

    public string Text { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Position within the conversation, zero-based and gapless. The ONLY ordering key: see the
    /// class remarks for why <see cref="CreatedAt"/> is not one.
    /// </summary>
    public int Sequence { get; set; }

    /// <summary>
    /// The client's id for the exchange this row answers — assistant rows only, null on user rows
    /// and on every row written before the turn record existed.
    ///
    /// <para>It is the idempotency key. A dropped connection, a StrictMode double-invoke and an
    /// impatient second tap all resend the same id, and the endpoint answers all three from the
    /// stored turn instead of buying a second one. The partial unique index
    /// <c>(ConversationId, ClientTurnId) WHERE "ClientTurnId" IS NOT NULL</c> is what makes that a
    /// database guarantee rather than a check-then-act.</para>
    /// </summary>
    public Guid? ClientTurnId { get; set; }

    /// <summary>One of <see cref="SearchTurnKind"/>. Server-derived; see that type.</summary>
    public string Kind { get; set; } = SearchTurnKind.Ask;

    /// <summary>One of <see cref="SearchTurnStatus"/>. Historical rows are <c>Completed</c>.</summary>
    public string Status { get; set; } = SearchTurnStatus.Completed;

    /// <summary>
    /// Why the turn failed, when it did: <c>client_aborted</c> for a cancelled stream, otherwise
    /// the pipeline's own code or the exception type. Null on every other status. A failed turn is
    /// a recoverable record — the client can show what happened and offer a retry — which is the
    /// whole reason a failure is persisted rather than left as a missing row.
    /// </summary>
    public string? FailureCode { get; set; }

    /// <summary>The interpreted <c>SearchIntent</c> as stored JSON (jsonb). Assistant rows only.</summary>
    public string? IntentJson { get; set; }

    /// <summary>
    /// The <c>conflict</c> frame this turn emitted, as stored JSON (jsonb) — <c>excludedGenres</c>,
    /// <c>excludedKeywords</c>, the year bounds and the <c>message</c>. Null on a turn that had no
    /// collision, which is most of them.
    ///
    /// <para><b>It is stored because it is the answer's explanation, and an explanation that does
    /// not survive a reload is not one.</b> The notice was a stream-only frame: ask for a 90s
    /// romantic comedy against standing exclusions on both genres, reload, and the thread comes back
    /// as four post-2000 dramas under that question with nothing left on the page saying why. The
    /// cards are restored from rows and the reason line is restored from a column; the one sentence
    /// that accounts for the distance between the question and the answer was the only part of the
    /// turn the server forgot.</para>
    ///
    /// <para>Stored rather than recomputed on read, for the same reason <see cref="Reason"/> is: the
    /// account's conditions can change between the turn and the resume, and a notice re-derived from
    /// today's rules would describe an answer that was produced under yesterday's. The transcript
    /// records what the user was told.</para>
    /// </summary>
    public string? ConflictJson { get; set; }

    /// <summary>The media filter the turn ran under, comma-separated (<c>Movie,Show</c>); null for "no filter".</summary>
    public string? MediaFilter { get; set; }

    /// <summary>The <c>done.reason</c> clause list exactly as the user was shown it.</summary>
    public string? Reason { get; set; }

    /// <summary>How many relaxation levels discovery had to widen through. Null on a turn that never discovered.</summary>
    public int? Relaxation { get; set; }

    /// <summary>Whether the account's rating floor was relaxed for this turn.</summary>
    public bool FloorRelaxed { get; set; }

    /// <summary>How many candidates discovery returned, before eligibility.</summary>
    public int? DiscoveredCount { get; set; }

    /// <summary>
    /// The model artifact whose predictions the stored cards carry — ONE per turn.
    ///
    /// <para>Deliberately not an FK and deliberately tolerant on read: <c>ModelArtifact</c>'s global
    /// filter hides artifacts whose feature-schema version is not the running one, so an id stored
    /// last week may simply not resolve today. On the read path <b>unresolvable equals differs</b>
    /// — both re-score through <c>OnDemandScorer</c> — because a stale number rendered as a current
    /// one is the failure this column exists to prevent.</para>
    /// </summary>
    public Guid? ModelArtifactId { get; set; }

    /// <summary>The Ask Reel prompt generation that produced the reply.</summary>
    public string? PromptVersion { get; set; }

    /// <summary>The chat model that produced the reply.</summary>
    public string? ModelId { get; set; }

    /// <summary>Wall-clock milliseconds from request to <c>done</c>.</summary>
    public int? LatencyMs { get; set; }

    /// <summary>The per-stage millisecond breakdown as stored JSON (jsonb).</summary>
    public string? StageTimingsJson { get; set; }

    /// <summary>When the turn reached <see cref="SearchTurnStatus.Completed"/> or <c>Failed</c>.</summary>
    public DateTime? CompletedAt { get; set; }
}
