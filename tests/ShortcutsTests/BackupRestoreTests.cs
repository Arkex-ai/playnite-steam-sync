using System;
using System.IO;
using System.Linq;
using Xunit;

namespace SteamShortcutsImporter.Tests;

public class BackupRestoreTests
{
    [Fact]
    public void GetFileFingerprint_IsStableAndDetectsTargetChanges()
    {
        using var temp = new TemporaryDirectory();
        var targetFile = Path.Combine(temp.Path, "shortcuts.vdf");
        ShortcutsFile.Write(targetFile, new[] { CreateShortcut("Current game", 202) });

        var original = ShortcutsFile.GetFileFingerprint(targetFile);
        var unchanged = ShortcutsFile.GetFileFingerprint(targetFile);
        var bytes = File.ReadAllBytes(targetFile);
        File.WriteAllBytes(targetFile, bytes.Concat(new byte[] { 0x7F }).ToArray());
        var changed = ShortcutsFile.GetFileFingerprint(targetFile);

        Assert.NotEmpty(original);
        Assert.Equal(original, unchanged);
        Assert.NotEqual(original, changed);
        Assert.Empty(ShortcutsFile.GetFileFingerprint(Path.Combine(temp.Path, "missing.vdf")));
    }

    [Fact]
    public void RestoreBackupFile_ValidatesCreatesCurrentBackupAndReplacesAtomically()
    {
        using var temp = new TemporaryDirectory();
        var backupFile = Path.Combine(temp.Path, "restore.bak.vdf");
        var targetFile = Path.Combine(temp.Path, "steam", "userdata", "123", "config", "shortcuts.vdf");
        var currentBackup = Path.Combine(temp.Path, "current-before-restore.vdf");
        ShortcutsFile.Write(backupFile, new[] { CreateShortcut("Restored game", 101) });
        ShortcutsFile.Write(targetFile, new[] { CreateShortcut("Current game", 202) });
        var createdCurrentBackup = false;

        var restored = BackupManager.RestoreBackupFile(
            backupFile,
            targetFile,
            path =>
            {
                File.Copy(path, currentBackup, overwrite: true);
                createdCurrentBackup = true;
                return true;
            },
            () => true);

        Assert.True(restored);
        Assert.True(createdCurrentBackup);
        Assert.Equal("Restored game", Assert.Single(ShortcutsFile.Read(targetFile)).AppName);
        Assert.Equal("Current game", Assert.Single(ShortcutsFile.Read(currentBackup)).AppName);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(targetFile)!, "*.restore.tmp"));
    }

    [Fact]
    public void RestoreBackupFile_InvalidBackupLeavesCurrentFileUntouched()
    {
        using var temp = new TemporaryDirectory();
        var backupFile = Path.Combine(temp.Path, "invalid.bak.vdf");
        var targetFile = Path.Combine(temp.Path, "shortcuts.vdf");
        File.WriteAllBytes(backupFile, new byte[] { 0x00, 0x01, 0x02 });
        ShortcutsFile.Write(targetFile, new[] { CreateShortcut("Current game", 202) });
        var original = File.ReadAllBytes(targetFile);
        var backupAttempted = false;

        var restored = BackupManager.RestoreBackupFile(
            backupFile,
            targetFile,
            _ => { backupAttempted = true; return true; },
            () => true);

        Assert.False(restored);
        Assert.False(backupAttempted);
        Assert.Equal(original, File.ReadAllBytes(targetFile));
    }

    [Fact]
    public void RestoreBackupFile_WhenSteamCannotBeConfirmedClosedLeavesTargetUntouched()
    {
        using var temp = new TemporaryDirectory();
        var backupFile = Path.Combine(temp.Path, "restore.bak.vdf");
        var targetFile = Path.Combine(temp.Path, "shortcuts.vdf");
        ShortcutsFile.Write(backupFile, new[] { CreateShortcut("Restored game", 101) });
        ShortcutsFile.Write(targetFile, new[] { CreateShortcut("Current game", 202) });
        var original = File.ReadAllBytes(targetFile);
        var backupAttempted = false;

        var restored = BackupManager.RestoreBackupFile(
            backupFile,
            targetFile,
            _ => { backupAttempted = true; return true; },
            () => false);

        Assert.False(restored);
        Assert.False(backupAttempted);
        Assert.Equal(original, File.ReadAllBytes(targetFile));
    }

    [Fact]
    public void RestoreBackupFile_BackupFailureLeavesTargetUntouched()
    {
        using var temp = new TemporaryDirectory();
        var backupFile = Path.Combine(temp.Path, "restore.bak.vdf");
        var targetFile = Path.Combine(temp.Path, "shortcuts.vdf");
        ShortcutsFile.Write(backupFile, new[] { CreateShortcut("Restored game", 101) });
        ShortcutsFile.Write(targetFile, new[] { CreateShortcut("Current game", 202) });
        var original = File.ReadAllBytes(targetFile);

        var restored = BackupManager.RestoreBackupFile(backupFile, targetFile, _ => false, () => true);

        Assert.False(restored);
        Assert.Equal(original, File.ReadAllBytes(targetFile));
    }

    [Fact]
    public void WriteShortcutsWithBackupCore_WhenVerifiedBackupFails_LeavesOriginalUntouched()
    {
        using var temp = new TemporaryDirectory();
        var targetFile = Path.Combine(temp.Path, "shortcuts.vdf");
        ShortcutsFile.Write(targetFile, new[] { CreateShortcut("Current game", 202) });
        var original = File.ReadAllBytes(targetFile);
        var backupAttempted = false;

        Assert.Throws<IOException>(() => BackupManager.WriteShortcutsWithBackupCore(
            targetFile,
            new[] { CreateShortcut("Changed game", 303) }.ToList(),
            ShortcutsFile.GetFileFingerprint(targetFile),
            _ => { backupAttempted = true; return false; },
            () => true));

        Assert.True(backupAttempted);
        Assert.Equal(original, File.ReadAllBytes(targetFile));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(targetFile)!, Path.GetFileName(targetFile) + ".*.tmp"));
    }

    [Fact]
    public void WriteShortcutsWithBackupCore_WhenTargetChangesDuringBackup_PreservesConcurrentFileWithoutCallerFingerprint()
    {
        using var temp = new TemporaryDirectory();
        var targetFile = Path.Combine(temp.Path, "shortcuts.vdf");
        ShortcutsFile.Write(targetFile, new[] { CreateShortcut("Current game", 202) });
        var concurrentShortcut = CreateShortcut("Concurrent game", 404);
        var backupAttempted = false;

        Assert.Throws<OperationCanceledException>(() => BackupManager.WriteShortcutsWithBackupCore(
            targetFile,
            new[] { CreateShortcut("Changed game", 303) }.ToList(),
            expectedFingerprint: null,
            path =>
            {
                backupAttempted = true;
                ShortcutsFile.Write(path, new[] { concurrentShortcut });
                return true;
            },
            () => true));

        Assert.True(backupAttempted);
        Assert.Equal("Concurrent game", Assert.Single(ShortcutsFile.Read(targetFile)).AppName);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(targetFile)!, Path.GetFileName(targetFile) + ".*.tmp"));
    }

    [Fact]
    public void WriteShortcutsWithBackupCore_WhenVerifiedBackupSucceeds_StoresOriginalThenReplacesTarget()
    {
        using var temp = new TemporaryDirectory();
        var targetFile = Path.Combine(temp.Path, "shortcuts.vdf");
        var backupFile = Path.Combine(temp.Path, "verified-backup.vdf");
        ShortcutsFile.Write(targetFile, new[] { CreateShortcut("Current game", 202) });
        var original = File.ReadAllBytes(targetFile);
        var backupCreated = false;

        BackupManager.WriteShortcutsWithBackupCore(
            targetFile,
            new[] { CreateShortcut("Updated game", 303) }.ToList(),
            expectedFingerprint: null,
            path =>
            {
                File.Copy(path, backupFile);
                backupCreated = true;
                return File.ReadAllBytes(backupFile).SequenceEqual(original);
            },
            () => true);

        Assert.True(backupCreated);
        Assert.Equal(original, File.ReadAllBytes(backupFile));
        Assert.Equal("Updated game", Assert.Single(ShortcutsFile.Read(targetFile)).AppName);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(targetFile)!, Path.GetFileName(targetFile) + ".*.tmp"));
    }

    private static SteamShortcut CreateShortcut(string name, uint appId)
    {
        return new SteamShortcut { AppName = name, Exe = name + ".exe", AppId = appId };
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NonSteamSyncBackupTests", Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, true);
        }
    }
}
