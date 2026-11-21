#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

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
        private static IAppLogger _logger = AppServices.Logger;
        public static IAppLogger Logger
        {
            get => _logger;
            set => _logger = value ?? throw new ArgumentNullException(nameof(value));
        }

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
        private static string? _loadedFilePath;
        private static readonly Dictionary<string, RuntimeStateDocument> _overrideDocuments = new(StringComparer.OrdinalIgnoreCase);
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
                var entry = GetOrCreateEntryUnderLock(account, key, filePathOverride, out var doc);
                if (account.LastUse > entry.LastUse)
                    entry.LastUse = account.LastUse;
                entry.WindowX = x;
                entry.WindowY = y;
                entry.WindowWidth = width;
                entry.WindowHeight = height;

                MarkDirtyUnderLock(filePathOverride, doc);
            }
        }

        public static void RecordLastUse(Account account, DateTime lastUse, string? filePathOverride = null)
        {
            if (account == null) return;
            string key = BuildAccountKey(account.UserID, account.Username);
            if (string.IsNullOrEmpty(key)) return;

            lock (_sync)
            {
                var entry = GetOrCreateEntryUnderLock(account, key, filePathOverride, out var doc);
                if (lastUse > entry.LastUse)
                    entry.LastUse = lastUse;

                MarkDirtyUnderLock(filePathOverride, doc);
            }
        }

        /// <summary>
        /// Hydrates in-memory <see cref="Account"/> models with persisted runtime timestamps and window coordinates
        /// without marking accounts dirty or triggering encrypted vault writes.
        /// </summary>
        public static int HydrateAccounts(IEnumerable<Account>? accounts, string? filePathOverride = null)
        {
            if (accounts == null) return 0;

            var snapshots = new List<(Account Account, DateTime LastUse, int? WindowX, int? WindowY, int? WindowWidth, int? WindowHeight)>();
            lock (_sync)
            {
                RuntimeStateDocument doc = filePathOverride != null
                    ? LoadDocumentFromDisk(filePathOverride)
                    : EnsureLoadedUnderLock(null);

                if (doc.Accounts.Count == 0)
                    return 0;

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

                    snapshots.Add((account, entry.LastUse, entry.WindowX, entry.WindowY, entry.WindowWidth, entry.WindowHeight));
                }
            }

            int hydratedCount = 0;
            foreach (var item in snapshots)
            {
                bool changed = false;
                if (item.LastUse > item.Account.LastUse)
                {
                    item.Account.LastUse = item.LastUse;
                    changed = true;
                }

                if (item.WindowX.HasValue &&
                    item.WindowY.HasValue &&
                    item.WindowWidth.HasValue &&
                    item.WindowHeight.HasValue)
                {
                    item.Account.ApplyWindowBoundsInMemory(
                        item.WindowX.Value,
                        item.WindowY.Value,
                        item.WindowWidth.Value,
                        item.WindowHeight.Value);
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
            lock (_sync)
            {
                if (filePathOverride == null)
                {
                    _debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                    if (!_dirty)
                        return false;
                }

                RuntimeStateDocument doc = EnsureLoadedUnderLock(filePathOverride);
                string targetPath = filePathOverride ?? StoragePaths.AccountRuntimeStateFile;
                byte[] payloadToWrite = JsonSerializer.SerializeToUtf8Bytes(doc, ShiroAppJson.Default.RuntimeStateDocument);

                try
                {
                    StoragePaths.AtomicWriteBytes(targetPath, payloadToWrite, durable: false);
                    if (filePathOverride == null)
                    {
                        _dirty = false;
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.Debug($"[AccountRuntimeStateStore.Flush] Failed writing runtime state: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// Flushes any pending runtime state changes and disposes the debounce timer.
        /// Subsequent writes lazily recreate the timer.
        /// </summary>
        public static bool Shutdown()
        {
            bool flushed = Flush();
            lock (_sync)
            {
                if (_debounceTimer != null)
                {
                    try { _debounceTimer.Change(Timeout.Infinite, Timeout.Infinite); }
                    catch (ObjectDisposedException) { /* already disposed */ }

                    try { _debounceTimer.Dispose(); }
                    catch (ObjectDisposedException) { /* already disposed */ }

                    _debounceTimer = null;
                }
            }
            return flushed;
        }

        internal static void ResetInMemoryCacheForTests()
        {
            lock (_sync)
            {
                _debounceTimer?.Dispose();
                _debounceTimer = null;
                _document = null;
                _loadedFilePath = null;
                _overrideDocuments.Clear();
                _dirty = false;
            }
        }

        private static RuntimeStateEntry GetOrCreateEntryUnderLock(
            Account account,
            string key,
            string? filePathOverride,
            out RuntimeStateDocument doc)
        {
            doc = EnsureLoadedUnderLock(filePathOverride);
            if (!doc.Accounts.TryGetValue(key, out var entry))
            {
                entry = new RuntimeStateEntry();
                doc.Accounts[key] = entry;
            }

            entry.UserId = account.UserID;
            entry.Username = account.Username ?? string.Empty;
            return entry;
        }

        private static RuntimeStateDocument EnsureLoadedUnderLock(string? filePathOverride)
        {
            if (filePathOverride != null)
            {
                string overrideKey = NormalizeOverrideKey(filePathOverride);
                if (!_overrideDocuments.TryGetValue(overrideKey, out var overrideDoc))
                {
                    overrideDoc = LoadDocumentFromDisk(filePathOverride);
                    _overrideDocuments[overrideKey] = overrideDoc;
                }
                return overrideDoc;
            }

            string resolvedPath = StoragePaths.AccountRuntimeStateFile;
            if (_document != null && string.Equals(_loadedFilePath, resolvedPath, StringComparison.OrdinalIgnoreCase))
                return _document;

            _loadedFilePath = resolvedPath;
            _document = LoadDocumentFromDisk(resolvedPath);
            return _document;
        }

        private static string NormalizeOverrideKey(string filePathOverride)
        {
            if (string.IsNullOrWhiteSpace(filePathOverride))
                return filePathOverride;

            try
            {
                return Path.GetFullPath(filePathOverride);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return filePathOverride;
            }
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
                _logger.Debug($"[AccountRuntimeStateStore] Could not read {Path.GetFileName(path)}: {ex.Message}");
            }

            return new RuntimeStateDocument();
        }

        private static void MarkDirtyUnderLock(string? filePathOverride, RuntimeStateDocument doc)
        {
            if (filePathOverride != null)
            {
                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(doc, ShiroAppJson.Default.RuntimeStateDocument);
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
