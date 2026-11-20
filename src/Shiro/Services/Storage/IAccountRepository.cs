#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Http;

namespace Shiro.Services
{
    public sealed class AccountsChangedEventArgs : EventArgs
    {
        public AccountsChangedEventArgs(IReadOnlyList<Account> accounts)
        {
            Accounts = accounts ?? Array.Empty<Account>();
        }

        public IReadOnlyList<Account> Accounts { get; }
    }

    public interface IAccountRepository
    {
        event EventHandler<AccountsChangedEventArgs>? AccountsChanged;
        int Count { get; }
        IReadOnlyList<Account> GetAll();
        Account? GetById(long userId);
        Account? GetByUsername(string username);
        Account? AddOrUpdate(string securityToken, string password = "", string? accountJson = null);
        Task<Account?> AddOrUpdateAsync(string securityToken, string password = "", string? accountJson = null, bool saveImmediate = true, CancellationToken cancellationToken = default);
        Task<Account?> AddOrUpdateWithHbaAsync(string securityToken, string password = "", string? accountJson = null, bool saveImmediate = true, RobloxHbaService.CaptureSnapshot? hbaCapture = null, CancellationToken cancellationToken = default);
        Account? RegisterValidated(Account validatedAccount, string securityToken, string password = "", bool saveImmediate = true);
        void AddThreadSafe(Account account);
        bool Remove(Account account);
        int RemoveRange(IEnumerable<Account> accounts);
        void Save(bool force = false);
        Task SaveAsync(bool force = false);
        void QueueDeferredSave();
        void Load(byte[]? masterPasswordHash = null);
        bool LoadFromStore(byte[]? masterPasswordHash = null);
        bool TryRecoverOrRestore(out string statusMessage);
        bool IsPasswordEncrypted();
        Task<(bool Success, long UserId, RestResponse? Response)> LookupUserIdAsync(string username, CancellationToken cancellationToken = default);
    }
}
