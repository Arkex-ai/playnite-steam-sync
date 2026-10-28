using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SteamShortcutsImporter;

internal sealed class AutoSyncSourceOption
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

internal static class AutoSyncSourceSelector
{
    public static List<AutoSyncSourceOption> GetSources(IEnumerable<Game> games, Guid importerPluginId)
    {
        if (games == null) throw new ArgumentNullException(nameof(games));

        return games
            .Where(game => game != null && game.PluginId != Guid.Empty && game.PluginId != importerPluginId && game.SourceId != Guid.Empty)
            .Where(game => !AutoSyncHandler.IsSteamSource(game.Source?.Name ?? game.Source?.ToString()))
            .GroupBy(game => game.SourceId)
            .Select(group => new AutoSyncSourceOption
            {
                Id = group.Key.ToString("D"),
                Name = group.Select(game => game.Source?.Name)
                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? group.Key.ToString("D")
            })
            .OrderBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool MigrateLegacyPluginSelections(PluginSettings settings, IEnumerable<Game> games, Guid importerPluginId)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        if (games == null) throw new ArgumentNullException(nameof(games));
        settings.AutoSyncSourceIds ??= new List<string>();
        settings.AutoSyncPluginIds ??= new List<string>();
        if (settings.AutoSyncPluginIds.Count == 0) return false;

        var oldPluginIds = settings.AutoSyncPluginIds
            .Select(value => Guid.TryParse(value, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty && id != importerPluginId)
            .ToHashSet();
        if (oldPluginIds.Count == 0)
        {
            settings.AutoSyncPluginIds.Clear();
            return true;
        }

        var libraryGames = games.Where(game => game != null).ToList();
        var migratedSourceIds = libraryGames
            .Where(game => oldPluginIds.Contains(game.PluginId) && game.SourceId != Guid.Empty)
            .Where(game => !AutoSyncHandler.IsSteamSource(game.Source?.Name ?? game.Source?.ToString()))
            .Select(game => game.SourceId.ToString("D"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var resolvedPluginIds = libraryGames
            .Where(game => oldPluginIds.Contains(game.PluginId) && game.SourceId != Guid.Empty)
            .Select(game => game.PluginId)
            .ToHashSet();

        foreach (var sourceId in migratedSourceIds)
        {
            if (!settings.AutoSyncSourceIds.Contains(sourceId, StringComparer.OrdinalIgnoreCase))
            {
                settings.AutoSyncSourceIds.Add(sourceId);
            }
        }

        settings.AutoSyncPluginIds.RemoveAll(value =>
            !Guid.TryParse(value, out var id) || id == Guid.Empty || id == importerPluginId || resolvedPluginIds.Contains(id));
        return migratedSourceIds.Count > 0 || oldPluginIds.Count != settings.AutoSyncPluginIds.Count;
    }
}
