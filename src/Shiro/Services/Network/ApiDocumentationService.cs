#nullable enable
using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shiro.Services
{
    /// <summary>
    /// Generates OpenAPI 3.0 specification and interactive documentation for Shiro Account Manager's local WebServer API.
    /// </summary>
    public static class ApiDocumentationService
    {
        private static string? _cachedOpenApiJson;

        public static string GetOpenApiJson()
        {
            if (_cachedOpenApiJson != null) return _cachedOpenApiJson;

            var doc = new JsonObject
            {
                ["openapi"] = "3.0.1",
                ["info"] = new JsonObject
                {
                    ["title"] = "Shiro Account Manager API",
                    ["description"] = "Local REST & WebSocket Gateway for Roblox Account Automation, Multi-Instance Management, and Performance Controls.",
                    ["version"] = "5.0.0"
                },
                ["servers"] = new JsonArray
                {
                    new JsonObject { ["url"] = "http://localhost:7963" }
                },
                ["paths"] = new JsonObject
                {
                    ["/Running"] = new JsonObject
                    {
                        ["get"] = new JsonObject
                        {
                            ["summary"] = "Health check",
                            ["responses"] = new JsonObject
                            {
                                ["200"] = new JsonObject { ["description"] = "Returns true if Shiro is active." }
                            }
                        }
                    },
                    ["/GetAccounts"] = new JsonObject
                    {
                        ["get"] = new JsonObject
                        {
                            ["summary"] = "List account usernames",
                            ["parameters"] = new JsonArray
                            {
                                new JsonObject { ["name"] = "Group", ["in"] = "query", ["schema"] = new JsonObject { ["type"] = "string" } }
                            },
                            ["responses"] = new JsonObject
                            {
                                ["200"] = new JsonObject { ["description"] = "Comma-separated usernames." }
                            }
                        }
                    },
                    ["/LaunchAccount"] = new JsonObject
                    {
                        ["get"] = new JsonObject
                        {
                            ["summary"] = "Launch a Roblox game for an account",
                            ["parameters"] = new JsonArray
                            {
                                new JsonObject { ["name"] = "Account", ["in"] = "query", ["required"] = true, ["schema"] = new JsonObject { ["type"] = "string" } },
                                new JsonObject { ["name"] = "PlaceId", ["in"] = "query", ["required"] = true, ["schema"] = new JsonObject { ["type"] = "integer" } },
                                new JsonObject { ["name"] = "JobId", ["in"] = "query", ["schema"] = new JsonObject { ["type"] = "string" } }
                            },
                            ["responses"] = new JsonObject
                            {
                                ["200"] = new JsonObject { ["description"] = "Launch result" }
                            }
                        }
                    },
                    ["/KillRoblox"] = new JsonObject
                    {
                        ["get"] = new JsonObject
                        {
                            ["summary"] = "Terminate all running Roblox client instances",
                            ["responses"] = new JsonObject
                            {
                                ["200"] = new JsonObject { ["description"] = "All clients closed." }
                            }
                        }
                    },
                    ["/AntiAfk/Status"] = new JsonObject
                    {
                        ["get"] = new JsonObject
                        {
                            ["summary"] = "Get Anti-AFK engine status",
                            ["responses"] = new JsonObject
                            {
                                ["200"] = new JsonObject { ["description"] = "Engine state and taps count" }
                            }
                        }
                    },
                    ["/AntiAfk/Toggle"] = new JsonObject
                    {
                        ["get"] = new JsonObject
                        {
                            ["summary"] = "Toggle Anti-AFK engine state",
                            ["responses"] = new JsonObject
                            {
                                ["200"] = new JsonObject { ["description"] = "Updated engine state" }
                            }
                        }
                    },
                    ["/AutoReconnect/Status"] = new JsonObject
                    {
                        ["get"] = new JsonObject
                        {
                            ["summary"] = "Get Auto-Reconnect crash recovery daemon status",
                            ["responses"] = new JsonObject
                            {
                                ["200"] = new JsonObject { ["description"] = "Daemon state, active sessions, and reconnection count" }
                            }
                        }
                    },
                    ["/AutoReconnect/Toggle"] = new JsonObject
                    {
                        ["get"] = new JsonObject
                        {
                            ["summary"] = "Toggle Auto-Reconnect crash recovery daemon",
                            ["responses"] = new JsonObject
                            {
                                ["200"] = new JsonObject { ["description"] = "Updated daemon state" }
                            }
                        }
                    },
                    ["/Accounts/Export"] = new JsonObject
                    {
                        ["get"] = new JsonObject
                        {
                            ["summary"] = "Export accounts in requested format (UserPassCookie, CookieOnly, UserPass, Csv, Json)",
                            ["responses"] = new JsonObject
                            {
                                ["200"] = new JsonObject { ["description"] = "Exported account data stream" }
                            }
                        }
                    },
                    ["/MultiRoblox/Status"] = new JsonObject
                    {
                        ["get"] = new JsonObject
                        {
                            ["summary"] = "Get Multi-Roblox singleton handle status",
                            ["responses"] = new JsonObject
                            {
                                ["200"] = new JsonObject { ["description"] = "Status and closed handles" }
                            }
                        }
                    },
                    ["/Memory/Trim"] = new JsonObject
                    {
                        ["get"] = new JsonObject
                        {
                            ["summary"] = "Trim working set RAM across running Roblox clients",
                            ["responses"] = new JsonObject
                            {
                                ["200"] = new JsonObject { ["description"] = "Bytes saved and clients count" }
                            }
                        }
                    },
                    ["/Roblox/ToggleWindows"] = new JsonObject
                    {
                        ["get"] = new JsonObject
                        {
                            ["summary"] = "Toggle hide/restore all Roblox client windows",
                            ["responses"] = new JsonObject
                            {
                                ["200"] = new JsonObject { ["description"] = "Hidden state and affected clients count" }
                            }
                        }
                    }
                }
            };

            _cachedOpenApiJson = doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            return _cachedOpenApiJson;
        }

        public static string GetDocsHtml()
        {
            return @"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <title>Shiro Account Manager • API Docs</title>
    <style>
        body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; background: #0f172a; color: #f8fafc; margin: 0; padding: 24px; }
        .container { max-width: 900px; margin: 0 auto; }
        h1 { color: #38bdf8; border-bottom: 1px solid #334155; padding-bottom: 12px; }
        .endpoint { background: #1e293b; border-radius: 8px; padding: 16px; margin-bottom: 16px; border: 1px solid #334155; }
        .method { display: inline-block; padding: 4px 10px; border-radius: 4px; font-weight: bold; font-size: 13px; margin-right: 8px; }
        .get { background: #0284c7; color: white; }
        .post { background: #16a34a; color: white; }
        .path { font-family: monospace; font-size: 16px; color: #e2e8f0; font-weight: 600; }
        .desc { margin-top: 8px; color: #94a3b8; font-size: 14px; }
        .badge { background: #334155; color: #38bdf8; padding: 2px 8px; border-radius: 4px; font-size: 12px; margin-left: 8px; }
        a { color: #38bdf8; text-decoration: none; }
        a:hover { text-decoration: underline; }
    </style>
</head>
<body>
    <div class=""container"">
        <h1>Shiro Account Manager • REST API</h1>
        <p style=""color: #94a3b8;"">Gateway v5.0.0 &bull; Raw OpenAPI: <a href=""/swagger.json"">/swagger.json</a> &bull; WebSocket: <span class=""badge"">ws://localhost:7963/ws</span></p>

        <div class=""endpoint"">
            <div><span class=""method get"">GET</span><span class=""path"">/Running</span></div>
            <div class=""desc"">Health check endpoint. Returns true if Shiro is active.</div>
        </div>

        <div class=""endpoint"">
            <div><span class=""method get"">GET</span><span class=""path"">/GetAccounts</span></div>
            <div class=""desc"">Returns list of loaded account usernames. Optional query: <code>?Group=Default</code></div>
        </div>

        <div class=""endpoint"">
            <div><span class=""method get"">GET</span><span class=""path"">/LaunchAccount</span></div>
            <div class=""desc"">Launches an account into a game place. Query: <code>?Account=&lt;username&gt;&amp;PlaceId=&lt;id&gt;&amp;JobId=&lt;guid&gt;</code></div>
        </div>

        <div class=""endpoint"">
            <div><span class=""method get"">GET</span><span class=""path"">/KillRoblox</span></div>
            <div class=""desc"">Terminates all running Roblox instances immediately.</div>
        </div>

        <div class=""endpoint"">
            <div><span class=""method get"">GET</span><span class=""path"">/AntiAfk/Status</span></div>
            <div class=""desc"">Retrieves Anti-AFK automation engine status and tap metrics.</div>
        </div>

        <div class=""endpoint"">
            <div><span class=""method get"">GET</span><span class=""path"">/AntiAfk/Toggle</span></div>
            <div class=""desc"">Toggles Anti-AFK state between Active and Idle.</div>
        </div>

        <div class=""endpoint"">
            <div><span class=""method get"">GET</span><span class=""path"">/AutoReconnect/Status</span></div>
            <div class=""desc"">Retrieves Auto-Reconnect daemon metrics and active sessions.</div>
        </div>

        <div class=""endpoint"">
            <div><span class=""method get"">GET</span><span class=""path"">/AutoReconnect/Toggle</span></div>
            <div class=""desc"">Toggles Auto-Reconnect crash recovery daemon.</div>
        </div>

        <div class=""endpoint"">
            <div><span class=""method get"">GET</span><span class=""path"">/Accounts/Export</span></div>
            <div class=""desc"">Exports accounts in requested format: <code>?Format=UserPassCookie|CookieOnly|UserPass|Csv|Json</code></div>
        </div>

        <div class=""endpoint"">
            <div><span class=""method get"">GET</span><span class=""path"">/MultiRoblox/Status</span></div>
            <div class=""desc"">Queries Multi-Roblox handle closer status.</div>
        </div>

        <div class=""endpoint"">
            <div><span class=""method get"">GET</span><span class=""path"">/Memory/Trim</span></div>
            <div class=""desc"">Trims working set RAM memory across all running clients.</div>
        </div>

        <div class=""endpoint"">
            <div><span class=""method get"">GET</span><span class=""path"">/Roblox/ToggleWindows</span></div>
            <div class=""desc"">Toggles hide/restore visibility for all running Roblox game windows.</div>
        </div>
    </div>
</body>
</html>";
        }
    }
}
