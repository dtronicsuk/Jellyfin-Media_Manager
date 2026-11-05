using System;
using System.Collections.Generic;
using Jellyfin.Plugin.MediaManager.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.MediaManager;

/// <summary>
/// Media Manager: upload and manage media files in library folders from inside Jellyfin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Media Manager";

    public override Guid Id => Guid.Parse("b7f2d4a1-6c3e-4f85-9a10-3d5e8c7b2f64");

    public override string Description =>
        "Upload media and manage files in your library folders from the Jellyfin dashboard.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = GetType().Namespace;

        // Main page, shown in the dashboard sidebar.
        yield return new PluginPageInfo
        {
            Name = "MediaManager",
            DisplayName = "Media Manager",
            EmbeddedResourcePath = $"{ns}.Web.dashboard.html",
            EnableInMainMenu = true,
            MenuSection = "server",
            MenuIcon = "folder_open"
        };

        // Settings page (reachable from Dashboard -> Plugins -> Media Manager).
        yield return new PluginPageInfo
        {
            Name = "MediaManagerSettings",
            EmbeddedResourcePath = $"{ns}.Web.settings.html"
        };
    }
}
