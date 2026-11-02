#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Shiro.Services
{
    internal sealed class PresenceService : IDisposable
    {
        private readonly SemaphoreSlim refreshGate = new SemaphoreSlim(1, 1);
        private readonly System.Threading.Lock recentRefreshLock = new();
        private readonly Dictionary<long, DateTime> recentRefreshes = new Dictionary<long, DateTime>();
        private bool disposed;

        // A timer tick, a manual refresh and the post-launch refresh can land
        // within the same second.  Presence is not latency critical enough to
        // send the same user IDs repeatedly, especially when several accounts
        // are being launched at once.
        private static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(2);

        public async Task RefreshAsync(long[] userIds, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (disposed || cancellationToken.IsCancellationRequested || userIds == null || userIds.Length == 0)
                return;

            await refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                DateTime now = DateTime.UtcNow;
                long[] pendingIds;
                lock (recentRefreshLock)
                {
                    pendingIds = userIds
                        .Where(id => id > 0 && (!recentRefreshes.TryGetValue(id, out DateTime last) || now - last >= CoalesceWindow))
                        .ToArray();

                    // Keep this cache bounded if a user imports a large number
                    // of accounts over a long-running session.
                    if (recentRefreshes.Count > 4096)
                    {
                        DateTime cutoff = now - CoalesceWindow - CoalesceWindow;
                        foreach (long staleId in recentRefreshes
                            .Where(pair => pair.Value < cutoff)
                            .Select(pair => pair.Key)
                            .ToArray())
                            recentRefreshes.Remove(staleId);
                    }
                }

                if (pendingIds.Length == 0)
                    return;

                await Presence.UpdatePresence(cancellationToken, pendingIds).ConfigureAwait(false);

                lock (recentRefreshLock)
                    foreach (long userId in pendingIds)
                        recentRefreshes[userId] = DateTime.UtcNow;
            }
            finally
            {
                refreshGate.Release();
            }
        }

        public void Dispose()
        {
            lock (recentRefreshLock)
            {
                if (disposed)
                    return;

                disposed = true;
                recentRefreshes.Clear();
            }

            refreshGate.Dispose();
        }
    }
}
