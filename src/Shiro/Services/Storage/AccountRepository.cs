#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Http;

namespace Shiro.Services
{
    public class AccountRepository : IAccountRepository
    {
        private static readonly Lazy<AccountRepository> _defaultInstance = new(() => new AccountRepository());
        public static AccountRepository Default => _defaultInstance.Value;

        public int Count => AccountManager.AccountCount;

        public IReadOnlyList<Account> GetAll() => AccountManager.GetAccountsSnapshot();

        public Account? GetById(long userId)
        {
            if (userId <= 0) return null;
            return GetAll().FirstOrDefault(a => a.UserID == userId);
        }

        public Account? GetByUsername(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return null;
            return GetAll().FirstOrDefault(a => string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase));
        }

        public Account? AddOrUpdate(string securityToken, string password = "", string? accountJson = null)
        {
            return AccountManager.AddAccount(securityToken, password, accountJson!);
        }

        public Task<Account?> AddOrUpdateAsync(string securityToken, string password = "", string? accountJson = null, bool saveImmediate = true, CancellationToken cancellationToken = default)
        {
            return AccountManager.AddAccountAsync(securityToken, password, accountJson!, saveImmediate, cancellationToken);
        }

        public Task<Account?> AddOrUpdateWithHbaAsync(string securityToken, string password = "", string? accountJson = null, bool saveImmediate = true, RobloxHbaService.CaptureSnapshot? hbaCapture = null, CancellationToken cancellationToken = default)
        {
            return AccountManager.AddAccountAsync(securityToken, password, accountJson!, saveImmediate, cancellationToken, hbaCapture);
        }

        public Account? RegisterValidated(Account validatedAccount, string securityToken, string password = "", bool saveImmediate = true)
        {
            return AccountManager.RegisterValidatedAccount(validatedAccount, securityToken, password, saveImmediate);
        }

        public void AddThreadSafe(Account account)
        {
            if (account != null)
                AccountManager.AddAccountThreadSafe(account);
        }

        public bool Remove(Account account)
        {
            if (account == null) return false;
            return AccountManager.RemoveAccountsFromDatabase(new[] { account }) > 0;
        }

        public int RemoveRange(IEnumerable<Account> accounts)
        {
            if (accounts == null) return 0;
            return AccountManager.RemoveAccountsFromDatabase(accounts);
        }

        public void Save(bool force = false)
        {
            AccountManager.SaveAccounts(BypassRateLimit: force, BypassCountCheck: force);
        }

        public Task SaveAsync(bool force = false)
        {
            return AccountManager.SaveAccountsAsync(BypassRateLimit: force, BypassCountCheck: force);
        }

        public void QueueDeferredSave()
        {
            AccountManager.QueueDeferredSave();
        }

        public void Load(byte[]? masterPasswordHash = null)
        {
            AccountManager.LoadAccountsFromStore(masterPasswordHash!);
        }

        public bool LoadFromStore(byte[]? masterPasswordHash = null)
        {
            return AccountManager.LoadAccountsFromStore(masterPasswordHash!);
        }

        public bool TryRecoverOrRestore(out string statusMessage)
        {
            return AccountManager.TryRecoverOrRestoreAccounts(out statusMessage);
        }

        public bool IsPasswordEncrypted()
        {
            return AccountManager.IsDatabasePasswordEncrypted();
        }

        public Task<(bool Success, long UserId, RestResponse? Response)> LookupUserIdAsync(string username, CancellationToken cancellationToken = default)
        {
            return AccountManager.GetUserIDAsync(username, cancellationToken);
        }
    }
}
