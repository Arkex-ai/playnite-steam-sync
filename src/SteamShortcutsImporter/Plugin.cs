
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace SteamShortcutsImporter;

public class Plugin : LibraryPlugin
{
    private static readonly System.Guid PluginGuid = System.Guid.Parse(Constants.PluginId);
    private readonly ShortcutsLibrary Implementation;

    public Plugin(IPlayniteAPI api) : base(api)
    {
        Implementation = new ShortcutsLibrary(api, this);
    }

    public override System.Guid Id => PluginGuid;

    public override string Name => Constants.PluginName;

    public override ISettings GetSettings(bool firstRunSettings) => Implementation.GetSettings(firstRunSettings);

    public override System.Windows.Controls.UserControl GetSettingsView(bool firstRunSettings) => Implementation.GetSettingsView(firstRunSettings);

    public override System.Collections.Generic.IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args) => Implementation.GetMainMenuItems(args);

    public override System.Collections.Generic.IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args) => Implementation.GetGameMenuItems(args);

    public override System.Collections.Generic.IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args) => Implementation.GetGames(args);

    public override void OnLibraryUpdated(OnLibraryUpdatedEventArgs args) => Implementation.OnLibraryUpdated(args);

    public override void OnApplicationStopped(OnApplicationStoppedEventArgs args) => Implementation.OnApplicationStopped(args);

    public override void Dispose()
    {
        Implementation.Dispose();
        base.Dispose();
    }

    internal void SaveSettings(PluginSettings settings) => SavePluginSettings(settings);
}
