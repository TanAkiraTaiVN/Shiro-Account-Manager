#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;

namespace Shiro.Services
{
    /// <summary>
    /// Coordinates batch modifications across accounts, such as bulk group assignment,
    /// proxy configuration, custom fields, session validations, and bulk deletions.
    /// </summary>
    public static class AccountBatchService
    {
        public static int BatchSetGroup(IEnumerable<Account> accounts, string group)
        {
            if (accounts == null) return 0;
            string grp = string.IsNullOrWhiteSpace(group) ? "Default" : group.Trim();
            int modified = 0;

            foreach (var acc in accounts)
            {
                if (acc != null && !string.Equals(acc.Group, grp, StringComparison.Ordinal))
                {
                    acc.Group = grp;
                    acc.IsDirty = true;
                    modified++;
                }
            }

            if (modified > 0)
                AccountManager.SaveAccounts();

            return modified;
        }

        public static int BatchSetProxy(IEnumerable<Account> accounts, string proxy)
        {
            if (accounts == null) return 0;
            string? p = string.IsNullOrWhiteSpace(proxy) ? null : proxy.Trim();
            int modified = 0;

            foreach (var acc in accounts)
            {
                if (acc != null && !string.Equals(acc.Proxy, p, StringComparison.Ordinal))
                {
                    acc.Proxy = p;
                    modified++;
                }
            }

            if (modified > 0)
                AccountManager.SaveAccounts();

            return modified;
        }

        public static int BatchSetField(IEnumerable<Account> accounts, string fieldName, string fieldValue)
        {
            if (accounts == null || string.IsNullOrWhiteSpace(fieldName)) return 0;
            string fn = fieldName.Trim();
            int modified = 0;

            foreach (var acc in accounts)
            {
                if (acc == null) continue;
                if (string.IsNullOrEmpty(fieldValue))
                {
                    if (acc.Fields.Remove(fn))
                    {
                        acc.IsDirty = true;
                        modified++;
                    }
                }
                else
                {
                    acc.Fields[fn] = fieldValue;
                    acc.IsDirty = true;
                    modified++;
                }
            }

            if (modified > 0)
                AccountManager.SaveAccounts();

            return modified;
        }

        public static int BatchDelete(IEnumerable<Account> accounts)
        {
            if (accounts == null) return 0;
            var toRemove = accounts.Where(a => a != null).ToList();
            return AccountManager.RemoveAccountsFromDatabase(toRemove);
        }

        public static async Task<Dictionary<string, bool>> BatchValidateSessionsAsync(
            IEnumerable<Account> accounts,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var results = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (accounts == null) return results;

            var list = accounts.Where(a => a != null).ToList();
            if (list.Count == 0) return results;

            int processed = 0;
            using var semaphore = new SemaphoreSlim(3, 3); // Max 3 concurrent session validations to avoid burst rate limits

            var tasks = list.Select(async acc =>
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    bool valid = await acc.ValidateSessionAsync(cancellationToken).ConfigureAwait(false);
                    lock (results)
                    {
                        results[acc.Username] = valid;
                        processed++;
                        progress?.Report((double)processed / list.Count);
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
            return results;
        }
    }
}
