using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Domain.Catalog;

namespace FrostAura.Reel.Application.CrossReference;

/// <summary>
/// The one definition of "which titles is the cross-reference allowed to judge", shared by the
/// enqueue gate in <c>BuildFeedJobHandler</c> and by the job that does the judging.
///
/// <para><b>Single-sourced through <see cref="EligibilityQueryBuilder"/> on purpose.</b> The pool is
/// the founder-locked eligible set and nothing else — a second spelling of "eligible" here would
/// drift from the one the feed serves, and the job would spend real money judging titles the
/// product can never show.</para>
///
/// <para><b>A persisted <c>TitleScore</c> for the ACTIVE artifact is required.</b> The judge is
/// shown the model's own number as evidence, and a title the current model has never scored has no
/// number to show. It is also the cheapest possible proxy for "this title actually reached a feed
/// build", which keeps the pool at the few thousand titles the account's builds have considered
/// rather than the whole catalogue.</para>
///
/// <para><b>Titles with neither an embedding nor extracted attributes are excluded, not merely
/// skipped later.</b> They are unjudgeable — a verdict on a name is the model's prior about the
/// title's reputation, which is the one thing this design is trying not to buy — and they are
/// counted separately so the completion copy can say so ("1,840 of 2,242 cross-referenced against
/// your history; 402 had no plot embedding yet"). Excluding them HERE rather than inside the batch
/// builder is what stops them from being permanently unjudged work: they would otherwise keep the
/// enqueue gate's anti-join answering "yes, there is work", and every feed build would queue a run
/// that judged nothing, requested a rebuild, and queued itself again through the front door.</para>
/// </summary>
public static class CrossReferencePool
{
    /// <summary>Eligible, scored by the active artifact, and carrying enough signal to be judged.</summary>
    public static IQueryable<Guid> Judgeable(
        IReelDbContext db, EligibilityQueryBuilder eligibility, Guid accountId, Guid modelArtifactId) =>
        Scored(db, eligibility, accountId, modelArtifactId)
            .Where(t => db.TitleEmbeddings.Any(e => e.TitleId == t.Id)
                || db.TitleAttributes.Any(a => a.TitleId == t.Id && a.Status == AttributeExtractionStatus.Done))
            .Select(t => t.Id);

    /// <summary>
    /// The complement inside the same pool: eligible and scored, but with nothing to reason from.
    /// Reported honestly rather than folded into the judged count.
    /// </summary>
    public static IQueryable<Guid> WithoutSignal(
        IReelDbContext db, EligibilityQueryBuilder eligibility, Guid accountId, Guid modelArtifactId) =>
        Scored(db, eligibility, accountId, modelArtifactId)
            .Where(t => !db.TitleEmbeddings.Any(e => e.TitleId == t.Id)
                && !db.TitleAttributes.Any(a => a.TitleId == t.Id && a.Status == AttributeExtractionStatus.Done))
            .Select(t => t.Id);

    private static IQueryable<Title> Scored(
        IReelDbContext db, EligibilityQueryBuilder eligibility, Guid accountId, Guid modelArtifactId) =>
        eligibility.EligibleTitles(accountId)
            // The account predicate is written out even though TitleScore is IAccountScoped: this
            // query is composed by a job whose scope is pinned by the runner, and a predicate that
            // depends on ambient state for its correctness is one refactor away from being wrong.
            .Where(t => db.TitleScores.Any(s =>
                s.AccountId == accountId && s.TitleId == t.Id && s.ModelArtifactId == modelArtifactId));
}
