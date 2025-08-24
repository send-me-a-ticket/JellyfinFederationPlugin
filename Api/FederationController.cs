using JellyfinFederationPlugin.Configuration;
using Microsoft.AspNetCore.Mvc;
using System;

namespace JellyfinFederationPlugin.Api
{
    [ApiController]
    [Route("Federation")]
    public class FederationController : ControllerBase
    {

        [HttpPost("AddServer")]
        public IActionResult AddServer([FromBody] AddServerRequest request)
        {
             var config = Plugin.Instance?.Configuration;
            if (config == null)
                return StatusCode(500, "Plugin configuration not loaded.");

            var newServer = new PluginConfiguration.FederatedServer
            {
                ServerUrl = request.ServerUrl,
                ApiKey = request.ApiKey,
                Port = request.Port
            };

            config.FederatedServers.Add(newServer);
            Plugin.Instance.UpdateConfiguration(config);

            // Return updated list for frontend
            return Ok(config.FederatedServers);
        }

        [HttpGet("Servers")]
        public IActionResult GetServers()
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
                return StatusCode(500, "Plugin configuration not loaded.");

            return Ok(config.FederatedServers);
        }

        public class AddServerRequest
        {
            public string ServerUrl { get; set; }
            public string ApiKey { get; set; }
            public int Port { get; set; }
        }
    }


    public class AddServerRequest
    {
        public string ServerUrl { get; set; }
        public string ApiKey { get; set; }
        public int Port { get; set; }
    }
}
