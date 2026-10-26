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
/// The closed vocabulary for <see cref="SearchTurn.FailureCode"/>, plus the one rule that decides
/// when a <see cref="SearchTurnStatus.Pending"/> row has stopped being a turn in flight and become
/// a corpse.
///
/// <para><b>Why the rule has to exist at all.</b> Both writers of a terminal status — the persist
/// step after <c>done</c> and the catch around the stream — live inside the request pipeline. A
/// process that dies mid-stream (a crash, a deploy restart, an OOM kill) therefore leaves the row
/// <see cref="SearchTurnStatus.Pending"/> with no <see cref="SearchTurn.CompletedAt"/> and no code,
/// and there is no path left anywhere that would ever close it. That is not a cosmetic leftover:
/// <c>/ask</c> refuses a new turn while one is Pending on the conversation, so the thread is wedged
/// <b>permanently</b> — every retry, every "Try again" and every new question in it answers
/// <c>409 turn_in_progress</c>, and <c>GET /turns/{id}</c> answers <c>202</c> forever. A
/// conversation the product can never speak in again is a worse record than a turn that admits it
/// died.</para>
///
/// <para><b>Two codes close it, and they are deliberately not one.</b> A restart is a fact the
/// server knows about ITSELF and can state precisely at boot; a timeout is an INFERENCE drawn at the
/// gate from a clock. Collapsing them would lose the only distinction that matters when reading a
/// stuck conversation back — whether the process died under the turn, or the turn simply never
/// returned while the process stayed up.</para>
/// </summary>
public static class SearchTurnFailure
{
    /// <summary>The user navigated away or pressed stop: the stream was cancelled from their side.</summary>
    public const string ClientAborted = "client_aborted";

    /// <summary>
    /// The API process died while the turn was streaming, and the row was closed by the startup
    /// reconciliation of the process that replaced it. A fact, stated at boot.
    /// </summary>
    public const string ServerRestarted = "server_restarted";

    /// <summary>
    /// The row was still Pending <see cref="StalePendingAfter"/> after it was written, so the gate
    /// treated it as dead rather than letting it keep the conversation shut. The inference, as
    /// opposed to <see cref="ServerRestarted"/>'s fact.
    /// </summary>
    public const string TurnTimedOut = "turn_timed_out";

    /// <summary>
    /// How long a Pending row is given before the gate stops believing in it.
    ///
    /// <para>Three minutes, against measured turns of 8–30 seconds — an order of magnitude of head
    /// room, on purpose. The two errors cost wildly different amounts: expiring a turn that was
    /// genuinely still running loses an answer someone is waiting for and lets a second stream write
    /// cards under a colliding <c>(TurnId, Rank)</c>, while waiting three minutes on a row that is
    /// already dead costs one more "one moment" message. This is not a latency budget and must not
    /// be tuned towards p99.</para>
    /// </summary>
    public static readonly TimeSpan StalePendingAfter = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Whether <paramref name="turn"/> is a Pending row old enough to be treated as dead.
    ///
    /// <para>Measured off <see cref="SearchTurn.CreatedAt"/> — when the row was written Pending,
    /// and the only timestamp a turn that never finished has. <see cref="SearchTurn.CompletedAt"/>
    /// is null by definition on exactly the rows this question is asked about.</para>
    /// </summary>
    public static bool IsStalePending(SearchTurn turn, DateTime utcNow) =>
        turn.Status == SearchTurnStatus.Pending && utcNow - turn.CreatedAt >= StalePendingAfter;
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
    /// <para>Trimmed, because a filter toggle and an impatient tap resend the same string with
    /// whatever whitespace the composer had; and ORDINAL — byte-equal, case and all — rather than
    /// culture-aware or case-insensitive.</para>
    ///
    /// <para><b>Case used to be folded away, and it cost the transcript a question.</b> A user who
    /// retyped their question in capitals got a <c>page</c>: a fresh grid of results with no user
    /// bubble recorded, and a resumed thread showing one question where five submissions had been
    /// made. Whitespace is a transport artefact and folding it is safe; capitals are something a
    /// person typed on purpose, and the conservative reading of "did they ask again" is the one that
    /// records the asking. A page is the narrower claim, so it takes the stricter test.</para>
    ///
    /// <para>A conversation with no user turn yet has nothing to be a page OF.</para>
    /// </summary>
    public static string Derive(string? lastUserText, string message) =>
        lastUserText is not null
        && string.Equals(lastUserText.Trim(), message.Trim(), StringComparison.Ordinal)
            ? Page
            : Ask;
}

/// <summary>
/// The closed vocabulary for <see cref="SearchTurn.Mode"/>: what kind of answer a turn gave.
///
/// <para>Persisted as a string, and read through <see cref="Parse"/>, which maps null and any value
/// this build does not know to <see cref="Picks"/> — the only mode that existed before the column
/// did, and the one a rolled-back image has to fall back to without failing a read.</para>
/// </summary>
public static class SearchTurnMode
{
    /// <summary>The turn answered with cards that match the request.</summary>
    public const string Picks = "Picks";

    /// <summary>
    /// Nothing left matches the request under the account's conditions, and the reply says so with
    /// the supply counts behind it. Zero picks by definition.
    /// </summary>
    public const string NoneFound = "NoneFound";

    /// <summary>
    /// The turn asked the user what they meant instead of searching — today only when a pointer
    /// ("the second one") has nothing on screen to point at. Zero cards by definition. An image that
    /// predates the value reads it as <see cref="Picks"/> with no cards, which renders as an empty
    /// answer rather than failing the read.
    /// </summary>
    public const string Clarify = "Clarify";

    /// <summary>Ask Reel v2: an answer in words — an item question, a count, small talk — with the grid untouched.</summary>
    public const string Answer = "Answer";

    /// <summary>The stored mode, or <see cref="Picks"/> for null and anything unknown.</summary>
    public static string Parse(string? stored) =>
        string.Equals(stored, NoneFound, StringComparison.Ordinal) ? NoneFound
        : string.Equals(stored, Clarify, StringComparison.Ordinal) ? Clarify
        : string.Equals(stored, Answer, StringComparison.Ordinal) ? Answer
        : Picks;
}

/// <summary>
/// The tokens of <see cref="SearchTurn.DegradedFlags"/>. A flag names a part of the turn that did not
/// run as designed; the turn still answered, honestly labelled.
/// </summary>
public static class SearchTurnDegraded
{
    /// <summary>Every model attempt to read the request failed; the reply is the degraded copy.</summary>
    public const string Plan = "plan";

    /// <summary>
    /// The prefix of the plan-cause flag, written as <c>plan-cause:{code}</c> beside <see cref="Plan"/>
    /// — the status-API code of the failure that degraded the plan (<c>budget_spent</c>,
    /// <c>payment_required</c>, <c>unauthorized</c> …), so a turn record joins the status panel on one
    /// code (IREV2-SYS-02). A separate token on purpose: <see cref="PlanFailed"/> and the frontend
    /// match the exact <c>plan</c> token, and folding the cause into it would let a degraded turn back
    /// into history.
    /// </summary>
    public const string PlanCausePrefix = "plan-cause:";

    /// <summary>
    /// The prefix of the judge flag, written as <c>judge:{failed}/{total}</c> — how many judge
    /// batches produced no model verdict, out of how many ran.
    /// </summary>
    public const string JudgePrefix = "judge:";

    /// <summary>
    /// The prefix of the missing-verdict flag, written as <c>judge-missing:{missing}/{judged}</c> —
    /// how many of the titles sent to the judge came back with no request verdict, out of how many
    /// were sent. A batch can answer for five titles and silently skip the sixth; <see cref="JudgePrefix"/>
    /// counts only batches that answered for none, so this is the flag that says a turn was partly
    /// unchecked (R1-01).
    /// </summary>
    public const string JudgeMissingPrefix = "judge-missing:";

    /// <summary>
    /// No language model was available for the turn at all — a keyless install. Nothing read the
    /// request and nothing checked a card against it; both halves of the turn are degraded.
    /// </summary>
    public const string NoModel = "no-model";

    /// <summary>
    /// The prefix of a retrieval-arm note, written as <c>arm:{name}</c> (<c>arm:semantic</c>,
    /// <c>arm:tmdb-late</c>, <c>arm:provisional</c> …) — an arm the turn asked for that contributed
    /// nothing, so the cards were fused without it (§4.10). Stored on the row only, so a thin
    /// retrieval is a query; it is never sent in <c>done.degraded</c> and neither <see cref="PlanFailed"/>
    /// nor <see cref="JudgeFailed"/> reads it, because the reply and every card on screen are still
    /// exactly what they claim to be.
    /// </summary>
    public const string ArmPrefix = "arm:";

    /// <summary>The value of <see cref="SearchTurn.ReasoningEffort"/> when no effort parameter was sent.</summary>
    public const string ProviderDefaultEffort = "provider-default";

    /// <summary>Whether the stored flags say the PLAN failed — the case history must never read back.</summary>
    public static bool PlanFailed(string? flags) =>
        Tokens(flags).Any(t => t == Plan || t == NoModel);

    /// <summary>
    /// Whether the stored flags say at least one card sent to the judge came back without a verdict —
    /// a whole failed batch, one skipped entry, or no model at all. The turn's cards were not all
    /// checked against the request, and the screen says so.
    /// </summary>
    public static bool JudgeFailed(string? flags) =>
        Tokens(flags).Any(t => t == NoModel || Counted(t, JudgePrefix) || Counted(t, JudgeMissingPrefix));

    /// <summary>A <c>prefix{n}/{m}</c> token whose n is not zero.</summary>
    private static bool Counted(string token, string prefix) =>
        token.StartsWith(prefix, StringComparison.Ordinal)
        && token.Length > prefix.Length
        && !token.AsSpan(prefix.Length).StartsWith("0/", StringComparison.Ordinal);

    private static string[] Tokens(string? flags) =>
        flags is { Length: > 0 }
            ? flags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
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
    /// Why the turn failed, when it did: one of <see cref="SearchTurnFailure"/>, otherwise the
    /// pipeline's own code or the exception type. Null on every other status. A failed turn is
    /// a recoverable record — the client can show what happened and offer a retry — which is the
    /// whole reason a failure is persisted rather than left as a missing row.
    ///
    /// <para>Two of those codes are written by nothing in the request pipeline at all:
    /// <see cref="SearchTurnFailure.ServerRestarted"/> at boot and
    /// <see cref="SearchTurnFailure.TurnTimedOut"/> at the gate, because a process that dies
    /// mid-stream leaves a Pending row no request-scoped code path can ever reach again.</para>
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

    /// <summary>
    /// The per-stage millisecond breakdown as stored JSON (jsonb) — every stage the turn actually
    /// ran, keyed by stage name. Written on every assistant row that reached the pipeline, completed
    /// or not; a stage that never ran is absent rather than zero.
    /// </summary>
    public string? StageTimingsJson { get; set; }

    /// <summary>
    /// What kind of answer this turn gave: one of <see cref="SearchTurnMode"/>. Null on every row
    /// written before the mode existed, which reads as <see cref="SearchTurnMode.Picks"/> — that is
    /// what every one of them was. Assistant rows only.
    /// </summary>
    public string? Mode { get; set; }

    /// <summary>
    /// Which parts of the turn did NOT run as designed, as a comma-separated list of
    /// <see cref="SearchTurnDegraded"/> tokens (<c>plan</c>, <c>judge:2/3</c>). Null on a turn where
    /// everything ran. A degraded turn is a real record of a real answer — it is never cached, never
    /// read back as history, and is shown to the user as degraded.
    /// </summary>
    public string? DegradedFlags { get; set; }

    /// <summary>
    /// The models behind the turn, by role, as stored JSON (jsonb): <c>{"plan": …, "judge": …,
    /// "embedding": …}</c>. <see cref="ModelId"/> is kept beside it for the readers that predate it.
    /// </summary>
    public string? ModelIds { get; set; }

    /// <summary>
    /// The reasoning effort the turn's model calls were made at, as the adapter reports it;
    /// <see cref="SearchTurnDegraded.ProviderDefaultEffort"/> where the request carried no effort
    /// parameter at all, which is a fact about the request rather than a guess about the model.
    /// </summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>When the turn reached <see cref="SearchTurnStatus.Completed"/> or <c>Failed</c>.</summary>
    /// <summary>
    /// Ask Reel v2's reading of the message — <c>NewRequest</c>, <c>Refine</c>, <c>CritiqueSet</c>,
    /// <c>CritiqueItem</c>, <c>ItemQuestion</c> or <c>Chitchat</c> — by name. Null on v1 turns and user
    /// rows; an unknown name reads as <c>NewRequest</c>.
    /// </summary>
    public string? Act { get; set; }

    /// <summary>Where the v2 plan came from: <c>Llm</c>, <c>LlmRetry</c> or <c>CarriedForward</c> (the unknown fallback).</summary>
    public string? PlanSource { get; set; }

    /// <summary>How many of the turn's model calls finished on <c>length</c>; null on v1 turns.</summary>
    public int? TruncationCount { get; set; }

    public DateTime? CompletedAt { get; set; }
}
