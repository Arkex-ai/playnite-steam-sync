using System;
using System.IO;
using System.Linq;
using SteamShortcutsImporter;
using Xunit;

namespace SteamShortcutsImporter.Tests;

public class SyncQueueStoreTests
{
    [Fact]
    public void EnqueueBatch_PersistsMonotonicSequencesAcrossRestart()
    {
        using var temp = new TemporaryDirectory();
        var store = new SyncQueueStore(temp.Path);
        var first = CreateEntry(Guid.NewGuid(), "one");
        first.GameExternalId = "provider-game-123";
        first.Roms.Add(new SyncGameRom { Name = "game.nsp", Path = @"C:\TestGames\Switch\game.nsp" });
        var second = CreateEntry(Guid.NewGuid(), "two");

        Assert.Equal(2, store.EnqueueBatch(new[] { first, second }));
        var beforeRestart = store.GetPending();
        Assert.Equal(new long[] { 1, 2 }, beforeRestart.Select(x => x.Sequence));

        var reopened = new SyncQueueStore(temp.Path);
        Assert.Equal("provider-game-123", reopened.GetPending().Single(x => x.Fingerprint == "one").GameExternalId);
        Assert.Equal(@"C:\TestGames\Switch\game.nsp", reopened.GetPending().Single(x => x.Fingerprint == "one").Roms.Single().Path);
        var third = CreateEntry(Guid.NewGuid(), "three");
        Assert.Equal(1, reopened.EnqueueBatch(new[] { third }));
        Assert.Equal(3, reopened.GetPending().Single(x => x.Fingerprint == "three").Sequence);
    }

    [Fact]
    public void BindSteamUserId_MigratesLegacyPendingOperationsAndPersistsAccount()
    {
        using var temp = new TemporaryDirectory();
        var store = new SyncQueueStore(temp.Path);
        var entry = CreateEntry(Guid.NewGuid(), "legacy");
        Assert.Equal(1, store.EnqueueBatch(new[] { entry }));

        store.BindSteamUserId("76561197960265729");

        var reopened = new SyncQueueStore(temp.Path);
        Assert.Equal("76561197960265729", reopened.SteamUserId);
        Assert.Equal("76561197960265729", Assert.Single(reopened.GetPending()).SteamUserId);
    }

    [Fact]
    public void BindSteamUserId_MigratesLargeLegacyJournalWithoutChangingQueueState()
    {
        using var temp = new TemporaryDirectory();
        var store = new SyncQueueStore(temp.Path);
        store.RecordManualExports(new[] { CreateEntry(Guid.NewGuid(), "baseline") }, establishInitialBaseline: true);
        var filtered = Enumerable.Range(1, 1367)
            .Select(index =>
            {
                var entry = CreateEntry(Guid.NewGuid(), "filtered-" + index);
                entry.InitialStatus = SyncQueueStore.FilteredOutStatus;
                return entry;
            })
            .ToList();
        Assert.Equal(1367, store.EnqueueBatch(filtered));
        store.MarkSessionStarted();

        store.BindSteamUserId("76561197960265729");

        var reopened = new SyncQueueStore(temp.Path);
        var states = reopened.GetStatusItems();
        Assert.Equal("76561197960265729", reopened.SteamUserId);
        Assert.True(reopened.InitialBaselineCompleted);
        Assert.False(reopened.WasLastSessionClean);
        Assert.Equal(1367, reopened.LastSequence);
        Assert.Equal(1367, states.Count);
        Assert.All(states, item =>
        {
            Assert.Equal(SyncQueueStore.FilteredOutStatus, item.Status);
            Assert.Equal("76561197960265729", item.Entry.SteamUserId);
        });
        Assert.Empty(reopened.GetPending());
    }

    [Fact]
    public void BindSteamUserId_RejectsJournalAlreadyBoundToAnotherAccount()
    {
        using var temp = new TemporaryDirectory();
        var store = new SyncQueueStore(temp.Path);
        var entry = CreateEntry(Guid.NewGuid(), "legacy");
        entry.SteamUserId = "76561197960265729";
        store.EnqueueBatch(new[] { entry });

        var error = Assert.Throws<InvalidOperationException>(() => store.BindSteamUserId("76561197960265730"));

        Assert.Contains("different Steam user", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("76561197960265729", new SyncQueueStore(temp.Path).GetPending().Single().SteamUserId);
    }

    [Fact]
    public void EnqueueBatch_DeduplicatesSamePendingGameAndFingerprint()
    {
        using var temp = new TemporaryDirectory();
        var store = new SyncQueueStore(temp.Path);
        var gameId = Guid.NewGuid();

        Assert.Equal(1, store.EnqueueBatch(new[] { CreateEntry(gameId, "same") }));
        Assert.Equal(0, store.EnqueueBatch(new[] { CreateEntry(gameId, "same") }));
        Assert.Single(store.GetPending());
    }

    [Fact]
    public void EnqueueBatch_ArtworkCheckTokenQueuesSameSnapshotAndCoalescesToLatestEvent()
    {
        using var temp = new TemporaryDirectory();
        var store = new SyncQueueStore(temp.Path);
        var gameId = Guid.NewGuid();
        var first = CreateEntry(gameId, "same-fingerprint");
        first.ArtworkCheckToken = Guid.NewGuid().ToString("N");
        var second = CreateEntry(gameId, "same-fingerprint");
        second.ArtworkCheckToken = Guid.NewGuid().ToString("N");

        Assert.Equal(1, store.EnqueueBatch(new[] { first }));
        Assert.Equal(1, store.EnqueueBatch(new[] { second }));
        var pending = Assert.Single(store.GetPending());
        Assert.Equal(second.ArtworkCheckToken, pending.ArtworkCheckToken);
        Assert.Contains(store.GetStatusItems(), item => item.Entry.Sequence == 1 && item.Status == SyncQueueStore.SupersededStatus);

        store.MarkCommitted(pending, 1234);
        var laterEvent = CreateEntry(gameId, "same-fingerprint");
        laterEvent.ArtworkCheckToken = Guid.NewGuid().ToString("N");
        Assert.Equal(1, store.EnqueueBatch(new[] { laterEvent }));
        Assert.Single(store.GetPending());
    }

    [Fact]
    public void GetPending_PrioritizesNewWorkOverDueSteamWaitRetries()
    {
        using var temp = new TemporaryDirectory();
        var store = new SyncQueueStore(temp.Path);
        var waiting = CreateEntry(Guid.NewGuid(), "waiting");
        var pending = CreateEntry(Guid.NewGuid(), "pending");
        Assert.Equal(2, store.EnqueueBatch(new[] { waiting, pending }));
        store.MarkWaitingForSteam(1, DateTime.UtcNow.AddSeconds(-1));

        var runnable = store.GetPending();

        Assert.Equal(new[] { "pending", "waiting" }, runnable.Select(entry => entry.Fingerprint));
    }

    [Fact]
    public void ArtworkSyncVersion_PersistsAcrossStoreRestart()
    {
        using var temp = new TemporaryDirectory();
        var store = new SyncQueueStore(temp.Path);
        Assert.Equal(0, store.ArtworkSyncVersion);

        store.SetArtworkSyncVersion(1);

        Assert.Equal(1, new SyncQueueStore(temp.Path).ArtworkSyncVersion);
    }

    [Fact]
    public void ObservedFilteredGame_SurvivesRestartAndCanEnterQueueAfterUpdate()
    {
        using var temp = new TemporaryDirectory();
        var store = new SyncQueueStore(temp.Path);
        var gameId = Guid.NewGuid();
        var sourceId = Guid.NewGuid().ToString("D");

        store.ObserveGames(new[] { (gameId.ToString("D"), sourceId) });
        var reopened = new SyncQueueStore(temp.Path);
        Assert.True(reopened.IsGameTracked(gameId.ToString("D")));

        var filtered = CreateEntry(gameId, "filtered");
        filtered.SourcePluginId = sourceId;
        filtered.InitialStatus = SyncQueueStore.FilteredOutStatus;
        Assert.Equal(1, reopened.EnqueueBatch(new[] { filtered }));
        Assert.Empty(reopened.GetPending());
        Assert.Equal(0, reopened.EnqueueBatch(new[] { filtered }));

        var afterInstall = CreateEntry(gameId, "installed-and-eligible");
        afterInstall.SourcePluginId = sourceId;
        Assert.Equal(1, reopened.EnqueueBatch(new[] { afterInstall }));
        Assert.Equal("installed-and-eligible", Assert.Single(reopened.GetPending()).Fingerprint);
    }

    [Fact]
    public void EnqueueBatch_CoalescesDuplicateGameSnapshotsToLatest()
    {
        using var temp = new TemporaryDirectory();
        var store = new SyncQueueStore(temp.Path);
        var gameId = Guid.NewGuid();

        Assert.Equal(1, store.EnqueueBatch(new[]
        {
            CreateEntry(gameId, "older"),
            CreateEntry(gameId, "latest")
        }));

        var pending = Assert.Single(store.GetPending());
        Assert.Equal("latest", pending.Fingerprint);
        Assert.Equal(1, pending.Sequence);
    }

    [Fact]
    public void FilteredOutSnapshot_SupersedesOlderPendingWork()
    {
        using var temp = new TemporaryDirectory();
        var store = new SyncQueueStore(temp.Path);
        var gameId = Guid.NewGuid();
        store.EnqueueBatch(new[] { CreateEntry(gameId, "eligible") });
        var pending = Assert.Single(store.GetPending());

        var filtered = CreateEntry(gameId, "filtered");
        filtered.InitialStatus = SyncQueueStore.FilteredOutStatus;
        Assert.Equal(1, store.EnqueueBatch(new[] { filtered }));

        Assert.Empty(store.GetPending());
        Assert.Contains(store.GetStatusItems(), x => x.Entry.Sequence == pending.Sequence && x.Status == SyncQueueStore.SupersededStatus);
        Assert.False(store.Retry(pending.Sequence + 1));
    }

    [Fact]
    public void JournalCompaction_PreservesLatestFilteredSnapshotForIdempotency()
    {
        using var temp = new TemporaryDirectory();
        var store = new SyncQueueStore(temp.Path);
        var latestFiltered = Enumerable.Range(0, 64)
            .Select(_ => CreateEntry(Guid.NewGuid(), "filtered-latest"))
            .ToList();
        var earlierFiltered = latestFiltered
            .Select(entry => CreateEntry(Guid.Parse(entry.PlayniteGameId), "filtered-earlier"))
            .ToList();
        foreach (var entry in earlierFiltered.Concat(latestFiltered))
        {
            entry.InitialStatus = SyncQueueStore.FilteredOutStatus;
            entry.InitialError = "Excluded by test filter";
            Assert.Equal(1, store.EnqueueBatch(new[] { entry }));
        }

        var eligible = CreateEntry(Guid.NewGuid(), "eligible-committed");
        Assert.Equal(1, store.EnqueueBatch(new[] { eligible }));
        store.MarkCommitted(Assert.Single(store.GetPending()), 0x1234);

        var reopened = new SyncQueueStore(temp.Path);
        var compactedStatuses = reopened.GetStatusItems();
        Assert.Equal(64, compactedStatuses.Count);
        Assert.All(compactedStatuses, item => Assert.Equal(SyncQueueStore.FilteredOutStatus, item.Status));
        Assert.Equal(0, reopened.EnqueueBatch(new[] { latestFiltered[0] }));
        Assert.Equal(129, reopened.LastSequence);
        Assert.Empty(reopened.GetPending());
    }

    [Fact]
    public void GetPending_CoalescesOlderSnapshotsAndCommitStoresAppId()
    {
        using var temp = new TemporaryDirectory();
        var store = new SyncQueueStore(temp.Path);
        var gameId = Guid.NewGuid();
        store.EnqueueBatch(new[] { CreateEntry(gameId, "old") });
        store.EnqueueBatch(new[] { CreateEntry(gameId, "new") });

        var pending = Assert.Single(store.GetPending());
        Assert.Equal("new", pending.Fingerprint);
        Assert.Equal(2, pending.Sequence);

        store.MarkCommitted(pending, 0x1234);
        Assert.Empty(store.GetPending());
        var record = store.GetGameRecord(gameId.ToString("D"));
        Assert.NotNull(record);
        Assert.Equal(0x1234u, record!.AppId);
        Assert.Equal("new", record.Fingerprint);
    }

    [Fact]
    public void NeedsAttention_IsNotRetriedUntilExplicitRetry()
    {
        using var temp = new TemporaryDirectory();
        var store = new SyncQueueStore(temp.Path);
        store.EnqueueBatch(new[] { CreateEntry(Guid.NewGuid(), "blocked") });
        var operation = Assert.Single(store.GetPending());

        store.MarkNeedsAttention(operation.Sequence, "Missing action");
        Assert.Empty(store.GetPending());
        Assert.True(store.Retry(operation.Sequence));
        Assert.Single(store.GetPending());
    }

    [Fact]
    public void GetPending_DoesNotApplyOlderSnapshotWhileNewestIsWaitingForRetry()
    {
        using var temp = new TemporaryDirectory();
        var store = new SyncQueueStore(temp.Path);
        var gameId = Guid.NewGuid();
        store.EnqueueBatch(new[] { CreateEntry(gameId, "old") });
        var old = Assert.Single(store.GetPending());
        store.MarkWaitingForSteam(old.Sequence, DateTime.UtcNow.AddMinutes(5));
        store.EnqueueBatch(new[] { CreateEntry(gameId, "new") });

        var pending = Assert.Single(store.GetPending());
        Assert.Equal("new", pending.Fingerprint);
        Assert.Contains(store.GetStatusItems(), x => x.Entry.Sequence == old.Sequence && x.Status == SyncQueueStore.SupersededStatus);
    }

    [Fact]
    public void RetryAllRecoverable_ResetsAttentionAndBackoffItems()
    {
        using var temp = new TemporaryDirectory();
        var store = new SyncQueueStore(temp.Path);
        store.EnqueueBatch(new[] { CreateEntry(Guid.NewGuid(), "attention"), CreateEntry(Guid.NewGuid(), "retry") });
        var pending = store.GetPending();
        store.MarkNeedsAttention(pending[0].Sequence, "missing action");
        store.MarkPrepared(pending[1].Sequence, new SyncPreparedShortcut { AppId = 0x44556677, AppName = "Prepared" });
        store.MarkRetryableFailure(pending[1].Sequence, "temporary failure");

        Assert.Equal(2, store.RetryAllRecoverable());
        Assert.Equal(2, store.GetPending().Count);
        Assert.Equal(0x44556677u, store.GetPreparedShortcut(pending[1].Sequence)!.AppId);
    }

    [Fact]
    public void PreparedShortcut_SurvivesRestartWithSameAppId()
    {
        using var temp = new TemporaryDirectory();
        var store = new SyncQueueStore(temp.Path);
        store.EnqueueBatch(new[] { CreateEntry(Guid.NewGuid(), "prepared") });
        var operation = Assert.Single(store.GetPending());
        store.MarkPrepared(operation.Sequence, new SyncPreparedShortcut
        {
            AppId = 0xABCDEF01,
            AppName = "Prepared game",
            Exe = @"C:\\Games\\prepared.exe"
        });

        var reopened = new SyncQueueStore(temp.Path);
        var replay = Assert.Single(reopened.GetPending());
        Assert.Equal(operation.Sequence, replay.Sequence);
        Assert.Equal(0xABCDEF01u, reopened.GetPreparedShortcut(replay.Sequence)!.AppId);
    }

    [Fact]
    public void RecordManualExports_CompletesBaselineAndStoresMappingsTogether()
    {
        using var temp = new TemporaryDirectory();
        var store = new SyncQueueStore(temp.Path);
        var id = Guid.NewGuid();
        store.RecordManualExports(new[] { CreateEntry(id, "baseline") }, establishInitialBaseline: true);

        var reopened = new SyncQueueStore(temp.Path);
        Assert.True(reopened.InitialBaselineCompleted);
        Assert.Equal("baseline", reopened.GetGameRecord(id.ToString("D"))!.Fingerprint);
    }

    [Fact]
    public void RecordManualExports_EmptyBatchDoesNotCompleteBaseline()
    {
        using var temp = new TemporaryDirectory();
        var store = new SyncQueueStore(temp.Path);

        store.RecordManualExports(Array.Empty<SyncQueueEntry>(), establishInitialBaseline: true);

        var reopened = new SyncQueueStore(temp.Path);
        Assert.False(reopened.InitialBaselineCompleted);
        Assert.Null(reopened.GetGameRecord(Guid.NewGuid().ToString("D")));
    }

    [Fact]
    public void Reopen_AcceptsUtf8BomInManifestWithoutRecovery()
    {
        using var temp = new TemporaryDirectory();
        var store = new SyncQueueStore(temp.Path);
        store.SetSteamUserId("1");
        store.MarkInitialBaselineCompleted();

        var manifestPath = Path.Combine(temp.Path, "sync-state.json");
        var json = File.ReadAllBytes(manifestPath);
        var withBom = new byte[json.Length + 3];
        withBom[0] = 0xEF;
        withBom[1] = 0xBB;
        withBom[2] = 0xBF;
        Buffer.BlockCopy(json, 0, withBom, 3, json.Length);
        File.WriteAllBytes(manifestPath, withBom);

        var reopened = new SyncQueueStore(temp.Path);

        Assert.True(reopened.InitialBaselineCompleted);
        Assert.False(reopened.NeedsRecovery);
    }

    [Fact]
    public void Reopen_RecoversValidJournalEntriesAndDropsMalformedLine()
    {
        using var temp = new TemporaryDirectory();
        var store = new SyncQueueStore(temp.Path);
        store.EnqueueBatch(new[] { CreateEntry(Guid.NewGuid(), "valid") });
        File.AppendAllText(Path.Combine(temp.Path, "sync-journal.jsonl"), "{broken json", System.Text.Encoding.UTF8);

        var reopened = new SyncQueueStore(temp.Path);
        Assert.True(reopened.NeedsRecovery);
        Assert.Single(reopened.GetPending());
        Assert.DoesNotContain("broken json", File.ReadAllText(Path.Combine(temp.Path, "sync-journal.jsonl")));
    }

    [Fact]
    public void SessionAndBaselineFlags_ArePersisted()
    {
        using var temp = new TemporaryDirectory();
        var store = new SyncQueueStore(temp.Path);
        Assert.True(store.WasLastSessionClean);
        Assert.False(store.InitialBaselineCompleted);

        store.MarkSessionStarted();
        store.MarkInitialBaselineCompleted();
        var reopened = new SyncQueueStore(temp.Path);

        Assert.False(reopened.WasLastSessionClean);
        Assert.True(reopened.InitialBaselineCompleted);
    }

    private static SyncQueueEntry CreateEntry(Guid id, string fingerprint)
    {
        return new SyncQueueEntry
        {
            PlayniteGameId = id.ToString("D"),
            SourcePluginId = Guid.NewGuid().ToString("D"),
            GameName = fingerprint,
            Fingerprint = fingerprint,
            AppId = 0x1234,
            Exe = @"C:\Games\game.exe"
        };
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NonSteamSyncTests", Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, true);
        }
    }
}
