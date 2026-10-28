using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using PlaynitePlugin = Playnite.SDK.Plugins.Plugin;

namespace SteamShortcutsImporter;

internal static class GameActionSelector
{
    public static GameAction CreatePlayniteStartAction(Guid gameId, string playniteExecutablePath, string? actionName = null)
    {
        if (gameId == Guid.Empty) throw new ArgumentException("A Playnite game ID is required.", nameof(gameId));
        if (string.IsNullOrWhiteSpace(playniteExecutablePath)) throw new ArgumentException("The Playnite executable path is required.", nameof(playniteExecutablePath));

        return new GameAction
        {
            Name = string.IsNullOrWhiteSpace(actionName) ? "Play via Playnite" : actionName,
            Type = GameActionType.File,
            Path = playniteExecutablePath,
            Arguments = $"--start {gameId:D} --actionindex 0 --nolibupdate",
            WorkingDir = Path.GetDirectoryName(playniteExecutablePath) ?? string.Empty,
            IsPlayAction = true
        };
    }

    public static GameAction? TryCreateSingleDynamicPlayAction(Game? game, IEnumerable<PlaynitePlugin>? plugins, string? playniteExecutablePath, ILogger? logger = null)
    {
        if (game == null || game.Id == Guid.Empty || string.IsNullOrWhiteSpace(playniteExecutablePath)) return null;
        if (game.GameActions?.Any(action => action != null && action.IsPlayAction) == true) return null;

        var controllers = new List<PlayController>();
        try
        {
            foreach (var plugin in plugins ?? Enumerable.Empty<PlaynitePlugin>())
            {
                if (plugin == null || (!game.IncludeLibraryPluginAction && plugin.Id == game.PluginId)) continue;

                try
                {
                    var provided = plugin.GetPlayActions(new GetPlayActionsArgs { Game = game });
                    if (provided != null) controllers.AddRange(provided.Where(controller => controller != null));
                }
                catch (Exception ex)
                {
                    logger?.Warn(ex, $"Could not inspect dynamic Play actions from plugin {plugin.Id} for '{game.Name}'.");
                }
            }

            if (!TryGetSoleDynamicActionIndex(game.GameActions, controllers.Count, out var actionIndex)) return null;
            var action = CreatePlayniteStartAction(game.Id, playniteExecutablePath!, controllers[0].Name);
            action.Arguments = $"--start {game.Id:D} --actionindex {actionIndex} --nolibupdate";
            return action;
        }
        finally
        {
            foreach (var controller in controllers)
            {
                try { controller.Dispose(); }
                catch (Exception ex) { logger?.Warn(ex, $"Could not dispose a dynamic Play action for '{game.Name}'."); }
            }
        }
    }

    internal static bool TryGetSoleDynamicActionIndex(IEnumerable<GameAction>? configuredActions, int dynamicActionCount, out int actionIndex)
    {
        actionIndex = 0;
        return dynamicActionCount == 1 &&
            configuredActions?.Any(action => action != null && action.IsPlayAction) != true;
    }

    public static string? GetCurrentProcessExecutablePath()
    {
        try { return Process.GetCurrentProcess().MainModule?.FileName; }
        catch { return null; }
    }

    public static GameAction? DiscoverFileAction(Game game, SteamPathResolver? pathResolver)
    {
        if (game == null || pathResolver == null) return null;

        var executable = pathResolver.TryDiscoverExecutable(game);
        if (string.IsNullOrWhiteSpace(executable)) return null;

        return new GameAction
        {
            Name = string.IsNullOrWhiteSpace(game.Name) ? System.IO.Path.GetFileNameWithoutExtension(executable) : game.Name,
            Type = GameActionType.File,
            Path = executable,
            WorkingDir = game.InstallDirectory,
            IsPlayAction = true
        };
    }

    public static GameAction? GetPreferredLaunchAction(IEnumerable<GameAction>? actions)
    {
        var available = actions?.Where(action => action != null).ToList() ?? new List<GameAction>();
        var playAction = available.FirstOrDefault(action => action.IsPlayAction && !IsGeneratedSteamPlayAction(action));
        if (playAction != null)
        {
            return IsSupported(playAction) ? playAction : null;
        }

        return available.FirstOrDefault(action => !IsGeneratedSteamPlayAction(action) && action.Type == GameActionType.File && !string.IsNullOrWhiteSpace(action.Path))
            ?? available.FirstOrDefault(action => !IsGeneratedSteamPlayAction(action) && action.Type == GameActionType.Emulator)
            ?? available.FirstOrDefault(action => !IsGeneratedSteamPlayAction(action) && action.Type == GameActionType.URL && !string.IsNullOrWhiteSpace(action.Path));
    }

    private static bool IsGeneratedSteamPlayAction(GameAction action)
    {
        return action.Type == GameActionType.URL &&
            string.Equals(action.Name, Constants.PlaySteamActionName, System.StringComparison.OrdinalIgnoreCase) &&
            (action.Path?.StartsWith("steam://rungameid/", System.StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static bool IsSupported(GameAction action)
    {
        return action.Type == GameActionType.File && !string.IsNullOrWhiteSpace(action.Path)
            || action.Type == GameActionType.Emulator
            || action.Type == GameActionType.URL && !string.IsNullOrWhiteSpace(action.Path);
    }
}
