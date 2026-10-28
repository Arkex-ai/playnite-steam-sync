using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace SteamShortcutsImporter;

internal class ImportExportService
{
    private static readonly ILogger Logger = LogManager.GetLogger();
    private readonly ShortcutsLibrary _library;
    private readonly ArtworkManager _artworkManager;
    private readonly SteamPathResolver _pathResolver;
    private readonly SelectionDialogBuilder _dialogBuilder;
    private readonly BackupManager _backupManager;

    internal ImportExportService(
        ShortcutsLibrary library,
        ArtworkManager artworkManager,
        SteamPathResolver pathResolver,
        SelectionDialogBuilder dialogBuilder,
        BackupManager backupManager)
    {
        _library = library;
        _artworkManager = artworkManager;
        _pathResolver = pathResolver;
        _dialogBuilder = dialogBuilder;
        _backupManager = backupManager;
    }

    /// <summary>
    /// Shows the import dialog for Steam shortcuts to Playnite.
    /// </summary>
    public void ShowImportDialog()
    {
        var vdfPath = _pathResolver.ResolveShortcutsVdfPathForUser(_library.Settings.SelectedSteamUserId);
        if (string.IsNullOrWhiteSpace(vdfPath) || !File.Exists(vdfPath))
        {
            _library.PlayniteApi.Dialogs.ShowErrorMessage(Constants.SteamPathRequiredMessage, _library.Name);
            return;
        }
        try
        {
            var shortcuts = ShortcutsFile.Read(vdfPath!).ToList();
            var detector = new DuplicateDetector(_library, _pathResolver);
            var gridDir = _artworkManager.TryGetGridDirFromVdf(vdfPath!);

            _dialogBuilder.ShowSelectionDialog(
                title: Constants.ImportDialogTitle,
                items: shortcuts.OrderBy(s => s.AppName, StringComparer.OrdinalIgnoreCase).ToList(),
                displayText: sc =>
                {
                    var target = LooksLikeUrl(sc.LaunchOptions) ? sc.LaunchOptions : (sc.Exe ?? string.Empty).Trim('"');
                    var existsAny = detector.ExistsAnyGameMatch(sc);
                    var baseText = $"{sc.AppName} — {target}";
                    return existsAny ? baseText + Constants.PlayniteTag : baseText;
                },
                previewImage: sc => _artworkManager.TryPickGridPreview(sc.AppId, gridDir),
                isInitiallyChecked: sc =>
                {
                    var existsAny = detector.ExistsAnyGameMatch(sc);
                    var isAlready = !string.IsNullOrEmpty(sc.StableId) && _library.PlayniteApi.Database.Games.Any(g => g.PluginId == _library.Id && string.Equals(g.GameId, sc.StableId, StringComparison.OrdinalIgnoreCase));
                    return !isAlready && !existsAny;
                },
                isNew: sc =>
                {
                    var existsAny = detector.ExistsAnyGameMatch(sc);
                    var isAlready = !string.IsNullOrEmpty(sc.StableId) && _library.PlayniteApi.Database.Games.Any(g => g.PluginId == _library.Id && string.Equals(g.GameId, sc.StableId, StringComparison.OrdinalIgnoreCase));
                    return !isAlready && !existsAny;
                },
                confirmLabel: Constants.ImportConfirmLabel,
                onConfirm: selected =>
                {
                    var imported = ImportShortcutsToPlaynite(selected, vdfPath!, out var skipped);
                    var msg = skipped > 0 ? $"Imported {imported} item(s). Skipped {skipped} existing item(s)." : $"Imported {imported} item(s) from Steam.";
                    _library.PlayniteApi.Dialogs.ShowMessage(msg, _library.Name);
                });
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to import from shortcuts.vdf");
            _library.PlayniteApi.Dialogs.ShowErrorMessage($"Import failed: {ex.Message}", _library.Name);
        }
    }

    private int ImportShortcutsToPlaynite(List<SteamShortcut> shortcuts, string vdfPath, out int skipped)
    {
        var existingById = _library.PlayniteApi.Database.Games
            .Where(g => g.PluginId == _library.Id && !string.IsNullOrEmpty(g.GameId))
            .ToDictionary(g => g.GameId, g => g, StringComparer.OrdinalIgnoreCase);

        // Get or create the source for Steam Shortcuts
        var source = _library.PlayniteApi.Database.Sources.FirstOrDefault(s => 
            string.Equals(s.Name, Constants.SteamShortcutsSourceName, StringComparison.OrdinalIgnoreCase));
        if (source == null)
        {
            source = new GameSource(Constants.SteamShortcutsSourceName);
            _library.PlayniteApi.Database.Sources.Add(source);
        }

        var newGames = new List<Game>();
        var detector = new DuplicateDetector(_library, _pathResolver);
        skipped = 0;
        foreach (var sc in shortcuts)
        {
            // Skip if an equivalent game already exists in Playnite (any library)
            if (detector.ExistsAnyGameMatch(sc))
            {
                skipped++; continue;
            }
            var id = string.IsNullOrEmpty(sc.StableId) ? sc.AppId.ToString() : sc.StableId;
            if (string.IsNullOrEmpty(id) || existingById.ContainsKey(id))
            {
                skipped++; continue;
            }
            var g = new Game
            {
                PluginId = _library.Id,
                GameId = id,
                Name = sc.AppName,
                SourceId = source.Id,
                InstallDirectory = string.IsNullOrEmpty(sc.StartDir) ? null : sc.StartDir,
                IsInstalled = true,
                GameActions = new ObservableCollection<GameAction>(_library.BuildActionsForShortcut(sc))
            };
            
            newGames.Add(g);
        }

        if (newGames.Count > 0)
        {
            _library.PlayniteApi.Database.Games.Add(newGames);
            try
            {
                var gridDir = _artworkManager.TryGetGridDirFromVdf(vdfPath);
                if (!string.IsNullOrEmpty(gridDir) && Directory.Exists(gridDir))
                {
                    foreach (var g in newGames)
                    {
                        var sc = shortcuts.FirstOrDefault(s => s.StableId == g.GameId || s.AppId.ToString() == g.GameId);
                        if (sc != null)
                        {
                            _artworkManager.TryImportArtworkFromGrid(g, sc.AppId, gridDir!);
                        }
                    }
                }
            }
            catch (Exception aex)
            {
                Logger.Warn(aex, "Artwork import from grid failed.");
            }
        }
        return newGames.Count;
    }

    /// <summary>
    /// Shows the export dialog for Playnite games to Steam.
    /// </summary>
    public void ShowAddToSteamDialog()
    {
        var vdfPath = _pathResolver.ResolveShortcutsVdfPathForUser(_library.Settings.SelectedSteamUserId);
        if (string.IsNullOrWhiteSpace(vdfPath))
        {
            _library.PlayniteApi.Dialogs.ShowErrorMessage(Constants.SteamPathRequiredMessage, _library.Name);
            return;
        }
        try
        {
            var allGames = _library.PlayniteApi.Database.Games
                .Where(g => !g.Hidden && g.PluginId != _library.Id)
                .Where(g => !AutoSyncHandler.IsSteamSource(g.Source?.Name ?? g.Source?.ToString()))
                .ToList();
            var isInitialBaseline = !_library.IsInitialAutoSyncBaselineComplete;
            var shortcuts = File.Exists(vdfPath) ? ShortcutsFile.Read(vdfPath!).ToList() : new List<SteamShortcut>();
            var existingShortcuts = new Dictionary<uint, SteamShortcut>();
            var existingByStable = new Dictionary<string, SteamShortcut>(StringComparer.OrdinalIgnoreCase);
            foreach (var sc in shortcuts)
            {
                existingShortcuts[sc.AppId] = sc;
                if (!string.IsNullOrEmpty(sc.StableId) && !existingByStable.ContainsKey(sc.StableId))
                {
                    existingByStable[sc.StableId] = sc;
                }
            }
            var byPlayniteId = BuildPlayniteIdLookup();

            var detectionById = new Dictionary<Guid, SelectionCandidate>();
            var detectionByRef = new Dictionary<Game, SelectionCandidate>();

            SelectionCandidate GetCandidate(Game game)
            {
                if (game == null)
                {
                    return SelectionCandidate.Empty;
                }

                if (game.Id != Guid.Empty && detectionById.TryGetValue(game.Id, out var cached))
                {
                    return cached;
                }

                if (game.Id == Guid.Empty && detectionByRef.TryGetValue(game, out cached))
                {
                    return cached;
                }

                var computed = BuildSelectionCandidate(
                    game, existingShortcuts, existingByStable, byPlayniteId, allowExecutableDiscovery: isInitialBaseline);
                if (game.Id != Guid.Empty)
                {
                    detectionById[game.Id] = computed;
                }
                else
                {
                    detectionByRef[game] = computed;
                }
                return computed;
            }

            if (isInitialBaseline)
            {
                var filteredGames = allGames
                    .Where(game => AutoSyncHandler.IsEligibleForInitialFilteredExport(
                        game,
                        _library.Settings,
                        _library.Id,
                        _pathResolver,
                        _library.PlayniteApi.Addons.Plugins,
                        GameActionSelector.GetCurrentProcessExecutablePath(),
                        Logger))
                    .ToList();
                var exportableGames = filteredGames
                    .Where(game => GetCandidate(game).HasSupportedLaunchAction)
                    .ToList();
                if (exportableGames.Count == 0)
                {
                    _library.PlayniteApi.Dialogs.ShowMessage(
                        "No games match the selected source and launch filters with a supported action. The initial baseline was not changed. Choose sources or adjust the filters, then try again.",
                        Constants.ExportDialogTitle);
                    return;
                }

                var unsupportedCount = filteredGames.Count - exportableGames.Count;
                var prompt = $"This is the one-time filtered export that establishes the automatic-sync baseline. It will add or update {exportableGames.Count} game(s) in one Steam VDF write.";
                if (unsupportedCount > 0)
                {
                    prompt += $" {unsupportedCount} matching game(s) have no supported, resolvable launch action and will be skipped.";
                }
                prompt += " Close Steam before continuing. Continue?";

                var confirmation = _library.PlayniteApi.Dialogs.ShowMessage(
                    prompt,
                    Constants.ExportDialogTitle,
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Question);
                if (confirmation == System.Windows.MessageBoxResult.Yes)
                {
                    var result = AddGamesToSteamCore(exportableGames, establishInitialBaseline: true);
                    if (result.Added > 0 || result.Updated > 0 || result.Skipped > 0)
                    {
                        _library.PlayniteApi.Dialogs.ShowMessage(
                            $"Initial filtered export finished. Created: {result.Added}, updated: {result.Updated}, skipped: {result.Skipped}. The baseline is complete only if at least one shortcut was written successfully.",
                            _library.Name);
                    }
                }
                return;
            }

            _dialogBuilder.ShowSelectionDialog(
                title: Constants.ExportDialogTitle,
                items: allGames.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                displayText: g =>
                {
                    var candidate = GetCandidate(g);
                    return string.IsNullOrEmpty(candidate.Label)
                        ? $"{g?.Name ?? string.Empty} — "
                        : candidate.Label;
                },
                previewImage: g => _artworkManager.TryPickPlaynitePreview(g),
                isInitiallyChecked: g =>
                {
                    var candidate = GetCandidate(g);
                    return candidate.ShouldSelect;
                },
                isNew: g =>
                {
                    var candidate = GetCandidate(g);
                    return candidate.IsNew;
                },
                confirmLabel: Constants.ExportConfirmLabel,
                onConfirm: selectedGames =>
                {
                    var res = AddGamesToSteamCore(selectedGames, establishInitialBaseline: false);
                    
                    if (res.Added == 0 && res.Updated == 0 && res.Skipped == 0)
                    {
                        return;
                    }

                    var msg = $"Steam shortcuts updated. Created: {res.Added}, Updated: {res.Updated}, Skipped: {res.Skipped}.";
                    _library.PlayniteApi.Dialogs.ShowMessage(msg, _library.Name);
                });
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Playnite→Steam sync failed");
            _library.PlayniteApi.Dialogs.ShowErrorMessage($"Failed to sync: {ex.Message}", _library.Name);
        }
    }

    /// <summary>
    /// Adds games to Steam shortcuts (for game context menu).
    /// </summary>
    public void AddGamesToSteam(IEnumerable<Game> games)
    {
        var vdfPath = _pathResolver.ResolveShortcutsVdfPathForUser(_library.Settings.SelectedSteamUserId);
        if (string.IsNullOrWhiteSpace(vdfPath))
        {
            _library.PlayniteApi.Dialogs.ShowErrorMessage(Constants.SteamPathRequiredMessage, _library.Name);
            return;
        }

        var res = AddGamesToSteamCore(games);
        
        if (res.Added == 0 && res.Updated == 0 && res.Skipped == 0)
        {
            return;
        }

        var msg = $"Added: {res.Added}, Updated: {res.Updated}";
        if (res.Skipped > 0)
        {
            msg += $", Skipped: {res.Skipped}";
            if (res.SkippedGames.Count > 0 && res.SkippedGames.Count <= 5)
            {
                msg += "\n\nSkipped games:\n• " + string.Join("\n• ", res.SkippedGames);
            }
            else if (res.SkippedGames.Count > 5)
            {
                msg += $"\n\nSkipped games:\n• " + string.Join("\n• ", res.SkippedGames.Take(5));
                msg += $"\n... and {res.SkippedGames.Count - 5} more (see logs for details)";
            }
        }

        _library.PlayniteApi.Dialogs.ShowMessage(msg, _library.Name);
    }

    private Dictionary<string, uint> BuildPlayniteIdLookup()
    {
        var byPlayniteId = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var exportMap = _library.Settings?.ExportMap;
            if (exportMap == null)
            {
                return byPlayniteId;
            }

            foreach (var kv in exportMap)
            {
                if (uint.TryParse(kv.Key, out var appId) && !string.IsNullOrEmpty(kv.Value))
                {
                    byPlayniteId[kv.Value] = appId;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to build inverted export map.");
        }

        return byPlayniteId;
    }

    private SelectionCandidate BuildSelectionCandidate(
        Game game,
        Dictionary<uint, SteamShortcut> existingById,
        Dictionary<string, SteamShortcut> existingByStable,
        Dictionary<string, uint> byPlayniteId,
        bool allowExecutableDiscovery = false)
    {
        if (game == null)
        {
            return SelectionCandidate.Empty;
        }

        try
        {
            var actions = game.GameActions?.Where(a => a != null).ToList() ?? new List<GameAction>();
            var primaryAction = actions.FirstOrDefault(a => a.IsPlayAction) ?? actions.FirstOrDefault();
            var selectedAction = GameActionSelector.GetPreferredLaunchAction(actions);
            if (selectedAction == null && allowExecutableDiscovery && !actions.Any(action => action.IsPlayAction))
            {
                selectedAction = GameActionSelector.TryCreateSingleDynamicPlayAction(
                    game,
                    _library.PlayniteApi.Addons.Plugins,
                    GameActionSelector.GetCurrentProcessExecutablePath(),
                    Logger);
            }
            if (selectedAction == null && allowExecutableDiscovery && !actions.Any(action => action.IsPlayAction))
            {
                selectedAction = GameActionSelector.DiscoverFileAction(game, _pathResolver);
            }
            var fileAction = selectedAction?.Type == GameActionType.File ? selectedAction : null;
            var emulatorAction = selectedAction?.Type == GameActionType.Emulator ? selectedAction : null;
            var resolvedEmulator = emulatorAction != null ? BuildEmulatorActionResult(game, emulatorAction) : default;
            var resolvedEmulatorAction = resolvedEmulator.action;
            var urlAction = selectedAction?.Type == GameActionType.URL ? selectedAction : null;

            string exePath = string.Empty;
            if (fileAction != null)
            {
                exePath = _pathResolver.ExpandPathVariables(game, fileAction.Path) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    exePath = fileAction.Path ?? string.Empty;
                }
            }
            else if (resolvedEmulatorAction != null)
            {
                exePath = resolvedEmulator.exePath ?? resolvedEmulatorAction.Path ?? string.Empty;
            }

            var hasAction = HasPlayableSelectionAction(fileAction, emulatorAction, resolvedEmulatorAction != null, urlAction);

            string name = !string.IsNullOrEmpty(game.Name)
                ? game.Name
                : (!string.IsNullOrEmpty(exePath)
                    ? Path.GetFileNameWithoutExtension(exePath) ?? string.Empty
                    : string.Empty);

            bool exists = false;

            if (game.Id != Guid.Empty && byPlayniteId.TryGetValue(game.Id.ToString(), out var mappedAppId) && mappedAppId != 0)
            {
                exists = existingById.ContainsKey(mappedAppId);
            }

            if (!exists && urlAction != null)
            {
                if (!string.IsNullOrEmpty(urlAction.Path))
                {
                    var maybeAppId = SteamPathResolver.TryParseAppIdFromRungameUrl(urlAction.Path);
                    if (maybeAppId != 0) exists = existingById.ContainsKey(maybeAppId);
                }
            }

            if (!exists && hasAction && !string.IsNullOrEmpty(exePath))
            {
                var stableKey = Utils.HashString($"{exePath}|{name}");
                if (existingByStable.TryGetValue(stableKey, out var match))
                {
                    exists = true;
                }
            }

            if (!exists && hasAction && !string.IsNullOrEmpty(exePath))
            {
                var computed = Utils.GenerateShortcutAppId(exePath, name);
                exists = existingById.ContainsKey(computed);
            }

            var displayAction = fileAction ?? resolvedEmulatorAction ?? urlAction ?? primaryAction;
            var target = displayAction != null
                ? (displayAction.Type == GameActionType.File
                    ? (string.IsNullOrEmpty(exePath) ? displayAction.Path ?? string.Empty : exePath)
                    : displayAction.Path ?? string.Empty)
                : string.Empty;

            // Only offer executable discovery when none of the supported actions resolved.
            var needsExeDiscovery = !hasAction && !string.IsNullOrEmpty(game.InstallDirectory);
            
            // Build display label
            string label;
            if (needsExeDiscovery)
            {
                // Show indicator that this game will prompt for exe selection
                label = name + " — " + Constants.ExeNotSetIndicator;
            }
            else
            {
                label = (name + " — " + target) + (exists ? Constants.SteamTag : string.Empty);
            }
            
            return new SelectionCandidate(label, hasAction || needsExeDiscovery, exists, hasAction);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"Failed to build selection candidate for '{game?.Name}'.");
            return SelectionCandidate.Empty;
        }
    }

    internal static bool HasPlayableSelectionAction(
        GameAction? fileAction,
        GameAction? emulatorAction,
        bool emulatorResolved,
        GameAction? urlAction)
    {
        return (fileAction != null && !string.IsNullOrEmpty(fileAction.Path))
            || (emulatorAction != null && emulatorResolved)
            || (urlAction != null && !string.IsNullOrEmpty(urlAction.Path));
    }

    private ExportResult AddGamesToSteamCore(IEnumerable<Game> games, bool establishInitialBaseline = false)
    {
        var selectedGames = games?.Where(game => game != null).ToList() ?? new List<Game>();
        if (selectedGames.Count == 0)
        {
            Logger.Info("No games were selected; shortcuts.vdf and the initial baseline were left unchanged.");
            return new ExportResult();
        }

        var vdfPath = _pathResolver.ResolveShortcutsVdfPathForUser(_library.Settings.SelectedSteamUserId);
        if (string.IsNullOrWhiteSpace(vdfPath))
        {
            return new ExportResult();
        }

        if (!SteamProcessHelper.TryIsSteamRunning(out var steamRunning))
        {
            _library.PlayniteApi.Dialogs.ShowErrorMessage("Steam process state could not be checked, so shortcuts.vdf was left unchanged.", _library.Name);
            return new ExportResult();
        }

        if (steamRunning)
        {
            _library.PlayniteApi.Dialogs.ShowMessage(Constants.SteamMustBeClosedMessage, _library.Name);
            return new ExportResult();
        }

        int added = 0, updated = 0, skipped = 0;
        var skippedGames = new List<string>();
        var exported = new List<ShortcutsLibrary.ExportedShortcut>();
        try
        {
            var sourceFingerprint = ShortcutsFile.GetFileFingerprint(vdfPath!);
            var shortcuts = File.Exists(vdfPath) ? ShortcutsFile.Read(vdfPath!).ToList() : new List<SteamShortcut>();
            var existing = shortcuts.GroupBy(s => s.AppId).ToDictionary(group => group.Key, group => group.First());
            var existingByStable = shortcuts
                .Where(shortcut => !string.IsNullOrWhiteSpace(shortcut.StableId))
                .GroupBy(shortcut => shortcut.StableId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
            var byPlayniteId = BuildPlayniteIdLookup();
            var batchAppIdOwners = new Dictionary<uint, Guid>();
            foreach (var mapping in byPlayniteId)
            {
                if (Guid.TryParse(mapping.Key, out var mappedGameId) && mappedGameId != Guid.Empty &&
                    !TryClaimBatchAppId(batchAppIdOwners, mappedGameId, mapping.Value))
                {
                    batchAppIdOwners[mapping.Value] = Guid.Empty;
                }
            }
            var context = new ExportContext { SkipAllRemaining = false };

            foreach (var g in selectedGames)
            {
                var (exePath, workDir, name, action, result) = GetGameActionDetailsWithFallback(g, existing, byPlayniteId, context);

                if (result == ExeDiscoveryResult.UserSkippedAll)
                {
                    // User chose "Skip All" - skip remaining games silently
                    skipped++;
                    skippedGames.Add($"{g.Name}: {Constants.SkipReasonUserSkipped}");
                    continue;
                }

                if (action == null || result != ExeDiscoveryResult.Success)
                {
                    skipped++;
                    var reason = GetSkipReason(g, result);
                    skippedGames.Add($"{g.Name}: {reason}");
                    Logger.Debug($"Skipping '{g.Name}': {reason}");
                    continue;
                }

                var appId = CreateOrUpdateShortcut(g, exePath, workDir, name, action, shortcuts, existing, existingByStable, byPlayniteId, batchAppIdOwners, ref added, ref updated);
                var exportedShortcut = shortcuts.FirstOrDefault(x => x.AppId == appId);
                if (exportedShortcut != null)
                {
                    UpdateShortcutArtwork(g, exportedShortcut, vdfPath!);
                    exported.Add(new ShortcutsLibrary.ExportedShortcut { Game = g, Shortcut = exportedShortcut });
                }
                UpdateGameActions(g, appId, exePath, workDir, action);
            }

            if (exported.Count == 0)
            {
                Logger.Info("No selected game produced a valid Steam shortcut; the VDF and initial baseline were left unchanged.");
                return new ExportResult { Added = added, Updated = updated, Skipped = skipped, SkippedGames = skippedGames };
            }

            WriteShortcutsWithBackup(vdfPath!, shortcuts, sourceFingerprint);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Playnite to Steam export failed before it could be completed.");
            _library.PlayniteApi.Dialogs.ShowErrorMessage($"Export failed. The shortcuts file was not confirmed as updated. Details: {ex.Message}", _library.Name);
            return new ExportResult();
        }

        try
        {
            _library.RecordSuccessfulExports(exported, establishInitialBaseline);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Steam shortcuts were written, but the baseline state could not be saved.");
            _library.PlayniteApi.Dialogs.ShowErrorMessage("Steam shortcuts were written, but the automatic-sync baseline could not be saved. Verify the Steam entries before repeating the initial export.", _library.Name);
            return new ExportResult();
        }

        return new ExportResult { Added = added, Updated = updated, Skipped = skipped, SkippedGames = skippedGames };
    }

    private static string GetSkipReason(Game g, ExeDiscoveryResult result)
    {
        return result switch
        {
            ExeDiscoveryResult.UserSkipped => Constants.SkipReasonUserSkipped,
            ExeDiscoveryResult.UserSkippedAll => Constants.SkipReasonUserSkipped,
            _ => string.IsNullOrEmpty(g.InstallDirectory) ? Constants.SkipReasonNoInstallDir : Constants.SkipReasonNoExecutable
        };
    }

    /// <summary>
    /// Gets game action details, preferring Playnite's designated Play action.
    /// When no action is designated, supported action types fall back to File, Emulator, then URL.
    /// Executable discovery and the browse dialog are used only when no supported action resolves.
    /// </summary>
    private (string exePath, string? workDir, string name, GameAction? action, ExeDiscoveryResult result) GetGameActionDetailsWithFallback(
        Game g, 
        Dictionary<uint, SteamShortcut> existing, 
        Dictionary<string, uint> byPlayniteId,
        ExportContext context)
    {
        // Check if user chose "Skip All" earlier
        if (context.SkipAllRemaining)
        {
            return (string.Empty, null, string.Empty, null, ExeDiscoveryResult.UserSkippedAll);
        }

        // Prefer Playnite's designated Play action, then use a supported-action fallback.
        var selectedAction = GameActionSelector.GetPreferredLaunchAction(g.GameActions);
        if (selectedAction == null && g.GameActions?.Any(action => action != null && action.IsPlayAction) != true)
        {
            selectedAction = GameActionSelector.TryCreateSingleDynamicPlayAction(
                g,
                _library.PlayniteApi.Addons.Plugins,
                GameActionSelector.GetCurrentProcessExecutablePath(),
                Logger);
        }
        var fileAction = selectedAction?.Type == GameActionType.File ? selectedAction : null;
        if (fileAction != null)
        {
            var result = BuildFileActionResult(g, fileAction);
            return (result.exePath, result.workDir, result.name, fileAction, ExeDiscoveryResult.Success);
        }

        var emulatorAction = selectedAction?.Type == GameActionType.Emulator ? selectedAction : null;
        if (emulatorAction != null)
        {
            var result = BuildEmulatorActionResult(g, emulatorAction);
            if (result.action != null)
            {
                Logger.Info($"Using Emulator action for '{g.Name}': EmulatorId={emulatorAction.EmulatorId}");
                return (result.exePath, result.workDir, result.name, result.action, ExeDiscoveryResult.Success);
            }
        }

        var urlAction = selectedAction?.Type == GameActionType.URL ? selectedAction : null;
        if (urlAction != null)
        {
            var result = BuildUrlActionResult(g, urlAction, existing, byPlayniteId);
            if (result.action != null)
            {
                Logger.Info($"Using URL action for '{g.Name}': {urlAction.Path}");
                return (result.exePath, result.workDir, result.name, result.action, ExeDiscoveryResult.Success);
            }
        }

        // 4. No valid GameActions - try exe discovery
        if (g.GameActions?.Any(action => action != null && action.IsPlayAction) == true)
        {
            Logger.Warn($"Cannot export '{g.Name}': the configured Play action is not directly supported by Steam shortcuts.");
            return (string.Empty, null, g.Name ?? string.Empty, null, ExeDiscoveryResult.NoAction);
        }

        if (!string.IsNullOrEmpty(g.InstallDirectory) && Directory.Exists(g.InstallDirectory))
        {
            // First try automatic discovery
            var discoveredExe = _pathResolver.TryDiscoverExecutable(g);
            if (!string.IsNullOrEmpty(discoveredExe))
            {
                var newAction = CreateAndPersistFileAction(g, discoveredExe!);
                var result = BuildFileActionResult(g, newAction);
                return (result.exePath, result.workDir, result.name, newAction, ExeDiscoveryResult.Success);
            }

            // Automatic discovery failed - show browse dialog
            var (selectedExe, userResult) = ShowBrowseForExeDialog(g, context);
            if (userResult == ExeDiscoveryResult.UserSkippedAll)
            {
                context.SkipAllRemaining = true;
                return (string.Empty, null, string.Empty, null, ExeDiscoveryResult.UserSkippedAll);
            }
            if (userResult == ExeDiscoveryResult.UserSkipped || string.IsNullOrEmpty(selectedExe))
            {
                return (string.Empty, null, string.Empty, null, ExeDiscoveryResult.UserSkipped);
            }

            // User selected an exe - persist and use it
            var action = CreateAndPersistFileAction(g, selectedExe!);
            var actionResult = BuildFileActionResult(g, action);
            return (actionResult.exePath, actionResult.workDir, actionResult.name, action, ExeDiscoveryResult.Success);
        }

        // 5. Nothing found - skip
        Logger.Warn($"Cannot export '{g.Name}': No GameActions, no InstallDirectory, or InstallDirectory doesn't exist.");
        return (string.Empty, null, string.Empty, null, ExeDiscoveryResult.NoAction);
    }

    internal bool TryCreateAutomaticShortcut(Game game, uint preferredAppId, out SyncPreparedShortcut shortcut, out string reason)
    {
        shortcut = new SyncPreparedShortcut();
        reason = string.Empty;
        if (game == null)
        {
            reason = "The Playnite game record is unavailable.";
            return false;
        }

        var selectedAction = GameActionSelector.GetPreferredLaunchAction(game.GameActions);
        var fileAction = selectedAction?.Type == GameActionType.File ? selectedAction : null;
        var emulatorAction = selectedAction?.Type == GameActionType.Emulator ? selectedAction : null;
        var urlAction = selectedAction?.Type == GameActionType.URL ? selectedAction : null;
        string exePath;
        string? workDir;
        string launchOptions;

        if (fileAction != null)
        {
            var expandedAction = _library.PlayniteApi.ExpandGameVariables(game, fileAction);
            var result = BuildFileActionResult(game, expandedAction);
            exePath = result.exePath;
            workDir = result.workDir;
            var expandedArguments = Environment.ExpandEnvironmentVariables(expandedAction.Arguments ?? string.Empty);
            launchOptions = EmulatorPathUtils.QuoteArgumentsIfNeeded(expandedArguments ?? string.Empty);
        }
        else if (emulatorAction != null)
        {
            var result = BuildEmulatorActionResult(game, emulatorAction);
            if (result.action == null)
            {
                reason = "The emulator profile or executable could not be resolved.";
                return false;
            }

            var resolved = BuildFileActionResult(game, result.action);
            exePath = resolved.exePath;
            workDir = resolved.workDir;
            // BuildEmulatorActionResult already expands Playnite variables, including {ImagePath}.
            // Passing a full command line through the path resolver treats flags as filesystem paths.
            launchOptions = EmulatorPathUtils.QuoteArgumentsIfNeeded(
                Environment.ExpandEnvironmentVariables(result.action.Arguments ?? string.Empty));
        }
        else if (urlAction != null)
        {
            exePath = _library.PlayniteApi.ExpandGameVariables(game, urlAction.Path);
            workDir = !string.IsNullOrWhiteSpace(game.InstallDirectory) && Directory.Exists(game.InstallDirectory)
                ? game.InstallDirectory
                : null;
            launchOptions = exePath;
        }
        else
        {
            reason = "No supported File, Emulator, or URL launch action is configured.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(exePath))
        {
            reason = "The launch action resolved to an empty executable or URL.";
            return false;
        }

        var unresolvedVariable = EmulatorPathUtils.FindUnexpandedGameVariable(exePath, workDir, launchOptions);
        if (unresolvedVariable != null)
        {
            reason = $"Playnite variable {unresolvedVariable} could not be resolved from the persisted game snapshot.";
            return false;
        }

        var appId = preferredAppId;
        if (appId == 0 && urlAction != null)
        {
            appId = SteamPathResolver.TryParseAppIdFromRungameUrl(exePath);
        }
        if (appId == 0)
        {
            appId = Utils.GenerateShortcutAppId(exePath, game.Name ?? string.Empty);
        }

        shortcut = new SyncPreparedShortcut
        {
            AppId = appId,
            AppName = game.Name ?? string.Empty,
            Exe = exePath,
            StartDir = workDir ?? string.Empty,
            LaunchOptions = launchOptions,
            Tags = new List<string>()
        };
        return true;
    }

    private (string exePath, string? workDir, string name) BuildFileActionResult(Game g, GameAction fileAction)
    {
        var exePath = _pathResolver.ExpandPathVariables(g, fileAction.Path) ?? string.Empty;
        var workDir = _pathResolver.ExpandPathVariables(g, fileAction.WorkingDir);
        
        if (string.IsNullOrWhiteSpace(workDir) && !string.IsNullOrWhiteSpace(exePath))
        {
            try { workDir = Path.GetDirectoryName(exePath); } 
            catch (Exception ex) { Logger.Warn(ex, "Failed to get directory name from path."); workDir = null; }
        }
        
        var name = string.IsNullOrEmpty(g.Name) ? (Path.GetFileNameWithoutExtension(exePath) ?? string.Empty) : g.Name;
        return (exePath, workDir, name);
    }

    private (string exePath, string? workDir, string name, GameAction? action) BuildUrlActionResult(
        Game g, 
        GameAction urlAction, 
        Dictionary<uint, SteamShortcut> existing,
        Dictionary<string, uint> byPlayniteId)
    {
        var name = string.IsNullOrEmpty(g.Name) ? urlAction.Path : g.Name;
        
        // Check if we have an existing shortcut to pull exe info from
        uint resolvedExistingAppId = 0;
        if (byPlayniteId.TryGetValue(g.Id.ToString(), out var mappedAppId) && mappedAppId != 0)
        {
            resolvedExistingAppId = mappedAppId;
        }
        else
        {
            var maybeAppId = SteamPathResolver.TryParseAppIdFromRungameUrl(urlAction.Path);
            if (maybeAppId != 0)
            {
                resolvedExistingAppId = maybeAppId;
            }
        }

        // If we have an existing shortcut, use its exe/workdir
        if (resolvedExistingAppId != 0 && existing.TryGetValue(resolvedExistingAppId, out var prev))
        {
            var exePath = prev.Exe ?? string.Empty;
            var workDir = string.IsNullOrWhiteSpace(prev.StartDir) ? null : prev.StartDir;
            return (exePath, workDir, name, urlAction);
        }

        // NEW: Allow URL as the "exe" for Steam shortcuts (double-launcher experience)
        // Steam can launch URLs directly
        var url = urlAction.Path ?? string.Empty;
        if (!string.IsNullOrEmpty(url))
        {
            // Use InstallDirectory as working dir if available
            var workDir = !string.IsNullOrEmpty(g.InstallDirectory) && Directory.Exists(g.InstallDirectory) 
                ? g.InstallDirectory 
                : null;
            
            Logger.Info($"Creating URL shortcut for '{g.Name}': {url}");
            return (url, workDir, name, urlAction);
        }

        return (string.Empty, null, name, null);
    }

    /// <summary>
    /// Builds the result tuple for an emulator-based game action.
    /// Resolves the emulator profile to get the executable path, arguments, and working directory.
    /// </summary>
    internal (string exePath, string? workDir, string name, GameAction? action) BuildEmulatorActionResult(
        Game g,
        GameAction emulatorAction)
    {
        return BuildEmulatorActionResult(_library.PlayniteApi, Logger, g, emulatorAction);
    }

    internal static (string exePath, string? workDir, string name, GameAction? action) BuildEmulatorActionResult(
        IPlayniteAPI playniteApi,
        ILogger logger,
        Game g,
        GameAction emulatorAction)
    {
        return BuildEmulatorActionResult(playniteApi.Database.Emulators, playniteApi.Emulation, playniteApi.ExpandGameVariables, logger, g, emulatorAction);
    }

    internal static (string exePath, string? workDir, string name, GameAction? action) BuildEmulatorActionResult(
        IEnumerable<Emulator> emulators,
        IEmulationAPI emulationApi,
        Func<Game, string, string, string> expandVariables,
        ILogger logger,
        Game g,
        GameAction emulatorAction)
    {
        var name = string.IsNullOrEmpty(g.Name) ? "Emulator Game" : g.Name;

        // Get emulator from database
        var emulator = emulators.FirstOrDefault(e => e.Id == emulatorAction.EmulatorId);

        if (emulator == null)
        {
            logger.Warn($"Cannot export '{g.Name}': Emulator not found (EmulatorId={emulatorAction.EmulatorId})");
            return (string.Empty, null, name, null);
        }

        // Get the profile
        var profile = emulator.GetProfile(emulatorAction.EmulatorProfileId);
        if (profile == null)
        {
            logger.Warn($"Cannot export '{g.Name}': Emulator profile not found (ProfileId={emulatorAction.EmulatorProfileId})");
            return (string.Empty, null, name, null);
        }

        // Handle CustomEmulatorProfile
        if (profile is CustomEmulatorProfile customProfile)
        {
            return BuildCustomEmulatorResult(expandVariables, logger, g, emulatorAction, customProfile, emulator.InstallDir, name);
        }

        // Handle BuiltInEmulatorProfile
        if (profile is BuiltInEmulatorProfile builtInProfile)
        {
            return BuildBuiltInEmulatorResult(expandVariables, logger, emulationApi, g, emulatorAction, builtInProfile, emulator.InstallDir, emulator.BuiltInConfigId, name);
        }

        logger.Warn($"Cannot export '{g.Name}': Unknown emulator profile type '{profile.GetType().Name}'");
        return (string.Empty, null, name, null);
    }

    /// <summary>
    /// Builds result for a custom emulator profile.
    /// </summary>
    internal (string exePath, string? workDir, string name, GameAction? action) BuildCustomEmulatorResult(
        Game g,
        GameAction emulatorAction,
        CustomEmulatorProfile profile,
        string? emulatorInstallDir,
        string name)
    {
        return BuildCustomEmulatorResult(_library.PlayniteApi.ExpandGameVariables, Logger, g, emulatorAction, profile, emulatorInstallDir, name);
    }

    internal static (string exePath, string? workDir, string name, GameAction? action) BuildCustomEmulatorResult(
        Func<Game, string, string, string> expandVariables,
        ILogger logger,
        Game g,
        GameAction emulatorAction,
        CustomEmulatorProfile profile,
        string? emulatorInstallDir,
        string name)
    {
        // Get executable path (no regex resolution - Playnite uses CustomEmulatorProfile.Executable directly)
        var exePath = profile.Executable;

        if (string.IsNullOrWhiteSpace(exePath))
        {
            logger.Warn($"Cannot export '{g.Name}': Emulator profile has no executable set");
            return (string.Empty, null, name, null);
        }
        // Build arguments (considering OverrideDefaultArgs and AdditionalArguments)
        string args;
        if (emulatorAction.OverrideDefaultArgs)
        {
            // Use only the action's additional arguments
            args = emulatorAction.AdditionalArguments ?? string.Empty;
        }
        else
        {
            // Combine profile arguments with action's additional arguments
            var profileArgs = profile.Arguments ?? string.Empty;
            var additionalArgs = emulatorAction.AdditionalArguments ?? string.Empty;
            args = string.IsNullOrWhiteSpace(additionalArgs)
                ? profileArgs
                : (string.IsNullOrWhiteSpace(profileArgs) ? additionalArgs : $"{profileArgs} {additionalArgs}");
        }

        // Expand variables using Playnite API
        var emulatorDir = emulatorInstallDir ?? string.Empty;
        var resolvedExePath = exePath ?? string.Empty;
        var expandedExe = expandVariables(g, resolvedExePath, emulatorDir);
        var expandedArgs = expandVariables(g, args, emulatorDir);
        expandedArgs = EmulatorPathUtils.QuoteArgumentsIfNeeded(expandedArgs);

        // Get working directory
        string? workDir = profile.WorkingDirectory;
        if (!string.IsNullOrWhiteSpace(workDir))
        {
            workDir = expandVariables(g, workDir, emulatorDir);
        }

        logger.Info($"Resolved emulator for '{g.Name}': Exe={expandedExe}, Args={expandedArgs}");

        // Create a synthetic File action with the resolved paths
        var syntheticAction = new GameAction
        {
            Name = emulatorAction.Name ?? "Play (Emulator)",
            Type = GameActionType.File,
            Path = expandedExe,
            Arguments = expandedArgs,
            WorkingDir = workDir,
            IsPlayAction = emulatorAction.IsPlayAction,
            TrackingMode = TrackingMode.Directory,
            TrackingPath = emulatorInstallDir
        };

        return (expandedExe, workDir, name, syntheticAction);
    }

    /// <summary>
    /// Builds result for a built-in emulator profile.
    /// Built-in profiles use definitions from Playnite's emulator database.
    /// </summary>
    internal (string exePath, string? workDir, string name, GameAction? action) BuildBuiltInEmulatorResult(
        Game g,
        GameAction emulatorAction,
        BuiltInEmulatorProfile profile,
        string? emulatorInstallDir,
        string? builtInConfigId,
        string name)
    {
        return BuildBuiltInEmulatorResult(_library.PlayniteApi.ExpandGameVariables, Logger, _library.PlayniteApi.Emulation, g, emulatorAction, profile, emulatorInstallDir, builtInConfigId, name);
    }

    internal static (string exePath, string? workDir, string name, GameAction? action) BuildBuiltInEmulatorResult(
        Func<Game, string, string, string> expandVariables,
        ILogger logger,
        IEmulationAPI emulationApi,
        Game g,
        GameAction emulatorAction,
        BuiltInEmulatorProfile profile,
        string? emulatorInstallDir,
        string? builtInConfigId,
        string name)
    {
        // Built-in profiles reference an emulator definition via the emulator's BuiltInConfigId
        if (string.IsNullOrEmpty(builtInConfigId))
        {
            logger.Warn($"Cannot export '{g.Name}': Emulator has no BuiltInConfigId set");
            return (string.Empty, null, name, null);
        }

        // Get the emulator definition from Playnite's emulation database
        var definition = emulationApi.GetEmulator(builtInConfigId);
        if (definition == null)
        {
            logger.Warn($"Cannot export '{g.Name}': Built-in emulator definition not found (Id={builtInConfigId})");
            return (string.Empty, null, name, null);
        }

        // Get the profile definition by name
        var profileDef = definition.Profiles?.FirstOrDefault(p => p.Name == profile.BuiltInProfileName);
        if (profileDef == null)
        {
            logger.Warn($"Cannot export '{g.Name}': Built-in emulator profile definition not found (Profile={profile.BuiltInProfileName})");
            return (string.Empty, null, name, null);
        }

        // Get executable from the definition
        var exePath = profileDef.StartupExecutable;

        // Resolve regex patterns to actual file paths
        if (EmulatorPathUtils.IsRegexPattern(exePath))
        {
            exePath = EmulatorPathUtils.ResolveExecutablePattern(exePath, emulatorInstallDir, logger, g.Name);
            if (string.IsNullOrEmpty(exePath))
            {
                return (string.Empty, null, name, null);
            }
        }

        if (string.IsNullOrWhiteSpace(exePath))
        {
            logger.Warn($"Cannot export '{g.Name}': Built-in emulator profile has no startup executable");
            return (string.Empty, null, name, null);
        }

        // Build arguments - consider both profile.OverrideDefaultArgs and emulatorAction.OverrideDefaultArgs
        string args;
        if (profile.OverrideDefaultArgs)
        {
            // Profile overrides built-in args with its custom args
            args = profile.CustomArguments ?? string.Empty;
        }
        else if (emulatorAction.OverrideDefaultArgs)
        {
            // Action overrides everything with its additional args
            args = emulatorAction.AdditionalArguments ?? string.Empty;
        }
        else
        {
            // Combine built-in args, profile custom args, and action additional args
            var builtInArgs = profileDef.StartupArguments ?? string.Empty;
            var customArgs = profile.CustomArguments ?? string.Empty;
            var additionalArgs = emulatorAction.AdditionalArguments ?? string.Empty;

            args = builtInArgs;
            if (!string.IsNullOrWhiteSpace(customArgs))
            {
                args = string.IsNullOrWhiteSpace(args) ? customArgs : $"{args} {customArgs}";
            }
            if (!string.IsNullOrWhiteSpace(additionalArgs))
            {
                args = string.IsNullOrWhiteSpace(args) ? additionalArgs : $"{args} {additionalArgs}";
            }
        }

        // Expand variables
        var emulatorDir = emulatorInstallDir ?? string.Empty;
        var resolvedExePath = exePath ?? string.Empty;
        var expandedExe = expandVariables(g, resolvedExePath, emulatorDir);
        var expandedArgs = expandVariables(g, args, emulatorDir);
        expandedArgs = EmulatorPathUtils.QuoteArgumentsIfNeeded(expandedArgs);

        // Working directory - use emulator install dir if not specified
        string? workDir = emulatorInstallDir;

        logger.Info($"Resolved built-in emulator for '{g.Name}': Exe={expandedExe}, Args={expandedArgs}");

        // Create a synthetic File action with the resolved paths
        var syntheticAction = new GameAction
        {
            Name = emulatorAction.Name ?? "Play (Emulator)",
            Type = GameActionType.File,
            Path = expandedExe,
            Arguments = expandedArgs,
            WorkingDir = workDir,
            IsPlayAction = emulatorAction.IsPlayAction,
            TrackingMode = TrackingMode.Directory,
            TrackingPath = emulatorInstallDir
        };

        return (expandedExe, workDir, name, syntheticAction);
    }

    /// <summary>
    /// Shows a file browse dialog for the user to select the game executable.
    /// </summary>
    private (string? selectedExe, ExeDiscoveryResult result) ShowBrowseForExeDialog(Game game, ExportContext context)
    {
        try
        {
            // Show a message dialog first with Skip/Skip All/Browse options
            var result = _library.PlayniteApi.Dialogs.ShowMessage(
                $"Could not automatically detect the executable for \"{game.Name}\".\n\n" +
                $"Install directory: {game.InstallDirectory}\n\n" +
                "Would you like to browse for the executable?",
                Constants.SelectExeDialogTitle,
                System.Windows.MessageBoxButton.YesNoCancel,
                System.Windows.MessageBoxImage.Question);

            if (result == System.Windows.MessageBoxResult.Cancel)
            {
                // Cancel = Skip All
                context.SkipAllRemaining = true;
                return (null, ExeDiscoveryResult.UserSkippedAll);
            }
            
            if (result == System.Windows.MessageBoxResult.No)
            {
                // No = Skip this game
                return (null, ExeDiscoveryResult.UserSkipped);
            }

            // Yes = Browse
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = $"{Constants.SelectExeDialogTitle} - {game.Name}",
                Filter = Constants.SelectExeDialogFilter,
                InitialDirectory = game.InstallDirectory
            };

            if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.FileName))
            {
                return (dialog.FileName, ExeDiscoveryResult.Success);
            }

            // User cancelled the file dialog
            return (null, ExeDiscoveryResult.UserSkipped);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Error showing browse dialog for '{game.Name}'");
            return (null, ExeDiscoveryResult.NoAction);
        }
    }

    /// <summary>
    /// Creates a new File GameAction and persists it to the game's GameActions.
    /// </summary>
    private GameAction CreateAndPersistFileAction(Game game, string exePath)
    {
        var action = new GameAction
        {
            Name = Constants.PlayDirectActionName,
            Type = GameActionType.File,
            Path = exePath,
            WorkingDir = Path.GetDirectoryName(exePath),
            IsPlayAction = game.GameActions == null || !game.GameActions.Any(a => a.IsPlayAction),
            TrackingMode = TrackingMode.Directory,
            TrackingPath = Path.GetDirectoryName(exePath)
        };

        try
        {
            var actions = game.GameActions?.ToList() ?? new List<GameAction>();
            
            // Insert at beginning so it becomes the primary action
            actions.Insert(0, action);
            
            game.GameActions = new ObservableCollection<GameAction>(actions);
            _library.PlayniteApi.Database.Games.Update(game);
            
            Logger.Info($"Persisted new GameAction for '{game.Name}': {exePath}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to persist GameAction for '{game.Name}'");
        }

        return action;
    }

    

    private uint CreateOrUpdateShortcut(Game g, string exePath, string? workDir, string name, GameAction action, List<SteamShortcut> shortcuts, Dictionary<uint, SteamShortcut> existing, Dictionary<string, List<SteamShortcut>> existingByStable, Dictionary<string, uint> byPlayniteId, Dictionary<uint, Guid> batchAppIdOwners, ref int added, ref int updated)
    {
        uint resolvedExistingAppId = 0;
        if (byPlayniteId.TryGetValue(g.Id.ToString(), out var mappedAppId) && mappedAppId != 0)
        {
            resolvedExistingAppId = mappedAppId;
        }
        else if (action.Type == GameActionType.URL)
        {
            var maybeAppId = SteamPathResolver.TryParseAppIdFromRungameUrl(action.Path);
            if (maybeAppId != 0)
            {
                resolvedExistingAppId = maybeAppId;
            }
        }

        if (resolvedExistingAppId == 0)
        {
            if (TryResolveStableShortcutAppId(name, exePath, existingByStable, out var stableAppId, out var stableError))
            {
                resolvedExistingAppId = stableAppId;
            }
            else if (!string.IsNullOrEmpty(stableError))
            {
                throw new InvalidDataException(stableError);
            }
        }

        var appId = resolvedExistingAppId != 0 ? resolvedExistingAppId : Utils.GenerateShortcutAppId(exePath, name);
        if (!TryClaimBatchAppId(batchAppIdOwners, g.Id, appId))
        {
            throw new InvalidDataException($"More than one Playnite game maps to Steam AppID {appId}. Resolve the conflicting export mapping before writing this batch.");
        }
        var newStableId = new SteamShortcut { AppName = name, Exe = exePath }.StableId;
        if (!existing.TryGetValue(appId, out var sc))
        {
            sc = new SteamShortcut { AppName = name, Exe = exePath, StartDir = workDir ?? string.Empty, AppId = appId };
            shortcuts.Add(sc);
            existing[appId] = sc;
            added++;
        }
        else
        {
            var matchesResolvedIdentity = resolvedExistingAppId == appId ||
                string.Equals(sc.StableId, newStableId, StringComparison.OrdinalIgnoreCase);
            if (!matchesResolvedIdentity)
            {
                throw new InvalidDataException($"AppID {appId} is already used by a different Steam shortcut. The batch was not written.");
            }

            var oldStableId = sc.StableId;
            sc.AppName = name;
            sc.Exe = exePath;
            sc.StartDir = workDir ?? sc.StartDir;
            if (existingByStable.TryGetValue(oldStableId, out var oldMatches))
            {
                oldMatches.RemoveAll(match => ReferenceEquals(match, sc));
                if (oldMatches.Count == 0) existingByStable.Remove(oldStableId);
            }
            updated++;
        }

        if (action.Type == GameActionType.URL)
        {
            sc.LaunchOptions = action.Path;
        }
        else if (action.Type == GameActionType.File)
        {
            var expandedArgs = _pathResolver.ExpandArgumentVariables(g, action.Arguments) ?? string.Empty;
            sc.LaunchOptions = EmulatorPathUtils.QuoteArgumentsIfNeeded(expandedArgs);
        }

        if (!existingByStable.TryGetValue(sc.StableId, out var newMatches))
        {
            newMatches = new List<SteamShortcut>();
            existingByStable[sc.StableId] = newMatches;
        }
        if (!newMatches.Contains(sc)) newMatches.Add(sc);
        byPlayniteId[g.Id.ToString("D")] = appId;

        return appId;
    }

    internal static bool TryClaimBatchAppId(IDictionary<uint, Guid> owners, Guid gameId, uint appId)
    {
        if (owners == null) throw new ArgumentNullException(nameof(owners));
        if (gameId == Guid.Empty || appId == 0) return false;
        if (owners.TryGetValue(appId, out var owner) && owner != gameId) return false;
        owners[appId] = gameId;
        return true;
    }

    internal static bool TryResolveStableShortcutAppId(
        string appName,
        string exePath,
        IDictionary<string, List<SteamShortcut>> existingByStable,
        out uint appId,
        out string error)
    {
        if (existingByStable == null) throw new ArgumentNullException(nameof(existingByStable));
        appId = 0;
        error = string.Empty;
        var stableId = new SteamShortcut { AppName = appName ?? string.Empty, Exe = exePath ?? string.Empty }.StableId;
        if (!existingByStable.TryGetValue(stableId, out var matches)) return false;
        if (matches.Count != 1 || matches[0].AppId == 0)
        {
            error = $"Steam contains {matches.Count} ambiguous shortcuts matching '{appName}'. Resolve the duplicate entries before exporting this batch.";
            return false;
        }

        appId = matches[0].AppId;
        return true;
    }

    private void UpdateShortcutArtwork(Game g, SteamShortcut shortcut, string vdfPath)
    {
        try
        {
            var gridDir = _artworkManager.TryGetGridDirFromVdf(vdfPath);
            if (!string.IsNullOrEmpty(gridDir))
            {
                _artworkManager.TryExportArtworkToGrid(g, shortcut.AppId, gridDir);
                var iconPath = _artworkManager.TryGetGridIconPath(shortcut.AppId, gridDir);
                if (!string.IsNullOrEmpty(iconPath))
                {
                    shortcut.Icon = iconPath!;
                }
            }
        }
        catch (Exception aex)
        {
            Logger.Warn(aex, "Exporting artwork to grid failed.");
        }
    }

    private void UpdateGameActions(Game g, uint appId, string exePath, string? workDir, GameAction action)
    {
        try
        {
            // Don't add "Play (Direct)" File action if the game already has an Emulator action.
            // Emulator games are launched via the emulator, not directly.
            var hasEmulatorAction = g.GameActions?.Any(a => a.Type == GameActionType.Emulator) == true;
            if (!hasEmulatorAction)
            {
                var fileArgs = action.Type == GameActionType.File
                    ? EmulatorPathUtils.QuoteArgumentsIfNeeded(_pathResolver.ExpandArgumentVariables(g, action.Arguments))
                    : null;
                _library.EnsureFileActionForExternalGame(g, exePath, workDir, fileArgs);
            }
            
            if (_library.Settings.LaunchViaSteam && appId != 0) { _library.EnsureSteamPlayActionForExternalGame(g, appId, workDir, _library.Settings.SteamActionIsDefault); }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to ensure play action.");
        }
    }

    private static bool LooksLikeUrl(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var val = s!.Trim();
        return val.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || val.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || val.StartsWith("steam://", StringComparison.OrdinalIgnoreCase);
    }

    public void WriteShortcutsWithBackup(string vdfPath, List<SteamShortcut> shortcuts, string? expectedFingerprint = null)
    {
        _backupManager.WriteShortcutsWithBackup(vdfPath, shortcuts, expectedFingerprint);
    }

    private sealed class ExportResult 
    { 
        public int Added; 
        public int Updated; 
        public int Skipped;
        public List<string> SkippedGames = new List<string>();
    }

    /// <summary>
    /// Result from attempting to get or discover game action details.
    /// </summary>
    private enum ExeDiscoveryResult
    {
        Success,
        NoAction,
        UserSkipped,
        UserSkippedAll
    }

    private sealed class SelectionCandidate
    {
        public static SelectionCandidate Empty { get; } = new SelectionCandidate(string.Empty, false, false);

        public SelectionCandidate(string label, bool hasPlayableAction, bool existsInSteam, bool hasSupportedLaunchAction = false)
        {
            Label = label;
            HasPlayableAction = hasPlayableAction;
            ExistsInSteam = existsInSteam;
            HasSupportedLaunchAction = hasSupportedLaunchAction;
        }

        public string Label { get; }
        public bool HasPlayableAction { get; }
        public bool HasSupportedLaunchAction { get; }
        public bool ExistsInSteam { get; }
        public bool ShouldSelect => HasPlayableAction && !ExistsInSteam;
        public bool IsNew => ShouldSelect;
    }
}
