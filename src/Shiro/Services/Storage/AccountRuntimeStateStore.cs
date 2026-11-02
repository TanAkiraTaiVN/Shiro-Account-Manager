#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using Shiro.Classes;

namespace Shiro.Services
{
    /// <summary>
    /// Lightweight debounced persistence store for high-frequency, non-secret runtime metadata
    /// (<c>LastUse</c> timestamps and <c>RobloxWatcher</c> window geometry).
    /// Prevents re-serializing and re-encrypting the entire AES-256-GCM <c>AccountData.json</c> vault
    /// every time a user drags a Roblox window or negotiates a short-lived CSRF/auth ticket.
    /// </summary>
    public static class AccountRuntimeStateStore
    {
        public sealed class RuntimeStateEntry
        {
            public long UserId { get; set; }
            public string Username { get; set; } = string.Empty;
            public DateTime LastUse { get; set; }
            public int? WindowX { get; set; }
            public int? WindowY { get; set; }
            public int? WindowWidth { get; set; }
            public int? WindowHeight { get; set; }
        }

        public sealed class RuntimeStateDocument
        {
            public int Version { get; set; } = 1;
            public Dictionary<string, RuntimeStateEntry> Accounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private const int DebounceSaveDelayMs = 1500;

        private static readonly System.Threading.Lock _sync = new();
        private static RuntimeStateDocument? _document;
        private static bool _dirty;
        private static Timer? _debounceTimer;

        public static string BuildAccountKey(long userId, string? username)
        {
            if (userId > 0)
                return $"uid:{userId}";

            string normalized = (username ?? string.Empty).Trim().ToLowerInvariant();
            return string.IsNullOrEmpty(normalized) ? string.Empty : $"usr:{normalized}";
        }

        public static void RecordWindowBounds(
            Account account,
            int x,
            int y,
            int width,
            int height,
            string? filePathOverride = null)
        {
            if (account == null) return;
            string key = BuildAccountKey(account.UserID, account.Username);
            if (string.IsNullOrEmpty(key)) return;

            lock (_sync)
            {
                var doc = EnsureLoadedUnderLock(filePathOverride);
                if (!doc.Accounts.TryGetValue(key, out var entry))
                {
                    entry = new RuntimeStateEntry();
                    doc.Accounts[key] = entry;
                }

                entry.UserId = account.UserID;
                entry.Username = account.Username ?? string.Empty;
                if (account.LastUse > entry.LastUse)
                    entry.LastUse = account.LastUse;
                entry.WindowX = x;
                entry.WindowY = y;
                entry.WindowWidth = width;
                entry.WindowHeight = height;

                MarkDirtyUnderLock(filePathOverride);
            }
        }

        public static void RecordLastUse(Account account, DateTime lastUse, string? filePathOverride = null)
        {
            if (account == null) return;
            string key = BuildAccountKey(account.UserID, account.Username);
            if (string.IsNullOrEmpty(key)) return;

            lock (_sync)
            {
                var doc = EnsureLoadedUnderLock(filePathOverride);
                if (!doc.Accounts.TryGetValue(key, out var entry))
                {
                    entry = new RuntimeStateEntry();
                    doc.Accounts[key] = entry;
                }

                entry.UserId = account.UserID;
                entry.Username = account.Username ?? string.Empty;
                if (lastUse > entry.LastUse)
                    entry.LastUse = lastUse;

                MarkDirtyUnderLock(filePathOverride);
            }
        }

        /// <summary>
        /// Hydrates in-memory <see cref="Account"/> models with persisted runtime timestamps and window coordinates
        /// without marking accounts dirty or triggering encrypted vault writes.
        /// </summary>
        public static int HydrateAccounts(IEnumerable<Account>? accounts, string? filePathOverride = null)
        {
            if (accounts == null) return 0;

            RuntimeStateDocument doc;
            lock (_sync)
            {
                doc = filePathOverride != null
                    ? LoadDocumentFromDisk(filePathOverride)
                    : EnsureLoadedUnderLock(null);
            }

            if (doc.Accounts.Count == 0)
                return 0;

            int hydratedCount = 0;
            foreach (var account in accounts)
            {
                if (account == null) continue;

                RuntimeStateEntry? entry = null;
                if (account.UserID > 0)
                {
                    doc.Accounts.TryGetValue($"uid:{account.UserID}", out entry);
                }
                if (entry == null && !string.IsNullOrWhiteSpace(account.Username))
                {
                    doc.Accounts.TryGetValue($"usr:{account.Username.Trim().ToLowerInvariant()}", out entry);
                }

                if (entry == null)
                    continue;

                bool changed = false;
                if (entry.LastUse > account.LastUse)
                {
                    account.LastUse = entry.LastUse;
                    changed = true;
                }

                if (entry.WindowX.HasValue &&
                    entry.WindowY.HasValue &&
                    entry.WindowWidth.HasValue &&
                    entry.WindowHeight.HasValue)
                {
                    account.ApplyWindowBoundsInMemory(
                        entry.WindowX.Value,
                        entry.WindowY.Value,
                        entry.WindowWidth.Value,
                        entry.WindowHeight.Value);
                    changed = true;
                }

                if (changed)
                    hydratedCount++;
            }

            return hydratedCount;
        }

        /// <summary>
        /// Immediately flushes any pending debounced runtime state changes to disk.
        /// </summary>
        public static bool Flush(string? filePathOverride = null)
        {
            byte[]? payloadToWrite = null;
            string targetPath;

            lock (_sync)
            {
                _debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                if (!_dirty && filePathOverride == null)
                    return false;

                var doc = EnsureLoadedUnderLock(filePathOverride);
                targetPath = filePathOverride ?? StoragePaths.AccountRuntimeStateFile;
                payloadToWrite = JsonSerializer.SerializeToUtf8Bytes(doc, ShiroAppJson.Default.RuntimeStateDocument);
                if (filePathOverride == null)
                    _dirty = false;
            }

            try
            {
                StoragePaths.AtomicWriteBytes(targetPath, payloadToWrite, durable: false);
                return true;
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[AccountRuntimeStateStore.Flush] Failed writing runtime state: {ex.Message}");
                return false;
            }
        }

        internal static void ResetInMemoryCacheForTests()
        {
            lock (_sync)
            {
                _debounceTimer?.Dispose();
                _debounceTimer = null;
                _document = null;
                _dirty = false;
            }
        }

        private static RuntimeStateDocument EnsureLoadedUnderLock(string? filePathOverride)
        {
            if (filePathOverride != null)
            {
                return _document ??= LoadDocumentFromDisk(filePathOverride);
            }

            return _document ??= LoadDocumentFromDisk(StoragePaths.AccountRuntimeStateFile);
        }

        private static RuntimeStateDocument LoadDocumentFromDisk(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    byte[] raw = File.ReadAllBytes(path);
                    if (raw.Length > 0)
                    {
                        var parsed = JsonSerializer.Deserialize(raw, ShiroAppJson.Default.RuntimeStateDocument);
                        if (parsed != null)
                        {
                            parsed.Accounts = new Dictionary<string, RuntimeStateEntry>(
                                parsed.Accounts ?? new Dictionary<string, RuntimeStateEntry>(),
                                StringComparer.OrdinalIgnoreCase);
                            return parsed;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[AccountRuntimeStateStore] Could not read {Path.GetFileName(path)}: {ex.Message}");
            }

            return new RuntimeStateDocument();
        }

        private static void MarkDirtyUnderLock(string? filePathOverride)
        {
            if (filePathOverride != null)
            {
                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(_document!, ShiroAppJson.Default.RuntimeStateDocument);
                StoragePaths.AtomicWriteBytes(filePathOverride, payload, durable: false);
                return;
            }

            _dirty = true;
            if (_debounceTimer == null)
            {
                _debounceTimer = new Timer(_ => Flush(), null, DebounceSaveDelayMs, Timeout.Infinite);
            }
            else
            {
                _debounceTimer.Change(DebounceSaveDelayMs, Timeout.Infinite);
            }
        }
    }
}
