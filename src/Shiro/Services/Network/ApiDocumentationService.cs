#nullable enable
using System;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shiro.Services
{
    /// <summary>
    /// Generates OpenAPI 3.0 specification and interactive documentation for Shiro Account Manager's REST and WebSocket API
    /// from one static typed route descriptor array matching <see cref="AccountApiDispatcher"/> and <see cref="KestrelWebServer"/>.
    /// </summary>
    public static class ApiDocumentationService
    {
        private const int DefaultGatewayPort = 7963;
        private static string? _cachedDefaultOpenApiJson;

        internal sealed record ApiParamSpec(string Name, string Type, bool Required = false, string? Description = null);

        internal sealed record ApiRouteSpec(
            string Path,
            string HttpMethod,
            string Summary,
            bool RequiresAuth,
            bool IncludeV2Alias = true,
            string? RequestBodyDescription = null,
            params ApiParamSpec[] QueryParams);

        private static readonly ApiParamSpec AccountParam = new("Account", "string", true, "Target account username or numeric UserID.");
        private static readonly ApiParamSpec GroupParam = new("Group", "string", false, "Optional case-insensitive account group filter.");
        private static readonly ApiParamSpec PlaceIdParam = new("PlaceId", "integer", true, "Roblox place ID.");
        private static readonly ApiParamSpec RequiredPlaceIdOptParam = new("PlaceId", "integer", false, "Optional place ID (defaults to active ServerBrowserState PlaceId).");
        private static readonly ApiParamSpec JobIdParam = new("JobId", "string", false, "Optional server instance job ID.");
        private static readonly ApiParamSpec RequiredJobIdParam = new("JobId", "string", true, "Target server instance job ID.");
        private static readonly ApiParamSpec UserIdParam = new("UserId", "integer", true, "Positive Roblox user ID.");
        private static readonly ApiParamSpec FieldParam = new("Field", "string", true, "Custom account field name.");

        internal static readonly ApiRouteSpec[] RouteDescriptors =
        {
            new("/Running", "get", "Health check endpoint", false),
            new("/GetWebSocketStats", "get", "Get active WebSocket client statistics", false),
            new("/WebSocketStats", "get", "Alias for /GetWebSocketStats", false),
            new("/swagger.json", "get", "OpenAPI 3.0 specification JSON", false),
            new("/openapi.json", "get", "Alias for /swagger.json", false),
            new("/docs", "get", "Interactive HTML API documentation", false),
            new("/api/docs", "get", "Alias for /docs", false),
            new("/ws", "get", "WebSocket real-time event stream (query ?token= or auth header)", true, IncludeV2Alias: false, QueryParams: new ApiParamSpec("token", "string", false, "Gateway password token for WebSocket handshake.")),
            new("/events", "get", "WebSocket real-time event stream alias", true, QueryParams: new ApiParamSpec("token", "string", false, "Gateway password token for WebSocket handshake.")),

            new("/GetAccounts", "get", "List account usernames", true, QueryParams: GroupParam),
            new("/GetAccountsJson", "get", "List account records as JSON", true, QueryParams: new[] { GroupParam, new ApiParamSpec("IncludeCookies", "boolean", false, "Include cookies when AllowGetCookie and valid password are provided.") }),
            new("/ImportCookie", "post", "Import or update an account from a .ROBLOSECURITY cookie", true, RequestBodyDescription: "Raw .ROBLOSECURITY cookie string (required for remote requests).", QueryParams: new ApiParamSpec("Cookie", "string", false, "Loopback-only fallback query parameter for cookie.")),
            new("/Accounts/Export", "get", "Export accounts in requested format", true, QueryParams: new[] { new ApiParamSpec("Format", "string", false, "UserPassCookie | CookieOnly | UserPass | Csv | Json | SimpleText"), GroupParam }),
            new("/ExportAccounts", "get", "Alias for /Accounts/Export", true, QueryParams: new[] { new ApiParamSpec("Format", "string", false, "UserPassCookie | CookieOnly | UserPass | Csv | Json | SimpleText"), GroupParam }),

            new("/AntiAfk/Status", "get", "Get Anti-AFK engine status", true),
            new("/AntiAfkStatus", "get", "Alias for /AntiAfk/Status", true),
            new("/AntiAfk/Toggle", "get", "Toggle Anti-AFK engine state", true),
            new("/ToggleAntiAfk", "get", "Alias for /AntiAfk/Toggle", true),
            new("/AntiAfk/Enable", "get", "Enable Anti-AFK engine", true),
            new("/AntiAfk/Disable", "get", "Disable Anti-AFK engine", true),
            new("/Memory/Trim", "get", "Trim working set RAM across running Roblox clients", true),
            new("/TrimMemory", "get", "Alias for /Memory/Trim", true),
            new("/CleanRAM", "get", "Alias for /Memory/Trim", true),
            new("/MultiRoblox/Status", "get", "Get Multi-Roblox singleton handle status", true),
            new("/MultiRoblox/Toggle", "get", "Toggle Multi-Roblox setting and handle state", true),
            new("/Roblox/HideWindows", "get", "Hide all running Roblox client windows", true),
            new("/HideWindows", "get", "Alias for /Roblox/HideWindows", true),
            new("/Roblox/ShowWindows", "get", "Show all hidden Roblox client windows", true),
            new("/ShowWindows", "get", "Alias for /Roblox/ShowWindows", true),
            new("/Roblox/ToggleWindows", "get", "Toggle hide/restore across Roblox client windows", true),
            new("/ToggleWindows", "get", "Alias for /Roblox/ToggleWindows", true),
            new("/PotatoMode/Toggle", "get", "Toggle Potato Mode graphics setting", true),
            new("/KillRoblox", "get", "Close all running Roblox processes", true),
            new("/KillAllRoblox", "get", "Alias for /KillRoblox", true),
            new("/AutoReconnect/Status", "get", "Get Auto-Reconnect daemon status", true),
            new("/AutoReconnect/Toggle", "get", "Toggle Auto-Reconnect daemon state", true),

            new("/SetProxy", "post", "Assign or clear an account proxy", true, RequestBodyDescription: "Proxy URI or host:port (required for remote requests).", QueryParams: new[] { AccountParam, new ApiParamSpec("Proxy", "string", false, "Loopback-only query parameter for proxy string.") }),
            new("/GetProxy", "get", "Get assigned proxy for an account", true, QueryParams: AccountParam),
            new("/GetCookie", "get", "Get .ROBLOSECURITY cookie when AllowGetCookie is enabled", true, QueryParams: AccountParam),
            new("/GetAlias", "get", "Get account alias", true, QueryParams: AccountParam),
            new("/GetDescription", "get", "Get account description", true, QueryParams: AccountParam),
            new("/GetField", "get", "Get custom field value", true, QueryParams: new[] { AccountParam, FieldParam }),
            new("/SetField", "get", "Set custom field value", true, QueryParams: new[] { AccountParam, FieldParam, new ApiParamSpec("Value", "string", true, "Field value.") }),
            new("/RemoveField", "get", "Remove custom field", true, QueryParams: new[] { AccountParam, FieldParam }),
            new("/SetAlias", "post", "Set account alias", true, RequestBodyDescription: "New alias text.", QueryParams: AccountParam),
            new("/SetDescription", "post", "Set account description", true, RequestBodyDescription: "New description text.", QueryParams: AccountParam),
            new("/AppendDescription", "post", "Append text to account description", true, RequestBodyDescription: "Text to append.", QueryParams: AccountParam),
            new("/LaunchAccount", "get", "Launch Roblox for an account via LaunchCoordinator and CookieGuard", true, QueryParams: new[] { AccountParam, PlaceIdParam, JobIdParam, new ApiParamSpec("FollowUser", "boolean"), new ApiParamSpec("JoinVIP", "boolean") }),
            new("/FollowUser", "get", "Follow a Roblox user into their active server via LaunchCoordinator", true, QueryParams: new[] { AccountParam, new ApiParamSpec("Username", "string", true, "Target Roblox username to follow.") }),
            new("/GetCSRFToken", "get", "Acquire or refresh an account CSRF token", true, QueryParams: AccountParam),
            new("/BlockUser", "get", "Block a Roblox user ID", true, QueryParams: new[] { AccountParam, UserIdParam }),
            new("/UnblockUser", "get", "Unblock a Roblox user ID", true, QueryParams: new[] { AccountParam, UserIdParam }),
            new("/GetBlockedList", "get", "Get blocked users list for an account", true, QueryParams: AccountParam),
            new("/UnblockEveryone", "get", "Unblock all blocked users for an account", true, QueryParams: AccountParam),
            new("/SetServer", "get", "Join/set a specific Roblox server job for an account", true, QueryParams: new[] { AccountParam, PlaceIdParam, RequiredJobIdParam }),
            new("/SetRecommendedServer", "get", "Select a cached server matching CachedServersPlaceId", true, QueryParams: new[] { AccountParam, RequiredPlaceIdOptParam }),
            new("/SetAvatar", "post", "Apply Roblox Avatar v4 configuration and report actual server result", true, RequestBodyDescription: "Avatar configuration JSON payload.", QueryParams: AccountParam)
        };

        public static string GetOpenApiJson() => GetOpenApiJson(DefaultGatewayPort);

        public static string GetOpenApiJson(int port)
        {
            int effectivePort = port is > 0 and <= 65535 ? port : DefaultGatewayPort;
            if (effectivePort == DefaultGatewayPort && _cachedDefaultOpenApiJson != null)
                return _cachedDefaultOpenApiJson;

            var paths = new JsonObject();
            foreach (ApiRouteSpec spec in RouteDescriptors)
            {
                paths[spec.Path] = new JsonObject { [spec.HttpMethod] = BuildOperationNode(spec, isV2: false) };
                if (spec.IncludeV2Alias)
                {
                    paths[$"/v2{spec.Path}"] = new JsonObject { [spec.HttpMethod] = BuildOperationNode(spec, isV2: true) };
                }
            }

            var doc = new JsonObject
            {
                ["openapi"] = "3.0.1",
                ["info"] = new JsonObject
                {
                    ["title"] = "Shiro Account Manager API",
                    ["description"] = "Local & LAN REST/WebSocket Gateway for Roblox Account Automation, Multi-Instance Management, and Performance Controls.",
                    ["version"] = "5.0.0"
                },
                ["servers"] = new JsonArray
                {
                    new JsonObject { ["url"] = $"http://localhost:{effectivePort}" },
                    new JsonObject { ["url"] = "/" }
                },
                ["components"] = BuildComponentsNode(),
                ["paths"] = paths
            };

            string json = doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            if (effectivePort == DefaultGatewayPort)
                _cachedDefaultOpenApiJson = json;
            return json;
        }

        private static JsonObject BuildComponentsNode() => new()
        {
            ["securitySchemes"] = new JsonObject
            {
                ["ApiKeyAuth"] = new JsonObject { ["type"] = "apiKey", ["in"] = "header", ["name"] = "X-Shiro-Auth" },
                ["LegacyApiKeyAuth"] = new JsonObject { ["type"] = "apiKey", ["in"] = "header", ["name"] = "X-RAM-Auth" },
                ["BearerAuth"] = new JsonObject { ["type"] = "http", ["scheme"] = "bearer" },
                ["QueryPasswordAuth"] = new JsonObject { ["type"] = "apiKey", ["in"] = "query", ["name"] = "Password", ["description"] = "Loopback only (remote requests must use headers; WebSocket supports ?token=)." }
            }
        };

        private static JsonObject BuildOperationNode(ApiRouteSpec spec, bool isV2)
        {
            var op = new JsonObject
            {
                ["summary"] = isV2 ? $"{spec.Summary} (v2 JSON envelope)" : spec.Summary
            };

            if (spec.RequiresAuth)
            {
                op["security"] = new JsonArray
                {
                    new JsonObject { ["ApiKeyAuth"] = new JsonArray() },
                    new JsonObject { ["LegacyApiKeyAuth"] = new JsonArray() },
                    new JsonObject { ["BearerAuth"] = new JsonArray() },
                    new JsonObject { ["QueryPasswordAuth"] = new JsonArray() }
                };
            }
            else
            {
                op["security"] = new JsonArray();
            }

            if (spec.QueryParams.Length > 0)
            {
                var paramArr = new JsonArray();
                foreach (ApiParamSpec p in spec.QueryParams)
                {
                    var pObj = new JsonObject
                    {
                        ["name"] = p.Name,
                        ["in"] = "query",
                        ["required"] = p.Required,
                        ["schema"] = new JsonObject { ["type"] = p.Type }
                    };
                    if (!string.IsNullOrEmpty(p.Description))
                        pObj["description"] = p.Description;
                    paramArr.Add(pObj);
                }
                op["parameters"] = paramArr;
            }

            if (!string.IsNullOrEmpty(spec.RequestBodyDescription))
            {
                op["requestBody"] = new JsonObject
                {
                    ["description"] = spec.RequestBodyDescription,
                    ["required"] = false,
                    ["content"] = new JsonObject
                    {
                        ["text/plain"] = new JsonObject { ["schema"] = new JsonObject { ["type"] = "string" } },
                        ["application/json"] = new JsonObject { ["schema"] = new JsonObject { ["type"] = "object" } }
                    }
                };
            }

            op["responses"] = new JsonObject
            {
                ["200"] = new JsonObject { ["description"] = isV2 ? "WebServerV2Response JSON envelope" : "Operation result" },
                ["400"] = new JsonObject { ["description"] = "Invalid parameters or request state" },
                ["401"] = new JsonObject { ["description"] = "Unauthorized or disabled gateway capability" },
                ["429"] = new JsonObject { ["description"] = "Client IP temporarily locked out due to repeated auth failures" }
            };
            return op;
        }

        public static string GetDocsHtml(int port = DefaultGatewayPort)
        {
            int effectivePort = port is > 0 and <= 65535 ? port : DefaultGatewayPort;
            var sb = new StringBuilder(4096);
            sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"UTF-8\">");
            sb.Append("<title>Shiro Account Manager • API Docs</title><style>");
            sb.Append("body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;background:#0f172a;color:#f8fafc;margin:0;padding:24px;}");
            sb.Append(".container{max-width:960px;margin:0 auto;}h1{color:#38bdf8;border-bottom:1px solid #334155;padding-bottom:12px;}");
            sb.Append(".endpoint{background:#1e293b;border-radius:8px;padding:14px 16px;margin-bottom:12px;border:1px solid #334155;}");
            sb.Append(".method{display:inline-block;padding:3px 9px;border-radius:4px;font-weight:700;font-size:12px;margin-right:8px;text-transform:uppercase;}");
            sb.Append(".get{background:#0284c7;color:#fff;}.post{background:#16a34a;color:#fff;}.path{font-family:monospace;font-size:15px;color:#e2e8f0;font-weight:600;}");
            sb.Append(".desc{margin-top:6px;color:#94a3b8;font-size:13px;}.badge{background:#334155;color:#38bdf8;padding:2px 8px;border-radius:4px;font-size:12px;margin-left:8px;}");
            sb.Append("a{color:#38bdf8;text-decoration:none;}</style></head><body><div class=\"container\">");
            sb.Append($"<h1>Shiro Account Manager • REST API</h1><p style=\"color:#94a3b8;\">Gateway v5.0.0 &bull; Raw OpenAPI: <a href=\"/swagger.json\">/swagger.json</a> &bull; WebSocket: <span class=\"badge\">ws://localhost:{effectivePort}/ws</span></p>");

            foreach (ApiRouteSpec spec in RouteDescriptors)
            {
                string mClass = spec.HttpMethod == "post" ? "post" : "get";
                string encodedSummary = WebUtility.HtmlEncode(spec.Summary);
                string v2Badge = spec.IncludeV2Alias ? $"<span class=\"badge\">/v2{WebUtility.HtmlEncode(spec.Path)}</span>" : string.Empty;
                sb.Append($"<div class=\"endpoint\"><div><span class=\"method {mClass}\">{WebUtility.HtmlEncode(spec.HttpMethod)}</span><span class=\"path\">{WebUtility.HtmlEncode(spec.Path)}</span>{v2Badge}</div>");
                sb.Append($"<div class=\"desc\">{encodedSummary}</div></div>");
            }

            sb.Append("</div></body></html>");
            return sb.ToString();
        }
    }
}
