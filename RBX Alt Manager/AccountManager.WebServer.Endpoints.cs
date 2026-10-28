#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Shiro.Classes;
using Shiro.Services;

namespace Shiro
{
    public partial class AccountManager
    {
        private async Task<string> HandleApiEndpointAsync(
            ApiRequestContext req,
            string Method,
            string? Account,
            string? Password,
            string Body,
            Func<string, bool, int, string?, string> Reply)
        {
            bool V2 = req.IsV2;

            if (Method == "GetWebSocketStats" || Method == "WebSocketStats")
            {
                return Reply(Utilities.FastJson.Serialize(new
                {
                    ActiveClients = KestrelWebServer.ActiveWebSocketCount,
                    Server = "Shiro Kestrel Gateway",
                    Endpoints = new[] { "/ws", "/events", "/v2/events" }
                }), true, -1, null);
            }

            if (Method == "GetAccounts")
            {
                if (!WebServer.Get<bool>("AllowGetAccounts")) return Reply("Method `GetAccounts` not allowed", false, 401, "Method not allowed");

                string? GroupFilter = req.GetQuery("Group");
                var filteredAccounts = GetAccountsSnapshot();
                if (!string.IsNullOrEmpty(GroupFilter))
                    filteredAccounts = filteredAccounts.Where(acc => acc.Group == GroupFilter).ToArray();

                string result = string.Join(",", filteredAccounts.Select(acc => acc.Username));
                return Reply(result, true, -1, result);
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

                return Reply(Utilities.FastJson.Serialize(Objects), true, -1, null);
            }

            if (Method == "ImportCookie")
            {
                string? cookieVal = req.GetQuery("Cookie");
                if (string.IsNullOrWhiteSpace(cookieVal))
                    return Reply("Cookie parameter required", false, 400, "Missing Cookie");
                Account? New = await AddAccountAsync(cookieVal, cancellationToken: lifetimeCancellation.Token).ConfigureAwait(false);

                bool Success = New != null;

                return Reply(Success ? "Cookie successfully imported" : "[ImportCookie] An error was encountered importing the cookie", Success, -1, Success ? "true" : "false");
            }

            // Global commands (do not require an Account parameter)
            if (Method.Equals("AntiAfk/Status", StringComparison.OrdinalIgnoreCase) || Method.Equals("AntiAfkStatus", StringComparison.OrdinalIgnoreCase))
                return Reply(Utilities.FastJson.Serialize(new { Enabled = AntiAfkService.IsEnabled, TapsSent = AntiAfkService.TapsSent, Status = AntiAfkService.StatusText }), true, -1, null);

            if (Method.Equals("AntiAfk/Toggle", StringComparison.OrdinalIgnoreCase) || Method.Equals("ToggleAntiAfk", StringComparison.OrdinalIgnoreCase))
            {
                AntiAfkService.Toggle();
                return Reply(Utilities.FastJson.Serialize(new { Enabled = AntiAfkService.IsEnabled, Status = AntiAfkService.StatusText }), true, -1, null);
            }

            if (Method.Equals("AntiAfk/Enable", StringComparison.OrdinalIgnoreCase))
            {
                AntiAfkService.Start();
                return Reply(Utilities.FastJson.Serialize(new { Enabled = AntiAfkService.IsEnabled, Status = AntiAfkService.StatusText }), true, -1, null);
            }

            if (Method.Equals("AntiAfk/Disable", StringComparison.OrdinalIgnoreCase))
            {
                AntiAfkService.Stop();
                return Reply(Utilities.FastJson.Serialize(new { Enabled = AntiAfkService.IsEnabled, Status = AntiAfkService.StatusText }), true, -1, null);
            }

            if (Method.Equals("Memory/Trim", StringComparison.OrdinalIgnoreCase) || Method.Equals("TrimMemory", StringComparison.OrdinalIgnoreCase) || Method.Equals("CleanRAM", StringComparison.OrdinalIgnoreCase))
            {
                var (bytesSaved, count) = MemoryOptimizer.TrimRobloxClients();
                return Reply(Utilities.FastJson.Serialize(new { BytesSaved = bytesSaved, ClientsCount = count }), true, -1, null);
            }

            if (Method.Equals("MultiRoblox/Status", StringComparison.OrdinalIgnoreCase))
                return Reply(Utilities.FastJson.Serialize(new { Enabled = General.Get<bool>("EnableMultiRbx"), Status = RobloxSingletonService.StatusText, TotalClosed = RobloxSingletonService.TotalClosed, MutexHeld = RobloxSingletonService.MutexHeld }), true, -1, null);

            if (Method.Equals("MultiRoblox/Toggle", StringComparison.OrdinalIgnoreCase))
            {
                bool next = !General.Get<bool>("EnableMultiRbx");
                General.Set("EnableMultiRbx", next ? "true" : "false");
                UpdateMultiRoblox();
                return Reply(Utilities.FastJson.Serialize(new { Enabled = next, Status = RobloxSingletonService.StatusText }), true, -1, null);
            }

            if (Method.Equals("Roblox/HideWindows", StringComparison.OrdinalIgnoreCase) || Method.Equals("HideWindows", StringComparison.OrdinalIgnoreCase))
            {
                var (hidden, total) = RobloxWindowService.HideAllWindows();
                return Reply(Utilities.FastJson.Serialize(new { HiddenCount = hidden, TotalClients = total, AreHidden = true }), true, -1, null);
            }

            if (Method.Equals("Roblox/ShowWindows", StringComparison.OrdinalIgnoreCase) || Method.Equals("ShowWindows", StringComparison.OrdinalIgnoreCase))
            {
                var (shown, total) = RobloxWindowService.ShowAllWindows();
                return Reply(Utilities.FastJson.Serialize(new { ShownCount = shown, TotalClients = total, AreHidden = false }), true, -1, null);
            }

            if (Method.Equals("Roblox/ToggleWindows", StringComparison.OrdinalIgnoreCase) || Method.Equals("ToggleWindows", StringComparison.OrdinalIgnoreCase))
            {
                var (nowHidden, affected) = RobloxWindowService.ToggleVisibility();
                return Reply(Utilities.FastJson.Serialize(new { AreHidden = nowHidden, AffectedCount = affected }), true, -1, null);
            }

            if (Method.Equals("PotatoMode/Toggle", StringComparison.OrdinalIgnoreCase))
            {
                bool next = !General.Get<bool>("EnablePotatoMode");
                General.Set("EnablePotatoMode", next ? "true" : "false");
                ClientSettingsPatcher.PatchSettings();
                return Reply(Utilities.FastJson.Serialize(new { PotatoMode = next }), true, -1, null);
            }

            if (Method.Equals("KillRoblox", StringComparison.OrdinalIgnoreCase) || Method.Equals("KillAllRoblox", StringComparison.OrdinalIgnoreCase))
            {
                this.InvokeIfRequired(() => ModernCloseRoblox());
                return Reply("Closed all Roblox processes", true, -1, null);
            }

            if (Method.Equals("AutoReconnect/Status", StringComparison.OrdinalIgnoreCase))
            {
                return Reply(Utilities.FastJson.Serialize(new
                {
                    Enabled = AutoReconnectService.Instance.IsEnabled,
                    ActiveSessions = AutoReconnectService.Instance.ActiveSessionsCount,
                    TotalReconnections = AutoReconnectService.Instance.TotalReconnections
                }), true, -1, null);
            }

            if (Method.Equals("AutoReconnect/Toggle", StringComparison.OrdinalIgnoreCase))
            {
                AutoReconnectService.Instance.IsEnabled = !AutoReconnectService.Instance.IsEnabled;
                return Reply(Utilities.FastJson.Serialize(new
                {
                    Enabled = AutoReconnectService.Instance.IsEnabled,
                    ActiveSessions = AutoReconnectService.Instance.ActiveSessionsCount
                }), true, -1, null);
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
                return Reply(exported, true, -1, null);
            }

            if (string.IsNullOrEmpty(Account)) return Reply("Empty Account", false, -1, null);

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

            if (account == null) return Reply("Invalid Account, the account's cookie may have expired and resulted in the account being logged out", false, -1, "Invalid Account");

            if (Method == "SetProxy")
            {
                string proxy = req.GetQuery("Proxy") ?? req.Body;
                ProxyBindingService.AssignProxy(account, proxy);
                QueueDeferredSave();
                return Reply($"Proxy updated for {account.Username}", true, -1, null);
            }

            if (Method == "GetProxy")
            {
                return Reply(account.Proxy ?? string.Empty, true, -1, null);
            }

            if (Method == "GetCookie")
            {
                if (!WebServer.Get<bool>("AllowGetCookie")) return Reply("Method `GetCookie` not allowed", false, 401, "Method not allowed");

                return Reply(account.SecurityToken, true, -1, null);
            }

            if (Method == "GetAlias") return Reply(account.Alias, true, -1, null);
            if (Method == "GetDescription") return Reply(account.Description, true, -1, null);

            string? fieldName = req.GetQuery("Field");
            if (Method == "GetField" && !string.IsNullOrEmpty(fieldName)) return Reply(account.GetField(fieldName), true, -1, null);

            string? fieldValue = req.GetQuery("Value");
            if (Method == "SetField" && !string.IsNullOrEmpty(fieldName) && !string.IsNullOrEmpty(fieldValue))
            {
                if (!WebServer.Get<bool>("AllowAccountEditing")) return Reply("Method `SetField` not allowed", false, 401, "Method not allowed");

                account.SetField(fieldName, fieldValue);
                QueueDeferredSave();

                return Reply($"Set Field {fieldName} to {fieldValue} for {account.Username}", true, -1, null);
            }
            if (Method == "RemoveField" && !string.IsNullOrEmpty(fieldName))
            {
                if (!WebServer.Get<bool>("AllowAccountEditing")) return Reply("Method `RemoveField` not allowed", false, 401, "Method not allowed");

                account.RemoveField(fieldName);
                QueueDeferredSave();

                return Reply($"Removed Field {fieldName} from {account.Username}", true, -1, null);
            }

            if (Method == "SetAlias" && !string.IsNullOrEmpty(Body))
            {
                if (!WebServer.Get<bool>("AllowAccountEditing")) return Reply("Method `SetAlias` not allowed", false, -1, "Method not allowed");

                account.Alias = Body;
                UpdateAccountView(account);
                QueueDeferredSave();

                return Reply($"Set Alias of {account.Username} to {Body}", true, -1, null);
            }
            if (Method == "SetDescription" && !string.IsNullOrEmpty(Body))
            {
                if (!WebServer.Get<bool>("AllowAccountEditing")) return Reply("Method `SetDescription` not allowed", false, -1, "Method not allowed");

                account.Description = Body;
                UpdateAccountView(account);
                QueueDeferredSave();

                return Reply($"Set Description of {account.Username} to {Body}", true, -1, null);
            }
            if (Method == "AppendDescription" && !string.IsNullOrEmpty(Body))
            {
                if (!WebServer.Get<bool>("AllowAccountEditing")) return V2 ? WebServerResponse("Method `AppendDescription` not allowed", false) : "Method not allowed";

                account.Description += Body;
                UpdateAccountView(account);
                QueueDeferredSave();

                return Reply($"Appended Description of {account.Username} with {Body}", true, -1, null);
            }

            if (Method == "LaunchAccount")
            {
                if (!WebServer.Get<bool>("AllowLaunchAccount")) return Reply("Method `LaunchAccount` not allowed", false, 401, "Method not allowed");

                bool ValidPlaceId = long.TryParse(req.GetQuery("PlaceId"), out long PlaceId); if (!ValidPlaceId) return Reply("Invalid PlaceId provided", false, -1, "Invalid PlaceId");

                string JobID = req.GetQuery("JobId") ?? string.Empty;
                string? FollowUser = req.GetQuery("FollowUser");
                string? JoinVIP = req.GetQuery("JoinVIP");

                await account.JoinServer(PlaceId, JobID, FollowUser == "true", JoinVIP == "true").ConfigureAwait(false);

                return Reply($"Launched {Account} to {PlaceId}", true, -1, null);
            }

            if (Method == "FollowUser")
            {
                if (!WebServer.Get<bool>("AllowLaunchAccount")) return Reply("Method `FollowUser` not allowed", false, 401, "Method not allowed");

                string? User = req.GetQuery("Username"); if (string.IsNullOrEmpty(User)) return Reply("Invalid Username Parameter", false, -1, null);

                var userLookup = await GetUserIDAsync(User).ConfigureAwait(false);
                if (!userLookup.Success)
                {
                    var Response = userLookup.Response;
                    return Reply($"[{Response?.StatusCode} {Response?.StatusDescription}] Failed to get UserId: {Response?.Content}", false, -1, null);
                }
                long UserId = userLookup.UserId;

                await account.JoinServer(UserId, "", true).ConfigureAwait(false);

                return Reply($"Joining {User}'s game on {Account}", true, -1, null);
            }

            // Endpoints below interact with authenticated Roblox APIs and require a valid session / CSRF token
            var csrfResult = await account.GetCSRFTokenAsync(lifetimeCancellation.Token).ConfigureAwait(false);
            if (!csrfResult.Success) return Reply("Invalid Account, the account's cookie may have expired and resulted in the account being logged out", false, -1, "Invalid Account");
            string Token = csrfResult.Token;

            if (Method == "GetCSRFToken") return Reply(Token, true, -1, null);

            if (Method == "BlockUser" && !string.IsNullOrEmpty(req.GetQuery("UserId")))
                try
                {
                    var Res = await account.BlockUserIdAsync(req.GetQuery("UserId") ?? string.Empty, cancellationToken: lifetimeCancellation.Token).ConfigureAwait(false);

                    return Reply(Res?.Content ?? string.Empty, Res?.IsSuccessful == true, Res != null ? (int)Res.StatusCode : 500, null);
                }
                catch (Exception x) { return Reply(x.Message, false, 500, null); }
            if (Method == "UnblockUser" && !string.IsNullOrEmpty(req.GetQuery("UserId")))
                try
                {
                    var Res = await account.UnblockUserIdAsync(req.GetQuery("UserId") ?? string.Empty, cancellationToken: lifetimeCancellation.Token).ConfigureAwait(false);

                    return Reply(Res?.Content ?? string.Empty, Res?.IsSuccessful == true, Res != null ? (int)Res.StatusCode : 500, null);
                }
                catch (Exception x) { return Reply(x.Message, false, 500, null); }
            if (Method == "GetBlockedList") try
                {
                    var Res = await account.GetBlockedListAsync(lifetimeCancellation.Token).ConfigureAwait(false);

                    return Reply(Res?.Content ?? string.Empty, Res?.IsSuccessful == true, Res != null ? (int)Res.StatusCode : 500, null);
                }
                catch (Exception x) { return Reply(x.Message, false, 500, null); }
            if (Method == "UnblockEveryone")
            {
                (bool Success, string Message) unblockResult =
                    await account.UnblockEveryoneAsync(lifetimeCancellation.Token).ConfigureAwait(false);
                return Reply(unblockResult.Message, unblockResult.Success, -1, null);
            }

            if (Method == "SetServer" && !string.IsNullOrEmpty(req.GetQuery("PlaceId")) && !string.IsNullOrEmpty(req.GetQuery("JobId")))
            {
                if (!long.TryParse(req.GetQuery("PlaceId"), out long setServerPlaceId))
                    return Reply("Invalid PlaceId provided", false, 400, null);

                string? setJobId = req.GetQuery("JobId");
                if (string.IsNullOrEmpty(setJobId)) return Reply("JobId parameter required", false, 400, null);
                var (Success, Message) = await account.SetServerAsync(setServerPlaceId, setJobId).ConfigureAwait(false);

                return Reply(Message, Success, -1, null);
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
                        return Reply("Too many failed attempts", false, -1, null);

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
                        return Reply(iMessage, true, -1, null);
                }

                return Reply("Failed to find a recommended server", false, -1, null);
            }

            if (Method == "SetAvatar" && !string.IsNullOrWhiteSpace(Body))
            {
                await account.SetAvatarAsync(Body, lifetimeCancellation.Token).ConfigureAwait(false);

                return Reply($"Attempting to set avatar of {account.Username} to {Body}", true, -1, null);
            }

            return Reply("404 not found", false, 404, null);
        }
    }
}
