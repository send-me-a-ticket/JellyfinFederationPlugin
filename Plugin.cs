using System;
using System.Collections.Generic;
using System.Globalization;
using JellyfinFederationPlugin.Api;
using JellyfinFederationPlugin.Configuration;
using JellyfinFederationPlugin.Library;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace JellyfinFederationPlugin
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public override string Name => "Jellyfin Federation Plugin";
        public override string Description =>
            "Enables federation across Jellyfin servers for streaming media without syncing files.";
        public override Guid Id => new Guid("931820b5-4177-4f48-be30-f0a34db3693f");

        public static Plugin Instance { get; private set; }

        private readonly FederationLibraryService _federationLibraryService;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<Plugin> _logger;
        private readonly IConfigurationManager _configManager;

        public Plugin(
            IApplicationPaths applicationPaths,
            IXmlSerializer xmlSerializer,
            ILibraryManager libraryManager,
            ILogger<Plugin> logger,
            IConfigurationManager configManager
        )
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;

            _libraryManager = libraryManager;
            _logger = logger;
            _configManager = configManager;

            try
            {
                var federationRequestHandler = new FederationRequestHandler(_logger);

                var playbackProxyService = new PlaybackProxyService(
                    federationRequestHandler,
                    _logger,
                    _configManager
                );

                _federationLibraryService = new FederationLibraryService(
                    _libraryManager,
                    federationRequestHandler,
                    _logger
                );

                // Initialize federation libraries asynchronously
                _ = InitializeFederationAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initializing Jellyfin Federation Plugin");
            }
        }

        private async System.Threading.Tasks.Task InitializeFederationAsync()
        {
            try
            {
                await _federationLibraryService.MergeFederatedLibrariesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error merging federated libraries");
            }
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            try
            {
                var embeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.html",
                    GetType().Namespace
                );

                _logger.LogInformation($"Embedded resource path: {embeddedResourcePath}");

                return new[]
                {
                    new PluginPageInfo
                    {
                        Name = this.Name,
                        EmbeddedResourcePath = embeddedResourcePath,
                    },
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting plugin pages");
                return Array.Empty<PluginPageInfo>(); // Return empty array instead of null
            }
        }

        public override void UpdateConfiguration(BasePluginConfiguration configuration)
        {
            try
            {
                base.UpdateConfiguration(configuration);
                _logger.LogInformation("Plugin configuration updated successfully");

                // Trigger re-initialization of federation services when config changes
                if (_federationLibraryService != null)
                {
                    _ = _federationLibraryService.MergeFederatedLibrariesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating plugin configuration");
            }
        }
    }
}
