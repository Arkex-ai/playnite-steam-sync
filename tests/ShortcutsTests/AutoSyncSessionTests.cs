using System;
using System.IO;
using SteamShortcutsImporter;
using Xunit;

namespace ShortcutsTests;

public sealed class AutoSyncSessionTests
{
    [Theory]
    [InlineData(0, 1000)]
    [InlineData(1, 1000)]
    [InlineData(2, 2000)]
    [InlineData(3, 4000)]
    [InlineData(10, 300000)]
    [InlineData(50, 300000)]
    public void RecoveryBackoffGrowsExponentiallyAndCapsAtFiveMinutes(int attempt, int expectedMilliseconds)
    {
        Assert.Equal(expectedMilliseconds, AutoSyncHandler.GetRecoveryBackoffMilliseconds(attempt));
    }

    [Theory]
    [InlineData(false, 0, 0, 0, 0, true)]
    [InlineData(true, 0, 0, 0, 0, false)]
    [InlineData(false, 1, 0, 0, 0, false)]
    [InlineData(false, 0, 1, 0, 0, false)]
    [InlineData(false, 0, 0, 1, 0, false)]
    [InlineData(false, 0, 0, 0, 1, false)]
    public void SessionIsCleanOnlyWhenRecoveryAndAllBackgroundWorkAreIdle(
        bool recoveryPending,
        int captures,
        int processing,
        int reconciling,
        int retrying,
        bool expected)
    {
        Assert.Equal(expected, AutoSyncHandler.CanMarkSessionClean(recoveryPending, captures, processing, reconciling, retrying));
    }

    [Fact]
    public void ShutdownDuringRecoveryLeavesSessionDirtyForNextStartup()
    {
        var path = Path.Combine(Path.GetTempPath(), "non-steam-sync-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var store = new SyncQueueStore(path);
            store.MarkSessionStarted();

            if (AutoSyncHandler.CanMarkSessionClean(recoveryPending: true, capturesInProgress: 0, processing: 0, reconciling: 0, retrying: 0))
            {
                store.MarkSessionClean();
            }

            var reopened = new SyncQueueStore(path);
            Assert.False(reopened.WasLastSessionClean);
        }
        finally
        {
            var tempRoot = Path.GetFullPath(Path.GetTempPath());
            var target = Path.GetFullPath(path);
            Assert.StartsWith(tempRoot, target, StringComparison.OrdinalIgnoreCase);
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public void QueueStoreKeepsItsSteamAccountIdentityAcrossRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), "non-steam-sync-account-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var store = new SyncQueueStore(path);
            store.SetSteamUserId("1");

            var reopened = new SyncQueueStore(path);

            Assert.Equal("1", reopened.SteamUserId);
        }
        finally
        {
            var tempRoot = Path.GetFullPath(Path.GetTempPath());
            var target = Path.GetFullPath(path);
            Assert.StartsWith(tempRoot, target, StringComparison.OrdinalIgnoreCase);
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public void LateActivityAfterCleanShutdownCanPersistRecoveryForNextStartup()
    {
        var path = Path.Combine(Path.GetTempPath(), "non-steam-sync-late-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var store = new SyncQueueStore(path);
            store.MarkSessionStarted();
            store.MarkSessionClean();

            // A callback that began before shutdown but finished afterward can mark the
            // session dirty again so the next process recovers the event safely.
            store.MarkSessionStarted();

            var reopened = new SyncQueueStore(path);
            Assert.False(reopened.WasLastSessionClean);
        }
        finally
        {
            var tempRoot = Path.GetFullPath(Path.GetTempPath());
            var target = Path.GetFullPath(path);
            Assert.StartsWith(tempRoot, target, StringComparison.OrdinalIgnoreCase);
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public void QueueStoreCannotBeReboundToAnotherAccountWhileAnOperationIsPending()
    {
        var path = Path.Combine(Path.GetTempPath(), "non-steam-sync-account-pending-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var store = new SyncQueueStore(path);
            store.SetSteamUserId("1");
            store.EnqueueBatch(new[]
            {
                new SyncQueueEntry
                {
                    PlayniteGameId = Guid.NewGuid().ToString("D"),
                    SourcePluginId = Guid.NewGuid().ToString("D"),
                    GameName = "Queue account binding test",
                    Fingerprint = "fingerprint"
                }
            });

            Assert.Throws<InvalidOperationException>(() => store.SetSteamUserId("another-account"));
            Assert.Equal("1", store.SteamUserId);
        }
        finally
        {
            var tempRoot = Path.GetFullPath(Path.GetTempPath());
            var target = Path.GetFullPath(path);
            Assert.StartsWith(tempRoot, target, StringComparison.OrdinalIgnoreCase);
            Directory.Delete(target, recursive: true);
        }
    }
}
