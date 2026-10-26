using System.Text.Json;
using FrostAura.Reel.Application.Sync;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Trakt;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Tests.Fakes;
using FrostAura.Reel.Tests.IntegrationTests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.Sync;

/// <summary>
/// The history side of the outbox dispatcher, on real Postgres with a recording Trakt fake:
/// a show AddToHistory entry is a permanent defect (a bare show object on sync/history marks
/// every aired episode watched — the ~801-fake-plays corruption) and must dead-letter without
/// any POST; RemoveFromHistory anchors on watched_at ± 5 minutes and removes exactly the event
/// ids Trakt returns; an entry whose window holds no events is Sent (idempotent removal).
/// </summary>
[Collection("postgres")]
public class OutboxDispatcherHistoryTests(PostgresFixture fixture)
{
    private static readonly TimeSpan AnchorWindow = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task Show_add_to_history_entry_dead_letters_without_posting()
    {
        if (!fixture.Available)
        {
            return; // no Postgres available — covered in CI's backend-integration job
        }

        var accountId = await SeedAccountWithConnectionAsync();
        var entryId = await SeedEntryAsync(accountId, OutboxKind.AddToHistory, MediaType.Show, watchedAt: DateTime.UtcNow);

        var trakt = new StubTraktClient();
        await RunOnceAsync(accountId, trakt);

        await using var verify = fixture.CreateContext(new AccountContext());
        var entry = await verify.TraktOutbox.SingleAsync(o => o.Id == entryId);
        Assert.Equal(OutboxStatus.DeadLetter, entry.Status);
        Assert.Equal("history writeback is movie-only", entry.Error);
        Assert.Empty(trakt.SyncBatches); // and above all: nothing was POSTed to sync/history
    }

    [Fact]
    public async Task Remove_from_history_reads_the_anchor_window_and_removes_exactly_the_returned_ids()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountWithConnectionAsync();
        var watchedAt = new DateTime(2026, 8, 1, 20, 15, 0, DateTimeKind.Utc);
        var traktId = Random.Shared.NextInt64(1, long.MaxValue);
        var entryId = await SeedEntryAsync(accountId, OutboxKind.RemoveFromHistory, MediaType.Movie, watchedAt, traktId);

        var trakt = new StubTraktClient
        {
            History =
            [
                new TraktHistoryItem(111, watchedAt, "watch", "movie", null, null, null),
                new TraktHistoryItem(222, watchedAt.AddSeconds(1), "watch", "movie", null, null, null),
            ],
        };
        await RunOnceAsync(accountId, trakt);

        var call = Assert.Single(trakt.HistoryCalls);
        Assert.Equal(("movies", traktId, watchedAt - AnchorWindow, watchedAt + AnchorWindow), call);

        var (endpoint, payload) = Assert.Single(trakt.SyncBatches);
        Assert.Equal("sync/history/remove", endpoint);
        var ids = JsonSerializer.SerializeToElement(payload).GetProperty("ids")
            .EnumerateArray().Select(e => e.GetInt64()).ToArray();
        Assert.Equal([111L, 222L], ids);

        await using var verify = fixture.CreateContext(new AccountContext());
        var entry = await verify.TraktOutbox.SingleAsync(o => o.Id == entryId);
        Assert.Equal(OutboxStatus.Sent, entry.Status);
    }

    [Fact]
    public async Task Remove_from_history_with_no_events_in_window_is_sent_without_posting()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountWithConnectionAsync();
        var entryId = await SeedEntryAsync(accountId, OutboxKind.RemoveFromHistory, MediaType.Movie, DateTime.UtcNow);

        var trakt = new StubTraktClient { History = [] };
        await RunOnceAsync(accountId, trakt);

        Assert.Single(trakt.HistoryCalls);
        Assert.Empty(trakt.SyncBatches); // nothing to remove ⇒ nothing POSTed — removal is idempotent

        await using var verify = fixture.CreateContext(new AccountContext());
        var entry = await verify.TraktOutbox.SingleAsync(o => o.Id == entryId);
        Assert.Equal(OutboxStatus.Sent, entry.Status);
    }

    /// <summary>
    /// R1-43 CR-2: an entry whose stored media type this build cannot name (a newer image wrote it)
    /// is dead-lettered on its own before any Trakt call. A good movie entry of the same kind still
    /// sends, and the unreadable entry's Trakt id reaches no history read and no sync batch.
    /// </summary>
    [Theory]
    [InlineData(OutboxKind.AddRating, "\"Documentary\"")]
    [InlineData(OutboxKind.AddRating, "7")]
    [InlineData(OutboxKind.RemoveFromHistory, "null")]
    [InlineData(OutboxKind.RemoveFromHistory, "\"Documentary\"")]
    [InlineData(OutboxKind.AddToWatchlist, "\"movie\"")]
    public async Task An_entry_with_a_media_type_this_build_cannot_name_dead_letters_before_any_trakt_call(
        OutboxKind kind, string storedMediaType)
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountWithConnectionAsync();
        var rating = kind == OutboxKind.AddRating ? (short?)8 : null;
        var watchedAt = kind == OutboxKind.RemoveFromHistory ? DateTime.UtcNow : (DateTime?)null;
        var goodTraktId = Random.Shared.NextInt64(1, long.MaxValue);
        var badTraktId = Random.Shared.NextInt64(1, long.MaxValue);
        var goodId = await SeedEntryAsync(accountId, kind, MediaType.Movie, watchedAt, goodTraktId, rating);
        var badId = await SeedEntryAsync(accountId, kind, MediaType.Show, watchedAt, badTraktId, rating);
        await using (var edit = fixture.CreateContext(new AccountContext()))
        {
            var bad = await edit.TraktOutbox.SingleAsync(o => o.Id == badId);
            // The column is jsonb, so the stored text is Postgres's rendering: edit the value, not the text.
            var payload = System.Text.Json.Nodes.JsonNode.Parse(bad.PayloadJson)!.AsObject();
            Assert.Equal("Show", payload["MediaType"]!.GetValue<string>());
            payload["MediaType"] = System.Text.Json.Nodes.JsonNode.Parse(storedMediaType);
            bad.PayloadJson = payload.ToJsonString();
            await edit.SaveChangesAsync();
        }

        var trakt = new StubTraktClient { History = [] };
        await RunOnceAsync(accountId, trakt);

        await using var verify = fixture.CreateContext(new AccountContext());
        var badEntry = await verify.TraktOutbox.SingleAsync(o => o.Id == badId);
        Assert.Equal(OutboxStatus.DeadLetter, badEntry.Status);
        Assert.Equal("unrecognised media type", badEntry.Error);
        Assert.Equal(OutboxStatus.Sent, (await verify.TraktOutbox.SingleAsync(o => o.Id == goodId)).Status);

        Assert.DoesNotContain(trakt.HistoryCalls, call => call.TraktId == badTraktId);
        Assert.All(trakt.SyncBatches, batch => Assert.DoesNotContain(
            badTraktId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonSerializer.Serialize(batch.Payload),
            StringComparison.Ordinal));
        if (kind != OutboxKind.RemoveFromHistory)
        {
            var (_, sent) = Assert.Single(trakt.SyncBatches);
            Assert.Contains(goodTraktId.ToString(System.Globalization.CultureInfo.InvariantCulture), JsonSerializer.Serialize(sent), StringComparison.Ordinal);
        }
    }

    private async Task RunOnceAsync(Guid accountId, StubTraktClient trakt)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var tokenStore = new TraktTokenStore(db, trakt, new IdentityProtector(), NullLogger<TraktTokenStore>.Instance);
        var dispatcher = new OutboxDispatcher(db, trakt, tokenStore, NullLogger<OutboxDispatcher>.Instance);
        await dispatcher.RunOnceAsync(CancellationToken.None);
    }

    private async Task<Guid> SeedAccountWithConnectionAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"outbox-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        db.TraktConnections.Add(new TraktConnection
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            AccessTokenEncrypted = "token",
            RefreshTokenEncrypted = "refresh",
            ExpiresAt = DateTime.UtcNow.AddDays(30), // far enough out that no refresh path fires
            Status = ConnectionStatus.Active,
        });
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<Guid> SeedEntryAsync(
        Guid accountId, OutboxKind kind, MediaType mediaType, DateTime? watchedAt, long? traktId = null, short? rating = null)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var payload = new OutboxDispatcher.OutboxPayload(
            Guid.NewGuid(), mediaType, TmdbId: Random.Shared.NextInt64(1, long.MaxValue),
            TraktId: traktId ?? Random.Shared.NextInt64(1, long.MaxValue), Rating: rating, WatchedAt: watchedAt);
        var entry = new TraktOutboxEntry
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Kind = kind,
            PayloadJson = JsonSerializer.Serialize(payload),
            EnqueuedAt = DateTime.UtcNow,
            NextAttemptAt = DateTime.UtcNow.AddMinutes(-1),
        };
        db.TraktOutbox.Add(entry);
        await db.SaveChangesAsync();
        return entry.Id;
    }

    /// <summary>The dispatcher never inspects token contents — pass-through beats crypto here.</summary>
    private sealed class IdentityProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;

        public string Unprotect(string ciphertext) => ciphertext;
    }
}
