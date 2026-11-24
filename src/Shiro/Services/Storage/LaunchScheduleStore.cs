#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Shiro.Services
{
    /// <summary>
    /// Thread-safe, atomic persistence store for scheduled launches and run history.
    /// Uses source-generated JSON serialization with .tmp write-through replacement
    /// and .backup recovery when the primary file is missing or corrupted.
    /// </summary>
    public sealed class LaunchScheduleStore
    {
        public const int MaxHistoryEntries = 50;

        private readonly Func<string> _filePathProvider;
        private readonly IAppLogger? _logger;
        private readonly Lock _sync = new();

        public LaunchScheduleStore(Func<string>? filePathProvider = null, IAppLogger? logger = null)
        {
            _filePathProvider = filePathProvider ?? (() => StoragePaths.LaunchSchedulesFile);
            _logger = logger;
        }

        private IAppLogger Logger => _logger ?? AppServices.Logger;

        public LaunchScheduleDocument Load()
        {
            lock (_sync)
            {
                string path = _filePathProvider();
                if (TryLoadFromFile(path, out LaunchScheduleDocument primaryDoc))
                    return primaryDoc;

                string backupPath = path + ".backup";
                if (TryLoadFromFile(backupPath, out LaunchScheduleDocument backupDoc))
                {
                    Logger.Warn($"[LaunchScheduleStore] Primary schedule file unavailable or invalid; restored from backup.");
                    return backupDoc;
                }

                return new LaunchScheduleDocument();
            }
        }

        public void Save(LaunchScheduleDocument doc)
        {
            ArgumentNullException.ThrowIfNull(doc);

            LaunchScheduleDocument normalized = NormalizeForSave(doc);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(normalized, ShiroAppJson.Default.LaunchScheduleDocument);

            lock (_sync)
            {
                string path = _filePathProvider();
                if (string.IsNullOrWhiteSpace(path))
                    throw new InvalidOperationException("Schedule storage file path cannot be empty.");

                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                string tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
                string backupPath = path + ".backup";

                try
                {
                    using (var stream = new FileStream(
                        tempPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 64 * 1024,
                        options: FileOptions.WriteThrough))
                    {
                        stream.Write(payload);
                        stream.Flush(flushToDisk: true);
                    }

                    if (File.Exists(path))
                    {
                        try
                        {
                            File.Replace(tempPath, path, backupPath, ignoreMetadataErrors: true);
                        }
                        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or UnauthorizedAccessException)
                        {
                            Logger.Debug($"[LaunchScheduleStore] File.Replace failed, falling back to copy+move: {ex.Message}");
                            RotateBackupViaCopy(path, backupPath);
                            File.Move(tempPath, path, overwrite: true);
                        }
                    }
                    else
                    {
                        File.Move(tempPath, path, overwrite: true);
                    }
                }
                finally
                {
                    TryDeleteFile(tempPath);
                }
            }
        }

        public void Flush()
        {
            lock (_sync)
            {
                // Save writes synchronously and durably; acquiring the lock acts as a completion barrier.
            }
        }

        private bool TryLoadFromFile(string path, out LaunchScheduleDocument document)
        {
            document = new LaunchScheduleDocument();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return false;

            try
            {
                byte[] raw = File.ReadAllBytes(path);
                if (raw.Length == 0)
                    return false;

                LaunchScheduleDocument? parsed = JsonSerializer.Deserialize(raw, ShiroAppJson.Default.LaunchScheduleDocument);
                if (parsed == null)
                    return false;

                document = NormalizeLoadedDocument(parsed);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Debug($"[LaunchScheduleStore] Failed to load '{Path.GetFileName(path)}': {ex.Message}");
                return false;
            }
        }

        private static LaunchScheduleDocument NormalizeLoadedDocument(LaunchScheduleDocument doc)
        {
            doc.SchemaVersion = doc.SchemaVersion <= 0 ? 1 : doc.SchemaVersion;
            doc.Schedules ??= new List<LaunchSchedule>();
            doc.History ??= new List<ScheduleRunEntry>();

            foreach (LaunchSchedule schedule in doc.Schedules)
            {
                if (schedule != null)
                    schedule.TargetAccounts ??= new List<ScheduleAccountRef>();
            }

            return doc;
        }

        private static LaunchScheduleDocument NormalizeForSave(LaunchScheduleDocument doc)
        {
            var schedules = new List<LaunchSchedule>(doc.Schedules?.Count ?? 0);
            if (doc.Schedules != null)
            {
                foreach (LaunchSchedule schedule in doc.Schedules)
                {
                    if (schedule != null)
                        schedules.Add(schedule.Clone());
                }
            }

            List<ScheduleRunEntry> history = (doc.History ?? new List<ScheduleRunEntry>())
                .Where(entry => entry != null)
                .OrderByDescending(entry => entry.TimestampUtc)
                .Take(MaxHistoryEntries)
                .ToList();

            return new LaunchScheduleDocument
            {
                SchemaVersion = doc.SchemaVersion <= 0 ? 1 : doc.SchemaVersion,
                Schedules = schedules,
                History = history
            };
        }

        private void RotateBackupViaCopy(string primaryPath, string backupPath)
        {
            string backupTemp = backupPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(primaryPath, backupTemp, overwrite: true);
                File.Move(backupTemp, backupPath, overwrite: true);
            }
            finally
            {
                TryDeleteFile(backupTemp);
            }
        }

        private void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                Logger.Debug($"[LaunchScheduleStore] Cleanup failed for '{Path.GetFileName(path)}': {ex.Message}");
            }
        }
    }
}
