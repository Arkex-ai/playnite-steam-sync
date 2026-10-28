using Playnite.SDK.Models;
using System;
using System.Collections.ObjectModel;
using Xunit;

namespace SteamShortcutsImporter.Tests;

public class GameActionSelectorTests
{
    [Fact]
    public void CreatePlayniteStartAction_UsesStableGameIdAndCliArguments()
    {
        var gameId = Guid.NewGuid();
        var action = GameActionSelector.CreatePlayniteStartAction(
            gameId,
            @"C:\Program Files\Playnite\Playnite.DesktopApp.exe");

        Assert.NotNull(action);
        Assert.Equal(GameActionType.File, action!.Type);
        Assert.Equal(@"C:\Program Files\Playnite\Playnite.DesktopApp.exe", action.Path);
        Assert.Equal($"--start {gameId:D} --actionindex 0 --nolibupdate", action.Arguments);
        Assert.Equal(@"C:\Program Files\Playnite", action.WorkingDir);
    }

    [Fact]
    public void TryGetSoleDynamicActionIndex_RejectsMultipleControllers()
    {
        var success = GameActionSelector.TryGetSoleDynamicActionIndex(null, 2, out var actionIndex);

        Assert.False(success);
        Assert.Equal(0, actionIndex);
    }

    [Fact]
    public void TryGetSoleDynamicActionIndex_DoesNotOverrideConfiguredPlayAction()
    {
        var game = new Game
        {
            Id = Guid.NewGuid(),
            PluginId = Guid.NewGuid(),
            Name = "Script game",
            GameActions = new ObservableCollection<GameAction>
            {
                new GameAction { Type = GameActionType.Script, IsPlayAction = true }
            }
        };
        var success = GameActionSelector.TryGetSoleDynamicActionIndex(game.GameActions, 1, out var actionIndex);

        Assert.False(success);
        Assert.Equal(0, actionIndex);
    }

    [Fact]
    public void GetPreferredLaunchAction_UsesPlayniteEmulatorPlayActionBeforeSecondaryFileAction()
    {
        var secondaryFileAction = new GameAction
        {
            Type = GameActionType.File,
            Path = @"C:\Tools\setup.exe",
            IsPlayAction = false
        };
        var playEmulatorAction = new GameAction
        {
            Type = GameActionType.Emulator,
            EmulatorProfileId = "play-profile",
            IsPlayAction = true
        };
        var actions = new ObservableCollection<GameAction> { secondaryFileAction, playEmulatorAction };

        Assert.Same(playEmulatorAction, GameActionSelector.GetPreferredLaunchAction(actions));
    }

    [Fact]
    public void GetPreferredLaunchAction_UsesPlayniteUrlPlayActionBeforeSecondaryFileAction()
    {
        var secondaryFileAction = new GameAction
        {
            Type = GameActionType.File,
            Path = @"C:\Tools\launcher.exe",
            IsPlayAction = false
        };
        var playUrlAction = new GameAction
        {
            Type = GameActionType.URL,
            Path = "https://example.invalid/launch",
            IsPlayAction = true
        };
        var actions = new ObservableCollection<GameAction> { secondaryFileAction, playUrlAction };

        Assert.Same(playUrlAction, GameActionSelector.GetPreferredLaunchAction(actions));
    }

    [Fact]
    public void GetPreferredLaunchAction_SkipsAddonGeneratedSteamUrlAndUsesDirectAction()
    {
        var directAction = new GameAction
        {
            Name = Constants.PlayDirectActionName,
            Type = GameActionType.File,
            Path = @"C:\Program Files\Playnite\Playnite.DesktopApp.exe",
            Arguments = "--start 3415f599-c59d-4ae2-a974-d8941f4fffc8 --actionindex 0 --nolibupdate",
            IsPlayAction = false
        };
        var generatedSteamAction = new GameAction
        {
            Name = Constants.PlaySteamActionName,
            Type = GameActionType.URL,
            Path = "steam://rungameid/14511047789871366144",
            IsPlayAction = true
        };
        var actions = new ObservableCollection<GameAction> { generatedSteamAction, directAction };

        Assert.Same(directAction, GameActionSelector.GetPreferredLaunchAction(actions));
    }

    [Fact]
    public void GetPreferredLaunchAction_DoesNotFallBackToSecondaryActionWhenPrimaryIsUnsupported()
    {
        var primaryScriptAction = new GameAction
        {
            Type = GameActionType.Script,
            Path = "launch.ps1",
            IsPlayAction = true
        };
        var secondaryFileAction = new GameAction
        {
            Type = GameActionType.File,
            Path = @"C:\Tools\launcher.exe",
            IsPlayAction = false
        };
        var actions = new ObservableCollection<GameAction> { secondaryFileAction, primaryScriptAction };

        Assert.Null(GameActionSelector.GetPreferredLaunchAction(actions));
    }

    [Fact]
    public void GetPreferredLaunchAction_UsesFileFallbackWhenNoActionIsMarkedAsPlay()
    {
        var fileAction = new GameAction
        {
            Type = GameActionType.File,
            Path = @"C:\Games\game.exe",
            IsPlayAction = false
        };
        var emulatorAction = new GameAction
        {
            Type = GameActionType.Emulator,
            IsPlayAction = false
        };

        Assert.Same(fileAction, GameActionSelector.GetPreferredLaunchAction(new[] { emulatorAction, fileAction }));
    }

    [Fact]
    public void WriteBack_DoesNotReplaceAnEmulatorPlayActionWithSecondaryFileAction()
    {
        var secondaryFileAction = new GameAction
        {
            Type = GameActionType.File,
            Path = @"C:\Tools\setup.exe",
            IsPlayAction = false
        };
        var playEmulatorAction = new GameAction
        {
            Type = GameActionType.Emulator,
            IsPlayAction = true
        };
        var game = new Game
        {
            Name = "Emulated game",
            GameActions = new ObservableCollection<GameAction> { secondaryFileAction, playEmulatorAction }
        };

        Assert.Null(WriteBackHandler.GetFileLaunchAction(game));
    }

}
