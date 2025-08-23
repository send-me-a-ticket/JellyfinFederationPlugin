using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using JellyfinFederationPlugin.Configuration;
using Microsoft.Extensions.Logging;
using BaseItemDto = MediaBrowser.Model.Dto.BaseItemDto;

namespace JellyfinFederationPlugin.Library
{
    public class FederationRequestHandler
    {
        private readonly ILogger _logger;

        public FederationRequestHandler(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<List<BaseItemDto>> GetFederatedLibrary(
            PluginConfiguration.FederatedServer server
        )
        {
            using (var httpClient = new HttpClient())
            {
                var requestUrl = $"{server.ServerUrl}/Items";
                _logger.LogInformation($"Requesting library from federated server {requestUrl}");

                httpClient.DefaultRequestHeaders.Add(
                    "X-Emby-Authorization",
                    $"MediaBrowser Client=\"JellyfinFederation\", Token=\"{server.ApiKey}\""
                );

                try
                {
                    var response = await httpClient.GetAsync(requestUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        var jsonString = await response.Content.ReadAsStringAsync();
                        _logger.LogInformation($"Response from {server.ServerUrl}: {jsonString}");
                        return JsonSerializer.Deserialize<List<BaseItemDto>>(
                            jsonString,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                        );
                    }
                    else
                    {
                        _logger.LogError(
                            $"Failed to fetch library from server {server.ServerUrl}. Status code: {response.StatusCode}"
                        );
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        $"Error requesting library from server {server.ServerUrl}: {ex.Message}"
                    );
                }
            }

            return new List<BaseItemDto>();
        }
    }
}
