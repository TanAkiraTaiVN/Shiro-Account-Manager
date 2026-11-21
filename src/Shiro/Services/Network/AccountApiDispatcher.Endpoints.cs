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

        private static bool IsRoute(string method, string target) =>
            string.Equals(method, target, StringComparison.OrdinalIgnoreCase);

        private static bool IsConfiguredPasswordValid(bool isLocal, string wsPassword) =>
            isLocal ? WebAuthPolicy.IsPasswordConfigured(wsPassword) : WebAuthPolicy.IsStrongExternalPassword(wsPassword);

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

            if (IsRoute(method, "GetWebSocketStats") || IsRoute(method, "WebSocketStats"))
            {
                var payload = new WebSocketStatsPayload(
                    KestrelWebServer.ActiveWebSocketCount,
                    "Shiro Kestrel Gateway",
                    DefaultWebSocketEndpoints);
                return reply(JsonSerializer.Serialize(payload, ShiroAppJson.Default.WebSocketStatsPayload), true, -1, null);
            }

            if (IsRoute(method, "GetAccounts"))
            {
                if (webServer == null || !webServer.Get<bool>("AllowGetAccounts"))
                    return reply("Method `GetAccounts` not allowed", false, 401, "Method not allowed");

                string? groupFilter = req.GetQuery("Group");
                var filteredAccounts = AppServices.AccountRepository.GetAll();
                if (!string.IsNullOrEmpty(groupFilter))
                    filteredAccounts = filteredAccounts.Where(acc => string.Equals(acc.Group, groupFilter, StringComparison.OrdinalIgnoreCase)).ToArray();

                string result = string.Join(",", filteredAccounts.Select(acc => acc.Username));
                return reply(result, true, -1, result);
            }

            if (IsRoute(method, "GetAccountsJson"))
            {
                if (webServer == null || !webServer.Get<bool>("AllowGetAccounts"))
                    return reply("Method `GetAccountsJson` not allowed", false, 401, "Method not allowed");

                string? groupFilter = req.GetQuery("Group");
                bool showCookies = IsConfiguredPasswordValid(req.IsLocal, wsPassword) &&
                                   CryptoHelper.FixedTimeEqualsSecret(password ?? string.Empty, wsPassword) &&
                                   string.Equals(req.GetQuery("IncludeCookies"), "true", StringComparison.OrdinalIgnoreCase) &&
                                   webServer.Get<bool>("AllowGetCookie");

                var allAccounts = AppServices.AccountRepository.GetAll();
                List<ApiAccountJsonItem> objects = new(allAccounts.Count);
                foreach (Account acc in allAccounts)
                {
                    if (!string.IsNullOrEmpty(groupFilter) && !string.Equals(acc.Group, groupFilter, StringComparison.OrdinalIgnoreCase)) continue;

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

            if (IsRoute(method, "ImportCookie"))
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

            if (IsRoute(method, "Accounts/Export") || IsRoute(method, "ExportAccounts"))
            {
                if (webServer == null || !webServer.Get<bool>("AllowGetAccounts") || !webServer.Get<bool>("AllowGetCookie"))
                    return reply("Method `Accounts/Export` not allowed", false, 401, "Method not allowed");

                if (!IsConfiguredPasswordValid(req.IsLocal, wsPassword) || !CryptoHelper.FixedTimeEqualsSecret(password ?? string.Empty, wsPassword))
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

            if (IsRoute(method, "SetProxy"))
            {
                if (webServer == null || !webServer.Get<bool>("AllowAccountEditing"))
                    return reply("Method `SetProxy` not allowed", false, 401, "Method not allowed");

                string? queryProxy = req.GetQuery("Proxy");
                if (!req.IsLocal && !string.IsNullOrWhiteSpace(queryProxy))
                    return reply("Remote SetProxy requests must send the proxy in the POST body", false, 400, "Use POST body");

                string proxy = !string.IsNullOrWhiteSpace(body) ? body.Trim() : (queryProxy ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(proxy) && ProxyBindingService.ParseWebProxy(proxy) == null)
                    return reply("Invalid proxy format", false, 400, "Invalid proxy");

                ProxyBindingService.AssignProxy(account, proxy);
                AppServices.AccountRepository.QueueDeferredSave();
                return reply($"Proxy updated for {account.Username}", true, -1, null);
            }

            if (IsRoute(method, "GetProxy"))
            {
                string rawProxy = account.Proxy ?? string.Empty;
                bool allowRaw = webServer != null && webServer.Get<bool>("AllowGetCookie");
                return reply(allowRaw ? rawProxy : ProxyManagerService.RedactProxy(rawProxy), true, -1, null);
            }

            if (IsRoute(method, "GetCookie"))
            {
                if (webServer == null || !webServer.Get<bool>("AllowGetCookie"))
                    return reply("Method `GetCookie` not allowed", false, 401, "Method not allowed");

                return reply(account.SecurityToken, true, -1, null);
            }

            if (IsRoute(method, "GetAlias")) return reply(account.Alias, true, -1, null);
            if (IsRoute(method, "GetDescription")) return reply(account.Description, true, -1, null);

            string? fieldName = req.GetQuery("Field");
            if ((IsRoute(method, "GetField") || IsRoute(method, "SetField") || IsRoute(method, "RemoveField")) && Account.IsProtectedInternalField(fieldName))
                return reply("Protected internal field", false, 403, "Forbidden field");
            if (IsRoute(method, "GetField"))
                return string.IsNullOrEmpty(fieldName) ? reply("Field parameter required", false, 400, "Missing Field") : reply(account.GetField(fieldName), true, -1, null);

            string? fieldValue = req.GetQuery("Value");
            if (IsRoute(method, "SetField"))
            {
                if (CheckAccountEditingDenied(webServer, method, reply) is string editErr) return editErr;
                if (string.IsNullOrEmpty(fieldName) || string.IsNullOrEmpty(fieldValue))
                    return reply("Field and Value parameters required", false, 400, "Missing Field or Value");

                account.SetField(fieldName, fieldValue);
                AppServices.AccountRepository.QueueDeferredSave();
                return reply($"Set Field {fieldName} to {fieldValue} for {account.Username}", true, -1, null);
            }

            if (IsRoute(method, "RemoveField"))
            {
                if (CheckAccountEditingDenied(webServer, method, reply) is string editErr) return editErr;
                if (string.IsNullOrEmpty(fieldName))
                    return reply("Field parameter required", false, 400, "Missing Field");

                account.RemoveField(fieldName);
                AppServices.AccountRepository.QueueDeferredSave();
                return reply($"Removed Field {fieldName} from {account.Username}", true, -1, null);
            }

            if (IsRoute(method, "SetAlias"))
            {
                if (CheckAccountEditingDenied(webServer, method, reply) is string editErr) return editErr;
                if (string.IsNullOrEmpty(body))
                    return reply("Alias body required", false, 400, "Missing Alias");
                if (body.Length > Account.MaxAliasLength)
                    return reply($"Alias exceeds maximum length ({Account.MaxAliasLength} chars)", false, 400, "Alias too long");

                account.Alias = body;
                AppServices.HostUiBridge.RefreshAccountView(account);
                AppServices.AccountRepository.QueueDeferredSave();
                return reply($"Set Alias of {account.Username} to {body}", true, -1, null);
            }

            if (IsRoute(method, "SetDescription"))
            {
                if (CheckAccountEditingDenied(webServer, method, reply) is string editErr) return editErr;
                if (string.IsNullOrEmpty(body))
                    return reply("Description body required", false, 400, "Missing Description");
                if (body.Length > Account.MaxDescriptionLength)
                    return reply($"Description exceeds maximum length ({Account.MaxDescriptionLength} chars)", false, 400, "Description too long");

                account.Description = body;
                AppServices.HostUiBridge.RefreshAccountView(account);
                AppServices.AccountRepository.QueueDeferredSave();
                return reply($"Set Description of {account.Username} to {body}", true, -1, null);
            }

            if (IsRoute(method, "AppendDescription"))
            {
                if (CheckAccountEditingDenied(webServer, method, reply) is string editErr) return editErr;
                if (string.IsNullOrEmpty(body))
                    return reply("Description body required", false, 400, "Missing Description");
                if ((account.Description?.Length ?? 0) + body.Length > Account.MaxDescriptionLength)
                    return reply($"Description exceeds maximum length ({Account.MaxDescriptionLength} chars)", false, 400, "Description too long");

                account.Description += body;
                AppServices.HostUiBridge.RefreshAccountView(account);
                AppServices.AccountRepository.QueueDeferredSave();
                return reply($"Appended Description of {account.Username} with {body}", true, -1, null);
            }

            if (IsRoute(method, "LaunchAccount"))
            {
                if (webServer == null || !webServer.Get<bool>("AllowLaunchAccount"))
                    return reply("Method `LaunchAccount` not allowed", false, 401, "Method not allowed");

                if (!long.TryParse(req.GetQuery("PlaceId"), out long placeId))
                    return reply("Invalid PlaceId provided", false, -1, "Invalid PlaceId");

                string jobId = req.GetQuery("JobId") ?? string.Empty;
                bool followFlag = string.Equals(req.GetQuery("FollowUser"), "true", StringComparison.OrdinalIgnoreCase);
                bool joinVipFlag = string.Equals(req.GetQuery("JoinVIP"), "true", StringComparison.OrdinalIgnoreCase);

                var launchResult = await AppServices.HostUiBridge.ExecuteSingleLaunchFromApiAsync(
                    account, placeId, jobId, followFlag, joinVipFlag).ConfigureAwait(false);
                if (launchResult.Outcome == LaunchOutcome.Success)
                    return reply($"Launched {accountParam} to {placeId}", true, -1, null);

                string launchErr = string.IsNullOrWhiteSpace(launchResult.Message)
                    ? $"Failed to launch {accountParam} to {placeId}"
                    : launchResult.Message;
                return reply(launchErr, false, -1, launchErr);
            }

            if (IsRoute(method, "FollowUser"))
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

                var followResult = await AppServices.HostUiBridge.ExecuteSingleLaunchFromApiAsync(
                    account, userLookup.UserId, string.Empty, true, false).ConfigureAwait(false);
                if (followResult.Outcome == LaunchOutcome.Success)
                    return reply($"Joining {user}'s game on {accountParam}", true, -1, null);

                string followErr = string.IsNullOrWhiteSpace(followResult.Message)
                    ? $"Failed to follow {user} on {accountParam}"
                    : followResult.Message;
                return reply(followErr, false, -1, followErr);
            }

            // Endpoints below validate route & inputs before touching live Roblox CSRF endpoints
            if (IsRoute(method, "GetCSRFToken"))
            {
                var csrfRes = await account.GetCSRFTokenAsync().ConfigureAwait(false);
                return csrfRes.Success
                    ? reply(csrfRes.Token, true, -1, null)
                    : reply("Invalid Account, the account's cookie may have expired and resulted in the account being logged out", false, -1, "Invalid Account");
            }

            if (IsRoute(method, "BlockUser"))
            {
                if (CheckAccountEditingDenied(webServer, method, reply) is string editErr) return editErr;
                string? rawUserId = req.GetQuery("UserId");
                if (!TryParsePositiveUserId(rawUserId, out long targetUserId))
                    return reply("Invalid UserId parameter", false, 400, "Invalid UserId");
                if (await TryEnsureCsrfAsync(account, reply).ConfigureAwait(false) is string csrfErr) return csrfErr;
                try
                {
                    var res = await RobloxSocialApiService.Instance.BlockUserIdAsync(account, targetUserId.ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
                    return ReplyUpstreamResult(res, reply);
                }
                catch (Exception x)
                {
                    AppServices.Logger.Warn("[AccountApiDispatcher] BlockUser failed", x);
                    return reply(InternalErrorMessage, false, 500, null);
                }
            }

            if (IsRoute(method, "UnblockUser"))
            {
                if (CheckAccountEditingDenied(webServer, method, reply) is string editErr) return editErr;
                string? rawUserId = req.GetQuery("UserId");
                if (!TryParsePositiveUserId(rawUserId, out long targetUserId))
                    return reply("Invalid UserId parameter", false, 400, "Invalid UserId");
                if (await TryEnsureCsrfAsync(account, reply).ConfigureAwait(false) is string csrfErr) return csrfErr;
                try
                {
                    var res = await RobloxSocialApiService.Instance.UnblockUserIdAsync(account, targetUserId.ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
                    return ReplyUpstreamResult(res, reply);
                }
                catch (Exception x)
                {
                    AppServices.Logger.Warn("[AccountApiDispatcher] UnblockUser failed", x);
                    return reply(InternalErrorMessage, false, 500, null);
                }
            }

            if (IsRoute(method, "GetBlockedList"))
            {
                if (await TryEnsureCsrfAsync(account, reply).ConfigureAwait(false) is string csrfErr) return csrfErr;
                try
                {
                    var res = await RobloxSocialApiService.Instance.GetBlockedListAsync(account).ConfigureAwait(false);
                    return ReplyUpstreamResult(res, reply);
                }
                catch (Exception x)
                {
                    AppServices.Logger.Warn("[AccountApiDispatcher] GetBlockedList failed", x);
                    return reply(InternalErrorMessage, false, 500, null);
                }
            }

            if (IsRoute(method, "UnblockEveryone"))
            {
                if (CheckAccountEditingDenied(webServer, method, reply) is string editErr) return editErr;
                if (await TryEnsureCsrfAsync(account, reply).ConfigureAwait(false) is string csrfErr) return csrfErr;
                (bool success, string message) unblockResult = await account.UnblockEveryoneAsync().ConfigureAwait(false);
                return reply(unblockResult.message, unblockResult.success, -1, null);
            }

            if (IsRoute(method, "SetServer"))
            {
                if (CheckAccountEditingDenied(webServer, method, reply) is string editErr) return editErr;
                if (!long.TryParse(req.GetQuery("PlaceId"), out long setServerPlaceId) || setServerPlaceId <= 0)
                    return reply("Invalid PlaceId provided", false, 400, null);

                string? setJobId = req.GetQuery("JobId");
                if (string.IsNullOrWhiteSpace(setJobId)) return reply("JobId parameter required", false, 400, null);
                if (await TryEnsureCsrfAsync(account, reply).ConfigureAwait(false) is string csrfErr) return csrfErr;
                var (success, message) = await account.SetServerAsync(setServerPlaceId, setJobId).ConfigureAwait(false);
                return reply(message, success, -1, null);
            }

            if (IsRoute(method, "SetRecommendedServer"))
            {
                if (CheckAccountEditingDenied(webServer, method, reply) is string editErr) return editErr;
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

                var browserState = AppServices.ServerBrowserState;
                if (browserState.CachedServersPlaceId != targetPlaceId)
                    return reply("Cached servers do not match the requested PlaceId", false, 400, null);

                var cachedServers = browserState.CachedServers;
                if (cachedServers == null)
                    return reply("Failed to find a recommended server", false, 400, null);

                ServerData[] serversSnapshot;
                lock (cachedServers)
                {
                    if (browserState.CachedServersPlaceId != targetPlaceId)
                        return reply("Cached servers do not match the requested PlaceId", false, 400, null);
                    serversSnapshot = cachedServers.ToArray();
                }

                if (await TryEnsureCsrfAsync(account, reply).ConfigureAwait(false) is string csrfErr) return csrfErr;
                int attempts = 0;
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

            if (IsRoute(method, "SetAvatar"))
            {
                if (CheckAccountEditingDenied(webServer, method, reply) is string editErr) return editErr;
                if (string.IsNullOrWhiteSpace(body))
                    return reply("Avatar JSON body required", false, 400, "Missing Avatar JSON");

                bool updated = await RobloxSettingsApiService.Instance.SetAvatarAsync(account, body, suppressMissingAssetsDialog: true).ConfigureAwait(false);
                return updated
                    ? reply($"Updated avatar for {account.Username}", true, 200, null)
                    : reply($"Failed to set avatar for {account.Username}", false, 400, null);
            }

            return reply("404 not found", false, 404, null);
        }
    }
}
