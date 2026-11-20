#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Http;

namespace Shiro.Services
{
    public sealed class AccountRegistrationService
    {
        public static AccountRegistrationService Default => AppServices.AccountRegistration;

        private readonly AccountRepository _repository;
        private readonly AccountPersistenceService _persistence;
        private readonly ConcurrentDictionary<string, long> _resolvedUserIdsCache = new(StringComparer.OrdinalIgnoreCase);

        internal AccountRegistrationService(AccountRepository? repository = null, AccountPersistenceService? persistence = null)
        {
            _repository = repository ?? (AppServices.AccountRepository as AccountRepository)
                ?? throw new InvalidOperationException("AccountRepository is not registered.");
            _persistence = persistence ?? AppServices.AccountPersistence;
        }

        public void NotifyAccountValidity(Account account, bool isValid)
        {
            if (account == null) return;
            account.Valid = isValid;
            if (isValid)
                account.AccountStatusIssue = string.Empty;
            AppServices.HostUiBridge.NotifyAccountValidityChanged(account, isValid);
        }

        internal void ClearResolvedUserIdsCacheForTesting() => _resolvedUserIdsCache.Clear();

        public async Task<(bool Success, long UserId, RestResponse? Response)> GetUserIdAsync(string username, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(username))
                return (false, -1, null);

            string trimmedName = username.Trim();

            if (_resolvedUserIdsCache.TryGetValue(trimmedName, out long cachedUserId) && cachedUserId > 0)
                return (true, cachedUserId, null);

            Account? knownAccount = _repository.FindFirst(a =>
                a.UserID > 0 &&
                string.Equals(a.Username, trimmedName, StringComparison.OrdinalIgnoreCase));
            if (knownAccount != null)
            {
                _resolvedUserIdsCache[trimmedName] = knownAccount.UserID;
                return (true, knownAccount.UserID, null);
            }

            try
            {
                RestRequest request = AppServices.SelectionState.LastValidAccount?.MakeRequest("v1/usernames/users", Method.Post) ?? new RestRequest("v1/usernames/users", Method.Post);
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
                                if (_resolvedUserIdsCache.Count > 2000)
                                    _resolvedUserIdsCache.Clear();
                                _resolvedUserIdsCache[trimmedName] = userId;
                                return (true, userId, response);
                            }
                        }
                    }
                    catch (System.Text.Json.JsonException ex)
                    {
                        AppServices.Logger?.Debug($"Parse GetUserIDAsync JSON failed: {ex.Message}");
                    }
                }

                return (false, -1, response);
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug($"GetUserIDAsync exception for {username}: {ex.Message}");
                return (false, -1, null);
            }
        }

        public Account? AddAccount(string securityToken, string password = "", string? accountJson = null, bool saveImmediate = true)
        {
            Account account = new Account(securityToken, accountJson);
            return RegisterValidatedAccount(account, account.SecurityToken, password, saveImmediate);
        }

        public Task<Account?> AddAccountAsync(
            string securityToken,
            string password = "",
            string? accountJson = null,
            bool saveImmediate = true,
            CancellationToken cancellationToken = default)
        {
            return AddAccountAsync(securityToken, password, accountJson, saveImmediate, cancellationToken, null);
        }

        public async Task<Account?> AddAccountAsync(
            string securityToken,
            string password = "",
            string? accountJson = null,
            bool saveImmediate = true,
            CancellationToken cancellationToken = default,
            RobloxHbaService.CaptureSnapshot? hbaCapture = null)
        {
            Account account = await Account.CreateAsync(securityToken, accountJson, cancellationToken, hbaCapture).ConfigureAwait(false);
            return RegisterValidatedAccount(account, account.SecurityToken, password, saveImmediate);
        }

        internal Account? RegisterValidatedAccount(Account account, string securityToken, string password = "", bool saveImmediate = true)
        {
            AppServices.Logger.Info($"[RegisterAccount] user='{account?.Username}' id={account?.UserID} valid={account?.Valid} payloadLen={Account.GetSessionTokenPayloadLength(securityToken)} shortLived={Account.IsShortLivedCookie(securityToken)}.");
            if (account == null || !account.Valid)
                return null;

            Account? registered;
            (IReadOnlyList<Account> Snapshot, long Version)? addedChange = null;
            lock (_repository.SyncRoot)
            {
                account.Password = password;

                Account? exists = _repository.FindFirst(acc =>
                    (account.UserID > 0 && acc.UserID == account.UserID) ||
                    (!string.IsNullOrEmpty(acc.Username) && !string.IsNullOrEmpty(account.Username) && acc.Username.Equals(account.Username, StringComparison.OrdinalIgnoreCase)));

                if (exists != null)
                {
                    exists.SecurityToken = securityToken;
                    account.CopyHbaAndFingerprintTo(exists);
                    if (!string.IsNullOrWhiteSpace(password))
                        exists.Password = password;
                    if (exists.UserID <= 0 && account.UserID > 0)
                        exists.UserID = account.UserID;
                    if (string.IsNullOrEmpty(exists.Username) && !string.IsNullOrEmpty(account.Username))
                        exists.Username = account.Username;
                    DateTime now = DateTime.Now;
                    exists.LastUse = now;
                    exists.LastAttemptedRefresh = now;
                    exists.CSRFToken = null;
                    exists.TokenSet = DateTime.MinValue;
                    exists.Valid = true;
                    exists.AccountStatusIssue = string.Empty;
                    exists.LastAuthTicketRateLimited = false;
                    registered = exists;
                }
                else
                {
                    account.LastAttemptedRefresh = DateTime.Now;
                    addedChange = _repository.AddWithoutNotifyUnsafe(account);
                    registered = account;
                }
            }

            if (addedChange is { } change)
            {
                _repository.RaiseAccountsChanged(change.Snapshot, change.Version);
            }

            if (saveImmediate)
            {
                NotifyAccountValidity(registered, true);
                _persistence.SaveAccounts(bypassRateLimit: true);
            }

            return registered;
        }

        public bool RemoveAccount(Account account) =>
            account != null && RemoveAccountsFromDatabase(new[] { account }) > 0;

        public int RemoveAccountsFromDatabase(IEnumerable<Account> accounts)
        {
            if (accounts == null) return 0;
            var toRemove = new HashSet<Account>(accounts.Where(a => a != null));
            if (toRemove.Count == 0) return 0;

            var (count, isNowEmpty) = _repository.RemoveMatching(toRemove);

            foreach (var a in toRemove)
            {
                AutoReconnectService.Instance.UnregisterSession(a.Username);
                try { RobloxProcessService.CloseAccountRobloxProcess(a); }
                catch (Exception ex) { AppServices.Logger?.Debug($"Closing removed account client threw: {ex.Message}"); }
                a.ClearSensitiveMemory();
            }

            if (count > 0)
            {
                _persistence.SaveAccounts(bypassRateLimit: true, bypassCountCheck: true);
                if (isNowEmpty)
                {
                    try
                    {
                        string backupFile = StoragePaths.AccountDataFile + ".backup";
                        if (File.Exists(backupFile)) File.Delete(backupFile);
                    }
                    catch (Exception ex)
                    {
                        AppServices.Logger?.Warn($"Failed to delete empty backup file: {ex.Message}");
                    }
                    RobloxProcessService.ClearRobloxClientSession();
                }
            }
            return count;
        }
    }
}
