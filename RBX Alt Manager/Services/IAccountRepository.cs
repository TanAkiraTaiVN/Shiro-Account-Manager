#nullable enable
using System;
using System.Collections.Generic;

namespace RBX_Alt_Manager.Services
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
        void Load(byte[]? masterPasswordHash = null);
        bool IsPasswordEncrypted();
    }
}
