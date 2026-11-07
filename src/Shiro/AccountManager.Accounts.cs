using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;
using Shiro.Http;
using Shiro.Services;

namespace Shiro
{
    public partial class AccountManager
    {
        private static void ApplyAccountValidityCore(Account account, bool isValid)
        {
            account.Valid = isValid;
            if (isValid)
                account.AccountStatusIssue = string.Empty;
            Shiro.AvaloniaUI.Views.MainWindow.Instance?.UpdateAccountValidity(account, isValid);
        }

        public void UpdateAccountValidity(Account account, bool isValid)
        {
            if (account == null) return;
            ApplyAccountValidityCore(account, isValid);
        }

        public static void NotifyAccountValidity(Account account, bool isValid)
        {
            if (account == null) return;
            if (Instance != null)
            {
                Instance.UpdateAccountValidity(account, isValid);
            }
            else
            {
                ApplyAccountValidityCore(account, isValid);
            }
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> ResolvedUserIdsCache =
            new(StringComparer.OrdinalIgnoreCase);
        internal static void ClearResolvedUserIdsCacheForTesting() => ResolvedUserIdsCache.Clear();

        public static async Task<(bool Success, long UserId, RestResponse? Response)> GetUserIDAsync(string Username, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(Username))
                return (false, -1, null);

            string trimmedName = Username.Trim();

            if (ResolvedUserIdsCache.TryGetValue(trimmedName, out long cachedUserId) && cachedUserId > 0)
                return (true, cachedUserId, null);

            lock (AccountListLock)
            {
                if (_accounts != null)
                {
                    Account? knownAccount = _accounts.FirstOrDefault(a =>
                        a != null && a.UserID > 0 &&
                        string.Equals(a.Username, trimmedName, StringComparison.OrdinalIgnoreCase));
                    if (knownAccount != null)
                    {
                        ResolvedUserIdsCache[trimmedName] = knownAccount.UserID;
                        return (true, knownAccount.UserID, null);
                    }
                }
            }

            try
            {
                RestRequest request = LastValidAccount?.MakeRequest("v1/usernames/users", Method.Post) ?? new RestRequest("v1/usernames/users", Method.Post);
                request.AddJsonBody(new UsernamesLookupPayload(new string[] { trimmedName }));

                RestResponse response = await AppServices.RobloxApi.Users.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);

                if (response != null && response.StatusCode == HttpStatusCode.OK && !string.IsNullOrEmpty(response.Content))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(response.Content);
                        if (doc.RootElement.TryGetProperty("data", out var dataArr) && dataArr.GetArrayLength() >= 1)
                        {
                            long userId = dataArr[0].TryGetProperty("id", out var idProp) ? idProp.GetInt64() : -1;
                            if (userId > 0)
                            {
                                if (ResolvedUserIdsCache.Count > 2000)
                                    ResolvedUserIdsCache.Clear();
                                ResolvedUserIdsCache[trimmedName] = userId;
                                return (true, userId, response);
                            }
                        }
                    }
                    catch (System.Text.Json.JsonException ex)
                    {
                        Program.Logger?.Debug($"Parse GetUserIDAsync JSON failed: {ex.Message}");
                    }
                }

                return (false, -1, response);
            }
            catch (Exception ex)
            {
                Program.Logger.Debug($"GetUserIDAsync exception for {Username}: {ex.Message}");
                return (false, -1, null);
            }
        }

        public void UpdateAccountView(Account? account = null) =>
            Shiro.AvaloniaUI.Views.MainWindow.Instance?.RefreshLiveProcessStatus();

        public static Account? AddAccount(string SecurityToken, string Password = "", string? AccountJSON = null, bool saveImmediate = true)
        {
            Account account = new Account(SecurityToken, AccountJSON);
            return RegisterValidatedAccount(account, account.SecurityToken, Password, saveImmediate);
        }

        public static async Task<Account?> AddAccountAsync(
            string SecurityToken,
            string Password = "",
            string? AccountJSON = null,
            bool saveImmediate = true,
            CancellationToken cancellationToken = default,
            RobloxHbaService.CaptureSnapshot? hbaCapture = null)
        {
            Account account = await Account.CreateAsync(SecurityToken, AccountJSON, cancellationToken, hbaCapture).ConfigureAwait(false);
            return RegisterValidatedAccount(account, account.SecurityToken, Password, saveImmediate);
        }

        internal static Account? RegisterValidatedAccount(Account account, string SecurityToken, string Password = "", bool saveImmediate = true)
        {
            Program.Logger.Info($"[RegisterAccount] user='{account?.Username}' id={account?.UserID} valid={account?.Valid} payloadLen={Account.GetSessionTokenPayloadLength(SecurityToken)} shortLived={Account.IsShortLivedCookie(SecurityToken)}.");
            if (account != null && account.Valid)
            {
                account.Password = Password;

                Account? exists;
                lock (AccountListLock)
                    exists = _accounts.FirstOrDefault(acc => (account.UserID > 0 && acc.UserID == account.UserID) ||
                                                                (!string.IsNullOrEmpty(acc.Username) && !string.IsNullOrEmpty(account.Username) && acc.Username.Equals(account.Username, StringComparison.OrdinalIgnoreCase)));

                if (exists != null)
                {
                    exists.SecurityToken = SecurityToken;
                    account.CopyHbaAndFingerprintTo(exists);
                    if (!string.IsNullOrWhiteSpace(Password))
                        exists.Password = Password;
                    if (exists.UserID <= 0 && account.UserID > 0)
                        exists.UserID = account.UserID;
                    if (string.IsNullOrEmpty(exists.Username) && !string.IsNullOrEmpty(account.Username))
                        exists.Username = account.Username;
                    exists.LastUse = DateTime.Now;
                    exists.CSRFToken = null;
                    exists.TokenSet = DateTime.MinValue;
                    exists.Valid = true;
                    exists.AccountStatusIssue = string.Empty;
                    exists.LastAuthTicketRateLimited = false;

                    account = exists;

                    if (saveImmediate)
                    {
                        Instance?.UpdateAccountValidity(exists, true);
                    }
                }
                else
                {
                    AddAccountThreadSafe(account);

                    if (saveImmediate)
                    {
                        Instance?.RefreshView(account);
                        Instance?.UpdateAccountValidity(account, true);
                    }
                }

                if (saveImmediate)
                    SaveAccounts(true);

                return account;
            }

            return null;
        }

        public static bool RemoveAccount(Account account) =>
            account != null && RemoveAccountsFromDatabase(new[] { account }) > 0;

        public static int RemoveAccountsFromDatabase(IEnumerable<Account> accountsToRemove)
        {
            if (accountsToRemove == null) return 0;
            var toRemove = new HashSet<Account>(accountsToRemove.Where(a => a != null));
            if (toRemove.Count == 0) return 0;

            int count = 0;
            bool isNowEmpty = false;
            lock (AccountListLock)
            {
                if (_accounts != null)
                {
                    count = _accounts.RemoveAll(a => toRemove.Contains(a));
                    if (count > 0)
                        _cachedAccountsSnapshot = Array.Empty<Account>();
                    isNowEmpty = _accounts.Count == 0;
                }
                else
                {
                    isNowEmpty = true;
                }
            }

            foreach (var a in toRemove)
            {
                AutoReconnectService.Instance.UnregisterSession(a.Username);
                try { RobloxProcessService.CloseAccountRobloxProcess(a); }
                catch (Exception ex) { Program.Logger?.Debug($"Closing removed account client threw: {ex.Message}"); }
            }

            if (count > 0)
            {
                SaveAccounts(BypassRateLimit: true, BypassCountCheck: true);
                if (isNowEmpty)
                {
                    try
                    {
                        string backupFile = StoragePaths.AccountDataFile + ".backup";
                        if (File.Exists(backupFile)) File.Delete(backupFile);
                    }
                    catch (Exception ex)
                    {
                        Program.Logger?.Warn($"Failed to delete empty backup file: {ex.Message}");
                    }
                    RobloxProcessService.ClearRobloxClientSession();
                }
            }
            return count;
        }

        internal async Task RefreshStaleCookiesAsync(CancellationToken cancellationToken)
        {
            if (RobloxHbaService.IsInteractiveLoginActive)
                return;

            try
            {
                if (!await cookieRefreshGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                    return;

                try
                {
                    IReadOnlyList<Account> snapshot = GetAccountsSnapshot();
                    if (snapshot.Count == 0) return;

                    bool protectCrossNetwork = RobloxHbaService.IsVpnOrTunnelInterfaceActive() ||
                                               RobloxHbaService.IsWithinPostInteractiveLoginGracePeriod(TimeSpan.FromMinutes(5));
                    string localNetSig = protectCrossNetwork ? RobloxHbaService.GetLocalNetworkSignature() : string.Empty;

                    var candidates = snapshot
                        .Where(a => a != null && !string.IsNullOrWhiteSpace(a.SecurityToken))
                        .Where(a => a.GetField("NoCookieRefresh") != "true")
                        .Where(a => !protectCrossNetwork ||
                                    string.IsNullOrWhiteSpace(a.LastBoundIpSignature) ||
                                    localNetSig is "offline" or "unknown" ||
                                    string.Equals(a.LastBoundIpSignature, RobloxHbaService.GetNetworkSignatureForAccount(a, localNetSig),
                                        StringComparison.Ordinal))
                        .Where(a => (DateTime.Now - a.LastAttemptedRefresh).TotalHours >= 24 || a.LastAttemptedRefresh == DateTime.MinValue)
                        .OrderByDescending(a => a.Valid)
                        .ThenBy(a => a.LastAttemptedRefresh)
                        .Take(4)
                        .ToList();

                    if (candidates.Count == 0)
                        return;

                    bool anyChanged = false;
                    foreach (Account account in candidates)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        account.LastAttemptedRefresh = DateTime.Now;

                        try
                        {
                            bool valid = await RobloxRateLimitCoordinator.Instance.ExecuteWithPacingAsync(
                                RateLimitCategory.SessionCheck,
                                () => account.ValidateSessionAsync(cancellationToken),
                                minIntervalMs: 350,
                                cancellationToken: cancellationToken).ConfigureAwait(false);
                            if (valid && !account.Valid)
                            {
                                account.Valid = true;
                                anyChanged = true;
                                UpdateAccountValidity(account, true);
                            }
                        }
                        catch (Exception ex)
                        {
                            Program.Logger.Warn($"[RefreshStaleCookiesAsync] Error refreshing {account.Username}: {ex.Message}");
                        }
                    }

                    if (anyChanged)
                    {
                        SaveAccounts(true);
                    }
                }
                finally
                {
                    cookieRefreshGate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                /* canceled */
            }
            catch (Exception exception)
            {
                Program.Logger.Warn($"Automatic cookie refresh stopped early: {exception.Message}");
            }
        }

        internal async Task ImportBulkUserPassAsync(string combos, Action<string>? statusCallback = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(combos) || combos == "/UC")
                return;

            string[] comboList = combos
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);

            var activeSlotTasks = new Task<(bool Success, string Username, string Error, int WindowSlot)>?[2];
            var failedAccounts = new List<(string Username, string Error)>();
            int validCount = 0;

            for (int i = 0; i < comboList.Length; i++)
            {
                if (cancellationToken.IsCancellationRequested || lifetimeCancellation.IsCancellationRequested)
                {
                    string cancelMsg = Shiro.Services.LocalizationService.Get("AccountManager.BulkLoginStopped");
                    SetDashboardStatus(cancelMsg);
                    statusCallback?.Invoke(cancelMsg);
                    break;
                }

                string combo = comboList[i].Trim();
                if (string.IsNullOrWhiteSpace(combo)) continue;

                int separator = combo.IndexOf(':');
                if (separator <= 0) separator = combo.IndexOf('|');
                if (separator <= 0) separator = combo.IndexOf('\t');

                if (separator <= 0 || separator >= combo.Length - 1)
                    continue;

                string username = combo.Substring(0, separator).Trim();
                string password = combo.Substring(separator + 1).Trim();
                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                    continue;

                validCount++;
                int windowSlot = activeSlotTasks[0] == null ? 0 : 1;
                activeSlotTasks[windowSlot] = RunBulkUserPassLoginAsync(username, password, windowSlot, cancellationToken);
                string msg = Shiro.Services.LocalizationService.Format("AccountManager.OpeningBrowser", username, validCount, comboList.Length);
                SetDashboardStatus(msg);
                statusCallback?.Invoke(msg);

                if (activeSlotTasks[0] != null && activeSlotTasks[1] != null)
                {
                    var completedTask = await Task.WhenAny(activeSlotTasks[0]!, activeSlotTasks[1]!);
                    var res = await completedTask;
                    activeSlotTasks[res.WindowSlot] = null;
                    if (!res.Success)
                        failedAccounts.Add((res.Username, res.Error));
                }
            }

            var remaining = activeSlotTasks.Where(t => t != null).Select(t => t!).ToArray();
            if (remaining.Length > 0)
            {
                var results = await Task.WhenAll(remaining);
                foreach (var res in results)
                {
                    if (!res.Success)
                        failedAccounts.Add((res.Username, res.Error));
                }
            }

            if (validCount == 0)
            {
                string msg = Shiro.Services.LocalizationService.Get("AccountManager.NoValidCredentials");
                SetDashboardStatus(msg);
                statusCallback?.Invoke(msg);
                return;
            }

            if (cancellationToken.IsCancellationRequested)
                return;

            if (failedAccounts.Count > 0)
            {
                string failedNames = string.Join(", ", failedAccounts.Select(f => f.Username));
                string msg = Shiro.Services.LocalizationService.Format("AccountManager.BulkLoginSummary", validCount - failedAccounts.Count, validCount, failedNames);
                SetDashboardStatus(msg);
                statusCallback?.Invoke(msg);
                Program.Logger.Warn($"Bulk sign-in completed with {failedAccounts.Count} failures: {failedNames}");
            }
            else
            {
                string msg = Shiro.Services.LocalizationService.Format("AccountManager.BulkLoginCompleted", validCount);
                SetDashboardStatus(msg);
                statusCallback?.Invoke(msg);
            }
        }

        private async Task<(bool Success, string Username, string Error, int WindowSlot)> RunBulkUserPassLoginAsync(string username, string password, int windowSlot, CancellationToken cancellationToken = default)
        {
            try
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token, cancellationToken);
                await browserService.SignInAsync(
                    username,
                    password,
                    windowSlot,
                    submitCredentials: true,
                    cancellationToken: linkedCts.Token);
                return (true, username, string.Empty, windowSlot);
            }
            catch (OperationCanceledException)
            {
                return (false, username, Shiro.Services.LocalizationService.Get("AccountManager.CancelledByUser"), windowSlot);
            }
            catch (Exception exception)
            {
                Program.Logger.Error($"Bulk user:pass login failed for {username}: {exception.Message}");
                return (false, username, exception.Message, windowSlot);
            }
        }

        private static readonly System.Buffers.SearchValues<char> InvalidFileNameChars =
            System.Buffers.SearchValues.Create(Path.GetInvalidFileNameChars());

        internal static string MakeSafeFileName(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;

            ReadOnlySpan<char> trimmed = value.AsSpan().Trim();
            if (trimmed.IndexOfAny(InvalidFileNameChars) < 0)
            {
                ReadOnlySpan<char> clean = trimmed.TrimEnd('.');
                if (clean.IsEmpty || clean.IsWhiteSpace())
                    return fallback;
                return clean.Length == value.Length ? value : clean.ToString();
            }

            Span<char> buffer = trimmed.Length <= 256
                ? stackalloc char[trimmed.Length]
                : new char[trimmed.Length];

            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                buffer[i] = InvalidFileNameChars.Contains(c) ? '_' : c;
            }

            ReadOnlySpan<char> resultSpan = ((ReadOnlySpan<char>)buffer).Trim().TrimEnd('.');
            return resultSpan.IsEmpty || resultSpan.IsWhiteSpace() ? fallback : resultSpan.ToString();
        }
    }
}
