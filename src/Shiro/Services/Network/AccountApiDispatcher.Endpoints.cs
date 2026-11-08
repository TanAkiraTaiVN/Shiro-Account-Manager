#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Shiro.Classes;

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

                return reply(Utilities.FastJson.Serialize(objects), true, -1, null);
            }

            if (method == "ImportCookie")
            {
                string? cookieVal = req.GetQuery("Cookie");
                if (string.IsNullOrWhiteSpace(cookieVal))
                    return reply("Cookie parameter required", false, 400, "Missing Cookie");
                Account? newAcc = await AppServices.AccountRepository.AddOrUpdateAsync(cookieVal).ConfigureAwait(false);

                bool success = newAcc != null;
                return reply(success ? "Cookie successfully imported" : "[ImportCookie] An error was encountered importing the cookie", success, -1, success ? "true" : "false");
            }

            // Global commands (do not require an Account parameter)
            if (method.Equals("AntiAfk/Status", StringComparison.OrdinalIgnoreCase) || method.Equals("AntiAfkStatus", StringComparison.OrdinalIgnoreCase))
            {
                var payload = new AntiAfkStatusPayload(AntiAfkService.IsEnabled, AntiAfkService.TapsSent, AntiAfkService.StatusText);
                return reply(JsonSerializer.Serialize(payload, ShiroAppJson.Default.AntiAfkStatusPayload), true, -1, null);
            }

            if (method.Equals("AntiAfk/Toggle", StringComparison.OrdinalIgnoreCase) || method.Equals("ToggleAntiAfk", StringComparison.OrdinalIgnoreCase))
            {
                AntiAfkService.Toggle();
                var payload = new AntiAfkTogglePayload(AntiAfkService.IsEnabled, AntiAfkService.StatusText);
                return reply(JsonSerializer.Serialize(payload, ShiroAppJson.Default.AntiAfkTogglePayload), true, -1, null);
            }

            if (method.Equals("AntiAfk/Enable", StringComparison.OrdinalIgnoreCase))
            {
                AntiAfkService.Start();
                var payload = new AntiAfkTogglePayload(AntiAfkService.IsEnabled, AntiAfkService.StatusText);
                return reply(JsonSerializer.Serialize(payload, ShiroAppJson.Default.AntiAfkTogglePayload), true, -1, null);
            }

            if (method.Equals("AntiAfk/Disable", StringComparison.OrdinalIgnoreCase))
            {
                AntiAfkService.Stop();
                var payload = new AntiAfkTogglePayload(AntiAfkService.IsEnabled, AntiAfkService.StatusText);
                return reply(JsonSerializer.Serialize(payload, ShiroAppJson.Default.AntiAfkTogglePayload), true, -1, null);
            }

            if (method.Equals("Memory/Trim", StringComparison.OrdinalIgnoreCase) || method.Equals("TrimMemory", StringComparison.OrdinalIgnoreCase) || method.Equals("CleanRAM", StringComparison.OrdinalIgnoreCase))
            {
                var (bytesSaved, count) = MemoryOptimizer.TrimRobloxClients();
                var payload = new MemoryTrimPayload(bytesSaved, count);
                return reply(JsonSerializer.Serialize(payload, ShiroAppJson.Default.MemoryTrimPayload), true, -1, null);
            }

            if (method.Equals("MultiRoblox/Status", StringComparison.OrdinalIgnoreCase))
            {
                var payload = new MultiRobloxStatusPayload(
                    general.Get<bool>("EnableMultiRbx"),
                    RobloxSingletonService.StatusText,
                    RobloxSingletonService.TotalClosed,
                    RobloxSingletonService.MutexHeld);
                return reply(JsonSerializer.Serialize(payload, ShiroAppJson.Default.MultiRobloxStatusPayload), true, -1, null);
            }

            if (method.Equals("MultiRoblox/Toggle", StringComparison.OrdinalIgnoreCase))
            {
                bool next = !general.Get<bool>("EnableMultiRbx");
                general.Set("EnableMultiRbx", next ? "true" : "false");
                AppServices.HostUiBridge.UpdateMultiRoblox();
                var payload = new MultiRobloxTogglePayload(next, RobloxSingletonService.StatusText);
                return reply(JsonSerializer.Serialize(payload, ShiroAppJson.Default.MultiRobloxTogglePayload), true, -1, null);
            }

            if (method.Equals("Roblox/HideWindows", StringComparison.OrdinalIgnoreCase) || method.Equals("HideWindows", StringComparison.OrdinalIgnoreCase))
            {
                var (hidden, total) = RobloxWindowService.HideAllWindows();
                var payload = new HideWindowsPayload(hidden, total, true);
                return reply(JsonSerializer.Serialize(payload, ShiroAppJson.Default.HideWindowsPayload), true, -1, null);
            }

            if (method.Equals("Roblox/ShowWindows", StringComparison.OrdinalIgnoreCase) || method.Equals("ShowWindows", StringComparison.OrdinalIgnoreCase))
            {
                var (shown, total) = RobloxWindowService.ShowAllWindows();
                var payload = new ShowWindowsPayload(shown, total, false);
                return reply(JsonSerializer.Serialize(payload, ShiroAppJson.Default.ShowWindowsPayload), true, -1, null);
            }

            if (method.Equals("Roblox/ToggleWindows", StringComparison.OrdinalIgnoreCase) || method.Equals("ToggleWindows", StringComparison.OrdinalIgnoreCase))
            {
                var (nowHidden, affected) = RobloxWindowService.ToggleVisibility();
                var payload = new ToggleWindowsPayload(nowHidden, affected);
                return reply(JsonSerializer.Serialize(payload, ShiroAppJson.Default.ToggleWindowsPayload), true, -1, null);
            }

            if (method.Equals("PotatoMode/Toggle", StringComparison.OrdinalIgnoreCase))
            {
                bool next = !general.Get<bool>("EnablePotatoMode");
                general.Set("EnablePotatoMode", next ? "true" : "false");
                ClientSettingsPatcher.PatchSettings();
                var payload = new PotatoModeTogglePayload(next);
                return reply(JsonSerializer.Serialize(payload, ShiroAppJson.Default.PotatoModeTogglePayload), true, -1, null);
            }

            if (method.Equals("KillRoblox", StringComparison.OrdinalIgnoreCase) || method.Equals("KillAllRoblox", StringComparison.OrdinalIgnoreCase))
            {
                AppServices.HostUiBridge.CloseAllRobloxFromUi();
                return reply("Closed all Roblox processes", true, -1, null);
            }

            if (method.Equals("AutoReconnect/Status", StringComparison.OrdinalIgnoreCase))
            {
                var payload = new AutoReconnectStatusPayload(
                    AutoReconnectService.Instance.IsEnabled,
                    AutoReconnectService.Instance.ActiveSessionsCount,
                    AutoReconnectService.Instance.TotalReconnections);
                return reply(JsonSerializer.Serialize(payload, ShiroAppJson.Default.AutoReconnectStatusPayload), true, -1, null);
            }

            if (method.Equals("AutoReconnect/Toggle", StringComparison.OrdinalIgnoreCase))
            {
                AutoReconnectService.Instance.IsEnabled = !AutoReconnectService.Instance.IsEnabled;
                var payload = new AutoReconnectTogglePayload(
                    AutoReconnectService.Instance.IsEnabled,
                    AutoReconnectService.Instance.ActiveSessionsCount);
                return reply(JsonSerializer.Serialize(payload, ShiroAppJson.Default.AutoReconnectTogglePayload), true, -1, null);
            }

            if (method.Equals("Accounts/Export", StringComparison.OrdinalIgnoreCase) || method.Equals("ExportAccounts", StringComparison.OrdinalIgnoreCase))
            {
                if (webServer == null || !webServer.Get<bool>("AllowGetAccounts") || !webServer.Get<bool>("AllowGetCookie"))
                    return reply("Method `Accounts/Export` not allowed", false, 401, "Method not allowed");

                if (string.IsNullOrEmpty(wsPassword) || wsPassword.Length < 6 || !CryptoHelper.FixedTimeEqualsSecret(password ?? string.Empty, wsPassword))
                    return reply("Invalid Password, make sure your password contains 6 or more characters", false, 401, "Invalid Password");

                string formatStr = req.GetQuery("Format") ?? "Json";
                if (!Enum.TryParse<AccountExportFormat>(formatStr, true, out var exportFormat))
                    exportFormat = AccountExportFormat.Json;

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
                string proxy = req.GetQuery("Proxy") ?? req.Body;
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
                    return v2 ? WebServerResponse("Method `AppendDescription` not allowed", false) : "Method not allowed";

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

                await account.JoinServer(placeId, jobId, followUser == "true", joinVip == "true").ConfigureAwait(false);
                return reply($"Launched {accountParam} to {placeId}", true, -1, null);
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

                await account.JoinServer(userLookup.UserId, "", true).ConfigureAwait(false);
                return reply($"Joining {user}'s game on {accountParam}", true, -1, null);
            }

            // Endpoints below interact with authenticated Roblox APIs
            var csrfResult = await account.GetCSRFTokenAsync().ConfigureAwait(false);
            if (!csrfResult.Success)
                return reply("Invalid Account, the account's cookie may have expired and resulted in the account being logged out", false, -1, "Invalid Account");

            string token = csrfResult.Token;
            if (method == "GetCSRFToken") return reply(token, true, -1, null);

            if (method == "BlockUser" && !string.IsNullOrEmpty(req.GetQuery("UserId")))
            {
                try
                {
                    var res = await account.BlockUserIdAsync(req.GetQuery("UserId") ?? string.Empty).ConfigureAwait(false);
                    return reply(res?.Content ?? string.Empty, res?.IsSuccessful == true, res != null ? (int)res.StatusCode : 500, null);
                }
                catch (Exception x)
                {
                    AppServices.Logger.Warn("[AccountApiDispatcher] BlockUser failed", x);
                    return reply(InternalErrorMessage, false, 500, null);
                }
            }

            if (method == "UnblockUser" && !string.IsNullOrEmpty(req.GetQuery("UserId")))
            {
                try
                {
                    var res = await account.UnblockUserIdAsync(req.GetQuery("UserId") ?? string.Empty).ConfigureAwait(false);
                    return reply(res?.Content ?? string.Empty, res?.IsSuccessful == true, res != null ? (int)res.StatusCode : 500, null);
                }
                catch (Exception x)
                {
                    AppServices.Logger.Warn("[AccountApiDispatcher] UnblockUser failed", x);
                    return reply(InternalErrorMessage, false, 500, null);
                }
            }

            if (method == "GetBlockedList")
            {
                try
                {
                    var res = await account.GetBlockedListAsync().ConfigureAwait(false);
                    return reply(res?.Content ?? string.Empty, res?.IsSuccessful == true, res != null ? (int)res.StatusCode : 500, null);
                }
                catch (Exception x)
                {
                    AppServices.Logger.Warn("[AccountApiDispatcher] GetBlockedList failed", x);
                    return reply(InternalErrorMessage, false, 500, null);
                }
            }

            if (method == "UnblockEveryone")
            {
                (bool success, string message) unblockResult = await account.UnblockEveryoneAsync().ConfigureAwait(false);
                return reply(unblockResult.message, unblockResult.success, -1, null);
            }

            if (method == "SetServer" && !string.IsNullOrEmpty(req.GetQuery("PlaceId")) && !string.IsNullOrEmpty(req.GetQuery("JobId")))
            {
                if (!long.TryParse(req.GetQuery("PlaceId"), out long setServerPlaceId))
                    return reply("Invalid PlaceId provided", false, 400, null);

                string? setJobId = req.GetQuery("JobId");
                if (string.IsNullOrEmpty(setJobId)) return reply("JobId parameter required", false, 400, null);
                var (success, message) = await account.SetServerAsync(setServerPlaceId, setJobId).ConfigureAwait(false);
                return reply(message, success, -1, null);
            }

            if (method == "SetRecommendedServer")
            {
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
                    long targetPlaceId = (!string.IsNullOrEmpty(req.GetQuery("PlaceId")) && long.TryParse(req.GetQuery("PlaceId"), out var reqPid))
                        ? reqPid
                        : (long.TryParse(AppServices.ServerBrowserState.CurrentPlaceId, out var pid) ? pid : 0);

                    var (iSuccess, iMessage) = await account.SetServerAsync(targetPlaceId, server.id).ConfigureAwait(false);
                    if (iSuccess) return reply(iMessage, true, -1, null);
                }

                return reply("Failed to find a recommended server", false, -1, null);
            }

            if (method == "SetAvatar" && !string.IsNullOrWhiteSpace(body))
            {
                await account.SetAvatarAsync(body).ConfigureAwait(false);
                return reply($"Attempting to set avatar of {account.Username} to {body}", true, -1, null);
            }

            return reply("404 not found", false, 404, null);
        }
    }
}
