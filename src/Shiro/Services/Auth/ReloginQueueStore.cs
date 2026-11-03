#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Shiro.Services
{
    /// <summary>
    /// Persists the pending batch re-login queue (by UserID, never cookies or passwords) so an
    /// interrupted run can be resumed after the dialog or the app is closed mid-way.
    /// </summary>
    internal static class ReloginQueueStore
    {
        private static IAppLogger _logger = AppServices.Logger;
        public static IAppLogger Logger
        {
            get => _logger;
            set => _logger = value ?? throw new ArgumentNullException(nameof(value));
        }

        private static string QueueFile => StoragePaths.GetUserFile("relogin_queue.json");

        public static void Save(IEnumerable<long> pendingUserIds)
        {
            try
            {
                long[] ids = pendingUserIds?.Where(id => id > 0).Distinct().ToArray() ?? Array.Empty<long>();
                if (ids.Length == 0)
                {
                    Clear();
                    return;
                }
                File.WriteAllText(QueueFile, JsonSerializer.Serialize(ids));
            }
            catch (Exception ex)
            {
                _logger.Debug($"[ReloginQueueStore] Save failed: {ex.Message}");
            }
        }

        public static IReadOnlyList<long> Load()
        {
            try
            {
                if (!File.Exists(QueueFile))
                    return Array.Empty<long>();
                // Ignore stale queues older than 3 days
                if (File.GetLastWriteTimeUtc(QueueFile) < DateTime.UtcNow.AddDays(-3))
                {
                    Clear();
                    return Array.Empty<long>();
                }
                return JsonSerializer.Deserialize<long[]>(File.ReadAllText(QueueFile)) ?? Array.Empty<long>();
            }
            catch (Exception ex)
            {
                _logger.Debug($"[ReloginQueueStore] Load failed: {ex.Message}");
                return Array.Empty<long>();
            }
        }

        public static void Clear()
        {
            try { if (File.Exists(QueueFile)) File.Delete(QueueFile); }
            catch (Exception ex) { _logger.Debug($"[ReloginQueueStore] Clear failed: {ex.Message}"); }
        }
    }
}
