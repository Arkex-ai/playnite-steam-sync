using Playnite.SDK;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace SteamShortcutsImporter;

internal class BackupManager
{
    private static readonly ILogger Logger = LogManager.GetLogger();
    private static readonly object ArtworkBackupLock = new object();
    private static BackupManager? Instance;

    public static BackupManager? InstanceStatic => Instance;
    
    private readonly ShortcutsLibrary _library;

    public BackupManager(ShortcutsLibrary library)
    {
        _library = library;
        Instance = this;
    }

    internal string GetBackupRootDir()
    {
        try
        {
            return Path.Combine(_library.GetPluginUserDataPath(), Constants.BackupsDirectory);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to get backup root dir.");
            return string.Empty;
        }
    }

    internal static string? TryGetBackupRootStatic()
    {
        return Instance?.GetBackupRootDir();
    }

    /// <summary>
    /// Gets the backup folder path for a specific Steam user.
    /// </summary>
    internal string GetBackupFolderForUser(string userId)
    {
        return Path.Combine(GetBackupRootDir(), userId);
    }

    /// <summary>
    /// Gets the backup folder for a specific Steam user (static version for settings view).
    /// </summary>
    internal static string? TryGetBackupFolderForUserStatic(string userId)
    {
        return Instance?.GetBackupFolderForUser(userId);
    }

    internal static bool TryCreateManagedArtworkBackupStatic(string sourceFilePath, string userId, string backupFileName, out string? backupPath)
    {
        var instance = Instance;
        if (instance == null)
        {
            backupPath = null;
            return false;
        }
        return instance.TryCreateManagedArtworkBackup(sourceFilePath, userId, backupFileName, out backupPath);
    }

    private bool TryCreateManagedArtworkBackup(string sourceFilePath, string userId, string backupFileName, out string? backupPath)
    {
        backupPath = null;
        string? tempPath = null;
        try
        {
            if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath) || string.IsNullOrWhiteSpace(userId)) return false;
            var safeName = Path.GetFileName(backupFileName);
            if (!string.Equals(safeName, backupFileName, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(safeName)) return false;

            lock (ArtworkBackupLock)
            {
                var backupRoot = GetBackupRootDir();
                if (string.IsNullOrWhiteSpace(backupRoot)) return false;
                var dir = Path.Combine(backupRoot, userId, "artwork");
                Directory.CreateDirectory(dir);
                var hash = GetFileHash(sourceFilePath);
                var destination = Path.Combine(dir, safeName + "." + hash + ".bak");
                if (File.Exists(destination))
                {
                    if (!FilesMatch(sourceFilePath, destination)) return false;
                    backupPath = destination;
                    return true;
                }

                tempPath = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.Copy(sourceFilePath, tempPath, overwrite: false);
                if (!FilesMatch(sourceFilePath, tempPath)) throw new IOException("The managed artwork backup did not match its source.");
                File.Move(tempPath, destination);
                tempPath = null;
                if (!FilesMatch(sourceFilePath, destination)) throw new IOException("The managed artwork backup failed post-write verification.");
                backupPath = destination;
                return true;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"Failed to create managed artwork backup for '{sourceFilePath}'.");
            return false;
        }
        finally
        {
            try { if (!string.IsNullOrEmpty(tempPath) && File.Exists(tempPath)) File.Delete(tempPath); }
            catch (Exception ex) { Logger.Warn(ex, "Failed to remove an incomplete artwork backup."); }
        }
    }

    private static string GetFileHash(string path)
    {
        using var sha = SHA256.Create();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
    }

    /// <summary>
    /// Restores a backup file to the shortcuts.vdf for the specified user.
    /// Creates a backup of the current shortcuts.vdf before restoring.
    /// </summary>
    internal bool RestoreBackup(string backupFilePath, string userId)
    {
        try
        {
            if (!File.Exists(backupFilePath))
            {
                Logger.Warn($"Backup file not found: {backupFilePath}");
                return false;
            }

            var targetVdfPath = GetShortcutsVdfPathForUser(userId);
            if (string.IsNullOrEmpty(targetVdfPath))
            {
                Logger.Warn($"Could not determine shortcuts.vdf path for user {userId}");
                return false;
            }

            var restored = RestoreBackupFile(
                backupFilePath,
                targetVdfPath!,
                path => TryCreateManagedBackup(path, userId, out _),
                IsSteamClosed);
            if (restored) Logger.Info($"Restored backup '{backupFilePath}' to '{targetVdfPath}'");
            return restored;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to restore backup '{backupFilePath}'");
            return false;
        }
    }

    internal static bool RestoreBackupFile(
        string backupFilePath,
        string targetVdfPath,
        Func<string, bool> createBackup,
        Func<bool> canReplace)
    {
        string? tempPath = null;
        try
        {
            if (string.IsNullOrWhiteSpace(backupFilePath) || !File.Exists(backupFilePath)
                || string.IsNullOrWhiteSpace(targetVdfPath) || createBackup == null || canReplace == null)
            {
                return false;
            }

            if (!canReplace()) return false;
            _ = ShortcutsFile.Read(backupFilePath).ToList();

            var targetDir = Path.GetDirectoryName(targetVdfPath);
            if (string.IsNullOrWhiteSpace(targetDir)) return false;
            Directory.CreateDirectory(targetDir);

            tempPath = targetVdfPath + "." + Guid.NewGuid().ToString("N") + ".restore.tmp";
            File.Copy(backupFilePath, tempPath);
            _ = ShortcutsFile.Read(tempPath).ToList();

            if (File.Exists(targetVdfPath) && !createBackup(targetVdfPath))
            {
                return false;
            }

            if (!canReplace()) return false;

            if (File.Exists(targetVdfPath))
            {
                File.Replace(tempPath, targetVdfPath, null, true);
            }
            else
            {
                File.Move(tempPath, targetVdfPath);
            }

            tempPath = null;
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to restore a validated shortcuts.vdf backup.");
            return false;
        }
        finally
        {
            if (!string.IsNullOrEmpty(tempPath) && File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch (Exception ex) { Logger.Warn(ex, $"Failed to delete temporary restore file '{tempPath}'."); }
            }
        }
    }

    private static bool IsSteamClosed()
    {
        return SteamProcessHelper.TryIsSteamRunning(out var running) && !running;
    }

    /// <summary>
    /// Restores a backup file (static version for settings view).
    /// </summary>
    internal static bool RestoreBackupStatic(string backupFilePath, string userId)
    {
        return Instance?.RestoreBackup(backupFilePath, userId) ?? false;
    }

    internal void CreateManagedBackup(string sourceFilePath, string userId)
    {
        TryCreateManagedBackup(sourceFilePath, userId, out _);
    }

    internal bool TryCreateManagedBackup(string sourceFilePath, string userId, out string? backupPath)
    {
        backupPath = null;
        string? createdBackupPath = null;
        try
        {
            if (string.IsNullOrEmpty(sourceFilePath) || !File.Exists(sourceFilePath)) return false;
            
            // Use new structure: backups/{userId}/
            var dir = GetBackupFolderForUser(userId);
            if (string.IsNullOrEmpty(dir)) return false;
            Directory.CreateDirectory(dir);

            string ts = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfffffff'Z'");
            string backupName = string.Format(Constants.BackupFilenameFormat, ts);
            string dst = Path.Combine(dir, backupName);
            createdBackupPath = dst;
            File.Copy(sourceFilePath, dst, overwrite: true);
            var sourceInfo = new FileInfo(sourceFilePath);
            var backupInfo = new FileInfo(dst);
            if (!backupInfo.Exists || sourceInfo.Length != backupInfo.Length || !FilesMatch(sourceFilePath, dst))
            {
                throw new IOException("The managed backup did not match the source file.");
            }

            backupPath = dst;

            // Keep last 5 backups for this user
            var files = new DirectoryInfo(dir)
                .GetFiles(Constants.BackupFileSearchPattern)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
            for (int i = 5; i < files.Count; i++)
            {
                try { files[i].Delete(); } catch (Exception ex) { Logger.Warn(ex, $"Failed to delete old backup '{files[i].FullName}'"); }
            }
            return true;
        }
        catch (Exception ex)
        {
            try
            {
                if (!string.IsNullOrEmpty(createdBackupPath) && File.Exists(createdBackupPath)) File.Delete(createdBackupPath);
            }
            catch (Exception cleanupEx)
            {
                Logger.Warn(cleanupEx, "Failed to remove an incomplete managed backup.");
            }
            Logger.Warn(ex, $"Failed to create managed backup for '{sourceFilePath}'");
            return false;
        }
    }

    private static bool FilesMatch(string leftPath, string rightPath)
    {
        using var sha = SHA256.Create();
        using var left = new FileStream(leftPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var leftHash = sha.ComputeHash(left);
        sha.Initialize();
        using var right = new FileStream(rightPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var rightHash = sha.ComputeHash(right);
        return leftHash.SequenceEqual(rightHash);
    }

    internal static string? TryGetSteamUserFromPath(string path)
    {
        try
        {
            var parts = path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            int idx = Array.FindIndex(parts, p => string.Equals(p, Constants.UserDataDirectory, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0 && idx + 1 < parts.Length)
            {
                return parts[idx + 1];
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to get steam user from path.");
        }
        return null;
    }

    private string? GetShortcutsVdfPathForUser(string userId)
    {
        try
        {
            var root = _library.Settings.SteamRootPath;
            if (string.IsNullOrWhiteSpace(root)) return null;

            // Convert SteamID64 to userdata folder account ID
            string userDataFolderId = userId;
            if (Utils.TryConvertSteamId64ToUserDataId(userId, out var accountId))
            {
                userDataFolderId = accountId;
            }

            var vdfPath = Path.Combine(root, Constants.UserDataDirectory, userDataFolderId, Constants.ConfigDirectory, "shortcuts.vdf");
            return vdfPath;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"Failed to get shortcuts.vdf path for user {userId}.");
            return null;
        }
    }

    internal void WriteShortcutsWithBackup(string vdfPath, List<SteamShortcut> shortcuts, string? expectedFingerprint = null)
    {
        WriteShortcutsWithBackupCore(
            vdfPath,
            shortcuts,
            expectedFingerprint,
            path =>
            {
                var userId = TryGetSteamUserFromPath(path) ?? Constants.DefaultUserSegment;
                return TryCreateManagedBackup(path, userId, out _);
            },
            () => SteamProcessHelper.TryIsSteamRunning(out var running) && !running);
    }

    internal static void WriteShortcutsWithBackupCore(
        string vdfPath,
        List<SteamShortcut> shortcuts,
        string? expectedFingerprint,
        Func<string, bool> createVerifiedBackup,
        Func<bool> canReplace)
    {
        if (createVerifiedBackup == null) throw new ArgumentNullException(nameof(createVerifiedBackup));
        if (canReplace == null) throw new ArgumentNullException(nameof(canReplace));

        var fingerprint = expectedFingerprint ?? ShortcutsFile.GetFileFingerprint(vdfPath);
        if (!string.Equals(fingerprint, ShortcutsFile.GetFileFingerprint(vdfPath), StringComparison.Ordinal))
        {
            throw new IOException("shortcuts.vdf changed during export; no changes were written.");
        }

        if (File.Exists(vdfPath))
        {
            if (!createVerifiedBackup(vdfPath))
            {
                throw new IOException("A verified backup of shortcuts.vdf could not be created.");
            }
        }

        ShortcutsFile.WriteAtomic(vdfPath, shortcuts, () =>
            canReplace()
            && string.Equals(fingerprint, ShortcutsFile.GetFileFingerprint(vdfPath), StringComparison.Ordinal));
    }
}
