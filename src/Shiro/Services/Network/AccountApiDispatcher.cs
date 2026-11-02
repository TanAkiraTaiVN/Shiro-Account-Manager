#nullable enable
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Shiro.Classes;

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
            public string Host { get; set; } = string.Empty;
            public string Origin { get; set; } = string.Empty;
            public string Body { get; set; } = string.Empty;
            public Func<string, string?> GetQuery { get; set; } = _ => null;
            public Func<string, string?> GetHeader { get; set; } = _ => null;
            public Action<int> SetStatusCode { get; set; } = _ => { };
            public Action<string> SetContentType { get; set; } = _ => { };
        }

        public static AccountApiDispatcher Instance { get; } = new();

        private const int MaxRequestBodySizeBytes = 256 * 1024; // 256 KB max payload
        private readonly System.Collections.Generic.HashSet<string> AttemptedJoins = new(StringComparer.Ordinal);

        internal static string WebServerResponse(object message, bool success) =>
            Utilities.FastJson.Serialize(new { Success = success, Message = message });

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
                Host = req.Host.Host,
                Origin = req.Headers["Origin"].ToString(),
                Body = body,
                GetQuery = key => req.Query.TryGetValue(key, out var val) ? val.ToString() : null,
                GetHeader = key => req.Headers.TryGetValue(key, out var val) ? val.ToString() : null,
                SetStatusCode = code => context.Response.StatusCode = code,
                SetContentType = type => context.Response.ContentType = type
            };

            return await ProcessApiRequestAsync(apiCtx).ConfigureAwait(false);
        }

        public static bool IsLoopbackHostName(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            host = host.Trim().TrimStart('[').TrimEnd(']');
            return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                   host.Equals("127.0.0.1", StringComparison.Ordinal) ||
                   host.Equals("::1", StringComparison.Ordinal);
        }

        public static bool IsTrustedLocalOrigin(string origin)
        {
            if (string.IsNullOrEmpty(origin)) return true;
            if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri) || uri == null) return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
            return IsLoopbackHostName(uri.Host);
        }

        public static bool FixedTimeEqualsPassword(string provided, string expected)
        {
            if (provided == null || expected == null) return false;
            int maxByteA = Encoding.UTF8.GetMaxByteCount(provided.Length);
            int maxByteB = Encoding.UTF8.GetMaxByteCount(expected.Length);
            if (maxByteA <= 256 && maxByteB <= 256)
            {
                Span<byte> bufA = stackalloc byte[maxByteA];
                Span<byte> bufB = stackalloc byte[maxByteB];
                int lenA = Encoding.UTF8.GetBytes(provided.AsSpan(), bufA);
                int lenB = Encoding.UTF8.GetBytes(expected.AsSpan(), bufB);
                return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(bufA[..lenA], bufB[..lenB]);
            }

            byte[] a = Encoding.UTF8.GetBytes(provided);
            byte[] b = Encoding.UTF8.GetBytes(expected);
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
        }

        public static string? ExtractHeaderPassword(Func<string, string?> getHeader)
        {
            string? password = getHeader("X-Shiro-Auth");
            if (string.IsNullOrEmpty(password))
                password = getHeader("X-RAM-Auth");
            if (string.IsNullOrEmpty(password))
            {
                string? authHeader = getHeader("Authorization");
                if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    password = authHeader.Substring(7).Trim();
            }

            return string.IsNullOrEmpty(password) ? null : password;
        }

        public static string? ExtractQueryPassword(Func<string, string?> getQuery)
        {
            string? password = getQuery("Password") ?? getQuery("password") ?? getQuery("token");
            return string.IsNullOrEmpty(password) ? null : password;
        }

        public static string ExtractRequestPassword(Func<string, string?> getHeader, Func<string, string?> getQuery)
        {
            return ExtractHeaderPassword(getHeader) ?? ExtractQueryPassword(getQuery) ?? string.Empty;
        }

        public bool ValidateWebSocketAuth(HttpContext context)
        {
            if (context == null) return false;

            bool isLocal = context.Connection.RemoteIpAddress != null &&
                           IPAddress.IsLoopback(context.Connection.RemoteIpAddress);

            var webServer = AccountManager.WebServer;
            bool allowExternal = webServer != null && webServer.Get<bool>("AllowExternalConnections");
            bool everyReq = webServer != null && webServer.Get<bool>("EveryRequestRequiresPassword");

            var settings = new AuthSettings(allowExternal, everyReq, AccountManager.WSPassword);
            var request = new AuthRequest(
                AuthScope.Api,
                isLocal,
                context.Request.Host.Host,
                context.Request.Headers["Origin"].ToString(),
                ExtractHeaderPassword(k => context.Request.Headers.TryGetValue(k, out var hVal) ? hVal.ToString() : null),
                ExtractQueryPassword(k => context.Request.Query.TryGetValue(k, out var qVal) ? qVal.ToString() : null));

            return WebAuthPolicy.Evaluate(request, settings) == AuthDecision.Allow;
        }

        public async Task<string> ProcessApiRequestAsync(ApiRequestContext req)
        {
            bool v2 = req.IsV2;
            string absolutePath = req.AbsolutePath;

            string Reply(string response, bool success = false, int code = -1, string? raw = null)
            {
                req.SetStatusCode(code > 0 ? code : (success ? 200 : 400));
                return v2 ? WebServerResponse(response, success) : (raw ?? response);
            }

            if (absolutePath == "/favicon.ico") return "";

            string body = req.Body;
            string method = absolutePath.Length > 1 ? absolutePath.Substring(1) : string.Empty;
            string? account = req.GetQuery("Account");

            bool isPublicTelemetryMethod =
                method.Equals("GetWebSocketStats", StringComparison.OrdinalIgnoreCase) ||
                method.Equals("WebSocketStats", StringComparison.OrdinalIgnoreCase);

            bool isSpecialDocOrHealth =
                absolutePath.Equals("/Running", StringComparison.OrdinalIgnoreCase) ||
                absolutePath.Equals("/swagger.json", StringComparison.OrdinalIgnoreCase) ||
                absolutePath.Equals("/openapi.json", StringComparison.OrdinalIgnoreCase) ||
                absolutePath.Equals("/api/docs", StringComparison.OrdinalIgnoreCase) ||
                absolutePath.Equals("/docs", StringComparison.OrdinalIgnoreCase);

            var scope = (isPublicTelemetryMethod || isSpecialDocOrHealth)
                ? AuthScope.PublicTelemetry
                : AuthScope.Api;

            var webServer = AccountManager.WebServer;
            bool allowExternal = webServer != null && webServer.Get<bool>("AllowExternalConnections");
            bool everyReq = webServer != null && webServer.Get<bool>("EveryRequestRequiresPassword");

            string? headerPass = ExtractHeaderPassword(req.GetHeader);
            string? queryPass = ExtractQueryPassword(req.GetQuery);

            var authSettings = new AuthSettings(allowExternal, everyReq, AccountManager.WSPassword);
            var authReq = new AuthRequest(
                scope,
                req.IsLocal,
                req.Host,
                req.Origin,
                headerPass,
                queryPass);

            var decision = WebAuthPolicy.Evaluate(authReq, authSettings);
            if (decision != AuthDecision.Allow)
            {
                int statusCode = WebAuthPolicy.ToStatusCode(decision);
                return decision switch
                {
                    AuthDecision.ForbiddenExternal => Reply("External connections are not allowed", false, statusCode, string.Empty),
                    AuthDecision.ForbiddenHost => Reply("Invalid host header", false, statusCode, "Forbidden"),
                    AuthDecision.ForbiddenOrigin => Reply("Cross-origin requests from external web pages are forbidden", false, statusCode, "Forbidden"),
                    AuthDecision.PasswordNotConfigured => Reply("WebServer password must be configured with 6 or more characters before accessing protected endpoints", false, statusCode, "Password Not Configured"),
                    _ => Reply("Invalid Password, make sure your password contains 6 or more characters", false, statusCode, "Invalid Password")
                };
            }

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
    }
}
