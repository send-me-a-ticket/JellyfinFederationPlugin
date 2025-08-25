#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using JellyfinFederationPlugin.Configuration;
using JellyfinFederationPlugin.Internal;
using JellyfinFederationPlugin.Web;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Library;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace JellyfinFederationPlugin.Configuration
{
    // -------------------- Configuration --------------------
    public sealed class PluginConfiguration : BasePluginConfiguration
    {
        // Master switch
        public bool EnableFederation { get; set; } = true;
        public bool ClientMode { get; set; } = true;
        public bool ServerMode { get; set; } = true;
        public bool RequireHttps { get; set; } = true;
        public bool AdminOnlyChanges { get; set; } = true;
        public List<string> SharedLibraryIds { get; set; } = new();
        public List<FederatedServer> FederatedServers { get; set; } = new();

        public sealed class FederatedServer
        {
            public string ServerUrl { get; set; } = string.Empty; // e.g., https://remote:8096
            public string ApiKey { get; set; } = string.Empty; // never returned by APIs
            public int Port { get; set; } = 8096;
        }
    }
}

namespace JellyfinFederationPlugin
{
    using Configuration;
    using Internal;
    using Web;

    // -------------------- Plugin Root --------------------
    public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public override string Name => "Jellyfin Federation Plugin";
        public override string Description =>
            "Federates Jellyfin servers securely without file sync.";
        public override Guid Id => new("931820b5-4177-4f48-be30-f0a34db3693f");

        public static Plugin Instance { get; private set; } = null!;

        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<Plugin> _logger;
        private readonly IConfigurationManager _configManager;
        private readonly FederationRequestHandler _requestHandler;
        private readonly FederationLibraryService _libraryService;
        private readonly PlaybackProxyService _playbackProxy;

        internal static FederationRequestHandler RequestHandler { get; private set; } = null!;
        internal static FederationLibraryService LibraryService { get; private set; } = null!;
        internal static PlaybackProxyService PlaybackProxy { get; private set; } = null!;

        private readonly object _configLock = new();
        private readonly SemaphoreSlim _mergeGate = new(1, 1);

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

            _requestHandler = new FederationRequestHandler(_logger);
            _playbackProxy = new PlaybackProxyService(_requestHandler, _logger, _configManager);
            _libraryService = new FederationLibraryService(
                _libraryManager,
                _requestHandler,
                _logger
            );

            RequestHandler = _requestHandler;
            LibraryService = _libraryService;
            PlaybackProxy = _playbackProxy;

            _logger.LogInformation("Jellyfin Federation Plugin initialized");

            _ = SafeMergeAsync();
        }

        internal async Task SafeMergeAsync(CancellationToken ct = default)
        {
            if (!Configuration.EnableFederation || !Configuration.ClientMode)
            {
                _logger.LogInformation(
                    "Federation disabled (EnableFederation/ClientMode); skip merge"
                );
                return;
            }

            await _mergeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _libraryService
                    .MergeFederatedLibrariesAsync(Configuration.RequireHttps, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Federation merge failed");
            }
            finally
            {
                _mergeGate.Release();
            }
        }

        public override void UpdateConfiguration(BasePluginConfiguration configuration)
        {
            base.UpdateConfiguration(configuration);
            _logger.LogInformation("Configuration updated");
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = Name,
                    EmbeddedResourcePath = string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}.Configuration.configPage.html",
                        GetType().Namespace
                    ),
                },
            };
        }
    }
}

namespace JellyfinFederationPlugin.Internal
{
    using Configuration;

    internal static class HttpDefaults
    {
        public static HttpClient NewClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            // When you call peers with absolute URIs, the BaseAddress is not required.
            return c;
        }

        /// <summary>
        /// Recommended Authorization header format for Jellyfin:
        /// Authorization: MediaBrowser Token="...", Client="...", Version="..."
        /// We include Token, and add X-MediaBrowser-Token for compatibility.
        /// </summary>
        public static void AddJellyfinAuthHeaders(HttpRequestMessage req, string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                return;

            req.Headers.TryAddWithoutValidation(
                "Authorization",
                $"MediaBrowser Token=\"{apiKey}\""
            );
            req.Headers.TryAddWithoutValidation("X-MediaBrowser-Token", apiKey);
        }
    }

    // -------------------- Request Handler (peer HTTP) --------------------
    internal sealed class FederationRequestHandler
    {
        private readonly ILogger _logger;

        public FederationRequestHandler(ILogger logger) => _logger = logger;

        private static bool NormalizeServerUrl(
            string serverUrl,
            bool requireHttps,
            out Uri? uri,
            out string error
        )
        {
            error = string.Empty;
            uri = null;
            var raw = (serverUrl ?? string.Empty).Trim().TrimEnd('/');
            if (
                !raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            )
            {
                raw = "http://" + raw;
            }
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var parsed))
            {
                error = "Invalid ServerUrl";
                return false;
            }
            if (requireHttps && parsed.Scheme != Uri.UriSchemeHttps)
            {
                error = "HTTPS required by configuration";
                return false;
            }
            uri = parsed;
            return true;
        }

        public async Task<(bool ok, string message, string preview)> TestConnectionAsync(
            PluginConfiguration.FederatedServer server,
            bool requireHttps,
            CancellationToken ct = default
        )
        {
            if (!NormalizeServerUrl(server.ServerUrl, requireHttps, out var uri, out var err))
                return (false, err, string.Empty);

            using var http = HttpDefaults.NewClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(uri!, "/System/Info"));
            HttpDefaults.AddJellyfinAuthHeaders(req, server.ApiKey);

            try
            {
                var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    return (
                        true,
                        $"Connected ({(int)resp.StatusCode})",
                        body.Length > 200 ? body[..200] + "..." : body
                    );
                }
                return (false, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}", string.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TestConnection failed for {Url}", server.ServerUrl);
                return (false, ex.Message, string.Empty);
            }
        }

        private sealed class ItemsEnvelope
        {
            public List<BaseItemDto> Items { get; set; } = new();
            public int TotalRecordCount { get; set; }
        }

        public async Task<List<BaseItemDto>> GetItemsAsync(
            PluginConfiguration.FederatedServer server,
            bool requireHttps,
            CancellationToken ct = default
        )
        {
            var list = new List<BaseItemDto>();
            if (!NormalizeServerUrl(server.ServerUrl, requireHttps, out var uri, out _))
                return list;

            using var http = HttpDefaults.NewClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(uri!, "/Items"));
            HttpDefaults.AddJellyfinAuthHeaders(req, server.ApiKey);

            try
            {
                var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    return list;

                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

                // Jellyfin typically returns an envelope: { Items: [...], TotalRecordCount: n }
                var env = JsonSerializer.Deserialize<ItemsEnvelope>(json, opts);
                if (env?.Items is { Count: > 0 })
                    list.AddRange(env.Items);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GetItemsAsync failed for {Url}", server.ServerUrl);
            }
            return list;
        }
    }

    internal sealed class FederationLibraryService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly FederationRequestHandler _handler;
        private readonly ILogger _logger;

        // simple cache of latest aggregate
        private readonly ConcurrentDictionary<string, AggregatedItem> _aggregate = new();

        public FederationLibraryService(
            ILibraryManager libraryManager,
            FederationRequestHandler handler,
            ILogger logger
        )
        {
            _libraryManager = libraryManager;
            _handler = handler;
            _logger = logger;
        }

        public sealed class AggregatedItem
        {
            public required string ServerUrl { get; init; }
            public required string RemoteItemId { get; init; } // Guid string (N or D)
            public required string Name { get; init; }
            public string? MediaType { get; init; }
        }

        public async Task MergeFederatedLibrariesAsync(
            bool requireHttps,
            CancellationToken ct = default
        )
        {
            _aggregate.Clear();
            var cfg = Plugin.Instance.Configuration;
            if (!cfg.EnableFederation || !cfg.ClientMode || cfg.FederatedServers.Count == 0)
            {
                _logger.LogInformation("Aggregation skipped: disabled or no peers");
                return;
            }

            foreach (var server in cfg.FederatedServers)
            {
                ct.ThrowIfCancellationRequested();
                var items = await _handler
                    .GetItemsAsync(server, requireHttps, ct)
                    .ConfigureAwait(false);

                if (items.Count == 0)
                    continue;

                var baseUrl = server.ServerUrl.TrimEnd('/');
                foreach (var item in items)
                {
                    if (item.Id == Guid.Empty)
                        continue;

                    var key = $"{baseUrl}\n{item.Id:N}";
                    _aggregate[key] = new AggregatedItem
                    {
                        ServerUrl = baseUrl,
                        RemoteItemId = item.Id.ToString("N"),
                        Name = item.Name ?? "(unnamed)",
                        MediaType = item.Type.ToString(),
                    };
                }

                _logger.LogInformation(
                    "Aggregated {Count} items from {Server}",
                    items.Count,
                    baseUrl
                );
            }
        }

        public IEnumerable<AggregatedItem> ListAggregate() => _aggregate.Values.ToArray();
    }

    // -------------------- Playback Proxy (redirect) --------------------
    internal sealed class PlaybackProxyService
    {
        private readonly FederationRequestHandler _requestHandler;
        private readonly ILogger _logger;
        private readonly IConfigurationManager _configManager;

        public PlaybackProxyService(
            FederationRequestHandler requestHandler,
            ILogger logger,
            IConfigurationManager configManager
        )
        {
            _requestHandler = requestHandler;
            _logger = logger;
            _configManager = configManager;
        }

        public string BuildRemoteStreamUrl(string baseUrl, string remoteItemId)
        {
            return $"{baseUrl.TrimEnd('/')}/Items/{remoteItemId}/Playback";
        }
    }
}

namespace JellyfinFederationPlugin.Web
{
    using Configuration;
    using Internal;

    // -------------------- Controller & HTML --------------------
    [ApiController]
    [Route("Federation")]
    [Authorize]
    public sealed class FederationController : ControllerBase
    {
        private readonly ILogger<FederationController> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserViewManager _userViewManager;
        private readonly IUserManager _userManager;

        public FederationController(
            ILogger<FederationController> logger,
            ILibraryManager libraryManager,
            IUserViewManager userViewManager,
            IUserManager userManager
        )
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _userViewManager = userViewManager;
            _userManager = userManager;
        }

        private bool IsAdmin()
        {
            return User?.Claims?.Any(c =>
                    (
                        c.Type.Equals("IsAdministrator", StringComparison.OrdinalIgnoreCase)
                        && c.Value.Equals("true", StringComparison.OrdinalIgnoreCase)
                    )
                    || (
                        c.Type.EndsWith("/role", StringComparison.OrdinalIgnoreCase)
                        && c.Value.Equals("Administrator", StringComparison.OrdinalIgnoreCase)
                    )
                ) == true;
        }

        private IActionResult RequireAdminForChanges()
        {
            var cfg = Plugin.Instance.Configuration;
            if (!cfg.AdminOnlyChanges)
                return null!;
            if (IsAdmin())
                return null!;
            return Forbid();
        }

        [HttpGet("ConfigForm")]
        public IActionResult ConfigForm(
            [FromQuery] string? status = null,
            [FromQuery] string? error = null
        )
        {
            var cfg = Plugin.Instance.Configuration;

            var rowsServers = string.Join(
                "",
                cfg.FederatedServers.Select(s =>
                    $"<tr><td>{WebUtility.HtmlEncode(s.ServerUrl)}</td><td>{s.Port}</td><td>{(string.IsNullOrEmpty(s.ApiKey) ? "No" : "Yes")}</td></tr>"
                )
            );

            var root = _libraryManager.RootFolder;
            var libraries = root
                .Children.OfType<CollectionFolder>()
                .OrderBy(f => f.Name ?? string.Empty)
                .Select(f => new
                {
                    Id = f.Id.ToString("N"),
                    Name = f.Name ?? "(unnamed)",
                    Type = f.CollectionType?.ToString() ?? "Mixed",
                })
                .ToList();

            var shared = new HashSet<string>(
                cfg.SharedLibraryIds ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase
            );

            var rowsLibs = string.Join(
                "",
                libraries.Select(lib =>
                {
                    var checkedAttr = shared.Contains(lib.Id) ? "checked" : string.Empty;
                    return $@"<tr>
<td><label><input type=""checkbox"" name=""SharedLibraryIds"" value=""{lib.Id}"" {checkedAttr}/> {WebUtility.HtmlEncode(lib.Name)}</label></td>
<td>{WebUtility.HtmlEncode(lib.Type)}</td>
<td><code>{lib.Id}</code></td>
</tr>";
                })
            );

            var html =
                $@"<!doctype html>
<html>
<head>
<meta charset=""utf-8"">
<title>Jellyfin Federation</title>
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<style>
body{{font-family:system-ui,'Segoe UI',Roboto,sans-serif;margin:24px;background:#f7f7f7}}
.card{{border:1px solid #ddd;border-radius:8px;padding:16px;margin-bottom:16px;background:#fff}}
label{{display:block;margin-top:8px}}
input[type=text],input[type=password],input[type=number]{{width:360px;max-width:100%;padding:8px}}
table{{border-collapse:collapse;width:100%;margin-top:8px}}
th,td{{border:1px solid #ddd;padding:8px;text-align:left}}
.ok{{color:#067d14}} .err{{color:#b00020}} .muted{{color:#666}}
code{{background:#f5f5f5;padding:2px 4px;border-radius:4px}}
</style>
</head>
<body>
<h1>Jellyfin Federation</h1>
{(string.IsNullOrWhiteSpace(status) ? "" : $"<p class='ok'>{WebUtility.HtmlEncode(status)}</p>")}
{(string.IsNullOrWhiteSpace(error) ? "" : $"<p class='err'>{WebUtility.HtmlEncode(error)}</p>")}

<div class=""card"">
  <h2>Modes</h2>
  <form method=""post"" action=""/Federation/UpdateModes"">
    <label><input type=""checkbox"" name=""EnableFederation"" {(cfg.EnableFederation ? "checked" : "")}/> Enable Federation (master switch)</label>
    <label><input type=""checkbox"" name=""ClientMode"" {(cfg.ClientMode ? "checked" : "")}/> Client Mode (consume from peers)</label>
    <label><input type=""checkbox"" name=""ServerMode"" {(cfg.ServerMode ? "checked" : "")}/> Server Mode (share to peers)</label>
    <label><input type=""checkbox"" name=""RequireHttps"" {(cfg.RequireHttps ? "checked" : "")}/> Require HTTPS for peers</label>
    <label><input type=""checkbox"" name=""AdminOnlyChanges"" {(cfg.AdminOnlyChanges ? "checked" : "")}/> Admin-only config changes</label>
    <p><button type=""submit"">Save Modes</button></p>
    {(cfg.EnableFederation ? "" : "<p class='muted'>Federation is disabled. Other settings won’t take effect until enabled.</p>")}
  </form>
</div>

<div class=""card"">
  <h2>Shareable Libraries (Server Mode)</h2>
  <form method=""post"" action=""/Federation/UpdateSharedLibraries"">
    <table>
      <thead><tr><th>Share?</th><th>Type</th><th>Id</th></tr></thead>
      <tbody>{rowsLibs}</tbody>
    </table>
    <p><button type=""submit"">Save Shared Libraries</button></p>
    <p class=""muted"">Only applies when <em>Enable Federation</em> and <em>Server Mode</em> are on.</p>
  </form>
</div>

<div class=""card"">
  <h2>Federated Servers</h2>
  <table>
    <thead><tr><th>Server URL</th><th>Port</th><th>Has API Key</th></tr></thead>
    <tbody>{rowsServers}</tbody>
  </table>

  <h3>Add Server</h3>
  <form method=""post"" action=""/Federation/AddServerForm"">
    <label>Server URL <input name=""ServerUrl"" required placeholder=""https://peer:8096""/></label>
    <label>Port <input type=""number"" name=""Port"" value=""8096"" min=""1"" max=""65535""/></label>
    <label>API Key <input type=""password"" name=""ApiKey""/></label>
    <p><button type=""submit"">Add</button></p>
  </form>

  <h3>Remove Server</h3>
  <form method=""post"" action=""/Federation/RemoveServerForm"">
    <label>Server URL <input name=""ServerUrl"" required placeholder=""https://peer:8096""/></label>
    <p><button type=""submit"">Remove</button></p>
  </form>

  <h3>Test Connection</h3>
  <form method=""post"" action=""/Federation/TestConnectionForm"">
    <label>Server URL <input name=""ServerUrl"" required placeholder=""https://peer:8096""/></label>
    <label>API Key <input type=""password"" name=""ApiKey""/></label>
    <p><button type=""submit"">Test</button></p>
  </form>

  <h3>How to stream</h3>
  <p>Call <code>GET /Federation/Stream?serverUrl=&lt;base&gt;&amp;id=&lt;remoteGuid&gt;</code> to be redirected to the remote stream URL.</p>
</div>

</body></html>";

            return Content(html, "text/html; charset=utf-8");
        }

        // -------------------- Writes --------------------
        [HttpPost("UpdateModes")]
        public IActionResult UpdateModes(
            [FromForm] bool? EnableFederation,
            [FromForm] bool? ClientMode,
            [FromForm] bool? ServerMode,
            [FromForm] bool? RequireHttps,
            [FromForm] bool? AdminOnlyChanges
        )
        {
            var forbid = RequireAdminForChanges();
            if (forbid != null)
                return forbid;

            var cfg = Plugin.Instance.Configuration;
            cfg.EnableFederation = EnableFederation == true;
            cfg.ClientMode = ClientMode == true;
            cfg.ServerMode = ServerMode == true;
            cfg.RequireHttps = RequireHttps == true;
            cfg.AdminOnlyChanges = AdminOnlyChanges == true;

            Plugin.Instance.UpdateConfiguration(cfg);
            _ = Plugin.Instance.SafeMergeAsync();
            return Redirect("/Federation/ConfigForm?status=Modes+updated");
        }

        [HttpPost("UpdateSharedLibraries")]
        public IActionResult UpdateSharedLibraries([FromForm] string[]? SharedLibraryIds)
        {
            var forbid = RequireAdminForChanges();
            if (forbid != null)
                return forbid;

            var cfg = Plugin.Instance.Configuration;
            cfg.SharedLibraryIds = (SharedLibraryIds ?? Array.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            Plugin.Instance.UpdateConfiguration(cfg);
            _logger.LogInformation("Updated shared libraries: {Count}", cfg.SharedLibraryIds.Count);
            return Redirect("/Federation/ConfigForm?status=Shared+libraries+updated");
        }

        // -------------------- DTOs --------------------
        public sealed class AddServerRequest
        {
            [Required, StringLength(200)]
            public string ServerUrl { get; set; } = string.Empty;

            [StringLength(200)]
            public string ApiKey { get; set; } = string.Empty;

            [Range(1, 65535)]
            public int Port { get; set; } = 8096;
        }

        public sealed class RemoveServerRequest
        {
            [Required, StringLength(200)]
            public string ServerUrl { get; set; } = string.Empty;
        }

        public sealed class TestConnectionRequest
        {
            [Required, StringLength(200)]
            public string ServerUrl { get; set; } = string.Empty;

            [StringLength(200)]
            public string? ApiKey { get; set; }
        }

        // -------------------- JSON APIs (secured; admin for writes) --------------------
        [HttpGet("Status")]
        public IActionResult GetStatus()
        {
            var cfg = Plugin.Instance.Configuration;
            var status = new
            {
                pluginName = Plugin.Instance.Name,
                pluginVersion = Plugin.Instance.Version?.ToString() ?? "Unknown",
                enableFederation = cfg.EnableFederation,
                clientMode = cfg.ClientMode,
                serverMode = cfg.ServerMode,
                requireHttps = cfg.RequireHttps,
                adminOnlyChanges = cfg.AdminOnlyChanges,
                serverCount = cfg.FederatedServers.Count,
                sharedLibraries = cfg.SharedLibraryIds?.ToArray() ?? Array.Empty<string>(),
                servers = cfg
                    .FederatedServers.Select(s => new
                    {
                        serverUrl = s.ServerUrl,
                        port = s.Port,
                        hasApiKey = !string.IsNullOrEmpty(s.ApiKey),
                    })
                    .ToArray(),
                endpoints = new
                {
                    listServers = "GET /Federation/Servers",
                    addServer = "POST /Federation/AddServer",
                    removeServer = "POST /Federation/RemoveServer",
                    testConnection = "POST /Federation/TestConnection",
                    aggregate = "GET /Federation/Aggregate",
                    stream = "GET /Federation/Stream?serverUrl=<>&id=<>",
                    configForm = "GET /Federation/ConfigForm",
                },
            };
            return Ok(status);
        }

        [HttpGet("Servers")]
        public IActionResult GetServers()
        {
            var cfg = Plugin.Instance.Configuration;
            var servers = cfg
                .FederatedServers.Select(s => new
                {
                    serverUrl = s.ServerUrl,
                    port = s.Port,
                    hasApiKey = !string.IsNullOrEmpty(s.ApiKey),
                    apiKeyPreview = string.IsNullOrEmpty(s.ApiKey)
                        ? null
                        : (s.ApiKey.Length > 8 ? s.ApiKey[..8] + "..." : "***"),
                })
                .ToArray();
            return Ok(new { count = servers.Length, servers });
        }

        [HttpGet("Libraries")]
        public IActionResult GetLibraries()
        {
            // Get current authenticated user id from claims
            var userIdStr = User
                ?.Claims?.FirstOrDefault(c =>
                    string.Equals(c.Type, "nameidentifier", StringComparison.OrdinalIgnoreCase)
                    || c.Type.EndsWith("/nameidentifier", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(c.Type, "sub", StringComparison.OrdinalIgnoreCase)
                )
                ?.Value;

            if (string.IsNullOrWhiteSpace(userIdStr) || !Guid.TryParse(userIdStr, out var userId))
                return Forbid();

            var views = _userViewManager.GetUserViews(
                new UserViewQuery
                {
                    UserId = userId,
                    IncludeExternalContent = true,
                    IncludeHidden = false,
                    // PresetViews = Array.Empty<CollectionType>(),
                }
            );

            var libs = views
                .OfType<Folder>()
                .Select(f => new
                {
                    id = f.Id.ToString("N"),
                    name = f.Name ?? "(unnamed)",
                    type = (f as ICollectionFolder)?.CollectionType?.ToString() ?? "Mixed",
                })
                .ToArray();

            return Ok(new { count = libs.Length, libraries = libs });
        }

        [HttpPost("Refresh")]
        public async Task<IActionResult> Refresh()
        {
            await Plugin.Instance.SafeMergeAsync();
            return Ok(new { started = true });
        }

        [HttpPost("AddServerForm")]
        public IActionResult AddServerForm([FromForm] AddServerRequest request)
        {
            var result = AddServer(request);
            if (result is OkObjectResult)
                return Redirect("/Federation/ConfigForm?status=Server+added");
            if (result is ObjectResult orr && orr.StatusCode is >= 400)
                return Redirect("/Federation/ConfigForm?error=Add+failed");
            return Redirect("/Federation/ConfigForm");
        }

        [HttpPost("RemoveServerForm")]
        public IActionResult RemoveServerForm([FromForm] RemoveServerRequest request)
        {
            var result = RemoveServer(request);
            if (result is OkObjectResult)
                return Redirect("/Federation/ConfigForm?status=Server+removed");
            if (result is ObjectResult orr && orr.StatusCode is >= 400)
                return Redirect("/Federation/ConfigForm?error=Remove+failed");
            return Redirect("/Federation/ConfigForm");
        }

        [HttpPost("TestConnectionForm")]
        public async Task<IActionResult> TestConnectionForm(
            [FromForm] TestConnectionRequest request
        )
        {
            var result = await TestConnection(request);
            if (result is OkObjectResult)
                return Redirect("/Federation/ConfigForm?status=Connection+tested");
            return Redirect("/Federation/ConfigForm?error=Test+failed");
        }

        [HttpPost("AddServer")]
        public IActionResult AddServer([FromBody] AddServerRequest request)
        {
            var forbid = RequireAdminForChanges();
            if (forbid != null)
                return forbid;
            if (!ModelState.IsValid)
                return ValidationProblem(ModelState);

            var cfg = Plugin.Instance.Configuration;
            var serverUrl = (request.ServerUrl ?? "").Trim().TrimEnd('/');
            if (
                !serverUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !serverUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            )
            {
                serverUrl = "http://" + serverUrl;
            }
            if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri))
                return BadRequest(new { error = "Invalid ServerUrl" });

            if (cfg.RequireHttps && uri.Scheme != Uri.UriSchemeHttps)
                return BadRequest(new { error = "HTTPS required by configuration" });

            if (
                cfg.FederatedServers.Any(s =>
                    s.ServerUrl.Equals(serverUrl, StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                return Conflict(new { error = "Server already exists", serverUrl });
            }

            var newServer = new PluginConfiguration.FederatedServer
            {
                ServerUrl = serverUrl,
                ApiKey = (request.ApiKey ?? string.Empty).Trim(),
                Port = request.Port,
            };

            cfg.FederatedServers.Add(newServer);
            Plugin.Instance.UpdateConfiguration(cfg);

            _logger.LogInformation("Added federated server: {Server}", serverUrl);
            _ = Plugin.Instance.SafeMergeAsync();

            return Ok(
                new
                {
                    success = true,
                    server = new
                    {
                        newServer.ServerUrl,
                        newServer.Port,
                        hasApiKey = !string.IsNullOrEmpty(newServer.ApiKey),
                    },
                    totalServers = cfg.FederatedServers.Count,
                }
            );
        }

        [HttpPost("RemoveServer")]
        public IActionResult RemoveServer([FromBody] RemoveServerRequest request)
        {
            var forbid = RequireAdminForChanges();
            if (forbid != null)
                return forbid;
            if (!ModelState.IsValid)
                return ValidationProblem(ModelState);

            var cfg = Plugin.Instance.Configuration;
            var server = cfg.FederatedServers.FirstOrDefault(s =>
                s.ServerUrl.Equals(request.ServerUrl, StringComparison.OrdinalIgnoreCase)
            );

            if (server is null)
                return NotFound(new { error = "Server not found", request.ServerUrl });

            cfg.FederatedServers.Remove(server);
            Plugin.Instance.UpdateConfiguration(cfg);

            _logger.LogInformation("Removed federated server: {Server}", request.ServerUrl);
            _ = Plugin.Instance.SafeMergeAsync();

            return Ok(
                new
                {
                    success = true,
                    removedServer = request.ServerUrl,
                    remainingServers = cfg.FederatedServers.Count,
                }
            );
        }

        [HttpPost("TestConnection")]
        public async Task<IActionResult> TestConnection([FromBody] TestConnectionRequest request)
        {
            if (!ModelState.IsValid)
                return ValidationProblem(ModelState);

            var cfg = Plugin.Instance.Configuration;
            var server = new PluginConfiguration.FederatedServer
            {
                ServerUrl = request.ServerUrl,
                ApiKey = request.ApiKey ?? string.Empty,
            };

            var result = await Plugin
                .RequestHandler.TestConnectionAsync(server, cfg.RequireHttps)
                .ConfigureAwait(false);

            return Ok(
                new
                {
                    success = result.ok,
                    message = result.message,
                    responsePreview = result.preview,
                }
            );
        }

        // -------------------- Aggregation & Playback --------------------
        [HttpGet("Aggregate")]
        public IActionResult Aggregate()
        {
            var items = Plugin
                .LibraryService.ListAggregate()
                .Select(x => new
                {
                    x.ServerUrl,
                    id = x.RemoteItemId,
                    x.Name,
                    x.MediaType,
                })
                .ToArray();

            return Ok(new { count = items.Length, items });
        }

        // Example: GET /Federation/Stream?serverUrl=https://peer:8096&id=<GUID-N>
        [HttpGet("Stream")]
        [AllowAnonymous]
        public IActionResult Stream(
            [FromQuery, Required] string serverUrl,
            [FromQuery, Required] string id
        )
        {
            if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(id))
                return BadRequest(new { error = "serverUrl and id are required" });

            var cfg = Plugin.Instance.Configuration;
            if (
                cfg.RequireHttps
                && serverUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            )
                return BadRequest(new { error = "HTTPS required by configuration" });

            var streamUrl = Plugin.PlaybackProxy.BuildRemoteStreamUrl(serverUrl, id);
            return Redirect(streamUrl);
        }
    }
}

