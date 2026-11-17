#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public sealed partial class AccountApiDispatcher
    {
        private static readonly string[] DefaultWebSocketEndpoints = new[] { "/ws", "/events", "/v2/events" };

        internal static Account? ResolveTargetAccount(string? accountParam)
        {
            if (string.IsNullOrEmpty(accountParam))
                return null;

            Account? byUsername = AppServices.AccountRepository.GetByUsername(accountParam);
            if (byUsername != null)
                return byUsername;

            if (long.TryParse(accountParam, out long targetAccountId))
                return AppServices.AccountRepository.GetById(targetAccountId);

            return null;
        }

        private async Task<string> HandleApiEndpointAsync(
            ApiRequestContext req,
            string method,
            string? accountParam,
            string? password,
            string body,
            Func<string, bool, int, string?, string> reply)
        {
            bool v2 = req.IsV2;
            var webServer = AppServices.Settings.GetSection("WebServer");
            var general = AppServices.GeneralSettings;
            string wsPassword = AppServices.ServerBrowserState.WebSocketPassword ?? string.Empty;

            if (method == "GetWebSocketStats" || method == "WebSocketStats")
            {
                var payload = new WebSocketStatsPayload(
                    KestrelWebServer.ActiveWebSocketCount,
                    "Shiro Kestrel Gateway",
                    DefaultWebSocketEndpoints);
                return reply(JsonSerializer.Serialize(payload, ShiroAppJson.Default.WebSocketStatsPayload), true, -1, null);
            }

            if (method == "GetAccounts")
            {
                if (webServer == null || !webServer.Get<bool>("AllowGetAccounts"))
                    return reply("Method `GetAccounts` not allowed", false, 401, "Method not allowed");

                string? groupFilter = req.GetQuery("Group");
                var filteredAccounts = AppServices.AccountRepository.GetAll();
                if (!string.IsNullOrEmpty(groupFilter))
                    filteredAccounts = filteredAccounts.Where(acc => acc.Group == groupFilter).ToArray();

                string result = string.Join(",", filteredAccounts.Select(acc => acc.Username));
                return reply(result, true, -1, result);
            }

            if (method == "GetAccountsJson")
            {
                if (webServer == null || !webServer.Get<bool>("AllowGetAccounts"))
                    return reply("Method `GetAccountsJson` not allowed", false, 401, "Method not allowed");

                string? groupFilter = req.GetQuery("Group");
                bool showCookies = !string.IsNullOrEmpty(wsPassword) && wsPassword.Length >= 6 &&
                                   CryptoHelper.FixedTimeEqualsSecret(password ?? string.Empty, wsPassword) &&
                                   req.GetQuery("IncludeCookies") == "true" && webServer.Get<bool>("AllowGetCookie");

                var allAccounts = AppServices.AccountRepository.GetAll();
                List<ApiAccountJsonItem> objects = new(allAccounts.Count);
                foreach (Account acc in allAccounts)
                {
                    if (!string.IsNullOrEmpty(groupFilter) && acc.Group != groupFilter) continue;

                    var accountObject = new ApiAccountJsonItem(
                        acc.Username,
                        acc.UserID,
                        acc.Alias,
                        acc.Description,
                        acc.Group,
                        acc.CSRFToken,
                        acc.LastUse.ToRobloxTick(),
                        showCookies ? acc.SecurityToken : null,
                        acc.GetPublicFieldsSnapshot());

                    objects.Add(accountObject);
                }

                return reply(FastJson.Serialize(objects), true, -1, null);
            }

            if (method == "ImportCookie")
            {
                if (webServer == null || !webServer.Get<bool>("AllowAccountEditing"))
                    return reply("Method `ImportCookie` not allowed", false, 401, "Method not allowed");

                string? cookieVal = !string.IsNullOrWhiteSpace(body) ? body : null;
                if (cookieVal == null)
                {
                    string? queryCookie = req.GetQuery("Cookie");
                    if (!string.IsNullOrWhiteSpace(queryCookie))
                    {
                        if (!req.IsLocal)
                            return reply("Remote ImportCookie requests must send the cookie in the POST body", false, 400, "Use POST body");
                        cookieVal = queryCookie;
                    }
                }
                if (string.IsNullOrWhiteSpace(cookieVal))
                    return reply("Cookie parameter required", false, 400, "Missing Cookie");
                Account? newAcc = await AppServices.AccountRepository.AddOrUpdateAsync(cookieVal).ConfigureAwait(false);

                bool success = newAcc != null;
                return reply(success ? "Cookie successfully imported" : "[ImportCookie] An error was encountered importing the cookie", success, -1, success ? "true" : "false");
            }

            // Global commands (do not require an Account parameter)
            if (TryHandleGlobalSystemCommand(method, general, reply) is string globalResult)
                return globalResult;

            if (method.Equals("Accounts/Export", StringComparison.OrdinalIgnoreCase) || method.Equals("ExportAccounts", StringComparison.OrdinalIgnoreCase))
            {
                if (webServer == null || !webServer.Get<bool>("AllowGetAccounts") || !webServer.Get<bool>("AllowGetCookie"))
                    return reply("Method `Accounts/Export` not allowed", false, 401, "Method not allowed");

                if (string.IsNullOrEmpty(wsPassword) || wsPassword.Length < 6 || !CryptoHelper.FixedTimeEqualsSecret(password ?? string.Empty, wsPassword))
                    return reply("Invalid Password, make sure your password contains 6 or more characters", false, 401, "Invalid Password");

                string formatStr = req.GetQuery("Format") ?? "Json";
                if (!Enum.TryParse<AccountExportFormat>(formatStr, true, out var exportFormat))
                    return reply("Invalid export Format parameter", false, 400, "Invalid Format");

                string? groupFilter = req.GetQuery("Group");
                var accountsToExport = AppServices.AccountRepository.GetAll();
                if (!string.IsNullOrEmpty(groupFilter))
                    accountsToExport = accountsToExport.Where(a => string.Equals(a.Group, groupFilter, StringComparison.OrdinalIgnoreCase)).ToArray();

                string exported = AccountExportService.ExportAccounts(accountsToExport, exportFormat);
                return reply(exported, true, -1, null);
            }

            if (string.IsNullOrEmpty(accountParam)) return reply("Empty Account", false, -1, null);

            Account? account = ResolveTargetAccount(accountParam);

            if (account == null)
                return reply("Invalid Account, the account's cookie may have expired and resulted in the account being logged out", false, -1, "Invalid Account");

            if (method == "SetProxy")
            {
                if (webServer == null || !webServer.Get<bool>("AllowAccountEditing"))
                    return reply("Method `SetProxy` not allowed", false, 401, "Method not allowed");

                string proxy = req.GetQuery("Proxy") ?? req.Body;
                if (!string.IsNullOrWhiteSpace(proxy) && ProxyBindingService.ParseWebProxy(proxy) == null)
                    return reply("Invalid proxy format", false, 400, "Invalid proxy");

                ProxyBindingService.AssignProxy(account, proxy);
                AppServices.AccountRepository.QueueDeferredSave();
                return reply($"Proxy updated for {account.Username}", true, -1, null);
            }

            if (method == "GetProxy") return reply(account.Proxy ?? string.Empty, true, -1, null);

            if (method == "GetCookie")
            {
                if (webServer == null || !webServer.Get<bool>("AllowGetCookie"))
                    return reply("Method `GetCookie` not allowed", false, 401, "Method not allowed");

                return reply(account.SecurityToken, true, -1, null);
            }

            if (method == "GetAlias") return reply(account.Alias, true, -1, null);
            if (method == "GetDescription") return reply(account.Description, true, -1, null);

            string? fieldName = req.GetQuery("Field");
            if ((method is "GetField" or "SetField" or "RemoveField") && Account.IsProtectedInternalField(fieldName))
                return reply("Protected internal field", false, 403, "Forbidden field");
            if (method == "GetField" && !string.IsNullOrEmpty(fieldName)) return reply(account.GetField(fieldName), true, -1, null);

            string? fieldValue = req.GetQuery("Value");
            if (method == "SetField" && !string.IsNullOrEmpty(fieldName) && !string.IsNullOrEmpty(fieldValue))
            {
                if (webServer == null || !webServer.Get<bool>("AllowAccountEditing"))
                    return reply("Method `SetField` not allowed", false, 401, "Method not allowed");

                account.SetField(fieldName, fieldValue);
                AppServices.AccountRepository.QueueDeferredSave();
                return reply($"Set Field {fieldName} to {fieldValue} for {account.Username}", true, -1, null);
            }

            if (method == "RemoveField" && !string.IsNullOrEmpty(fieldName))
            {
                if (webServer == null || !webServer.Get<bool>("AllowAccountEditing"))
                    return reply("Method `RemoveField` not allowed", false, 401, "Method not allowed");

                account.RemoveField(fieldName);
                AppServices.AccountRepository.QueueDeferredSave();
                return reply($"Removed Field {fieldName} from {account.Username}", true, -1, null);
            }

            if (method == "SetAlias" && !string.IsNullOrEmpty(body))
            {
                if (webServer == null || !webServer.Get<bool>("AllowAccountEditing"))
                    return reply("Method `SetAlias` not allowed", false, -1, "Method not allowed");

                account.Alias = body;
                AppServices.HostUiBridge.RefreshAccountView(account);
                AppServices.AccountRepository.QueueDeferredSave();
                return reply($"Set Alias of {account.Username} to {body}", true, -1, null);
            }

            if (method == "SetDescription" && !string.IsNullOrEmpty(body))
            {
                if (webServer == null || !webServer.Get<bool>("AllowAccountEditing"))
                    return reply("Method `SetDescription` not allowed", false, -1, "Method not allowed");

                account.Description = body;
                AppServices.HostUiBridge.RefreshAccountView(account);
                AppServices.AccountRepository.QueueDeferredSave();
                return reply($"Set Description of {account.Username} to {body}", true, -1, null);
            }

            if (method == "AppendDescription" && !string.IsNullOrEmpty(body))
            {
                if (webServer == null || !webServer.Get<bool>("AllowAccountEditing"))
                    return reply("Method `AppendDescription` not allowed", false, 401, "Method not allowed");

                account.Description += body;
                AppServices.HostUiBridge.RefreshAccountView(account);
                AppServices.AccountRepository.QueueDeferredSave();
                return reply($"Appended Description of {account.Username} with {body}", true, -1, null);
            }

            if (method == "LaunchAccount")
            {
                if (webServer == null || !webServer.Get<bool>("AllowLaunchAccount"))
                    return reply("Method `LaunchAccount` not allowed", false, 401, "Method not allowed");

                bool validPlaceId = long.TryParse(req.GetQuery("PlaceId"), out long placeId);
                if (!validPlaceId) return reply("Invalid PlaceId provided", false, -1, "Invalid PlaceId");

                string jobId = req.GetQuery("JobId") ?? string.Empty;
                string? followUser = req.GetQuery("FollowUser");
                string? joinVip = req.GetQuery("JoinVIP");

                var launchResult = await account.JoinServerResult(placeId, jobId, followUser == "true", joinVip == "true").ConfigureAwait(false);
                if (launchResult.Outcome == LaunchOutcome.Success)
                    return reply($"Launched {accountParam} to {placeId}", true, -1, null);

                string launchErr = string.IsNullOrWhiteSpace(launchResult.Message)
                    ? $"Failed to launch {accountParam} to {placeId}"
                    : launchResult.Message;
                return reply(launchErr, false, -1, launchErr);
            }

            if (method == "FollowUser")
            {
                if (webServer == null || !webServer.Get<bool>("AllowLaunchAccount"))
                    return reply("Method `FollowUser` not allowed", false, 401, "Method not allowed");

                string? user = req.GetQuery("Username");
                if (string.IsNullOrEmpty(user)) return reply("Invalid Username Parameter", false, -1, null);

                var userLookup = await AppServices.AccountRepository.LookupUserIdAsync(user).ConfigureAwait(false);
                if (!userLookup.Success)
                {
                    var response = userLookup.Response;
                    return reply($"[{response?.StatusCode} {response?.StatusDescription}] Failed to get UserId: {response?.Content}", false, -1, null);
                }

                var followResult = await account.JoinServerResult(userLookup.UserId, "", true).ConfigureAwait(false);
                if (followResult.Outcome == LaunchOutcome.Success)
                    return reply($"Joining {user}'s game on {accountParam}", true, -1, null);

                string followErr = string.IsNullOrWhiteSpace(followResult.Message)
                    ? $"Failed to follow {user} on {accountParam}"
                    : followResult.Message;
                return reply(followErr, false, -1, followErr);
            }

            // Endpoints below validate route & inputs before touching live Roblox CSRF endpoints
            if (method == "GetCSRFToken")
            {
                var csrfRes = await account.GetCSRFTokenAsync().ConfigureAwait(false);
                return csrfRes.Success
                    ? reply(csrfRes.Token, true, -1, null)
                    : reply("Invalid Account, the account's cookie may have expired and resulted in the account being logged out", false, -1, "Invalid Account");
            }

            if (method == "BlockUser")
            {
                string? rawUserId = req.GetQuery("UserId");
                if (!TryParsePositiveUserId(rawUserId, out long targetUserId))
                    return reply("Invalid UserId parameter", false, 400, "Invalid UserId");
                if (await TryEnsureCsrfAsync(account, reply).ConfigureAwait(false) is string csrfErr) return csrfErr;
                try
                {
                    var res = await account.BlockUserIdAsync(targetUserId.ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
                    return ReplyUpstreamResult(res, reply);
                }
                catch (Exception x)
                {
                    AppServices.Logger.Warn("[AccountApiDispatcher] BlockUser failed", x);
                    return reply(InternalErrorMessage, false, 500, null);
                }
            }

            if (method == "UnblockUser")
            {
                string? rawUserId = req.GetQuery("UserId");
                if (!TryParsePositiveUserId(rawUserId, out long targetUserId))
                    return reply("Invalid UserId parameter", false, 400, "Invalid UserId");
                if (await TryEnsureCsrfAsync(account, reply).ConfigureAwait(false) is string csrfErr) return csrfErr;
                try
                {
                    var res = await account.UnblockUserIdAsync(targetUserId.ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
                    return ReplyUpstreamResult(res, reply);
                }
                catch (Exception x)
                {
                    AppServices.Logger.Warn("[AccountApiDispatcher] UnblockUser failed", x);
                    return reply(InternalErrorMessage, false, 500, null);
                }
            }

            if (method == "GetBlockedList")
            {
                if (await TryEnsureCsrfAsync(account, reply).ConfigureAwait(false) is string csrfErr) return csrfErr;
                try
                {
                    var res = await account.GetBlockedListAsync().ConfigureAwait(false);
                    return ReplyUpstreamResult(res, reply);
                }
                catch (Exception x)
                {
                    AppServices.Logger.Warn("[AccountApiDispatcher] GetBlockedList failed", x);
                    return reply(InternalErrorMessage, false, 500, null);
                }
            }

            if (method == "UnblockEveryone")
            {
                if (await TryEnsureCsrfAsync(account, reply).ConfigureAwait(false) is string csrfErr) return csrfErr;
                (bool success, string message) unblockResult = await account.UnblockEveryoneAsync().ConfigureAwait(false);
                return reply(unblockResult.message, unblockResult.success, -1, null);
            }

            if (method == "SetServer")
            {
                if (!long.TryParse(req.GetQuery("PlaceId"), out long setServerPlaceId) || setServerPlaceId <= 0)
                    return reply("Invalid PlaceId provided", false, 400, null);

                string? setJobId = req.GetQuery("JobId");
                if (string.IsNullOrWhiteSpace(setJobId)) return reply("JobId parameter required", false, 400, null);
                if (await TryEnsureCsrfAsync(account, reply).ConfigureAwait(false) is string csrfErr) return csrfErr;
                var (success, message) = await account.SetServerAsync(setServerPlaceId, setJobId).ConfigureAwait(false);
                return reply(message, success, -1, null);
            }

            if (method == "SetRecommendedServer")
            {
                string? rawPlaceId = req.GetQuery("PlaceId");
                long targetPlaceId;
                if (!string.IsNullOrEmpty(rawPlaceId))
                {
                    if (!long.TryParse(rawPlaceId, out targetPlaceId) || targetPlaceId <= 0)
                        return reply("Invalid PlaceId provided", false, 400, null);
                }
                else if ((AppServices.ServerBrowserState as ServerBrowserStateService)?.IsPlaceIdExplicitlyCleared == true)
                {
                    targetPlaceId = 0;
                }
                else
                {
                    targetPlaceId = long.TryParse(AppServices.ServerBrowserState.CurrentPlaceId, out var pid) ? pid : 0;
                }

                if (targetPlaceId <= 0)
                    return reply("Invalid PlaceId provided", false, 400, null);

                if (await TryEnsureCsrfAsync(account, reply).ConfigureAwait(false) is string csrfErr) return csrfErr;
                int attempts = 0;
                var cachedServers = AppServices.ServerBrowserState.CachedServers;
                ServerData[] serversSnapshot;
                if (cachedServers == null)
                {
                    serversSnapshot = Array.Empty<ServerData>();
                }
                else
                {
                    lock (cachedServers)
                    {
                        serversSnapshot = cachedServers.ToArray();
                    }
                }

                for (int i = serversSnapshot.Length - 1; i >= 0; i--)
                {
                    if (attempts > 10) return reply("Too many failed attempts", false, -1, null);

                    ServerData server = serversSnapshot[i];
                    if (server == null || string.IsNullOrEmpty(server.id)) continue;

                    lock (AttemptedJoins)
                    {
                        if (AttemptedJoins.Contains(server.id)) continue;
                        if (AttemptedJoins.Count > 100) AttemptedJoins.Clear();
                        AttemptedJoins.Add(server.id);
                    }

                    attempts++;
                    var (iSuccess, iMessage) = await account.SetServerAsync(targetPlaceId, server.id).ConfigureAwait(false);
                    if (iSuccess) return reply(iMessage, true, -1, null);
                }

                return reply("Failed to find a recommended server", false, -1, null);
            }

            if (method == "SetAvatar" && !string.IsNullOrWhiteSpace(body))
            {
                if (await TryEnsureCsrfAsync(account, reply).ConfigureAwait(false) is string csrfErr) return csrfErr;
                await account.SetAvatarAsync(body).ConfigureAwait(false);
                return reply($"Attempting to set avatar of {account.Username} to {body}", true, -1, null);
            }

            return reply("404 not found", false, 404, null);
        }
    }
}
