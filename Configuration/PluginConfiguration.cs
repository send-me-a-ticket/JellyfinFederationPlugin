using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace JellyfinFederationPlugin.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public List<FederatedServer> FederatedServers { get; set; }

        public PluginConfiguration()
        {
            FederatedServers = new List<FederatedServer>();
        }

        public class FederatedServer
        {
            public string ServerUrl { get; set; }
            public string ApiKey { get; set; }
            public int Port { get; set; }
        }
    }
}
