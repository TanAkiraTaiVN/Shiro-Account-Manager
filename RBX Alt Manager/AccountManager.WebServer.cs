using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Services;

namespace RBX_Alt_Manager
{
    public partial class AccountManager
    {
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
            string path = req.Path.Value ?? "/";
            bool isV2 = path.StartsWith("/v2/", StringComparison.OrdinalIgnoreCase);
            string absolutePath = isV2 ? path.Substring(3) : path;

            bool isLocal = Context.Connection.RemoteIpAddress != null &&
                           IPAddress.IsLoopback(Context.Connection.RemoteIpAddress);

            string body = string.Empty;
            if (req.ContentLength > 0 || req.Body.CanRead)
            {
                using var reader = new StreamReader(req.Body, Encoding.UTF8);
                body = await reader.ReadToEndAsync().ConfigureAwait(false);
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

            if (WebServer.Get<bool>("EveryRequestRequiresPassword"))
            {
                if (!string.IsNullOrEmpty(WSPassword) && WSPassword.Length >= 6)
                {
                    string password = context.Request.Headers["X-RAM-Auth"].ToString();
                    if (string.IsNullOrEmpty(password))
                    {
                        string authHeader = context.Request.Headers["Authorization"].ToString();
                        if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                            password = authHeader.Substring(7).Trim();
                    }
                    if (string.IsNullOrEmpty(password) && context.Request.Query.TryGetValue("token", out var tokenVal))
                        password = tokenVal.ToString();
                    if (string.IsNullOrEmpty(password) && context.Request.Query.TryGetValue("password", out var passVal))
                        password = passVal.ToString();

                    return FixedTimeEqualsPassword(password, WSPassword);
                }
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

            // Extract Password from headers (Bearer token or X-RAM-Auth) or fallback to QueryString
            string? Password = req.GetHeader("X-RAM-Auth");
            if (string.IsNullOrEmpty(Password))
            {
                string? authHeader = req.GetHeader("Authorization");
                if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    Password = authHeader.Substring(7).Trim();
            }
            if (string.IsNullOrEmpty(Password))
                Password = req.GetQuery("Password");

            bool isSensitiveMethod = Method == "GetCookie" ||
                                     Method == "GetAccounts" ||
                                     Method == "GetAccountsJson" ||
                                     Method == "LaunchAccount" ||
                                     Method == "FollowUser" ||
                                     Method == "ImportCookie" ||
                                     Method.Equals("KillRoblox", StringComparison.OrdinalIgnoreCase) ||
                                     Method.Equals("KillAllRoblox", StringComparison.OrdinalIgnoreCase);

            if (WebServer.Get<bool>("EveryRequestRequiresPassword") || isSensitiveMethod)
            {
                if (WSPassword != null && WSPassword.Length >= 6 && (string.IsNullOrEmpty(Password) || !FixedTimeEqualsPassword(Password ?? string.Empty, WSPassword)))
                    return Reply("Invalid Password, make sure your password contains 6 or more characters", false, 401, "Invalid Password");
            }

            if (Method == "GetWebSocketStats" || Method == "WebSocketStats")
            {
                return Reply(Utilities.FastJson.Serialize(new
                {
                    ActiveClients = KestrelWebServer.ActiveWebSocketCount,
                    Server = "Shiro Kestrel Gateway",
                    Endpoints = new[] { "/ws", "/events", "/v2/events" }
                }), true);
            }

            if (Method == "GetAccounts")
            {
                if (!WebServer.Get<bool>("AllowGetAccounts")) return Reply("Method `GetAccounts` not allowed", false, 401, "Method not allowed");

                string? GroupFilter = req.GetQuery("Group");
                var filteredAccounts = GetAccountsSnapshot();
                if (!string.IsNullOrEmpty(GroupFilter))
                    filteredAccounts = filteredAccounts.Where(acc => acc.Group == GroupFilter).ToArray();

                string result = string.Join(",", filteredAccounts.Select(acc => acc.Username));
                return Reply(result, true, Raw: result);
            }

            if (Method == "GetAccountsJson")
            {
                if (!WebServer.Get<bool>("AllowGetAccounts")) return Reply("Method `GetAccountsJson` not allowed", false, 401, "Method not allowed");

                string? GroupFilter = req.GetQuery("Group");
                bool ShowCookies = !string.IsNullOrEmpty(WSPassword) && WSPassword.Length >= 6 && FixedTimeEqualsPassword(Password ?? string.Empty, WSPassword) && req.GetQuery("IncludeCookies") == "true" && WebServer.Get<bool>("AllowGetCookie");

                List<object> Objects = new List<object>();

                foreach (Account acc in GetAccountsSnapshot())
                {
                    if (!string.IsNullOrEmpty(GroupFilter) && acc.Group != GroupFilter) continue;

                    object AccountObject = new
                    {
                        acc.Username,
                        acc.UserID,
                        acc.Alias,
                        acc.Description,
                        acc.Group,
                        acc.CSRFToken,
                        LastUsed = acc.LastUse.ToRobloxTick(),
                        Cookie = ShowCookies ? acc.SecurityToken : null,
                        acc.Fields,
                    };

                    Objects.Add(AccountObject);
                }

                return Reply(Utilities.FastJson.Serialize(Objects), true);
            }

            if (Method == "ImportCookie")
            {
                string? cookieVal = req.GetQuery("Cookie");
                if (string.IsNullOrWhiteSpace(cookieVal))
                    return Reply("Cookie parameter required", false, 400, "Missing Cookie");
                Account? New = await AddAccountAsync(cookieVal, cancellationToken: lifetimeCancellation.Token).ConfigureAwait(false);

                bool Success = New != null;

                return Reply(Success ? "Cookie successfully imported" : "[ImportCookie] An error was encountered importing the cookie", Success, Raw: Success ? "true" : "false");
            }

            // Global commands (do not require an Account parameter)
            if (Method.Equals("AntiAfk/Status", StringComparison.OrdinalIgnoreCase) || Method.Equals("AntiAfkStatus", StringComparison.OrdinalIgnoreCase))
                return Reply(Utilities.FastJson.Serialize(new { Enabled = AntiAfkService.IsEnabled, TapsSent = AntiAfkService.TapsSent, Status = AntiAfkService.StatusText }), true);

            if (Method.Equals("AntiAfk/Toggle", StringComparison.OrdinalIgnoreCase) || Method.Equals("ToggleAntiAfk", StringComparison.OrdinalIgnoreCase))
            {
                AntiAfkService.Toggle();
                return Reply(Utilities.FastJson.Serialize(new { Enabled = AntiAfkService.IsEnabled, Status = AntiAfkService.StatusText }), true);
            }

            if (Method.Equals("AntiAfk/Enable", StringComparison.OrdinalIgnoreCase))
            {
                AntiAfkService.Start();
                return Reply(Utilities.FastJson.Serialize(new { Enabled = AntiAfkService.IsEnabled, Status = AntiAfkService.StatusText }), true);
            }

            if (Method.Equals("AntiAfk/Disable", StringComparison.OrdinalIgnoreCase))
            {
                AntiAfkService.Stop();
                return Reply(Utilities.FastJson.Serialize(new { Enabled = AntiAfkService.IsEnabled, Status = AntiAfkService.StatusText }), true);
            }

            if (Method.Equals("Memory/Trim", StringComparison.OrdinalIgnoreCase) || Method.Equals("TrimMemory", StringComparison.OrdinalIgnoreCase) || Method.Equals("CleanRAM", StringComparison.OrdinalIgnoreCase))
            {
                var (bytesSaved, count) = MemoryOptimizer.TrimRobloxClients();
                return Reply(Utilities.FastJson.Serialize(new { BytesSaved = bytesSaved, ClientsCount = count }), true);
            }

            if (Method.Equals("MultiRoblox/Status", StringComparison.OrdinalIgnoreCase))
                return Reply(Utilities.FastJson.Serialize(new { Enabled = General.Get<bool>("EnableMultiRbx"), Status = RobloxSingletonService.StatusText, TotalClosed = RobloxSingletonService.TotalClosed, MutexHeld = RobloxSingletonService.MutexHeld }), true);

            if (Method.Equals("MultiRoblox/Toggle", StringComparison.OrdinalIgnoreCase))
            {
                bool next = !General.Get<bool>("EnableMultiRbx");
                General.Set("EnableMultiRbx", next ? "true" : "false");
                UpdateMultiRoblox();
                return Reply(Utilities.FastJson.Serialize(new { Enabled = next, Status = RobloxSingletonService.StatusText }), true);
            }

            if (Method.Equals("Roblox/HideWindows", StringComparison.OrdinalIgnoreCase) || Method.Equals("HideWindows", StringComparison.OrdinalIgnoreCase))
            {
                var (hidden, total) = RobloxWindowService.HideAllWindows();
                return Reply(Utilities.FastJson.Serialize(new { HiddenCount = hidden, TotalClients = total, AreHidden = true }), true);
            }

            if (Method.Equals("Roblox/ShowWindows", StringComparison.OrdinalIgnoreCase) || Method.Equals("ShowWindows", StringComparison.OrdinalIgnoreCase))
            {
                var (shown, total) = RobloxWindowService.ShowAllWindows();
                return Reply(Utilities.FastJson.Serialize(new { ShownCount = shown, TotalClients = total, AreHidden = false }), true);
            }

            if (Method.Equals("Roblox/ToggleWindows", StringComparison.OrdinalIgnoreCase) || Method.Equals("ToggleWindows", StringComparison.OrdinalIgnoreCase))
            {
                var (nowHidden, affected) = RobloxWindowService.ToggleVisibility();
                return Reply(Utilities.FastJson.Serialize(new { AreHidden = nowHidden, AffectedCount = affected }), true);
            }

            if (Method.Equals("PotatoMode/Toggle", StringComparison.OrdinalIgnoreCase))
            {
                bool next = !General.Get<bool>("EnablePotatoMode");
                General.Set("EnablePotatoMode", next ? "true" : "false");
                ClientSettingsPatcher.PatchSettings();
                return Reply(Utilities.FastJson.Serialize(new { PotatoMode = next }), true);
            }

            if (Method.Equals("KillRoblox", StringComparison.OrdinalIgnoreCase) || Method.Equals("KillAllRoblox", StringComparison.OrdinalIgnoreCase))
            {
                this.InvokeIfRequired(() => ModernCloseRoblox());
                return Reply("Closed all Roblox processes", true);
            }

            if (Method.Equals("AutoReconnect/Status", StringComparison.OrdinalIgnoreCase))
            {
                return Reply(Utilities.FastJson.Serialize(new
                {
                    Enabled = AutoReconnectService.Instance.IsEnabled,
                    ActiveSessions = AutoReconnectService.Instance.ActiveSessionsCount,
                    TotalReconnections = AutoReconnectService.Instance.TotalReconnections
                }), true);
            }

            if (Method.Equals("AutoReconnect/Toggle", StringComparison.OrdinalIgnoreCase))
            {
                AutoReconnectService.Instance.IsEnabled = !AutoReconnectService.Instance.IsEnabled;
                return Reply(Utilities.FastJson.Serialize(new
                {
                    Enabled = AutoReconnectService.Instance.IsEnabled,
                    ActiveSessions = AutoReconnectService.Instance.ActiveSessionsCount
                }), true);
            }

            if (Method.Equals("Accounts/Export", StringComparison.OrdinalIgnoreCase) || Method.Equals("ExportAccounts", StringComparison.OrdinalIgnoreCase))
            {
                string formatStr = req.GetQuery("Format") ?? "Json";
                if (!Enum.TryParse<AccountExportFormat>(formatStr, true, out var exportFormat))
                    exportFormat = AccountExportFormat.Json;

                string? groupFilter = req.GetQuery("Group");
                var accountsToExport = GetAccountsSnapshot();
                if (!string.IsNullOrEmpty(groupFilter))
                    accountsToExport = accountsToExport.Where(a => string.Equals(a.Group, groupFilter, StringComparison.OrdinalIgnoreCase)).ToList();

                string exported = AccountExportService.ExportAccounts(accountsToExport, exportFormat);
                return Reply(exported, true);
            }

            if (string.IsNullOrEmpty(Account)) return Reply("Empty Account", false);

            bool isNumericAccountId = long.TryParse(Account, out long targetAccountId);
            Account? account = null;
            foreach (Account candidate in GetAccountsSnapshot())
            {
                if (candidate == null) continue;
                if (string.Equals(candidate.Username, Account, StringComparison.OrdinalIgnoreCase) ||
                    (isNumericAccountId && candidate.UserID == targetAccountId))
                {
                    account = candidate;
                    break;
                }
            }

            if (account == null) return Reply("Invalid Account, the account's cookie may have expired and resulted in the account being logged out", false, Raw: "Invalid Account");

            if (Method == "SetProxy")
            {
                string proxy = req.GetQuery("Proxy") ?? req.Body;
                ProxyBindingService.AssignProxy(account, proxy);
                QueueDeferredSave();
                return Reply($"Proxy updated for {account.Username}", true);
            }

            if (Method == "GetProxy")
            {
                return Reply(account.Proxy ?? string.Empty, true);
            }

            if (Method == "GetCookie")
            {
                if (!WebServer.Get<bool>("AllowGetCookie")) return Reply("Method `GetCookie` not allowed", false, 401, "Method not allowed");

                return Reply(account.SecurityToken, true);
            }

            if (Method == "GetAlias") return Reply(account.Alias, true);
            if (Method == "GetDescription") return Reply(account.Description, true);

            string? fieldName = req.GetQuery("Field");
            if (Method == "GetField" && !string.IsNullOrEmpty(fieldName)) return Reply(account.GetField(fieldName), true);

            string? fieldValue = req.GetQuery("Value");
            if (Method == "SetField" && !string.IsNullOrEmpty(fieldName) && !string.IsNullOrEmpty(fieldValue))
            {
                if (!WebServer.Get<bool>("AllowAccountEditing")) return Reply("Method `SetField` not allowed", false, 401, "Method not allowed");

                account.SetField(fieldName, fieldValue);
                QueueDeferredSave();

                return Reply($"Set Field {fieldName} to {fieldValue} for {account.Username}", true);
            }
            if (Method == "RemoveField" && !string.IsNullOrEmpty(fieldName))
            {
                if (!WebServer.Get<bool>("AllowAccountEditing")) return Reply("Method `RemoveField` not allowed", false, 401, "Method not allowed");

                account.RemoveField(fieldName);
                QueueDeferredSave();

                return Reply($"Removed Field {fieldName} from {account.Username}", true);
            }

            if (Method == "SetAlias" && !string.IsNullOrEmpty(Body))
            {
                if (!WebServer.Get<bool>("AllowAccountEditing")) return Reply("Method `SetAlias` not allowed", false, Raw: "Method not allowed");

                account.Alias = Body;
                UpdateAccountView(account);
                QueueDeferredSave();

                return Reply($"Set Alias of {account.Username} to {Body}", true);
            }
            if (Method == "SetDescription" && !string.IsNullOrEmpty(Body))
            {
                if (!WebServer.Get<bool>("AllowAccountEditing")) return Reply("Method `SetDescription` not allowed", false, Raw: "Method not allowed");

                account.Description = Body;
                UpdateAccountView(account);
                QueueDeferredSave();

                return Reply($"Set Description of {account.Username} to {Body}", true);
            }
            if (Method == "AppendDescription" && !string.IsNullOrEmpty(Body))
            {
                if (!WebServer.Get<bool>("AllowAccountEditing")) return V2 ? WebServerResponse("Method `AppendDescription` not allowed", false) : "Method not allowed";

                account.Description += Body;
                UpdateAccountView(account);
                QueueDeferredSave();

                return Reply($"Appended Description of {account.Username} with {Body}", true);
            }

            if (Method == "LaunchAccount")
            {
                if (!WebServer.Get<bool>("AllowLaunchAccount")) return Reply("Method `LaunchAccount` not allowed", false, 401, "Method not allowed");

                bool ValidPlaceId = long.TryParse(req.GetQuery("PlaceId"), out long PlaceId); if (!ValidPlaceId) return Reply("Invalid PlaceId provided", false, Raw: "Invalid PlaceId");

                string JobID = req.GetQuery("JobId") ?? string.Empty;
                string? FollowUser = req.GetQuery("FollowUser");
                string? JoinVIP = req.GetQuery("JoinVIP");

                await account.JoinServer(PlaceId, JobID, FollowUser == "true", JoinVIP == "true").ConfigureAwait(false);

                return Reply($"Launched {Account} to {PlaceId}", true);
            }

            if (Method == "FollowUser")
            {
                if (!WebServer.Get<bool>("AllowLaunchAccount")) return Reply("Method `FollowUser` not allowed", false, 401, "Method not allowed");

                string? User = req.GetQuery("Username"); if (string.IsNullOrEmpty(User)) return Reply("Invalid Username Parameter", false);

                var userLookup = await GetUserIDAsync(User).ConfigureAwait(false);
                if (!userLookup.Success)
                {
                    var Response = userLookup.Response;
                    return Reply($"[{Response?.StatusCode} {Response?.StatusDescription}] Failed to get UserId: {Response?.Content}", false);
                }
                long UserId = userLookup.UserId;

                await account.JoinServer(UserId, "", true).ConfigureAwait(false);

                return Reply($"Joining {User}'s game on {Account}", true);
            }

            // Endpoints below interact with authenticated Roblox APIs and require a valid session / CSRF token
            var csrfResult = await account.GetCSRFTokenAsync(lifetimeCancellation.Token).ConfigureAwait(false);
            if (!csrfResult.Success) return Reply("Invalid Account, the account's cookie may have expired and resulted in the account being logged out", false, Raw: "Invalid Account");
            string Token = csrfResult.Token;

            if (Method == "GetCSRFToken") return Reply(Token, true);

            if (Method == "BlockUser" && !string.IsNullOrEmpty(req.GetQuery("UserId")))
                try
                {
                    var Res = await account.BlockUserIdAsync(req.GetQuery("UserId") ?? string.Empty, cancellationToken: lifetimeCancellation.Token).ConfigureAwait(false);

                    return Reply(Res?.Content ?? string.Empty, Res?.IsSuccessful == true, Res != null ? (int)Res.StatusCode : 500);
                }
                catch (Exception x) { return Reply(x.Message, false, 500); }
            if (Method == "UnblockUser" && !string.IsNullOrEmpty(req.GetQuery("UserId")))
                try
                {
                    var Res = await account.UnblockUserIdAsync(req.GetQuery("UserId") ?? string.Empty, cancellationToken: lifetimeCancellation.Token).ConfigureAwait(false);

                    return Reply(Res?.Content ?? string.Empty, Res?.IsSuccessful == true, Res != null ? (int)Res.StatusCode : 500);
                }
                catch (Exception x) { return Reply(x.Message, false, 500); }
            if (Method == "GetBlockedList") try
                {
                    var Res = await account.GetBlockedListAsync(lifetimeCancellation.Token).ConfigureAwait(false);

                    return Reply(Res?.Content ?? string.Empty, Res?.IsSuccessful == true, Res != null ? (int)Res.StatusCode : 500);
                }
                catch (Exception x) { return Reply(x.Message, false, 500); }
            if (Method == "UnblockEveryone")
            {
                (bool Success, string Message) unblockResult =
                    await account.UnblockEveryoneAsync(lifetimeCancellation.Token).ConfigureAwait(false);
                return Reply(unblockResult.Message, unblockResult.Success);
            }

            if (Method == "SetServer" && !string.IsNullOrEmpty(req.GetQuery("PlaceId")) && !string.IsNullOrEmpty(req.GetQuery("JobId")))
            {
                if (!long.TryParse(req.GetQuery("PlaceId"), out long setServerPlaceId))
                    return Reply("Invalid PlaceId provided", false, 400);

                string? setJobId = req.GetQuery("JobId");
                if (string.IsNullOrEmpty(setJobId)) return Reply("JobId parameter required", false, 400);
                var (Success, Message) = await account.SetServerAsync(setServerPlaceId, setJobId).ConfigureAwait(false);

                return Reply(Message, Success);
            }

            if (Method == "SetRecommendedServer")
            {
                int attempts = 0;

                ServerData[] serversSnapshot;
                lock (CachedServers)
                {
                    serversSnapshot = CachedServers.ToArray();
                }

                for (int i = serversSnapshot.Length - 1; i >= 0; i--)
                {
                    if (attempts > 10)
                        return Reply("Too many failed attempts", false);

                    ServerData server = serversSnapshot[i];
                    if (server == null || string.IsNullOrEmpty(server.id)) continue;

                    lock (AttemptedJoins)
                    {
                        if (AttemptedJoins.Contains(server.id)) continue;
                        if (AttemptedJoins.Count > 100) AttemptedJoins.Clear();
                        AttemptedJoins.Add(server.id);
                    }

                    attempts++;

                    long targetPlaceId = (!string.IsNullOrEmpty(req.GetQuery("PlaceId")) && long.TryParse(req.GetQuery("PlaceId"), out var reqPid))
                        ? reqPid
                        : (long.TryParse(CurrentPlaceId, out var pid) ? pid : 0);

                    var (iSuccess, iMessage) = await account.SetServerAsync(targetPlaceId, server.id).ConfigureAwait(false);

                    if (iSuccess)
                        return Reply(iMessage, true);
                }

                return Reply("Failed to find a recommended server", false);
            }

            if (Method == "SetAvatar" && !string.IsNullOrWhiteSpace(Body))
            {
                await account.SetAvatarAsync(Body, lifetimeCancellation.Token).ConfigureAwait(false);

                return Reply($"Attempting to set avatar of {account.Username} to {Body}", true);
            }

            return Reply("404 not found", false, 404);
        }
    }
}
