using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace SteamShortcutsImporter;

/// <summary>
/// Manages artwork import/export between Playnite and Steam grid folders.
/// </summary>
internal class ArtworkManager
{
    private static readonly ILogger Logger = LogManager.GetLogger();
    private readonly IPlayniteAPI _playniteApi;

    public ArtworkManager(IPlayniteAPI playniteApi)
    {
        _playniteApi = playniteApi ?? throw new ArgumentNullException(nameof(playniteApi));
    }

    /// <summary>
    /// Gets the grid directory path from a shortcuts.vdf path.
    /// </summary>
    public string? TryGetGridDirFromVdf(string vdfPath)
    {
        try
        {
            var cfgDir = Path.GetDirectoryName(vdfPath);
            if (string.IsNullOrEmpty(cfgDir)) return null;
            return Path.Combine(cfgDir, "grid");
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to get grid dir from vdf.");
            return null;
        }
    }

    /// <summary>
    /// Exports game artwork (cover, icon, background, plus a logo proxy) to the Steam grid folder.
    /// </summary>
    public bool TryExportArtworkToGrid(Playnite.SDK.Models.Game game, uint appId, string? gridDir)
    {
        if (appId == 0 || string.IsNullOrEmpty(gridDir)) return false;

        try
        {
            Directory.CreateDirectory(gridDir);
            var targets = GetExportTargets(game, appId).ToList();
            if (targets.Count == 0) return true;

            var configDir = Directory.GetParent(gridDir)?.FullName;
            if (string.IsNullOrWhiteSpace(configDir)) return false;
            var vdfPath = Path.Combine(configDir, "shortcuts.vdf");
            var userId = BackupManager.TryGetSteamUserFromPath(vdfPath) ?? Constants.DefaultUserSegment;
            var allSourcesSynchronized = true;

            foreach (var (dbPath, targetBase) in targets)
            {
                if (string.IsNullOrEmpty(dbPath)) continue;
                var src = _playniteApi.Database.GetFullFilePath(dbPath);
                if (string.IsNullOrEmpty(src) || !File.Exists(src))
                {
                    allSourcesSynchronized = false;
                    continue;
                }
                if (!TrySynchronizeArtworkAsset(src, gridDir!, targetBase, existingPath =>
                    BackupManager.TryCreateManagedArtworkBackupStatic(existingPath, userId, Path.GetFileName(existingPath), out _)))
                {
                    allSourcesSynchronized = false;
                }
            }
            return allSourcesSynchronized;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"Failed exporting artwork to grid for appId={appId}");
            return false;
        }
    }

    internal static bool TrySynchronizeArtworkAsset(string sourcePath, string gridDir, string targetBase, Func<string, bool> backupExistingFile)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath) ||
            string.IsNullOrWhiteSpace(gridDir) || string.IsNullOrWhiteSpace(targetBase) || backupExistingFile == null)
        {
            return false;
        }

        string? stagedPath = null;
        try
        {
            Directory.CreateDirectory(gridDir);
            var sourceHash = GetFileHash(sourcePath);
            var extension = Path.GetExtension(sourcePath);
            var destinationPath = Path.Combine(gridDir, targetBase + extension);
            var existingVariants = GetTargetVariants(gridDir, targetBase);

            if (File.Exists(destinationPath) && HashesMatch(sourceHash, GetFileHash(destinationPath)))
            {
                foreach (var stalePath in existingVariants.Where(path => !PathsEqual(path, destinationPath)))
                {
                    if (!backupExistingFile(stalePath)) return false;
                    File.Delete(stalePath);
                }

                return HashesMatch(sourceHash, GetFileHash(sourcePath)) &&
                    HashesMatch(sourceHash, GetFileHash(destinationPath));
            }

            stagedPath = Path.Combine(gridDir, ".nss-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.Copy(sourcePath, stagedPath, overwrite: false);
            var copiedHash = GetFileHash(stagedPath);
            if (!HashesMatch(sourceHash, copiedHash) || !HashesMatch(sourceHash, GetFileHash(sourcePath)))
            {
                return false;
            }

            foreach (var existingPath in existingVariants)
            {
                if (!backupExistingFile(existingPath)) return false;
            }

            if (File.Exists(destinationPath))
            {
                File.Replace(stagedPath, destinationPath, null, true);
            }
            else
            {
                File.Move(stagedPath, destinationPath);
            }
            stagedPath = null;

            if (!HashesMatch(sourceHash, GetFileHash(sourcePath)) ||
                !HashesMatch(sourceHash, GetFileHash(destinationPath)))
            {
                return false;
            }

            foreach (var stalePath in existingVariants.Where(path => !PathsEqual(path, destinationPath)))
            {
                if (File.Exists(stalePath)) File.Delete(stalePath);
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"Failed to synchronize artwork asset '{sourcePath}' to slot '{targetBase}'.");
            return false;
        }
        finally
        {
            try { if (!string.IsNullOrEmpty(stagedPath) && File.Exists(stagedPath)) File.Delete(stagedPath); }
            catch (Exception ex) { Logger.Warn(ex, "Failed to remove an incomplete staged artwork file."); }
        }
    }

    private static List<string> GetTargetVariants(string gridDir, string targetBase)
    {
        return Directory.GetFiles(gridDir, targetBase + ".*", SearchOption.TopDirectoryOnly)
            .Where(path => string.Equals(Path.GetFileNameWithoutExtension(path), targetBase, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    }

    private static bool HashesMatch(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetFileHash(string path)
    {
        using var sha = SHA256.Create();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
    }

    /// <summary>
    /// Maps a Playnite game's artwork fields to the Steam grid filename bases (no extension)
    /// they should be copied to. Playnite has no logo field, so the game icon is reused as the
    /// closest emblem-shaped proxy for Steam's logo slot.
    /// </summary>
    internal static IEnumerable<(string Source, string TargetBase)> GetExportTargets(Playnite.SDK.Models.Game game, uint appId)
    {
        if (!string.IsNullOrEmpty(game.CoverImage))
        {
            yield return (game.CoverImage, appId.ToString());
            yield return (game.CoverImage, appId + "p");
        }

        if (!string.IsNullOrEmpty(game.Icon))
        {
            yield return (game.Icon, appId + "_icon");
            // Playnite has no logo field; reuse the icon for Steam's logo slot (#31).
            yield return (game.Icon, appId + "_logo");
        }

        if (!string.IsNullOrEmpty(game.BackgroundImage))
        {
            yield return (game.BackgroundImage, appId + "_hero");
        }
    }

    /// <summary>
    /// Imports artwork from Steam grid folder to Playnite game.
    /// </summary>
    public void TryImportArtworkFromGrid(Playnite.SDK.Models.Game game, uint appId, string gridDir)
    {
        try
        {
            if (appId == 0 || !Directory.Exists(gridDir)) return;

            string[] hero = Directory.GetFiles(gridDir, appId + "_hero.*", SearchOption.TopDirectoryOnly);
            string[] icon = Directory.GetFiles(gridDir, appId + "_icon.*", SearchOption.TopDirectoryOnly);
            string[] cover = Directory.GetFiles(gridDir, appId + ".*", SearchOption.TopDirectoryOnly);
            string[] poster = Directory.GetFiles(gridDir, appId + "p.*", SearchOption.TopDirectoryOnly);

            string? Pick(string[] arr) => arr.FirstOrDefault();

            var bg = Pick(hero);
            var ic = Pick(icon);
            var cv = Pick(poster.Length > 0 ? poster : cover);

            if (!string.IsNullOrEmpty(bg)) game.BackgroundImage = _playniteApi.Database.AddFile(bg, game.Id);
            if (!string.IsNullOrEmpty(ic)) game.Icon = _playniteApi.Database.AddFile(ic, game.Id);
            if (!string.IsNullOrEmpty(cv)) game.CoverImage = _playniteApi.Database.AddFile(cv, game.Id);

            _playniteApi.Database.Games.Update(game);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"Failed importing artwork from grid for appId={appId}");
        }
    }

    /// <summary>
    /// Finds the best preview image from Steam grid for a given appId.
    /// Priority: hero > poster > cover > icon
    /// </summary>
    public string? TryPickGridPreview(uint appId, string? gridDir)
    {
        try
        {
            if (appId == 0 || string.IsNullOrEmpty(gridDir) || !Directory.Exists(gridDir)) return null;

            string[] hero = Directory.GetFiles(gridDir, appId + "_hero.*", SearchOption.TopDirectoryOnly);
            string[] poster = Directory.GetFiles(gridDir, appId + "p.*", SearchOption.TopDirectoryOnly);
            string[] cover = Directory.GetFiles(gridDir, appId + ".*", SearchOption.TopDirectoryOnly);
            string[] icon = Directory.GetFiles(gridDir, appId + "_icon.*", SearchOption.TopDirectoryOnly);

            return hero.FirstOrDefault() ?? poster.FirstOrDefault() ?? cover.FirstOrDefault() ?? icon.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to pick grid preview.");
            return null;
        }
    }

    /// <summary>
    /// Finds the best preview image from a Playnite game.
    /// Priority: cover > icon > background
    /// </summary>
    public string? TryPickPlaynitePreview(Playnite.SDK.Models.Game game)
    {
        try
        {
            string? path = null;
            if (!string.IsNullOrEmpty(game.CoverImage))
            {
                path = _playniteApi.Database.GetFullFilePath(game.CoverImage);
            }
            if (string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(game.Icon))
            {
                path = _playniteApi.Database.GetFullFilePath(game.Icon);
            }
            if (string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(game.BackgroundImage))
            {
                path = _playniteApi.Database.GetFullFilePath(game.BackgroundImage);
            }
            return File.Exists(path ?? string.Empty) ? path : null;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to pick playnite preview.");
            return null;
        }
    }

    /// <summary>
    /// Gets the icon file path for a given appId from Steam grid.
    /// </summary>
    public string? TryGetGridIconPath(uint appId, string? gridDir)
    {
        try
        {
            if (appId == 0 || string.IsNullOrEmpty(gridDir) || !Directory.Exists(gridDir)) return null;
            var matches = Directory.GetFiles(gridDir, appId + "_icon.*", SearchOption.TopDirectoryOnly);
            return matches.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to get grid icon path.");
            return null;
        }
    }
}
