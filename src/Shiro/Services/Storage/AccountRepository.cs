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
        private sealed class SnapshotIndex
        {
            public readonly IReadOnlyList<Account> Snapshot;
            public readonly Dictionary<long, Account> ById;
            public readonly Dictionary<string, Account> ByUsername;

            public SnapshotIndex(IReadOnlyList<Account> snapshot)
            {
                Snapshot = snapshot;
                int capacity = snapshot.Count;
                ById = new Dictionary<long, Account>(capacity);
                ByUsername = new Dictionary<string, Account>(capacity, StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < capacity; i++)
                {
                    Account? acc = snapshot[i];
                    if (acc == null) continue;
                    if (acc.UserID > 0 && !ById.ContainsKey(acc.UserID))
                    {
                        ById[acc.UserID] = acc;
                    }
                    if (!string.IsNullOrWhiteSpace(acc.Username) && !ByUsername.ContainsKey(acc.Username))
                    {
                        ByUsername[acc.Username] = acc;
                    }
                }
            }
        }

        private static readonly Lazy<AccountRepository> _defaultInstance = new(() => new AccountRepository());
        public static AccountRepository Default => _defaultInstance.Value;

        private readonly System.Threading.Lock _syncRoot = new();
        private List<Account> _accounts = new();
        private Account[] _cachedSnapshot = Array.Empty<Account>();
        private volatile SnapshotIndex? _cachedIndex;

        internal System.Threading.Lock SyncRoot => _syncRoot;

        public int Count
        {
            get
            {
                lock (_syncRoot) return _accounts?.Count ?? 0;
            }
        }

        public IReadOnlyList<Account> GetAll()
        {
            lock (_syncRoot)
            {
                if (_accounts == null || _accounts.Count == 0)
                    return Array.Empty<Account>();

                Account[] cached = _cachedSnapshot;
                if (cached != null && cached.Length == _accounts.Count)
                {
                    bool same = true;
                    for (int i = 0; i < cached.Length; i++)
                    {
                        if (!ReferenceEquals(cached[i], _accounts[i]))
                        {
                            same = false;
                            break;
                        }
                    }
                    if (same)
                        return cached;
                }

                int nonNullCount = 0;
                for (int i = 0; i < _accounts.Count; i++)
                {
                    if (_accounts[i] != null)
                        nonNullCount++;
                }

                if (nonNullCount == 0)
                {
                    _cachedSnapshot = Array.Empty<Account>();
                    return _cachedSnapshot;
                }

                var result = new Account[nonNullCount];
                int idx = 0;
                for (int i = 0; i < _accounts.Count; i++)
                {
                    var acc = _accounts[i];
                    if (acc != null)
                        result[idx++] = acc;
                }

                _cachedSnapshot = result;
                return result;
            }
        }

        private SnapshotIndex GetOrBuildIndex(IReadOnlyList<Account> currentSnapshot)
        {
            SnapshotIndex? current = _cachedIndex;
            if (current != null && ReferenceEquals(current.Snapshot, currentSnapshot))
                return current;

            var built = new SnapshotIndex(currentSnapshot);
            _cachedIndex = built;
            return built;
        }

        public Account? GetById(long userId)
        {
            if (userId <= 0) return null;
            IReadOnlyList<Account> snapshot = GetAll();
            if (snapshot.Count == 0) return null;

            SnapshotIndex index = GetOrBuildIndex(snapshot);
            if (index.ById.TryGetValue(userId, out Account? match) && match.UserID == userId)
                return match;

            for (int i = 0; i < snapshot.Count; i++)
            {
                Account? acc = snapshot[i];
                if (acc != null && acc.UserID == userId)
                {
                    _cachedIndex = new SnapshotIndex(snapshot);
                    return acc;
                }
            }

            return null;
        }

        public Account? GetByUsername(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return null;
            IReadOnlyList<Account> snapshot = GetAll();
            if (snapshot.Count == 0) return null;

            SnapshotIndex index = GetOrBuildIndex(snapshot);
            if (index.ByUsername.TryGetValue(username, out Account? match) &&
                string.Equals(match.Username, username, StringComparison.OrdinalIgnoreCase))
            {
                return match;
            }

            for (int i = 0; i < snapshot.Count; i++)
            {
                Account? acc = snapshot[i];
                if (acc != null && string.Equals(acc.Username, username, StringComparison.OrdinalIgnoreCase))
                {
                    _cachedIndex = new SnapshotIndex(snapshot);
                    return acc;
                }
            }

            return null;
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
            if (account == null) return;
            lock (_syncRoot)
            {
                _accounts ??= new List<Account>();
                _accounts.Add(account);
                _cachedSnapshot = Array.Empty<Account>();
                _cachedIndex = null;
            }
        }

        internal IReadOnlyList<Account> ReplaceAll(IEnumerable<Account>? accounts)
        {
            var nextList = accounts != null ? new List<Account>(accounts) : new List<Account>();
            foreach (var acc in nextList)
            {
                if (acc != null)
                {
                    if (string.IsNullOrWhiteSpace(acc.SecurityToken))
                    {
                        acc.Valid = false;
                    }
                    else if (acc.Valid && acc.HasExpiredShortLivedCookie())
                    {
                        acc.Valid = false;
                        acc.IsDirty = true;
                    }
                }
            }

            lock (_syncRoot)
            {
                _accounts = nextList;
                _cachedSnapshot = Array.Empty<Account>();
                _cachedIndex = null;
                return GetAll();
            }
        }

        internal Account? FindFirst(Func<Account, bool> predicate)
        {
            if (predicate == null) return null;
            lock (_syncRoot)
            {
                if (_accounts == null || _accounts.Count == 0) return null;
                for (int i = 0; i < _accounts.Count; i++)
                {
                    Account? acc = _accounts[i];
                    if (acc != null && predicate(acc))
                        return acc;
                }
                return null;
            }
        }

        internal (int RemovedCount, bool IsNowEmpty) RemoveMatching(ISet<Account> toRemove)
        {
            if (toRemove == null || toRemove.Count == 0)
                return (0, Count == 0);

            lock (_syncRoot)
            {
                if (_accounts == null)
                    return (0, true);

                int removed = _accounts.RemoveAll(a => a != null && toRemove.Contains(a));
                if (removed > 0)
                {
                    _cachedSnapshot = Array.Empty<Account>();
                    _cachedIndex = null;
                }
                return (removed, _accounts.Count == 0);
            }
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

        public void EnsureBackendInitialized()
        {
            AccountManager.EnsureBackendInitialized();
        }
    }
}
