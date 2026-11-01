#nullable enable
using System;
using System.Collections.Generic;

namespace Shiro.Services
{
    public interface IAccountRepository
    {
        IReadOnlyList<Account> GetAll();
        Account? GetById(long userId);
        Account? GetByUsername(string username);
        Account? AddOrUpdate(string securityToken, string password = "", string? accountJson = null);
        bool Remove(Account account);
        int RemoveRange(IEnumerable<Account> accounts);
        void Save(bool force = false);
        System.Threading.Tasks.Task SaveAsync(bool force = false);
        void Load(byte[]? masterPasswordHash = null);
        bool IsPasswordEncrypted();
    }
}
