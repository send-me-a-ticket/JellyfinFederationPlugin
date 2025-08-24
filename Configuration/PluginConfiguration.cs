using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace JellyfinFederationPlugin.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public List<FederatedServer> FederatedServers { get; set; } = new List<FederatedServer>();

        public void AddFederatedServer(FederatedServer server)
        {
            FederatedServers.Add(server);
        }

        public void RemoveFederatedServer(FederatedServer server)
        {
            FederatedServers.Remove(server);
        }

        public class FederatedServer
        {
            public string ServerUrl { get; set; }
            public string ApiKey { get; set; }
            public int Port { get; set; }
        }
    }
}
