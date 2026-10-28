#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Shiro.Services
{
    public class AccountRepository : IAccountRepository
    {
        private static readonly Lazy<AccountRepository> _defaultInstance = new(() => new AccountRepository());
        public static AccountRepository Default => _defaultInstance.Value;

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

        public void Load(byte[]? masterPasswordHash = null)
        {
            AccountManager.LoadAccountsFromStore(masterPasswordHash!);
        }

        public bool IsPasswordEncrypted()
        {
            return AccountManager.IsDatabasePasswordEncrypted();
        }
    }
}
