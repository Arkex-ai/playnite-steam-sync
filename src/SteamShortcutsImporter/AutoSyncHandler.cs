using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Threading;

namespace SteamShortcutsImporter;

/// <summary>
/// Captures new Playnite database items in a durable queue and serially applies them to Steam.
/// The event callback only filters, snapshots and persists. VDF work always runs on the worker timer.
/// </summary>
internal sealed class AutoSyncHandler : IDisposable
{
    private const int CurrentArtworkSyncVersion = 1;
    private readonly ILogger _logger;
    private readonly IPlayniteAPI _playniteApi;
    private readonly ShortcutsLibrary _library;
    private readonly SteamPathResolver _pathResolver;
    private readonly ArtworkManager _artworkManager;
    private readonly ImportExportService _importExportService;
    private readonly BackupManager _backupManager;
    private readonly PluginSettings _settings;
    private readonly Dispatcher _uiDispatcher;
    private readonly object _sync = new object();
    private SyncQueueStore? _store;
    private string _storeKey = string.Empty;
    private Timer? _workerTimer;
    private Timer? _recoveryTimer;
    private Timer? _artworkMigrationTimer;
    private int _processing;
    private int _reconciling;
    private int _retrying;
    private int _captureInProgress;
    private int _reconciliationRequested;
    private int _artworkReconciliationRequested;
    private int _artworkMigrationPending;
    private int _recoveryAttempts;
    private volatile bool _disposed;
    private volatile bool _stopping;
    private bool _wasEnabled;
    private volatile bool _recoveryPending;
    private string _lastFilterSettingsKey = string.Empty;

    public AutoSyncHandler(
        ILogger logger,
        IPlayniteAPI playniteApi,
        ShortcutsLibrary library,
        SteamPathResolver pathResolver,
        ArtworkManager artworkManager,
        ImportExportService importExportService,
        BackupManager backupManager,
        PluginSettings settings)
    {
        _logger = logger;
        _playniteApi = playniteApi;
        _library = library;
        _pathResolver = pathResolver;
        _artworkManager = artworkManager;
        _importExportService = importExportService;
        _backupManager = backupManager;
        _settings = settings;
        _uiDispatcher = _playniteApi.MainView.UIDispatcher;

        EnsureStore();

        try
        {
            _playniteApi.Database.Games.ItemCollectionChanged += Games_ItemCollectionChanged;
            _playniteApi.Database.Games.ItemUpdated += Games_ItemUpdated;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to attach Playnite game-add event for automatic Steam sync.");
        }

        _wasEnabled = _settings.AutoSyncEnabled;
        _lastFilterSettingsKey = GetFilterSettingsKey(_settings);
        if (_settings.AutoSyncEnabled) ScheduleWorker(1500);
        if (_settings.AutoSyncEnabled && _store?.InitialBaselineCompleted == true && _store.ArtworkSyncVersion < CurrentArtworkSyncVersion)
        {
            ScheduleArtworkMigration(3000);
        }
        if (_recoveryPending) ScheduleRecovery(15000);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _stopping = true;
            Interlocked.Exchange(ref _workerTimer, null)?.Dispose();
            Interlocked.Exchange(ref _recoveryTimer, null)?.Dispose();
            Interlocked.Exchange(ref _artworkMigrationTimer, null)?.Dispose();
            Interlocked.Exchange(ref _artworkMigrationPending, 0);
            try
            {
                _playniteApi.Database.Games.ItemCollectionChanged -= Games_ItemCollectionChanged;
                _playniteApi.Database.Games.ItemUpdated -= Games_ItemUpdated;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to detach Playnite game-add event.");
            }

            TryMarkSessionCleanIfSafe(_store);
        }
    }

    public bool IsInitialBaselineCompleted => GetCurrentStore()?.InitialBaselineCompleted == true;

    public void OnLibraryUpdated()
    {
        if (_disposed || _stopping) return;
        if (_settings.AutoSyncEnabled && _store?.InitialBaselineCompleted == true && _store.ArtworkSyncVersion < CurrentArtworkSyncVersion)
        {
            ScheduleArtworkMigration(250);
        }
        if (_recoveryPending) ScheduleRecovery(2000);
        if (_settings.AutoSyncEnabled) ScheduleWorker(250);
    }

    public void OnApplicationStopped()
    {
        lock (_sync)
        {
            _stopping = true;
            Interlocked.Exchange(ref _workerTimer, null)?.Dispose();
            Interlocked.Exchange(ref _recoveryTimer, null)?.Dispose();
            Interlocked.Exchange(ref _artworkMigrationTimer, null)?.Dispose();
            Interlocked.Exchange(ref _artworkMigrationPending, 0);
            TryMarkSessionCleanIfSafe(_store);
        }
    }

    public void OnSettingsChanged()
    {
        if (_disposed || _stopping) return;
        EnsureStore();

        var currentFilterSettingsKey = GetFilterSettingsKey(_settings);
        var filtersChanged = !string.Equals(_lastFilterSettingsKey, currentFilterSettingsKey, StringComparison.Ordinal);
        _lastFilterSettingsKey = currentFilterSettingsKey;

        if (_settings.AutoSyncEnabled)
        {
            // Enabling or changing filters is an explicit user action; reconcile once to cover
            // games added while sync was disabled or while a source was not selected.
            var artworkMigrationRequired = _store!.ArtworkSyncVersion < CurrentArtworkSyncVersion;
            if (!_wasEnabled || filtersChanged || artworkMigrationRequired)
            {
                if (_store.InitialBaselineCompleted)
                {
                    if (artworkMigrationRequired) ScheduleArtworkMigration(250);
                    else ScheduleReconciliation(forceArtworkCheck: !_wasEnabled || filtersChanged);
                }
            }
            _wasEnabled = true;
            if (_store!.InitialBaselineCompleted) ScheduleWorker(250);
        }
        else
        {
            _wasEnabled = false;
            Interlocked.Exchange(ref _workerTimer, null)?.Dispose();
            Interlocked.Exchange(ref _artworkMigrationTimer, null)?.Dispose();
            Interlocked.Exchange(ref _artworkMigrationPending, 0);
        }

        if (_recoveryPending && _settings.AutoSyncEnabled) ScheduleRecovery(1000);
    }

    public void RecordManualExports(IEnumerable<ShortcutsLibrary.ExportedShortcut> exports, bool establishInitialBaseline)
    {
        EnsureStore();
        var successful = exports?.Where(x => x?.Game != null && x.Shortcut != null).ToList()
                         ?? new List<ShortcutsLibrary.ExportedShortcut>();
        var newSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var manualEntries = new List<SyncQueueEntry>();
        foreach (var exported in successful)
        {
            var game = exported.Game;
            if (game.Id == Guid.Empty) continue;
            var entry = CreateSnapshot(game, selectedAction: null);
            entry.AppId = exported.Shortcut.AppId;
            entry.Exe = exported.Shortcut.Exe ?? string.Empty;
            entry.StartDir = exported.Shortcut.StartDir ?? string.Empty;
            entry.LaunchOptions = exported.Shortcut.LaunchOptions ?? string.Empty;
            entry.Icon = exported.Shortcut.Icon ?? string.Empty;
            entry.Tags = new List<string>(exported.Shortcut.Tags ?? new List<string>());
            manualEntries.Add(entry);
            _settings.ExportMap[exported.Shortcut.AppId.ToString()] = game.Id.ToString("D");
            if (game.SourceId != Guid.Empty) newSources.Add(game.SourceId.ToString("D"));
        }

        _store!.RecordManualExports(manualEntries, establishInitialBaseline);

        if (_settings.AutoSyncSourceIds == null) _settings.AutoSyncSourceIds = new List<string>();
        var settingsChanged = false;
        if (_settings.AutoSyncSourceIds.Count == 0 && newSources.Count > 0)
        {
            _settings.AutoSyncSourceIds = newSources.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            settingsChanged = true;
        }

        if (successful.Count > 0 || settingsChanged)
        {
            try { _library.SavePluginSettings(_settings); }
            catch (Exception ex) { _logger.Error(ex, "The export was recorded, but Playnite settings could not be saved."); }
        }

        _lastFilterSettingsKey = GetFilterSettingsKey(_settings);

        if (establishInitialBaseline)
        {
            _recoveryPending = false;
            Interlocked.Exchange(ref _recoveryAttempts, 0);
            Interlocked.Exchange(ref _recoveryTimer, null)?.Dispose();
        }

        if (_settings.AutoSyncEnabled && _store!.InitialBaselineCompleted)
        {
            ScheduleReconciliation(forceArtworkCheck: true);
            ScheduleWorker(250);
        }
    }

    public int ReconcileNow()
    {
        EnsureStore();
        if (!_store!.InitialBaselineCompleted) return 0;
        return ScheduleReconciliation(forceArtworkCheck: true) ? -1 : 0;
    }

    public int RetryAllRecoverable()
    {
        EnsureStore();
        var store = GetCurrentStore();
        if (store == null) return 0;
        if (!_settings.AutoSyncEnabled || !store.InitialBaselineCompleted)
        {
            return store.RetryAllRecoverable();
        }

        if (Interlocked.CompareExchange(ref _retrying, 1, 0) != 0) return 0;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                if (_disposed || _stopping || !IsCurrentStoreForSelectedUser(store)) return;
                ReconcileEligibleGames(forceArtworkCheck: true);
                if (!IsCurrentStoreForSelectedUser(store)) return;
                store.RetryAllRecoverable();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Retrying automatic Steam sync operations failed.");
            }
            finally
            {
                Interlocked.Exchange(ref _retrying, 0);
                if (_settings.AutoSyncEnabled) ScheduleWorker(250);
                if (_stopping) TryMarkSessionCleanIfSafe(GetCurrentStore());
            }
        });
        return -1;
    }

    public string GetStatusText()
    {
        EnsureStore();
        var items = _store!.GetStatusItems();
        var counts = items.GroupBy(x => x.Status, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
        int Count(string status) => counts.TryGetValue(status, out var value) ? value : 0;
        var status = _store.InitialBaselineCompleted ? "initial export complete" : "waiting for the initial filtered export";
        var lines = new List<string>
        {
            $"Automatic sync: {(_settings.AutoSyncEnabled ? "enabled" : "disabled")}; {status}.",
            $"Pending: {Count(SyncQueueStore.PendingStatus) + Count("Prepared") + Count(SyncQueueStore.RetryableFailureStatus)}; waiting for Steam: {Count(SyncQueueStore.WaitingForSteamStatus)}; needs attention: {Count(SyncQueueStore.NeedsAttentionStatus)}; synced history: {Count(SyncQueueStore.CommittedStatus)}."
        };

        var selectedSteamUserId = GetSelectedSteamUserId();
        if (string.IsNullOrWhiteSpace(selectedSteamUserId))
        {
            lines.Add("Steam account could not be resolved automatically; choose a profile in extension settings.");
        }
        else if (!string.Equals(_store.SteamUserId, selectedSteamUserId, StringComparison.OrdinalIgnoreCase))
        {
            lines.Add("The saved sync queue is bound to another Steam profile; automatic processing is paused.");
        }

        foreach (var item in items.Where(x => x.Status == SyncQueueStore.NeedsAttentionStatus)
                     .Take(4))
        {
            lines.Add($"• {item.Entry.GameName}: {item.Error}");
        }

        if (_recoveryPending) lines.Add("Recovery reconciliation is waiting for the library to load.");
        if (Volatile.Read(ref _reconciling) != 0) lines.Add("A filtered library reconciliation is running in the background.");
        if (Volatile.Read(ref _retrying) != 0) lines.Add("Retry is refreshing game snapshots in the background.");
        return string.Join(Environment.NewLine, lines);
    }

    private void Games_ItemCollectionChanged(object sender, ItemCollectionChangedEventArgs<Game> e)
    {
        if (_disposed || _stopping || !_settings.AutoSyncEnabled || e?.AddedItems == null || e.AddedItems.Count == 0) return;
        Interlocked.Increment(ref _captureInProgress);
        try
        {
            if (_disposed || _stopping)
            {
                PreserveRecoveryForNextSession(GetCurrentStore());
                return;
            }
            EnsureStore();
            var store = GetCurrentStore();
            if (store == null || !store.InitialBaselineCompleted || !IsCurrentStoreForSelectedUser(store)) return;

            var addedGames = e.AddedItems.Where(game => game != null).ToList();
            var capture = InvokeOnUi(() =>
            {
                var observed = addedGames
                    .Where(IsEligibleSource)
                    .Select(game => (game.Id.ToString("D"), game.SourceId.ToString("D")))
                    .ToList();
                var snapshots = addedGames
                    .Select(game => CreateEligibleSnapshot(game, store))
                    .Where(entry => entry != null)
                    .Cast<SyncQueueEntry>()
                    .ToList();
                return (Observed: observed, Snapshots: snapshots);
            });
            if (!IsCurrentStoreForSelectedUser(store)) return;
            store.ObserveGames(capture.Observed);
            EnqueueCapturedSnapshots(store, capture.Snapshots, 500);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Capturing newly added Playnite games for automatic Steam sync failed.");
            try
            {
                if (GetCurrentStore()?.GetPending().Count > 0) ScheduleWorker(1000);
            }
            catch (Exception recoveryEx)
            {
                _logger.Error(recoveryEx, "Automatic sync could not schedule recovery after a queue persistence error.");
            }
            RequestRecoveryAfterFailure(GetCurrentStore());
        }
        finally
        {
            Interlocked.Decrement(ref _captureInProgress);
            if (_stopping) TryMarkSessionCleanIfSafe(GetCurrentStore());
        }
    }

    private void Games_ItemUpdated(object sender, ItemUpdatedEventArgs<Game> e)
    {
        if (_disposed || _stopping || !_settings.AutoSyncEnabled || e?.UpdatedItems == null || e.UpdatedItems.Count == 0) return;
        Interlocked.Increment(ref _captureInProgress);
        try
        {
            if (_disposed || _stopping)
            {
                PreserveRecoveryForNextSession(GetCurrentStore());
                return;
            }
            EnsureStore();
            var store = GetCurrentStore();
            if (store == null || !store.InitialBaselineCompleted || !IsCurrentStoreForSelectedUser(store)) return;

            var snapshots = InvokeOnUi(() => e.UpdatedItems
                .Select(update => update?.NewData)
                .Where(game => game != null && store.IsGameTracked(game.Id.ToString("D")))
                .Cast<Game>()
                .Select(game =>
                {
                    var entry = CreateEligibleSnapshot(game, store);
                    if (entry != null && string.Equals(entry.InitialStatus, SyncQueueStore.PendingStatus, StringComparison.Ordinal))
                    {
                        entry.ArtworkCheckToken = Guid.NewGuid().ToString("N");
                    }
                    return entry;
                })
                .Where(entry => entry != null)
                .Cast<SyncQueueEntry>()
                .ToList());
            if (!IsCurrentStoreForSelectedUser(store)) return;
            EnqueueCapturedSnapshots(store, snapshots, 500);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Capturing updated Playnite games for automatic Steam sync failed.");
            try
            {
                if (GetCurrentStore()?.GetPending().Count > 0) ScheduleWorker(1000);
            }
            catch (Exception recoveryEx)
            {
                _logger.Error(recoveryEx, "Automatic sync could not schedule recovery after a game update capture error.");
            }
            RequestRecoveryAfterFailure(GetCurrentStore());
        }
        finally
        {
            Interlocked.Decrement(ref _captureInProgress);
            if (_stopping) TryMarkSessionCleanIfSafe(GetCurrentStore());
        }
    }

    private void EnqueueCapturedSnapshots(SyncQueueStore store, IReadOnlyCollection<SyncQueueEntry> snapshots, int delayMs)
    {
        if (snapshots.Count > 0 && store.EnqueueBatch(snapshots) > 0) ScheduleWorker(delayMs);
    }

    private SyncQueueEntry? CreateEligibleSnapshot(Game game, SyncQueueStore? store = null)
    {
        if (!IsEligibleSource(game)) return null;

        var eligibility = SelectActionForAutomaticSync(game, _settings);
        var selected = eligibility.Action;
        var filteredReason = eligibility.FilteredReason;

        var entry = CreateSnapshot(game, selected);
        if (!string.IsNullOrEmpty(filteredReason))
        {
            entry.InitialStatus = SyncQueueStore.FilteredOutStatus;
            entry.InitialError = filteredReason;
            entry.Fingerprint = ComputeFilteredFingerprint(entry);
        }
        else if (selected == null && game.GameActions?.Any(action => action != null && action.IsPlayAction) == true)
        {
            entry.InitialStatus = SyncQueueStore.NeedsAttentionStatus;
            entry.InitialError = "The configured Play action cannot be represented as a direct Steam launch; choose a supported action before retrying.";
        }
        else if (selected == null && (!string.IsNullOrWhiteSpace(game.InstallDirectory) || game.PluginId != Guid.Empty))
        {
            // Persist first; executable scanning happens on the serial worker, not inside the
            // Playnite database event callback. A missing or ambiguous match becomes attention.
            entry.InitialStatus = SyncQueueStore.PendingStatus;
            entry.InitialError = "Waiting for automatic executable discovery.";
        }
        else if (selected == null)
        {
            entry.InitialStatus = SyncQueueStore.NeedsAttentionStatus;
            entry.InitialError = "No supported launch action is configured for this game.";
        }

        var previous = (store ?? GetCurrentStore())?.GetGameRecord(entry.PlayniteGameId);
        entry.AppId = previous?.AppId ?? 0;
        entry.SteamUserId = GetSelectedSteamUserId();
        return entry;
    }

    internal static (GameAction? Action, string FilteredReason) SelectActionForAutomaticSync(
        Game game,
        PluginSettings settings,
        SteamPathResolver? pathResolver = null)
    {
        var selected = GameActionSelector.GetPreferredLaunchAction(game.GameActions);
        if (game.Hidden)
        {
            return (selected, "The game is hidden by the current automatic sync filters.");
        }

        if (settings.AutoSyncOnlyInstalled && !game.IsInstalled)
        {
            return (selected, "The game is not installed and is excluded by the current automatic sync filters.");
        }

        if (selected?.Type == GameActionType.Emulator && !settings.AutoSyncIncludeEmulators)
        {
            return (null, "Emulator actions are excluded by the current automatic sync filters.");
        }

        if (selected?.Type == GameActionType.URL && !settings.AutoSyncIncludeUrls)
        {
            return (null, "URL launch actions are excluded by the current automatic sync filters.");
        }

        var hasConfiguredPlayAction = game.GameActions?.Any(action => action != null && action.IsPlayAction) == true;

        // Some library integrations expose a dynamic PlayController instead of storing a
        // GameAction. Do not turn those into an arbitrary discovered executable here.
        if (selected == null && !hasConfiguredPlayAction && pathResolver != null)
        {
            selected = GameActionSelector.DiscoverFileAction(game, pathResolver);
        }

        return (selected, string.Empty);
    }

    internal static bool IsEligibleForInitialFilteredExport(
        Game game,
        PluginSettings settings,
        Guid importerPluginId,
        SteamPathResolver? pathResolver = null,
        IEnumerable<Playnite.SDK.Plugins.Plugin>? plugins = null,
        string? playniteExecutablePath = null,
        ILogger? logger = null)
    {
        if (!IsEligibleSource(game, settings, importerPluginId)) return false;
        var selection = SelectActionForAutomaticSync(game, settings, pathResolver);
        if (!string.IsNullOrEmpty(selection.FilteredReason)) return false;
        if (selection.Action != null) return true;
        return game.PluginId != Guid.Empty &&
            GameActionSelector.TryCreateSingleDynamicPlayAction(game, plugins, playniteExecutablePath, logger) != null;
    }

    private SyncQueueEntry CreateSnapshot(Game game, GameAction? selectedAction)
    {
        var actions = game.GameActions?.ToList() ?? new List<GameAction>();
        var action = selectedAction ?? GameActionSelector.GetPreferredLaunchAction(actions);
        var tags = game.Tags?.Select(x => x?.Name).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToList()
                   ?? new List<string>();
        var entry = new SyncQueueEntry
        {
            PlayniteGameId = game.Id.ToString("D"),
            SourcePluginId = game.PluginId.ToString("D"),
            PlayniteSourceId = game.SourceId.ToString("D"),
            GameName = game.Name ?? string.Empty,
            SteamUserId = GetSelectedSteamUserId(),
            InstallDirectory = game.InstallDirectory ?? string.Empty,
            ActionType = action?.Type.ToString() ?? string.Empty,
            ActionPath = action?.Path ?? string.Empty,
            ActionArguments = action?.Arguments ?? string.Empty,
            ActionWorkingDirectory = action?.WorkingDir ?? string.Empty,
            EmulatorId = action?.EmulatorId.ToString("D") ?? string.Empty,
            EmulatorProfileId = action?.EmulatorProfileId ?? string.Empty,
            EmulatorAdditionalArguments = action?.AdditionalArguments ?? string.Empty,
            EmulatorOverrideDefaultArgs = action?.OverrideDefaultArgs ?? false,
            IsInstalled = game.IsInstalled,
            IsHidden = game.Hidden,
            Icon = game.Icon ?? string.Empty,
            CoverImage = game.CoverImage ?? string.Empty,
            BackgroundImage = game.BackgroundImage ?? string.Empty,
            GameExternalId = game.GameId ?? string.Empty,
            Roms = game.Roms?.Where(rom => rom != null)
                .Select(rom => new SyncGameRom { Name = rom.Name ?? string.Empty, Path = rom.Path ?? string.Empty })
                .ToList() ?? new List<SyncGameRom>(),
            Tags = tags
        };
        entry.Fingerprint = ComputeFingerprint(entry);
        return entry;
    }

    private bool IsEligibleSource(Game game)
    {
        return IsEligibleSource(game, _settings, _library.Id);
    }

    internal static bool IsEligibleSource(Game game, PluginSettings settings, Guid importerPluginId)
    {
        if (game == null || game.Id == Guid.Empty || game.PluginId == importerPluginId) return false;
        var sourceName = game.Source?.Name ?? game.Source?.ToString() ?? string.Empty;
        if (IsSteamSource(sourceName)) return false;

        // Playnite's emulated games can be database items without a library provider.
        // They still have a stable game ID and an Emulator action, so the explicit
        // emulator filter is their source opt-in when PluginId is Guid.Empty.
        if (game.PluginId == Guid.Empty)
        {
            return settings.AutoSyncIncludeEmulators &&
                GameActionSelector.GetPreferredLaunchAction(game.GameActions)?.Type == GameActionType.Emulator;
        }

        return game.SourceId != Guid.Empty &&
            settings.AutoSyncSourceIds?.Any(id => string.Equals(id, game.SourceId.ToString("D"), StringComparison.OrdinalIgnoreCase)) == true;
    }

    internal static bool IsSteamSource(string? sourceName)
    {
        return string.Equals(sourceName, "Steam", StringComparison.OrdinalIgnoreCase) ||
            (sourceName?.StartsWith("Steam ", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    internal static string ComputeFingerprint(SyncQueueEntry entry)
    {
        var value = string.Join("\n", new[]
        {
            entry.SourcePluginId, entry.PlayniteSourceId, entry.GameName, entry.InstallDirectory, entry.ActionType,
            entry.ActionPath, entry.ActionArguments, entry.ActionWorkingDirectory, entry.EmulatorId,
            entry.EmulatorProfileId, entry.EmulatorAdditionalArguments, entry.GameExternalId,
            entry.EmulatorOverrideDefaultArgs.ToString(), entry.IsInstalled.ToString(), entry.IsHidden.ToString(),
            entry.Icon, entry.CoverImage, entry.BackgroundImage,
            string.Join("\n", (entry.Roms ?? new List<SyncGameRom>()).Select(rom => (rom.Name ?? string.Empty) + "\0" + (rom.Path ?? string.Empty)))
        });
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static string ComputeFilteredFingerprint(SyncQueueEntry entry)
    {
        var value = string.Join("\n", "filtered", entry.PlayniteGameId, entry.SourcePluginId, entry.PlayniteSourceId);
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static string GetFilterSettingsKey(PluginSettings settings)
    {
        var sourceIds = (settings.AutoSyncSourceIds ?? new List<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase);
        return string.Join("|", new[]
        {
            settings.AutoSyncOnlyInstalled.ToString(),
            settings.AutoSyncIncludeEmulators.ToString(),
            settings.AutoSyncIncludeUrls.ToString(),
            string.Join(",", sourceIds)
        });
    }

    private int ReconcileEligibleGames(bool forceArtworkCheck, bool artworkMigration = false)
    {
        if (_stopping && _settings.AutoSyncEnabled)
        {
            if (artworkMigration) Interlocked.Exchange(ref _artworkMigrationPending, 0);
            PreserveRecoveryForNextSession(GetCurrentStore());
            return 0;
        }
        if (_disposed || _stopping || !_settings.AutoSyncEnabled)
        {
            if (artworkMigration) Interlocked.Exchange(ref _artworkMigrationPending, 0);
            return 0;
        }
        var store = GetCurrentStore();
        if (store?.InitialBaselineCompleted != true || !IsCurrentStoreForSelectedUser(store))
        {
            if (artworkMigration) Interlocked.Exchange(ref _artworkMigrationPending, 0);
            return 0;
        }
        if (artworkMigration && store.ArtworkSyncVersion >= CurrentArtworkSyncVersion)
        {
            Interlocked.Exchange(ref _artworkMigrationPending, 0);
            return 0;
        }
        try
        {
            var capture = InvokeOnUi(() =>
            {
                var games = _playniteApi.Database.Games.Where(game => game != null).ToList();
                var observed = games
                    .Where(IsEligibleSource)
                    .Select(game => (game.Id.ToString("D"), game.SourceId.ToString("D")))
                    .ToList();
                var snapshots = games
                    .Select(game => CreateEligibleSnapshot(game, store))
                    .Where(entry => entry != null)
                    .Cast<SyncQueueEntry>()
                    .ToList();
                if (forceArtworkCheck)
                {
                    foreach (var entry in snapshots.Where(entry => string.Equals(entry.InitialStatus, SyncQueueStore.PendingStatus, StringComparison.Ordinal)))
                    {
                        entry.ArtworkCheckToken = Guid.NewGuid().ToString("N");
                    }
                }
                return (Observed: observed, Snapshots: snapshots);
            });
            if (!IsCurrentStoreForSelectedUser(store)) return 0;
            store.ObserveGames(capture.Observed);
            var snapshots = capture.Snapshots;
            var added = store.EnqueueBatch(snapshots);
            if (forceArtworkCheck)
            {
                store.SetArtworkSyncVersion(CurrentArtworkSyncVersion);
                if (store.ArtworkSyncVersion >= CurrentArtworkSyncVersion)
                {
                    Interlocked.Exchange(ref _artworkMigrationPending, 0);
                }
            }
            if (added > 0) ScheduleWorker(250);
            _recoveryPending = false;
            Interlocked.Exchange(ref _recoveryAttempts, 0);
            store.MarkRecoveryCompleted();
            _logger.Info(forceArtworkCheck
                ? $"Automatic artwork reconciliation captured {added} tracked game snapshot(s) for content verification."
                : $"Automatic sync reconciliation captured {added} new or changed game snapshot(s).");
            return added;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Automatic sync reconciliation failed.");
            try
            {
                if (GetCurrentStore()?.GetPending().Count > 0) ScheduleWorker(1000);
            }
            catch (Exception recoveryEx)
            {
                _logger.Error(recoveryEx, "Automatic sync could not schedule recovery after reconciliation failed.");
            }
            RequestRecoveryAfterFailure(store);
            return 0;
        }
    }

    private void ScheduleRecovery(int delayMs)
    {
        if (_disposed || _stopping || !_recoveryPending) return;
        var timer = new Timer(_ =>
        {
            if (!_disposed && !_stopping && _settings.AutoSyncEnabled && _store?.InitialBaselineCompleted == true)
            {
                ScheduleReconciliation(forceArtworkCheck: true);
            }
        }, null, delayMs, Timeout.Infinite);
        Interlocked.Exchange(ref _recoveryTimer, timer)?.Dispose();
    }

    private void ScheduleArtworkMigration(int delayMs)
    {
        if (_disposed || _stopping || !_settings.AutoSyncEnabled || _store?.InitialBaselineCompleted != true ||
            _store.ArtworkSyncVersion >= CurrentArtworkSyncVersion)
        {
            return;
        }
        if (Interlocked.CompareExchange(ref _artworkMigrationPending, 1, 0) != 0) return;

        var timer = new Timer(_ =>
        {
            Interlocked.Exchange(ref _artworkMigrationTimer, null)?.Dispose();
            if (!_disposed && !_stopping && _settings.AutoSyncEnabled &&
                _store?.InitialBaselineCompleted == true && _store.ArtworkSyncVersion < CurrentArtworkSyncVersion)
            {
                ScheduleReconciliation(forceArtworkCheck: true, artworkMigration: true);
            }
            else
            {
                Interlocked.Exchange(ref _artworkMigrationPending, 0);
            }
        }, null, delayMs, Timeout.Infinite);
        Interlocked.Exchange(ref _artworkMigrationTimer, timer)?.Dispose();
    }

    internal static int GetRecoveryBackoffMilliseconds(int attempt)
    {
        var exponent = Math.Min(9, Math.Max(0, attempt - 1));
        return (int)(Math.Min(300d, Math.Pow(2d, exponent)) * 1000d);
    }

    private void RequestRecoveryAfterFailure(SyncQueueStore? store)
    {
        if (store == null) return;
        _recoveryPending = true;
        try { store.MarkSessionStarted(); }
        catch (Exception ex) { _logger.Error(ex, "The sync session could not persist its recovery marker after a capture failure."); }

        if (_disposed || _stopping || !_settings.AutoSyncEnabled || !store.InitialBaselineCompleted || !IsCurrentStoreForSelectedUser(store)) return;
        var attempt = Interlocked.Increment(ref _recoveryAttempts);
        ScheduleRecovery(GetRecoveryBackoffMilliseconds(attempt));
    }

    private bool ScheduleReconciliation(bool forceArtworkCheck = false, bool artworkMigration = false)
    {
        if (_disposed || _stopping || !_settings.AutoSyncEnabled || _store?.InitialBaselineCompleted != true)
        {
            if (artworkMigration) Interlocked.Exchange(ref _artworkMigrationPending, 0);
            return false;
        }
        if (forceArtworkCheck) Interlocked.Exchange(ref _artworkReconciliationRequested, 1);
        if (artworkMigration) Interlocked.Exchange(ref _artworkMigrationPending, 1);
        if (Interlocked.CompareExchange(ref _reconciling, 1, 0) != 0)
        {
            Interlocked.Exchange(ref _reconciliationRequested, 1);
            return false;
        }
        var runArtworkCheck = Interlocked.Exchange(ref _artworkReconciliationRequested, 0) != 0;
        var runArtworkMigration = Volatile.Read(ref _artworkMigrationPending) != 0;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                ReconcileEligibleGames(runArtworkCheck, runArtworkMigration);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Background library reconciliation failed unexpectedly.");
            }
            finally
            {
                Interlocked.Exchange(ref _reconciling, 0);
                var requested = Interlocked.Exchange(ref _reconciliationRequested, 0) != 0;
                if (requested && !_disposed && !_stopping)
                {
                    ScheduleReconciliation(
                        forceArtworkCheck: Interlocked.Exchange(ref _artworkReconciliationRequested, 0) != 0,
                        artworkMigration: Volatile.Read(ref _artworkMigrationPending) != 0);
                }
                else if (requested && _stopping)
                {
                    _recoveryPending = true;
                }
                if (_stopping) TryMarkSessionCleanIfSafe(GetCurrentStore());
            }
        });
        return true;
    }

    private void ScheduleWorker(int delayMs)
    {
        if (_disposed || _stopping || !_settings.AutoSyncEnabled || Volatile.Read(ref _retrying) != 0) return;
        var timer = new Timer(_ => ProcessNext(), null, delayMs, Timeout.Infinite);
        Interlocked.Exchange(ref _workerTimer, timer)?.Dispose();
    }

    private void ProcessNext()
    {
        if (_disposed || _stopping || !_settings.AutoSyncEnabled || Volatile.Read(ref _retrying) != 0 || Interlocked.CompareExchange(ref _processing, 1, 0) != 0) return;
        SyncQueueEntry? operation = null;
        SyncQueueStore? operationStore = null;
        try
        {
            EnsureStore();
            operationStore = GetCurrentStore();
            if (_disposed || _stopping || !_settings.AutoSyncEnabled || operationStore == null || !operationStore.InitialBaselineCompleted) return;
            if (!IsCurrentStoreForSelectedUser(operationStore)) return;
            operation = operationStore.GetPending().FirstOrDefault();
            if (operation == null) return;
            if (!string.Equals(operationStore.SteamUserId, operation.SteamUserId, StringComparison.OrdinalIgnoreCase))
            {
                operationStore.MarkNeedsAttention(operation.Sequence, "The queued Steam account does not match its account-specific queue.");
                return;
            }
            if (!CanProcessOnSelectedStore(operationStore, operation)) return;

            var vdfPath = _pathResolver.ResolveShortcutsVdfPathForUser(operation.SteamUserId);
            if (string.IsNullOrWhiteSpace(vdfPath))
            {
                operationStore.MarkNeedsAttention(operation.Sequence, "The shortcuts.vdf path for the selected Steam account could not be resolved.");
                return;
            }
            var targetVdfPath = vdfPath!;

            var prepared = operationStore.GetPreparedShortcut(operation.Sequence);
            if (prepared != null)
            {
                var unresolvedPreparedVariable = EmulatorPathUtils.FindUnexpandedGameVariable(prepared.Exe, prepared.StartDir, prepared.LaunchOptions);
                if (unresolvedPreparedVariable != null)
                {
                    operationStore.MarkNeedsAttention(operation.Sequence,
                        $"The persisted shortcut still contains Playnite variable {unresolvedPreparedVariable}; refresh the game snapshot and retry.");
                    return;
                }
            }

            Game? game = null;
            if (prepared == null)
            {
                if (!CanProcessOnSelectedStore(operationStore, operation)) return;
                var gameToPrepare = CreateGameSnapshot(operation);
                if (GameActionSelector.GetPreferredLaunchAction(gameToPrepare.GameActions) == null)
                {
                    GameAction? dynamicPlayAction = null;
                    if (Guid.TryParse(operation.SourcePluginId, out var sourcePluginId) && sourcePluginId != Guid.Empty)
                    {
                        dynamicPlayAction = InvokeOnUi(() =>
                        {
                            var liveGame = _playniteApi.Database.Games.FirstOrDefault(candidate =>
                                string.Equals(candidate.Id.ToString("D"), operation.PlayniteGameId, StringComparison.OrdinalIgnoreCase));
                            return GameActionSelector.TryCreateSingleDynamicPlayAction(
                                liveGame,
                                _playniteApi.Addons.Plugins,
                                GameActionSelector.GetCurrentProcessExecutablePath(),
                                _logger);
                        });
                    }

                    if (dynamicPlayAction != null)
                    {
                        gameToPrepare.GameActions = new System.Collections.ObjectModel.ObservableCollection<GameAction>
                        {
                            dynamicPlayAction
                        };
                    }
                }

                if (GameActionSelector.GetPreferredLaunchAction(gameToPrepare.GameActions) == null)
                {
                    var discoveredAction = GameActionSelector.DiscoverFileAction(gameToPrepare, _pathResolver);
                    if (discoveredAction == null)
                    {
                        operationStore.MarkNeedsAttention(operation.Sequence,
                            "No unambiguous executable could be discovered. Configure a Play action in Playnite and retry.");
                        return;
                    }

                    gameToPrepare.GameActions = new System.Collections.ObjectModel.ObservableCollection<GameAction>
                    {
                        discoveredAction
                    };
                }
                game = gameToPrepare;
                var preparation = InvokeOnUi(() =>
                {
                    var success = _importExportService.TryCreateAutomaticShortcut(gameToPrepare, operation.AppId, out var shortcut, out var reason);
                    return (Success: success, Shortcut: shortcut, Reason: reason);
                });
                if (!CanProcessOnSelectedStore(operationStore, operation)) return;
                prepared = preparation.Shortcut;
                if (!preparation.Success)
                {
                    operationStore.MarkNeedsAttention(operation.Sequence, preparation.Reason);
                    return;
                }

                prepared.Tags = new List<string>(operation.Tags ?? new List<string>());

                var current = File.Exists(targetVdfPath) ? ShortcutsFile.Read(targetVdfPath).ToList() : new List<SteamShortcut>();
                if (!ResolveExistingShortcutIdentity(operationStore, operation, prepared, current, out var existing, out var identityError))
                {
                    operationStore.MarkNeedsAttention(operation.Sequence, identityError);
                    return;
                }

                if (existing != null) prepared.AppId = existing.AppId;

            }

            if (!CanProcessOnSelectedStore(operationStore, operation)) return;
            if (game == null) game = CreateGameSnapshot(operation);

            var gridDir = _artworkManager.TryGetGridDirFromVdf(targetVdfPath);
            if (string.IsNullOrEmpty(gridDir))
            {
                operationStore.MarkRetryableFailure(operation.Sequence, "The Steam artwork grid directory could not be resolved; the operation will be retried.");
                return;
            }
            if (!_artworkManager.TryExportArtworkToGrid(game, prepared!.AppId, gridDir!))
            {
                operationStore.MarkRetryableFailure(operation.Sequence, "One or more Playnite artwork files could not be copied and verified; the operation will be retried.");
                return;
            }
            var gridIconPath = _artworkManager.TryGetGridIconPath(prepared.AppId, gridDir!);
            if (!string.IsNullOrWhiteSpace(gridIconPath)) prepared.Icon = gridIconPath!;
            operationStore.MarkPrepared(operation.Sequence, prepared);

            var shortcuts = File.Exists(targetVdfPath) ? ShortcutsFile.Read(targetVdfPath).ToList() : new List<SteamShortcut>();
            var sourceFingerprint = GetFileFingerprint(targetVdfPath);
            if (!ResolveExistingShortcutIdentity(operationStore, operation, prepared!, shortcuts, out var currentShortcut, out var collisionError))
            {
                operationStore.MarkNeedsAttention(operation.Sequence, collisionError);
                return;
            }

            var shortcutChanged = currentShortcut == null;
            if (currentShortcut == null)
            {
                currentShortcut = new SteamShortcut { AppId = prepared!.AppId };
                shortcuts.Add(currentShortcut);
            }

            if (currentShortcut.AppId != prepared!.AppId) { currentShortcut.AppId = prepared.AppId; shortcutChanged = true; }
            if (!string.Equals(currentShortcut.AppName, prepared.AppName, StringComparison.Ordinal)) { currentShortcut.AppName = prepared.AppName; shortcutChanged = true; }
            if (!string.Equals(currentShortcut.Exe, prepared.Exe, StringComparison.Ordinal)) { currentShortcut.Exe = prepared.Exe; shortcutChanged = true; }
            if (!string.Equals(currentShortcut.StartDir, prepared.StartDir, StringComparison.Ordinal)) { currentShortcut.StartDir = prepared.StartDir; shortcutChanged = true; }
            if (!string.Equals(currentShortcut.LaunchOptions, prepared.LaunchOptions, StringComparison.Ordinal)) { currentShortcut.LaunchOptions = prepared.LaunchOptions; shortcutChanged = true; }
            if (!string.IsNullOrWhiteSpace(prepared.Icon) && !string.Equals(currentShortcut.Icon, prepared.Icon, StringComparison.Ordinal))
            {
                currentShortcut.Icon = prepared.Icon!;
                shortcutChanged = true;
            }
            if (prepared.Tags?.Count > 0)
            {
                var existingTags = currentShortcut.Tags?.ToList() ?? new List<string>();
                var mergedTags = existingTags.ToList();
                foreach (var tag in prepared.Tags)
                {
                    if (!mergedTags.Contains(tag, StringComparer.OrdinalIgnoreCase)) mergedTags.Add(tag);
                }
                if (!existingTags.SequenceEqual(mergedTags, StringComparer.OrdinalIgnoreCase))
                {
                    currentShortcut.Tags = mergedTags;
                    shortcutChanged = true;
                }
            }

            if (shortcutChanged)
            {
                if (!CanProcessOnSelectedStore(operationStore, operation)) return;
                if (!SteamProcessHelper.TryIsSteamRunning(out var steamRunning) || steamRunning)
                {
                    operationStore.MarkWaitingForSteam(operation.Sequence, DateTime.UtcNow.AddSeconds(5));
                    return;
                }
                var backupUserId = BackupManager.TryGetSteamUserFromPath(targetVdfPath) ?? Constants.DefaultUserSegment;
                if (File.Exists(targetVdfPath) && !_backupManager.TryCreateManagedBackup(targetVdfPath, backupUserId, out _))
                {
                    operationStore.MarkRetryableFailure(operation.Sequence, "A verified managed backup could not be created.");
                    return;
                }

                var steamBecameActive = false;
                var vdfChangedDuringWrite = false;
                var selectedAccountChanged = false;
                try
                {
                    ShortcutsFile.WriteAtomic(targetVdfPath, shortcuts, () =>
                    {
                        if (!CanProcessOnSelectedStore(operationStore, operation))
                        {
                            selectedAccountChanged = true;
                            return false;
                        }

                        if (!SteamProcessHelper.TryIsSteamRunning(out var nowRunning) || nowRunning)
                        {
                            steamBecameActive = true;
                            return false;
                        }

                        if (!string.Equals(sourceFingerprint, GetFileFingerprint(targetVdfPath), StringComparison.Ordinal))
                        {
                            vdfChangedDuringWrite = true;
                            return false;
                        }

                        return true;
                    });
                }
                catch (OperationCanceledException)
                {
                    if (selectedAccountChanged)
                    {
                        return;
                    }
                    if (steamBecameActive)
                    {
                        operationStore.MarkWaitingForSteam(operation.Sequence, DateTime.UtcNow.AddSeconds(5));
                    }
                    else if (vdfChangedDuringWrite)
                    {
                        operationStore.MarkRetryableFailure(operation.Sequence, "shortcuts.vdf changed during export; it will be reread before retrying.");
                    }
                    else
                    {
                        operationStore.MarkRetryableFailure(operation.Sequence, "The VDF replace safety check failed; the operation will be retried.");
                    }
                    return;
                }
            }

            if (!CanProcessOnSelectedStore(operationStore, operation)) return;
            operationStore.MarkCommitted(operation, prepared.AppId);
            _settings.ExportMap[prepared.AppId.ToString()] = operation.PlayniteGameId;
            try { InvokeOnUi(() => { _library.SavePluginSettings(_settings); return true; }); }
            catch (Exception ex) { _logger.Warn(ex, "Steam shortcut was committed, but the optional Playnite export map could not be saved."); }
            _logger.Info($"Automatic Steam sync committed sequence {operation.Sequence} for '{operation.GameName}' (AppID {prepared.AppId}).");
        }
        catch (InvalidDataException ex)
        {
            if (operation != null) operationStore?.MarkNeedsAttention(operation.Sequence, "The shortcuts.vdf could not be parsed or the generated file failed validation: " + ex.Message);
            _logger.Warn(ex, "Automatic sync stopped because shortcuts.vdf parsing or generated-file validation failed.");
        }
        catch (Exception ex)
        {
            if (operation != null) operationStore?.MarkRetryableFailure(operation.Sequence, ex.Message);
            _logger.Error(ex, "Automatic Steam sync operation failed and will be retried.");
        }
        finally
        {
            Interlocked.Exchange(ref _processing, 0);
            try
            {
                if (_stopping) TryMarkSessionCleanIfSafe(GetCurrentStore());
                if (!_disposed && !_stopping && _settings.AutoSyncEnabled)
                {
                    var currentStore = GetCurrentStore();
                    var retryAt = currentStore?.GetNextRetryUtc();
                    if (currentStore?.GetPending().Count > 0)
                    {
                        ScheduleWorker(250);
                    }
                    else if (retryAt.HasValue)
                    {
                        var wait = Math.Max(1000, (int)Math.Min(int.MaxValue, (retryAt.Value - DateTime.UtcNow).TotalMilliseconds));
                        ScheduleWorker(wait);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Automatic sync could not schedule its next background attempt.");
            }
        }
    }

    private bool ResolveExistingShortcutIdentity(
        SyncQueueStore store,
        SyncQueueEntry operation,
        SyncPreparedShortcut prepared,
        IList<SteamShortcut> shortcuts,
        out SteamShortcut? match,
        out string error)
    {
        match = null;
        error = string.Empty;
        var byAppId = shortcuts.Where(x => x.AppId == prepared.AppId).ToList();
        if (byAppId.Count > 1)
        {
            error = $"Steam contains multiple shortcuts with AppID {prepared.AppId}; resolve the duplicate before retrying.";
            return false;
        }

        if (byAppId.Count == 1)
        {
            var record = store.GetGameRecord(operation.PlayniteGameId);
            if (record?.AppId == prepared.AppId || string.Equals(byAppId[0].StableId, new SteamShortcut { AppName = prepared.AppName, Exe = prepared.Exe }.StableId, StringComparison.OrdinalIgnoreCase))
            {
                match = byAppId[0];
                return true;
            }

            error = $"AppID {prepared.AppId} is already used by a different Steam shortcut.";
            return false;
        }

        var stableId = new SteamShortcut { AppName = prepared.AppName, Exe = prepared.Exe }.StableId;
        var byStable = shortcuts.Where(x => string.Equals(x.StableId, stableId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byStable.Count > 1)
        {
            error = "Steam contains duplicate shortcuts matching this game's launch action.";
            return false;
        }

        if (byStable.Count == 1) match = byStable[0];
        return true;
    }

    internal static Game CreateGameSnapshot(SyncQueueEntry entry)
    {
        var game = new Game
        {
            Id = Guid.Parse(entry.PlayniteGameId),
            PluginId = Guid.TryParse(entry.SourcePluginId, out var pluginId) ? pluginId : Guid.Empty,
            GameId = string.IsNullOrWhiteSpace(entry.GameExternalId) ? null : entry.GameExternalId,
            Name = entry.GameName,
            InstallDirectory = string.IsNullOrWhiteSpace(entry.InstallDirectory) ? null : entry.InstallDirectory,
            IsInstalled = entry.IsInstalled,
            Hidden = entry.IsHidden,
            Icon = string.IsNullOrWhiteSpace(entry.Icon) ? null : entry.Icon,
            CoverImage = string.IsNullOrWhiteSpace(entry.CoverImage) ? null : entry.CoverImage,
            BackgroundImage = string.IsNullOrWhiteSpace(entry.BackgroundImage) ? null : entry.BackgroundImage
        };

        game.Roms = new System.Collections.ObjectModel.ObservableCollection<GameRom>(
            (entry.Roms ?? new List<SyncGameRom>())
                .Select(rom => new GameRom(rom.Name ?? string.Empty, rom.Path ?? string.Empty)));

        if (Enum.TryParse<GameActionType>(entry.ActionType, true, out var type))
        {
            game.GameActions = new System.Collections.ObjectModel.ObservableCollection<GameAction>
            {
                new GameAction
                {
                    Name = "Play",
                    Type = type,
                    Path = entry.ActionPath,
                    Arguments = entry.ActionArguments,
                    WorkingDir = entry.ActionWorkingDirectory,
                    EmulatorId = Guid.TryParse(entry.EmulatorId, out var emulatorId) ? emulatorId : Guid.Empty,
                    EmulatorProfileId = entry.EmulatorProfileId,
                    AdditionalArguments = entry.EmulatorAdditionalArguments,
                    OverrideDefaultArgs = entry.EmulatorOverrideDefaultArgs,
                    IsPlayAction = true
                }
            };
        }

        return game;
    }

    private static string GetFileFingerprint(string path)
    {
        if (!File.Exists(path)) return string.Empty;
        using var sha = SHA256.Create();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
    }

    private T InvokeOnUi<T>(Func<T> action)
    {
        return _uiDispatcher.CheckAccess() ? action() : _uiDispatcher.Invoke(action);
    }

    private SyncQueueStore? GetCurrentStore()
    {
        lock (_sync) return _store;
    }

    private bool IsCurrentStoreForSelectedUser(SyncQueueStore store)
    {
        lock (_sync)
        {
            return ReferenceEquals(_store, store) &&
                   string.Equals(store.SteamUserId, GetSelectedSteamUserId(), StringComparison.OrdinalIgnoreCase);
        }
    }

    private string GetSelectedSteamUserId()
    {
        var selectedUserId = _settings.SelectedSteamUserId;
        if (selectedUserId != null)
        {
            var normalizedUserId = selectedUserId.Trim();
            if (normalizedUserId.Length > 0) return normalizedUserId;
        }
        return _pathResolver.ResolveAutoDetectedSteamUserId() ?? string.Empty;
    }

    private bool CanProcessOnSelectedStore(SyncQueueStore store, SyncQueueEntry operation)
    {
        return !_disposed && !_stopping && _settings.AutoSyncEnabled &&
               IsCurrentStoreForSelectedUser(store) &&
               string.Equals(store.SteamUserId, operation.SteamUserId ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool CanMarkSessionClean(bool recoveryPending, int capturesInProgress, int processing, int reconciling, int retrying)
    {
        return !recoveryPending && capturesInProgress == 0 && processing == 0 && reconciling == 0 && retrying == 0;
    }

    private void TryMarkSessionCleanIfSafe(SyncQueueStore? store)
    {
        if (store == null) return;
        if (CanMarkSessionClean(
                _recoveryPending,
                Volatile.Read(ref _captureInProgress),
                Volatile.Read(ref _processing),
                Volatile.Read(ref _reconciling),
                Volatile.Read(ref _retrying)))
        {
            try { store.MarkSessionClean(); }
            catch (Exception ex) { _logger.Error(ex, "The sync session could not be marked clean during shutdown."); }
        }
    }

    private void PreserveRecoveryForNextSession(SyncQueueStore? store)
    {
        _recoveryPending = true;
        store ??= GetCurrentStore();
        if (store == null) return;
        try { store.MarkSessionStarted(); }
        catch (Exception ex) { _logger.Error(ex, "The sync session could not persist its pending recovery state."); }
    }

    private void EnsureStore()
    {
        var selectedUser = GetSelectedSteamUserId();
        var safeUser = string.IsNullOrWhiteSpace(selectedUser)
            ? "default"
            : new string(selectedUser.Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_').ToArray());
        if (string.IsNullOrWhiteSpace(safeUser)) safeUser = "default";
        var key = safeUser.ToLowerInvariant();

        lock (_sync)
        {
            if (_store != null &&
                string.Equals(_storeKey, key, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_store.SteamUserId, selectedUser, StringComparison.OrdinalIgnoreCase)) return;

            TryMarkSessionCleanIfSafe(_store);
            var syncRoot = Path.Combine(_library.GetPluginUserDataPath(), "automatic-sync");
            var dataPath = Path.Combine(syncRoot, key);
            SyncQueueStore? selectedStore = null;
            var queueBindingFailed = false;

            if (!string.IsNullOrWhiteSpace(selectedUser) &&
                !string.Equals(key, "default", StringComparison.OrdinalIgnoreCase))
            {
                var accountManifest = Path.Combine(dataPath, "sync-state.json");
                var accountJournal = Path.Combine(dataPath, "sync-journal.jsonl");
                if (!File.Exists(accountManifest) && !File.Exists(accountManifest + ".bak") &&
                    !File.Exists(accountJournal) && !File.Exists(accountJournal + ".bak"))
                {
                    var legacyPath = Path.Combine(syncRoot, "default");
                    if (Directory.Exists(legacyPath))
                    {
                        var legacyStore = new SyncQueueStore(legacyPath, _logger);
                        var legacyQueueHasNoAccount = string.IsNullOrWhiteSpace(legacyStore.SteamUserId);
                        var autoDetectCanBindLegacyQueue = string.IsNullOrWhiteSpace(_settings.SelectedSteamUserId);
                        var legacyQueueMatchesSelectedAccount = string.Equals(legacyStore.SteamUserId, selectedUser, StringComparison.OrdinalIgnoreCase);
                        if (legacyQueueMatchesSelectedAccount || (legacyQueueHasNoAccount && autoDetectCanBindLegacyQueue))
                        {
                            try
                            {
                                if (legacyQueueHasNoAccount)
                                {
                                    legacyStore.BindSteamUserId(selectedUser);
                                }
                                selectedStore = legacyStore;
                                dataPath = legacyPath;
                            }
                            catch (Exception ex)
                            {
                                _logger.Error(ex, "The legacy automatic sync queue could not be safely bound to the auto-detected Steam account; processing remains paused.");
                                selectedStore = legacyStore;
                                dataPath = legacyPath;
                                queueBindingFailed = true;
                            }
                        }
                    }
                }
            }

            selectedStore ??= new SyncQueueStore(dataPath, _logger);
            if (!queueBindingFailed && !string.IsNullOrWhiteSpace(selectedUser))
            {
                selectedStore.SetSteamUserId(selectedUser);
            }

            _store = selectedStore;
            _recoveryPending = queueBindingFailed || !_store.WasLastSessionClean || _store.NeedsRecovery;
            if (!queueBindingFailed && string.Equals(_store.SteamUserId, selectedUser, StringComparison.OrdinalIgnoreCase))
            {
                _store.MarkSessionStarted();
            }
            _storeKey = key;
        }
    }
}
