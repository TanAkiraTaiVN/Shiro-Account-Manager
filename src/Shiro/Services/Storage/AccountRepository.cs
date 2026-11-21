#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

        private readonly Lock _syncRoot = new();
        private List<Account> _accounts = new();
        private ReadOnlyCollection<Account> _cachedSnapshot = ReadOnlyCollection<Account>.Empty;
        private volatile SnapshotIndex? _cachedIndex;
        private AccountPersistenceService? _localPersistence;
        private AccountRegistrationService? _localRegistration;
        private long _snapshotVersion;

        public event EventHandler<AccountsChangedEventArgs>? AccountsChanged;

        internal AccountPersistenceService PersistenceInstance =>
            _localPersistence ??= new AccountPersistenceService(this);

        internal AccountRegistrationService RegistrationInstance =>
            _localRegistration ??= new AccountRegistrationService(this, PersistenceInstance);

        internal Lock SyncRoot => _syncRoot;

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
                return GetSnapshotCoreUnsafe();
            }
        }

        private ReadOnlyCollection<Account> GetSnapshotCoreUnsafe()
        {
            if (_accounts == null || _accounts.Count == 0)
                return ReadOnlyCollection<Account>.Empty;

            ReadOnlyCollection<Account> cached = _cachedSnapshot;
            if (cached != null && cached.Count == _accounts.Count)
            {
                bool same = true;
                for (int i = 0; i < cached.Count; i++)
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
                _cachedSnapshot = ReadOnlyCollection<Account>.Empty;
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

            _cachedSnapshot = Array.AsReadOnly(result);
            return _cachedSnapshot;
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
            return RegistrationInstance.AddAccount(securityToken, password, accountJson!);
        }

        public Task<Account?> AddOrUpdateAsync(string securityToken, string password = "", string? accountJson = null, bool saveImmediate = true, CancellationToken cancellationToken = default)
        {
            return RegistrationInstance.AddAccountAsync(securityToken, password, accountJson!, saveImmediate, cancellationToken);
        }

        public Task<Account?> AddOrUpdateWithHbaAsync(string securityToken, string password = "", string? accountJson = null, bool saveImmediate = true, RobloxHbaService.CaptureSnapshot? hbaCapture = null, CancellationToken cancellationToken = default)
        {
            return RegistrationInstance.AddAccountAsync(securityToken, password, accountJson!, saveImmediate, cancellationToken, hbaCapture);
        }

        public Account? RegisterValidated(Account validatedAccount, string securityToken, string password = "", bool saveImmediate = true)
        {
            return RegistrationInstance.RegisterValidatedAccount(validatedAccount, securityToken, password, saveImmediate);
        }

        internal (IReadOnlyList<Account> Snapshot, long Version) AddWithoutNotifyUnsafe(Account account)
        {
            _accounts ??= new List<Account>();
            _accounts.Add(account);
            _cachedSnapshot = ReadOnlyCollection<Account>.Empty;
            _cachedIndex = null;
            return (GetSnapshotCoreUnsafe(), ++_snapshotVersion);
        }

        internal void RaiseAccountsChanged(IReadOnlyList<Account> snapshot, long version)
        {
            AccountsChanged?.Invoke(this, new AccountsChangedEventArgs(snapshot, version));
        }

        public void AddThreadSafe(Account account)
        {
            if (account == null) return;
            IReadOnlyList<Account> snapshot;
            long version;
            lock (_syncRoot)
            {
                (snapshot, version) = AddWithoutNotifyUnsafe(account);
            }

            RaiseAccountsChanged(snapshot, version);
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

            IReadOnlyList<Account> snapshot;
            long version;
            lock (_syncRoot)
            {
                _accounts = nextList;
                _cachedSnapshot = ReadOnlyCollection<Account>.Empty;
                _cachedIndex = null;
                snapshot = GetSnapshotCoreUnsafe();
                version = ++_snapshotVersion;
            }

            RaiseAccountsChanged(snapshot, version);
            return snapshot;
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

            int removed;
            bool isNowEmpty;
            IReadOnlyList<Account>? snapshot = null;
            long version = 0;
            lock (_syncRoot)
            {
                if (_accounts == null)
                    return (0, true);

                removed = _accounts.RemoveAll(a => a != null && toRemove.Contains(a));
                if (removed > 0)
                {
                    _cachedSnapshot = ReadOnlyCollection<Account>.Empty;
                    _cachedIndex = null;
                    snapshot = GetSnapshotCoreUnsafe();
                    version = ++_snapshotVersion;
                }
                isNowEmpty = _accounts.Count == 0;
            }

            if (snapshot != null)
            {
                RaiseAccountsChanged(snapshot, version);
            }

            return (removed, isNowEmpty);
        }

        public bool Remove(Account account)
        {
            if (account == null) return false;
            return RegistrationInstance.RemoveAccount(account);
        }

        public int RemoveRange(IEnumerable<Account> accounts)
        {
            if (accounts == null) return 0;
            return RegistrationInstance.RemoveAccountsFromDatabase(accounts);
        }

        public void Save(bool force = false)
        {
            PersistenceInstance.SaveAccounts(bypassRateLimit: force, bypassCountCheck: force);
        }

        public Task SaveAsync(bool force = false)
        {
            return PersistenceInstance.SaveAccountsAsync(bypassRateLimit: force, bypassCountCheck: force);
        }

        public void QueueDeferredSave()
        {
            PersistenceInstance.QueueDeferredSave();
        }

        public void Load(byte[]? masterPasswordHash = null)
        {
            PersistenceInstance.LoadAccounts(masterPasswordHash);
        }

        public bool LoadFromStore(byte[]? masterPasswordHash = null)
        {
            return PersistenceInstance.LoadAccountsFromStore(masterPasswordHash);
        }

        public Task<bool> ResolvePendingBackupRestoreAsync()
        {
            return PersistenceInstance.ResolvePendingBackupRestoreAsync();
        }

        public bool TryRecoverOrRestore(out string statusMessage)
        {
            return PersistenceInstance.TryRecoverOrRestoreAccounts(out statusMessage);
        }

        public bool IsPasswordEncrypted()
        {
            return PersistenceInstance.IsDatabasePasswordEncrypted();
        }

        public Task<(bool Success, long UserId, RestResponse? Response)> LookupUserIdAsync(string username, CancellationToken cancellationToken = default)
        {
            return RegistrationInstance.GetUserIdAsync(username, cancellationToken);
        }
    }
}
