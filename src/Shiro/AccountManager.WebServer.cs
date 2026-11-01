using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Shiro.Classes;
using Shiro.Services;

namespace Shiro
{
    public partial class AccountManager
    {
        private const int MaxRequestBodySizeBytes = 256 * 1024; // 256 KB max payload
        private readonly HashSet<string> AttemptedJoins = new HashSet<string>(StringComparer.Ordinal);

        private string WebServerResponse(object Message, bool Success) => Utilities.FastJson.Serialize(new { Success, Message });

        internal sealed class ApiRequestContext
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

        public async Task<string> SendKestrelResponse(HttpContext Context)
        {
            var req = Context.Request;

            if (req.ContentLength > MaxRequestBodySizeBytes)
            {
                Context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return WebServerResponse("Payload too large (max 256 KB)", false);
            }

            string path = req.Path.Value ?? "/";
            bool isV2 = path.StartsWith("/v2/", StringComparison.OrdinalIgnoreCase);
            string absolutePath = isV2 ? path.Substring(3) : path;

            bool isLocal = Context.Connection.RemoteIpAddress != null &&
                           IPAddress.IsLoopback(Context.Connection.RemoteIpAddress);

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
                            Context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
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
                SetStatusCode = code => Context.Response.StatusCode = code,
                SetContentType = type => Context.Response.ContentType = type
            };

            return await ProcessApiRequestAsync(apiCtx).ConfigureAwait(false);
        }

        /// <summary>
        /// Returns true when the host name is exactly a loopback name. Prefix
        /// matching is deliberately avoided so "localhost.evil.com" is rejected.
        /// </summary>
        internal static bool IsLoopbackHostName(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            host = host.Trim().TrimStart('[').TrimEnd(']');
            return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                   host.Equals("127.0.0.1", StringComparison.Ordinal) ||
                   host.Equals("::1", StringComparison.Ordinal);
        }

        /// <summary>
        /// An empty Origin (non-browser client) is trusted; otherwise the parsed
        /// origin host must be an exact loopback name.
        /// </summary>
        internal static bool IsTrustedLocalOrigin(string origin)
        {
            if (string.IsNullOrEmpty(origin)) return true;
            if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri) || uri == null) return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
            return IsLoopbackHostName(uri.Host);
        }

        /// <summary>
        /// Constant-time password comparison to avoid timing side-channels.
        /// Uses stack-allocated UTF-8 buffers for typical password lengths to avoid per-request heap allocations.
        /// </summary>
        internal static bool FixedTimeEqualsPassword(string provided, string expected)
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

        internal static string ExtractRequestPassword(Func<string, string?> getHeader, Func<string, string?> getQuery)
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
            if (string.IsNullOrEmpty(password))
                password = getQuery("Password") ?? getQuery("password") ?? getQuery("token");

            return password ?? string.Empty;
        }

        internal bool ValidateWebSocketAuth(HttpContext context)
        {
            if (context == null) return false;

            bool isLocal = context.Connection.RemoteIpAddress != null &&
                           IPAddress.IsLoopback(context.Connection.RemoteIpAddress);

            if (!isLocal && !WebServer.Get<bool>("AllowExternalConnections"))
                return false;

            if (!WebServer.Get<bool>("AllowExternalConnections"))
            {
                if (!IsLoopbackHostName(context.Request.Host.Host))
                    return false;

                if (!IsTrustedLocalOrigin(context.Request.Headers["Origin"].ToString()))
                    return false;
            }

            if (WebServer.Get<bool>("EveryRequestRequiresPassword") || (!string.IsNullOrEmpty(WSPassword) && WSPassword.Length >= 6))
            {
                if (string.IsNullOrEmpty(WSPassword) || WSPassword.Length < 6)
                    return false;

                string password = ExtractRequestPassword(
                    k => context.Request.Headers.TryGetValue(k, out var hVal) ? hVal.ToString() : null,
                    k => context.Request.Query.TryGetValue(k, out var qVal) ? qVal.ToString() : null);

                return FixedTimeEqualsPassword(password, WSPassword);
            }

            return true;
        }

        internal async Task<string> ProcessApiRequestAsync(ApiRequestContext req)
        {
            bool V2 = req.IsV2;
            string AbsolutePath = req.AbsolutePath;

            string Reply(string Response, bool Success = false, int Code = -1, string? Raw = null)
            {
                req.SetStatusCode(Code > 0 ? Code : (Success ? 200 : 400));

                return V2 ? WebServerResponse(Response, Success) : (Raw ?? Response);
            }

            if (!req.IsLocal && !WebServer.Get<bool>("AllowExternalConnections")) return Reply("External connections are not allowed", false, 401, string.Empty);

            if (!WebServer.Get<bool>("AllowExternalConnections"))
            {
                string host = req.Host;
                if (!string.IsNullOrEmpty(host) && !IsLoopbackHostName(host))
                    return Reply("Invalid host header", false, 403, "Forbidden");

                if (!IsTrustedLocalOrigin(req.Origin))
                    return Reply("Cross-origin requests from external web pages are forbidden", false, 403, "Forbidden");
            }

            if (AbsolutePath == "/favicon.ico") return ""; // always return nothing

            if (AbsolutePath == "/Running") return Reply("Shiro is running", true, Raw: "true");

            if (AbsolutePath.Equals("/swagger.json", StringComparison.OrdinalIgnoreCase) || AbsolutePath.Equals("/openapi.json", StringComparison.OrdinalIgnoreCase))
            {
                req.SetContentType?.Invoke("application/json; charset=utf-8");
                return ApiDocumentationService.GetOpenApiJson();
            }

            if (AbsolutePath.Equals("/api/docs", StringComparison.OrdinalIgnoreCase) || AbsolutePath.Equals("/docs", StringComparison.OrdinalIgnoreCase))
            {
                req.SetContentType?.Invoke("text/html; charset=utf-8");
                return ApiDocumentationService.GetDocsHtml();
            }

            string Body = req.Body;
            string Method = AbsolutePath.Length > 1 ? AbsolutePath.Substring(1) : string.Empty;
            string? Account = req.GetQuery("Account");

            // Extract Password from headers (X-Shiro-Auth, X-RAM-Auth, Bearer token) or fallback to QueryString
            string Password = ExtractRequestPassword(req.GetHeader, req.GetQuery);

            bool isPublicTelemetryMethod =
                Method.Equals("GetWebSocketStats", StringComparison.OrdinalIgnoreCase) ||
                Method.Equals("WebSocketStats", StringComparison.OrdinalIgnoreCase);

            bool isSensitiveMethod = !isPublicTelemetryMethod;

            if (WebServer.Get<bool>("EveryRequestRequiresPassword") || isSensitiveMethod)
            {
                if (string.IsNullOrEmpty(WSPassword) || WSPassword.Length < 6)
                    return Reply("WebServer password must be configured with 6 or more characters before accessing protected endpoints", false, 401, "Password Not Configured");

                if (string.IsNullOrEmpty(Password) || !FixedTimeEqualsPassword(Password, WSPassword))
                    return Reply("Invalid Password, make sure your password contains 6 or more characters", false, 401, "Invalid Password");
            }

            return await HandleApiEndpointAsync(req, Method, Account, Password, Body, Reply).ConfigureAwait(false);
        }
    }
}
