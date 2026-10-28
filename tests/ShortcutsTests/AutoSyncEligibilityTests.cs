using Playnite.SDK.Models;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace SteamShortcutsImporter.Tests;

public class AutoSyncEligibilityTests
{
    [Fact]
    public void ProviderlessEmulatorGame_IsEligibleWhenEmulatorsAreIncluded()
    {
        var settings = new PluginSettings { AutoSyncIncludeEmulators = true };
        var game = new Game
        {
            Id = Guid.NewGuid(),
            PluginId = Guid.Empty,
            IsInstalled = true,
            GameActions = new ObservableCollection<GameAction>
            {
                new GameAction { Type = GameActionType.Emulator, IsPlayAction = true }
            }
        };

        Assert.True(AutoSyncHandler.IsEligibleSource(game, settings, Guid.NewGuid()));
    }

    [Fact]
    public void ProviderlessEmulatorGame_IsExcludedWhenEmulatorsAreDisabled()
    {
        var settings = new PluginSettings { AutoSyncIncludeEmulators = false };
        var game = new Game
        {
            Id = Guid.NewGuid(),
            PluginId = Guid.Empty,
            GameActions = new ObservableCollection<GameAction>
            {
                new GameAction { Type = GameActionType.Emulator, IsPlayAction = true }
            }
        };

        Assert.False(AutoSyncHandler.IsEligibleSource(game, settings, Guid.NewGuid()));
    }

    [Fact]
    public void ProviderlessGameWithoutEmulatorAction_RemainsExcluded()
    {
        var settings = new PluginSettings { AutoSyncIncludeEmulators = true };
        var game = new Game
        {
            Id = Guid.NewGuid(),
            PluginId = Guid.Empty,
            GameActions = new ObservableCollection<GameAction>
            {
                new GameAction { Type = GameActionType.File, Path = @"C:\Games\game.exe" }
            }
        };

        Assert.False(AutoSyncHandler.IsEligibleSource(game, settings, Guid.NewGuid()));
    }

    [Fact]
    public void ProviderBackedGame_StillRequiresItsSourceToBeSelected()
    {
        var selectedSource = Guid.NewGuid();
        var settings = new PluginSettings
        {
            AutoSyncIncludeEmulators = true,
            AutoSyncSourceIds = new List<string>()
        };
        var game = new Game
        {
            Id = Guid.NewGuid(),
            PluginId = Guid.NewGuid(),
            SourceId = selectedSource,
            GameActions = new ObservableCollection<GameAction>
            {
                new GameAction { Type = GameActionType.Emulator, IsPlayAction = true }
            }
        };

        Assert.False(AutoSyncHandler.IsEligibleSource(game, settings, Guid.NewGuid()));

        settings.AutoSyncSourceIds.Add(selectedSource.ToString("D"));
        Assert.True(AutoSyncHandler.IsEligibleSource(game, settings, Guid.NewGuid()));
    }

    [Fact]
    public void SelectingOnePlayniteSource_DoesNotSelectAnotherSourceFromSamePlugin()
    {
        var sharedPluginId = Guid.NewGuid();
        var xboxSourceId = Guid.NewGuid();
        var gamePassSourceId = Guid.NewGuid();
        var settings = new PluginSettings
        {
            AutoSyncSourceIds = new List<string> { xboxSourceId.ToString("D") }
        };
        var xboxGame = new Game { Id = Guid.NewGuid(), PluginId = sharedPluginId, SourceId = xboxSourceId };
        var gamePassGame = new Game { Id = Guid.NewGuid(), PluginId = sharedPluginId, SourceId = gamePassSourceId };

        Assert.True(AutoSyncHandler.IsEligibleSource(xboxGame, settings, Guid.NewGuid()));
        Assert.False(AutoSyncHandler.IsEligibleSource(gamePassGame, settings, Guid.NewGuid()));
    }

    [Fact]
    public void InitialFilteredExport_UsesExactSourceAndInstalledFilter()
    {
        var selectedSource = Guid.NewGuid();
        var settings = new PluginSettings
        {
            AutoSyncOnlyInstalled = true,
            AutoSyncSourceIds = new List<string> { selectedSource.ToString("D") }
        };
        var game = new Game
        {
            Id = Guid.NewGuid(),
            PluginId = Guid.NewGuid(),
            SourceId = selectedSource,
            IsInstalled = true,
            GameActions = new ObservableCollection<GameAction>
            {
                new GameAction { Type = GameActionType.File, Path = @"C:\Games\game.exe", IsPlayAction = true }
            }
        };

        Assert.True(AutoSyncHandler.IsEligibleForInitialFilteredExport(game, settings, Guid.NewGuid()));
        game.IsInstalled = false;
        Assert.False(AutoSyncHandler.IsEligibleForInitialFilteredExport(game, settings, Guid.NewGuid()));
    }

    [Fact]
    public void InitialFilteredExport_RequiresEmulatorAndUrlOptIns()
    {
        var importerPluginId = Guid.NewGuid();
        var emulator = new Game
        {
            Id = Guid.NewGuid(),
            PluginId = Guid.Empty,
            IsInstalled = true,
            GameActions = new ObservableCollection<GameAction>
            {
                new GameAction { Type = GameActionType.Emulator, IsPlayAction = true }
            }
        };
        var settings = new PluginSettings { AutoSyncIncludeEmulators = false };
        Assert.False(AutoSyncHandler.IsEligibleForInitialFilteredExport(emulator, settings, importerPluginId));
        settings.AutoSyncIncludeEmulators = true;
        Assert.True(AutoSyncHandler.IsEligibleForInitialFilteredExport(emulator, settings, importerPluginId));

        var sourceId = Guid.NewGuid();
        var urlGame = new Game
        {
            Id = Guid.NewGuid(),
            PluginId = Guid.NewGuid(),
            SourceId = sourceId,
            IsInstalled = true,
            GameActions = new ObservableCollection<GameAction>
            {
                new GameAction { Type = GameActionType.URL, Path = "https://example.invalid/game", IsPlayAction = true }
            }
        };
        settings.AutoSyncSourceIds = new List<string> { sourceId.ToString("D") };
        settings.AutoSyncIncludeUrls = false;
        Assert.False(AutoSyncHandler.IsEligibleForInitialFilteredExport(urlGame, settings, importerPluginId));
        settings.AutoSyncIncludeUrls = true;
        Assert.True(AutoSyncHandler.IsEligibleForInitialFilteredExport(urlGame, settings, importerPluginId));
    }

    [Fact]
    public void BatchAppIdOwnership_RejectsCollisionsBetweenDifferentPlayniteGames()
    {
        var owners = new Dictionary<uint, Guid>();
        var firstGame = Guid.NewGuid();
        var secondGame = Guid.NewGuid();

        Assert.True(ImportExportService.TryClaimBatchAppId(owners, firstGame, 12345));
        Assert.True(ImportExportService.TryClaimBatchAppId(owners, firstGame, 12345));
        Assert.False(ImportExportService.TryClaimBatchAppId(owners, secondGame, 12345));
        Assert.False(ImportExportService.TryClaimBatchAppId(owners, Guid.Empty, 12346));
    }

    [Fact]
    public void StableShortcutIdentity_ReusesSingleExistingAppIdAndRejectsDuplicates()
    {
        const string name = "Sample Adventure";
        const string exe = @"C:\TestGames\Emulator.exe";
        var existing = new SteamShortcut { AppName = name, Exe = exe, AppId = 123456789u };
        var byStable = new Dictionary<string, List<SteamShortcut>>(StringComparer.OrdinalIgnoreCase)
        {
            [existing.StableId] = new List<SteamShortcut> { existing }
        };

        Assert.NotEqual(existing.AppId, Utils.GenerateShortcutAppId(exe, name));
        Assert.True(ImportExportService.TryResolveStableShortcutAppId(name, exe, byStable, out var appId, out var error));
        Assert.Equal(123456789u, appId);
        Assert.Empty(error);

        byStable[existing.StableId].Add(new SteamShortcut { AppName = name, Exe = exe, AppId = 2387725352u });
        Assert.False(ImportExportService.TryResolveStableShortcutAppId(name, exe, byStable, out appId, out error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void SourcePicker_SeparatesSourcesProvidedByTheSamePlugin()
    {
        var sharedPluginId = Guid.NewGuid();
        var xboxSourceId = Guid.NewGuid();
        var gamePassSourceId = Guid.NewGuid();
        var games = new[]
        {
            new Game { Id = Guid.NewGuid(), PluginId = sharedPluginId, SourceId = xboxSourceId },
            new Game { Id = Guid.NewGuid(), PluginId = sharedPluginId, SourceId = gamePassSourceId }
        };

        var sources = AutoSyncSourceSelector.GetSources(games, Guid.NewGuid());

        Assert.Equal(2, sources.Count);
        Assert.Equal(2, sources.Select(source => source.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(sources, source => source.Id == xboxSourceId.ToString("D"));
        Assert.Contains(sources, source => source.Id == gamePassSourceId.ToString("D"));
    }

    [Fact]
    public void LegacyPluginSelections_MigrateToExactPlayniteSourceIds()
    {
        var sharedPluginId = Guid.NewGuid();
        var xboxSourceId = Guid.NewGuid();
        var gamePassSourceId = Guid.NewGuid();
        var settings = new PluginSettings { AutoSyncPluginIds = new List<string> { sharedPluginId.ToString("D") } };
        var games = new[]
        {
            new Game { Id = Guid.NewGuid(), PluginId = sharedPluginId, SourceId = xboxSourceId },
            new Game { Id = Guid.NewGuid(), PluginId = sharedPluginId, SourceId = gamePassSourceId }
        };

        Assert.True(AutoSyncSourceSelector.MigrateLegacyPluginSelections(settings, games, Guid.NewGuid()));
        Assert.Contains(xboxSourceId.ToString("D"), settings.AutoSyncSourceIds);
        Assert.Contains(gamePassSourceId.ToString("D"), settings.AutoSyncSourceIds);
        Assert.Empty(settings.AutoSyncPluginIds);
    }

    [Fact]
    public void LegacyPluginSelections_RemoveMalformedAndEmptyIds()
    {
        var settings = new PluginSettings
        {
            AutoSyncPluginIds = new List<string> { "not-a-guid", Guid.Empty.ToString("D") }
        };

        Assert.True(AutoSyncSourceSelector.MigrateLegacyPluginSelections(settings, Array.Empty<Game>(), Guid.NewGuid()));
        Assert.Empty(settings.AutoSyncPluginIds);
        Assert.Empty(settings.AutoSyncSourceIds);
    }

    [Fact]
    public void ProviderBackedGameWithoutPlayniteSourceId_IsExcluded()
    {
        var pluginId = Guid.NewGuid();
        var settings = new PluginSettings { AutoSyncSourceIds = new List<string> { pluginId.ToString("D") } };
        var game = new Game { Id = Guid.NewGuid(), PluginId = pluginId, SourceId = Guid.Empty };

        Assert.False(AutoSyncHandler.IsEligibleSource(game, settings, Guid.NewGuid()));
    }

    [Theory]
    [InlineData("Steam", true)]
    [InlineData("steam", true)]
    [InlineData("Steam Family Sharing", true)]
    [InlineData("Steam Shortcuts", true)]
    [InlineData("Epic Games", false)]
    public void IsSteamSource_FiltersSteamByNameCaseInsensitively(string sourceName, bool expected)
    {
        Assert.Equal(expected, AutoSyncHandler.IsSteamSource(sourceName));
    }

    [Fact]
    public void SelectActionForAutomaticSync_FiltersUninstalledGame()
    {
        var game = CreateActionGame(new GameAction { Type = GameActionType.File, Path = @"C:\Games\game.exe" });
        game.IsInstalled = false;

        var result = AutoSyncHandler.SelectActionForAutomaticSync(game, new PluginSettings { AutoSyncOnlyInstalled = true });

        Assert.NotNull(result.Action);
        Assert.Contains("not installed", result.FilteredReason);
    }

    [Fact]
    public void SelectActionForAutomaticSync_FiltersHiddenGame()
    {
        var game = CreateActionGame(new GameAction { Type = GameActionType.File, Path = @"C:\Games\game.exe" });
        game.Hidden = true;

        var result = AutoSyncHandler.SelectActionForAutomaticSync(game, new PluginSettings());

        Assert.NotNull(result.Action);
        Assert.Contains("hidden", result.FilteredReason);
    }

    [Fact]
    public void SelectActionForAutomaticSync_FiltersPrimaryEmulatorActionEvenWhenSecondaryFileActionExists()
    {
        var secondaryFileAction = new GameAction { Type = GameActionType.File, Path = @"C:\Tools\setup.exe" };
        var playEmulatorAction = new GameAction { Type = GameActionType.Emulator, IsPlayAction = true };
        var game = CreateActionGame(secondaryFileAction, playEmulatorAction);

        var result = AutoSyncHandler.SelectActionForAutomaticSync(
            game,
            new PluginSettings { AutoSyncIncludeEmulators = false });

        Assert.Null(result.Action);
        Assert.Contains("Emulator actions", result.FilteredReason);
    }

    [Fact]
    public void SelectActionForAutomaticSync_FiltersUrlUntilExplicitlyIncluded()
    {
        var urlAction = new GameAction { Type = GameActionType.URL, Path = "https://example.invalid/game", IsPlayAction = true };
        var game = CreateActionGame(urlAction);

        var excluded = AutoSyncHandler.SelectActionForAutomaticSync(game, new PluginSettings { AutoSyncIncludeUrls = false });
        var included = AutoSyncHandler.SelectActionForAutomaticSync(game, new PluginSettings { AutoSyncIncludeUrls = true });

        Assert.Null(excluded.Action);
        Assert.Contains("URL launch actions", excluded.FilteredReason);
        Assert.Same(urlAction, included.Action);
        Assert.Empty(included.FilteredReason);
    }

    [Fact]
    public void SelectActionForAutomaticSync_LeavesFilePlayActionEligibleWhenOtherActionFiltersAreOff()
    {
        var fileAction = new GameAction { Type = GameActionType.File, Path = @"C:\Games\game.exe", IsPlayAction = true };
        var game = CreateActionGame(fileAction);

        var result = AutoSyncHandler.SelectActionForAutomaticSync(
            game,
            new PluginSettings { AutoSyncIncludeEmulators = false, AutoSyncIncludeUrls = false });

        Assert.Same(fileAction, result.Action);
        Assert.Empty(result.FilteredReason);
    }

    [Fact]
    public void SelectActionForAutomaticSync_UsesUnambiguousDiscoveredExeForInstalledProviderGame()
    {
        var sourceId = Guid.NewGuid();
        var pluginId = Guid.NewGuid();
        var installDirectory = Path.Combine(Path.GetTempPath(), "non-steam-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(installDirectory);
        var executable = Path.Combine(installDirectory, "Example Game.exe");
        File.WriteAllText(executable, "test executable marker");

        try
        {
            var game = new Game
            {
                Id = Guid.NewGuid(),
                PluginId = pluginId,
                SourceId = sourceId,
                Name = "Example Game",
                InstallDirectory = installDirectory,
                IsInstalled = true,
                GameActions = new ObservableCollection<GameAction>()
            };
            var settings = new PluginSettings
            {
                AutoSyncOnlyInstalled = true,
                AutoSyncSourceIds = new List<string> { sourceId.ToString("D") }
            };

            var result = AutoSyncHandler.SelectActionForAutomaticSync(game, settings, new SteamPathResolver(null));

            Assert.Empty(result.FilteredReason);
            Assert.Equal(GameActionType.File, result.Action?.Type);
            Assert.Equal(executable, result.Action?.Path);
            Assert.True(AutoSyncHandler.IsEligibleForInitialFilteredExport(game, settings, Guid.NewGuid(), new SteamPathResolver(null)));
        }
        finally
        {
            Directory.Delete(installDirectory, recursive: true);
        }
    }

    [Fact]
    public void SelectActionForAutomaticSync_LeavesAmbiguousExecutableFolderWithoutAction()
    {
        var installDirectory = Path.Combine(Path.GetTempPath(), "non-steam-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(installDirectory);
        File.WriteAllText(Path.Combine(installDirectory, "Alpha.exe"), "test executable marker");
        File.WriteAllText(Path.Combine(installDirectory, "Beta.exe"), "test executable marker");

        try
        {
            var sourceId = Guid.NewGuid();
            var game = new Game
            {
                Id = Guid.NewGuid(),
                PluginId = Guid.NewGuid(),
                SourceId = sourceId,
                Name = "Ambiguous Game",
                InstallDirectory = installDirectory,
                IsInstalled = true,
                GameActions = new ObservableCollection<GameAction>()
            };
            var settings = new PluginSettings
            {
                AutoSyncSourceIds = new List<string> { sourceId.ToString("D") }
            };

            var result = AutoSyncHandler.SelectActionForAutomaticSync(game, settings, new SteamPathResolver(null));

            Assert.Null(result.Action);
            Assert.Empty(result.FilteredReason);
            Assert.False(AutoSyncHandler.IsEligibleForInitialFilteredExport(game, settings, Guid.NewGuid(), new SteamPathResolver(null)));
        }
        finally
        {
            Directory.Delete(installDirectory, recursive: true);
        }
    }

    [Fact]
    public void ExecutableDiscovery_ExcludesXboxGameLaunchHelper()
    {
        var installDirectory = Path.Combine(Path.GetTempPath(), "non-steam-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(installDirectory);
        File.WriteAllText(Path.Combine(installDirectory, "gamelaunchhelper.exe"), "test launcher marker");

        try
        {
            Assert.Empty(new SteamPathResolver(null).ScanForExecutables(installDirectory));
        }
        finally
        {
            Directory.Delete(installDirectory, recursive: true);
        }
    }

    [Fact]
    public void CreateGameSnapshot_RestoresPlayniteProviderGameIdForEmulatorVariableExpansion()
    {
        var gameId = Guid.NewGuid();
        var externalGameId = "rom-provider-id-456";
        var emulatorId = Guid.NewGuid();
        var entry = new SyncQueueEntry
        {
            PlayniteGameId = gameId.ToString("D"),
            SourcePluginId = Guid.Empty.ToString("D"),
            GameExternalId = externalGameId,
            GameName = "Emulated game",
            ActionType = GameActionType.Emulator.ToString(),
            EmulatorId = emulatorId.ToString("D"),
            EmulatorProfileId = "profile-1"
        };

        var snapshot = AutoSyncHandler.CreateGameSnapshot(entry);

        Assert.Equal(externalGameId, snapshot.GameId);
        Assert.Equal(emulatorId, Assert.Single(snapshot.GameActions!).EmulatorId);
    }

    [Fact]
    public void CreateGameSnapshot_RestoresRomFilesForImagePathExpansion()
    {
        var romPath = @"C:\TestGames\Switch\game.nsp";
        var entry = new SyncQueueEntry
        {
            PlayniteGameId = Guid.NewGuid().ToString("D"),
            GameName = "Emulated game",
            Roms = new List<SyncGameRom>
            {
                new SyncGameRom { Name = "game.nsp", Path = romPath }
            }
        };

        var snapshot = AutoSyncHandler.CreateGameSnapshot(entry);

        var rom = Assert.Single(snapshot.Roms!);
        Assert.Equal("game.nsp", rom.Name);
        Assert.Equal(romPath, rom.Path);
    }

    [Fact]
    public void ComputeFingerprint_ChangesWhenPlayniteArtworkReferencesChange()
    {
        var baseline = new SyncQueueEntry
        {
            PlayniteGameId = Guid.NewGuid().ToString("D"),
            Icon = "icon-v1",
            CoverImage = "cover-v1",
            BackgroundImage = "background-v1"
        };

        var iconChanged = baseline.Clone();
        iconChanged.Icon = "icon-v2";
        var coverChanged = baseline.Clone();
        coverChanged.CoverImage = "cover-v2";
        var backgroundChanged = baseline.Clone();
        backgroundChanged.BackgroundImage = "background-v2";

        var baselineFingerprint = AutoSyncHandler.ComputeFingerprint(baseline);
        Assert.NotEqual(baselineFingerprint, AutoSyncHandler.ComputeFingerprint(iconChanged));
        Assert.NotEqual(baselineFingerprint, AutoSyncHandler.ComputeFingerprint(coverChanged));
        Assert.NotEqual(baselineFingerprint, AutoSyncHandler.ComputeFingerprint(backgroundChanged));
    }

    private static Game CreateActionGame(params GameAction[] actions)
    {
        return new Game
        {
            Id = Guid.NewGuid(),
            PluginId = Guid.NewGuid(),
            IsInstalled = true,
            GameActions = new ObservableCollection<GameAction>(actions)
        };
    }
}
