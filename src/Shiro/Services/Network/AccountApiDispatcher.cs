#nullable enable
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Shiro.Services
{
    /// <summary>
    /// Handles Kestrel HTTP API routing, authorization policies, CORS,
    /// and REST API dispatching decoupled from the AccountManager God Class.
    /// </summary>
    public sealed partial class AccountApiDispatcher
    {
        public class ApiRequestContext
        {
            public string AbsolutePath { get; set; } = string.Empty;
            public bool IsV2 { get; set; }
            public bool IsLocal { get; set; }
            public string? ClientKey { get; set; }
            public string Host { get; set; } = string.Empty;
            public string Origin { get; set; } = string.Empty;
            public string Body { get; set; } = string.Empty;
            public Func<string, string?> GetQuery { get; set; } = _ => null;
            public Func<string, string?> GetHeader { get; set; } = _ => null;
            public Action<int> SetStatusCode { get; set; } = _ => { };
            public Action<string> SetContentType { get; set; } = _ => { };
        }

        public static AccountApiDispatcher Instance { get; } = new();
        internal const string InternalErrorMessage = "Internal server error";

        private const int MaxRequestBodySizeBytes = 256 * 1024; // 256 KB max payload
        private readonly System.Collections.Generic.HashSet<string> AttemptedJoins = new(StringComparer.Ordinal);

        internal static string WebServerResponse(object message, bool success) =>
            JsonSerializer.Serialize(
                new WebServerV2Response(success, message?.ToString() ?? string.Empty),
                ShiroAppJson.Default.WebServerV2Response);

        public async Task<string> SendKestrelResponseAsync(HttpContext context)
        {
            var req = context.Request;

            if (req.ContentLength > MaxRequestBodySizeBytes)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return WebServerResponse("Payload too large (max 256 KB)", false);
            }

            string path = req.Path.Value ?? "/";
            bool isV2 = path.StartsWith("/v2/", StringComparison.OrdinalIgnoreCase);
            string absolutePath = isV2 ? path.Substring(3) : path;

            bool isLocal = context.Connection.RemoteIpAddress != null &&
                           IPAddress.IsLoopback(context.Connection.RemoteIpAddress);
            string? clientKey = context.Connection.RemoteIpAddress?.ToString();
            string host = req.Host.Value ?? string.Empty;
            string origin = req.Headers["Origin"].ToString();
            string? headerPass = WebAuthPolicy.ExtractHeaderPassword(k => req.Headers.TryGetValue(k, out var val) ? val.ToString() : null);
            string? queryPass = WebAuthPolicy.ExtractQueryPassword(k => req.Query.TryGetValue(k, out var val) ? val.ToString() : null);

            string? authError = EvaluateGatewayAuth(
                absolutePath,
                absolutePath.Length > 1 ? absolutePath.Substring(1) : string.Empty,
                isLocal,
                clientKey,
                host,
                origin,
                headerPass,
                queryPass,
                isV2,
                code => context.Response.StatusCode = code);
            if (authError != null)
            {
                return authError;
            }

            string body = string.Empty;
            if (req.ContentLength > 0 || req.Body.CanRead)
            {
                using var ms = new MemoryStream();
                byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(4096);
                try
                {
                    int totalRead = 0;
                    int read;
                    while ((read = await req.Body.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                    {
                        totalRead += read;
                        if (totalRead > MaxRequestBodySizeBytes)
                        {
                            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                            return WebServerResponse("Payload too large (max 256 KB)", false);
                        }
                        ms.Write(buffer, 0, read);
                    }
                    body = Encoding.UTF8.GetString(ms.ToArray());
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            var apiCtx = new ApiRequestContext
            {
                AbsolutePath = absolutePath,
                IsV2 = isV2,
                IsLocal = isLocal,
                ClientKey = clientKey,
                Host = host,
                Origin = origin,
                Body = body,
                GetQuery = key => req.Query.TryGetValue(key, out var val) ? val.ToString() : null,
                GetHeader = key => req.Headers.TryGetValue(key, out var val) ? val.ToString() : null,
                SetStatusCode = code => context.Response.StatusCode = code,
                SetContentType = type => context.Response.ContentType = type
            };

            return await ProcessAuthenticatedRequestAsync(apiCtx, headerPass, queryPass).ConfigureAwait(false);
        }

        private static string? EvaluateGatewayAuth(
            string absolutePath,
            string method,
            bool isLocal,
            string? clientKey,
            string host,
            string origin,
            string? headerPass,
            string? queryPass,
            bool isV2,
            Action<int> setStatusCode)
        {
            string AuthReply(string message, int code, string raw)
            {
                setStatusCode(code);
                return isV2 ? WebServerResponse(message, false) : raw;
            }

            bool isPublicTelemetryMethod =
                method.Equals("GetWebSocketStats", StringComparison.OrdinalIgnoreCase) ||
                method.Equals("WebSocketStats", StringComparison.OrdinalIgnoreCase) ||
                absolutePath.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase);

            bool isSpecialDocOrHealth =
                absolutePath.Equals("/Running", StringComparison.OrdinalIgnoreCase) ||
                absolutePath.Equals("/swagger.json", StringComparison.OrdinalIgnoreCase) ||
                absolutePath.Equals("/openapi.json", StringComparison.OrdinalIgnoreCase) ||
                absolutePath.Equals("/api/docs", StringComparison.OrdinalIgnoreCase) ||
                absolutePath.Equals("/docs", StringComparison.OrdinalIgnoreCase);

            var scope = (isPublicTelemetryMethod || isSpecialDocOrHealth)
                ? AuthScope.PublicTelemetry
                : AuthScope.Api;

            var webServer = AppServices.Settings.GetSection("WebServer");
            bool allowExternal = webServer != null && webServer.Get<bool>("AllowExternalConnections");
            bool everyReq = webServer != null && webServer.Get<bool>("EveryRequestRequiresPassword");

            if (!string.IsNullOrEmpty(clientKey) && WebAuthPolicy.IsClientLockedOut(clientKey))
            {
                return AuthReply("Too many failed authentication attempts. Please try again later.", StatusCodes.Status429TooManyRequests, "Too Many Requests");
            }

            var authSettings = new AuthSettings(allowExternal, everyReq, AppServices.ServerBrowserState.WebSocketPassword);
            var authReq = new AuthRequest(scope, isLocal, host, origin, headerPass, queryPass);
            var decision = WebAuthPolicy.Evaluate(authReq, authSettings);
            WebAuthPolicy.RecordAuthResult(clientKey, decision);
            if (decision == AuthDecision.Allow)
                return null;

            int statusCode = WebAuthPolicy.ToStatusCode(decision);
            return decision switch
            {
                AuthDecision.ForbiddenExternal => AuthReply("External connections are not allowed", statusCode, string.Empty),
                AuthDecision.ForbiddenHost => AuthReply("Invalid host header", statusCode, "Forbidden"),
                AuthDecision.ForbiddenOrigin => AuthReply("Cross-origin requests from external web pages are forbidden", statusCode, "Forbidden"),
                AuthDecision.PasswordNotConfigured => AuthReply("WebServer password must be configured with 6 or more characters before accessing protected endpoints", statusCode, "Password Not Configured"),
                _ => AuthReply("Invalid Password, make sure your password contains 6 or more characters", statusCode, "Invalid Password")
            };
        }

        public bool ValidateWebSocketAuth(HttpContext context)
        {
            if (context == null) return false;

            bool isLocal = context.Connection.RemoteIpAddress != null &&
                           IPAddress.IsLoopback(context.Connection.RemoteIpAddress);
            string? clientKey = context.Connection.RemoteIpAddress?.ToString();
            if (WebAuthPolicy.IsClientLockedOut(clientKey))
                return false;

            var webServer = AppServices.Settings.GetSection("WebServer");
            bool allowExternal = webServer != null && webServer.Get<bool>("AllowExternalConnections");
            bool everyReq = webServer != null && webServer.Get<bool>("EveryRequestRequiresPassword");

            var settings = new AuthSettings(allowExternal, everyReq, AppServices.ServerBrowserState.WebSocketPassword);
            var request = new AuthRequest(
                AuthScope.Api,
                isLocal,
                context.Request.Host.Host,
                context.Request.Headers["Origin"].ToString(),
                WebAuthPolicy.ExtractHeaderPassword(k => context.Request.Headers.TryGetValue(k, out var hVal) ? hVal.ToString() : null),
                WebAuthPolicy.ExtractQueryPassword(k => context.Request.Query.TryGetValue(k, out var qVal) ? qVal.ToString() : null));

            var decision = WebAuthPolicy.Evaluate(request, settings);
            WebAuthPolicy.RecordAuthResult(clientKey, decision);
            return decision == AuthDecision.Allow;
        }

        public async Task<string> ProcessApiRequestAsync(ApiRequestContext req)
        {
            string absolutePath = req.AbsolutePath;
            string method = absolutePath.Length > 1 ? absolutePath.Substring(1) : string.Empty;
            string? headerPass = WebAuthPolicy.ExtractHeaderPassword(req.GetHeader);
            string? queryPass = WebAuthPolicy.ExtractQueryPassword(req.GetQuery);

            string? authError = EvaluateGatewayAuth(
                absolutePath,
                method,
                req.IsLocal,
                req.ClientKey,
                req.Host,
                req.Origin,
                headerPass,
                queryPass,
                req.IsV2,
                req.SetStatusCode);
            if (authError != null)
                return authError;

            return await ProcessAuthenticatedRequestAsync(req, headerPass, queryPass).ConfigureAwait(false);
        }

        private async Task<string> ProcessAuthenticatedRequestAsync(ApiRequestContext req, string? headerPass, string? queryPass)
        {
            bool v2 = req.IsV2;
            string absolutePath = req.AbsolutePath;

            string Reply(string response, bool success = false, int code = -1, string? raw = null)
            {
                req.SetStatusCode(code > 0 ? code : (success ? 200 : 400));
                return v2 ? WebServerResponse(response, success) : (raw ?? response);
            }

            if (absolutePath.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase))
            {
                req.SetStatusCode(StatusCodes.Status204NoContent);
                return string.Empty;
            }

            string body = req.Body;
            string method = absolutePath.Length > 1 ? absolutePath.Substring(1) : string.Empty;
            string? account = req.GetQuery("Account");

            if (absolutePath.Equals("/Running", StringComparison.OrdinalIgnoreCase)) return Reply("Shiro is running", true, raw: "true");

            if (absolutePath.Equals("/swagger.json", StringComparison.OrdinalIgnoreCase) || absolutePath.Equals("/openapi.json", StringComparison.OrdinalIgnoreCase))
            {
                req.SetContentType?.Invoke("application/json; charset=utf-8");
                return ApiDocumentationService.GetOpenApiJson();
            }

            if (absolutePath.Equals("/api/docs", StringComparison.OrdinalIgnoreCase) || absolutePath.Equals("/docs", StringComparison.OrdinalIgnoreCase))
            {
                req.SetContentType?.Invoke("text/html; charset=utf-8");
                return ApiDocumentationService.GetDocsHtml();
            }

            string password = headerPass ?? (req.IsLocal ? queryPass : null) ?? string.Empty;

            return await HandleApiEndpointAsync(req, method, account, password, body, Reply).ConfigureAwait(false);
        }

        private static bool TryParsePositiveUserId(string? raw, out long userId) =>
            long.TryParse(raw, out userId) && userId > 0;

        private static async Task<string?> TryEnsureCsrfAsync(Account account, Func<string, bool, int, string?, string> reply)
        {
            var csrfResult = await account.GetCSRFTokenAsync().ConfigureAwait(false);
            if (!csrfResult.Success)
                return reply("Invalid Account, the account's cookie may have expired and resulted in the account being logged out", false, -1, "Invalid Account");
            return null;
        }

        private static string ReplyUpstreamResult(RestResponse? res, Func<string, bool, int, string?, string> reply)
        {
            if (res == null || (int)res.StatusCode <= 0)
                return reply("Upstream transport failure", false, StatusCodes.Status502BadGateway, null);

            int status = (int)res.StatusCode;
            string content = status >= 500 ? InternalErrorMessage : (res.Content ?? string.Empty);
            return reply(content, res.IsSuccessful, status, null);
        }

        private static string? TryHandleGlobalSystemCommand(
            string method,
            ISettingsService general,
            Func<string, bool, int, string?, string> reply)
        {
            if (method.Equals("AntiAfk/Status", StringComparison.OrdinalIgnoreCase) || method.Equals("AntiAfkStatus", StringComparison.OrdinalIgnoreCase))
                return reply(JsonSerializer.Serialize(new AntiAfkStatusPayload(AntiAfkService.IsEnabled, AntiAfkService.TapsSent, AntiAfkService.StatusText), ShiroAppJson.Default.AntiAfkStatusPayload), true, -1, null);

            if (method.Equals("AntiAfk/Toggle", StringComparison.OrdinalIgnoreCase) || method.Equals("ToggleAntiAfk", StringComparison.OrdinalIgnoreCase))
            {
                AntiAfkService.Toggle();
                return reply(JsonSerializer.Serialize(new AntiAfkTogglePayload(AntiAfkService.IsEnabled, AntiAfkService.StatusText), ShiroAppJson.Default.AntiAfkTogglePayload), true, -1, null);
            }

            if (method.Equals("AntiAfk/Enable", StringComparison.OrdinalIgnoreCase))
            {
                AntiAfkService.Start();
                return reply(JsonSerializer.Serialize(new AntiAfkTogglePayload(AntiAfkService.IsEnabled, AntiAfkService.StatusText), ShiroAppJson.Default.AntiAfkTogglePayload), true, -1, null);
            }

            if (method.Equals("AntiAfk/Disable", StringComparison.OrdinalIgnoreCase))
            {
                AntiAfkService.Stop();
                return reply(JsonSerializer.Serialize(new AntiAfkTogglePayload(AntiAfkService.IsEnabled, AntiAfkService.StatusText), ShiroAppJson.Default.AntiAfkTogglePayload), true, -1, null);
            }

            if (method.Equals("Memory/Trim", StringComparison.OrdinalIgnoreCase) || method.Equals("TrimMemory", StringComparison.OrdinalIgnoreCase) || method.Equals("CleanRAM", StringComparison.OrdinalIgnoreCase))
            {
                var (bytesSaved, count) = MemoryOptimizer.TrimRobloxClients();
                return reply(JsonSerializer.Serialize(new MemoryTrimPayload(bytesSaved, count), ShiroAppJson.Default.MemoryTrimPayload), true, -1, null);
            }

            if (method.Equals("MultiRoblox/Status", StringComparison.OrdinalIgnoreCase))
            {
                var payload = new MultiRobloxStatusPayload(general.Get<bool>("EnableMultiRbx"), RobloxSingletonService.StatusText, RobloxSingletonService.TotalClosed, RobloxSingletonService.MutexHeld);
                return reply(JsonSerializer.Serialize(payload, ShiroAppJson.Default.MultiRobloxStatusPayload), true, -1, null);
            }

            if (method.Equals("MultiRoblox/Toggle", StringComparison.OrdinalIgnoreCase))
            {
                bool next = !general.Get<bool>("EnableMultiRbx");
                general.Set("EnableMultiRbx", next ? "true" : "false");
                AppServices.HostUiBridge.UpdateMultiRoblox();
                return reply(JsonSerializer.Serialize(new MultiRobloxTogglePayload(next, RobloxSingletonService.StatusText), ShiroAppJson.Default.MultiRobloxTogglePayload), true, -1, null);
            }

            if (method.Equals("Roblox/HideWindows", StringComparison.OrdinalIgnoreCase) || method.Equals("HideWindows", StringComparison.OrdinalIgnoreCase))
            {
                var (hidden, total) = RobloxWindowService.HideAllWindows();
                return reply(JsonSerializer.Serialize(new HideWindowsPayload(hidden, total, true), ShiroAppJson.Default.HideWindowsPayload), true, -1, null);
            }

            if (method.Equals("Roblox/ShowWindows", StringComparison.OrdinalIgnoreCase) || method.Equals("ShowWindows", StringComparison.OrdinalIgnoreCase))
            {
                var (shown, total) = RobloxWindowService.ShowAllWindows();
                return reply(JsonSerializer.Serialize(new ShowWindowsPayload(shown, total, false), ShiroAppJson.Default.ShowWindowsPayload), true, -1, null);
            }

            if (method.Equals("Roblox/ToggleWindows", StringComparison.OrdinalIgnoreCase) || method.Equals("ToggleWindows", StringComparison.OrdinalIgnoreCase))
            {
                var (nowHidden, affected) = RobloxWindowService.ToggleVisibility();
                return reply(JsonSerializer.Serialize(new ToggleWindowsPayload(nowHidden, affected), ShiroAppJson.Default.ToggleWindowsPayload), true, -1, null);
            }

            if (method.Equals("PotatoMode/Toggle", StringComparison.OrdinalIgnoreCase))
            {
                bool next = !general.Get<bool>("EnablePotatoMode");
                general.Set("EnablePotatoMode", next ? "true" : "false");
                ClientSettingsPatcher.PatchSettings();
                return reply(JsonSerializer.Serialize(new PotatoModeTogglePayload(next), ShiroAppJson.Default.PotatoModeTogglePayload), true, -1, null);
            }

            if (method.Equals("KillRoblox", StringComparison.OrdinalIgnoreCase) || method.Equals("KillAllRoblox", StringComparison.OrdinalIgnoreCase))
            {
                AppServices.HostUiBridge.CloseAllRobloxFromUi();
                return reply("Closed all Roblox processes", true, -1, null);
            }

            if (method.Equals("AutoReconnect/Status", StringComparison.OrdinalIgnoreCase))
            {
                var payload = new AutoReconnectStatusPayload(AutoReconnectService.Instance.IsEnabled, AutoReconnectService.Instance.ActiveSessionsCount, AutoReconnectService.Instance.TotalReconnections);
                return reply(JsonSerializer.Serialize(payload, ShiroAppJson.Default.AutoReconnectStatusPayload), true, -1, null);
            }

            if (method.Equals("AutoReconnect/Toggle", StringComparison.OrdinalIgnoreCase))
            {
                AutoReconnectService.Instance.IsEnabled = !AutoReconnectService.Instance.IsEnabled;
                var payload = new AutoReconnectTogglePayload(AutoReconnectService.Instance.IsEnabled, AutoReconnectService.Instance.ActiveSessionsCount);
                return reply(JsonSerializer.Serialize(payload, ShiroAppJson.Default.AutoReconnectTogglePayload), true, -1, null);
            }

            return null;
        }
    }
}
