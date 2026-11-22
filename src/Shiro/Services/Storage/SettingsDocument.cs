#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    /// <summary>
    /// Unified atomic JSON settings document supporting multi-section updates,
    /// batched save coalescing, and empty-string value preservation.
    /// </summary>
    public sealed class SettingsDocument
    {
        private readonly Lock _sync = new();
        private readonly SemaphoreSlim _flushGate = new(1, 1);
        private readonly IniFile _ini;
        private readonly Action? _onAtomicWrite;
        private int _batchDepth;
        private bool _dirty;
        private bool _deferredSaveInBatch;
        private long _mutationVersion;

        public string FilePath { get; }

        public bool IsDirty
        {
            get
            {
                lock (_sync)
                    return _dirty;
            }
        }

        public IniFile BackingIni => _ini;

        public SettingsDocument(string path, Action? onAtomicWrite = null)
            : this(new IniFile(path), path, onAtomicWrite)
        {
        }

        internal SettingsDocument(IniFile backingIni, string path, Action? onAtomicWrite = null)
        {
            _ini = backingIni ?? throw new ArgumentNullException(nameof(backingIni));
            FilePath = path ?? throw new ArgumentNullException(nameof(path));
            _onAtomicWrite = onAtomicWrite;
            _ini.OnModified += MarkDirty;
        }

        private void MarkDirty()
        {
            lock (_sync)
            {
                _dirty = true;
                _mutationVersion++;
            }
        }

        public IniSection Section(string? sectionName) => _ini.Section(sectionName);

        public string? Get(string section, string key) => _ini.Section(section).Get(key);

        public T? Get<T>(string section, string key, T? defaultValue = default) =>
            _ini.Section(section).Get(key, defaultValue);

        public bool Exists(string section, string key) => _ini.Section(section).Exists(key);

        public void Set(string section, string key, string? value)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (_sync)
            {
                _ini.Section(section).Set(key, value);
                _dirty = true;
                _mutationVersion++;
            }
        }

        public void Remove(string section, string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (_sync)
            {
                _ini.Section(section).RemoveProperty(key);
                _dirty = true;
                _mutationVersion++;
            }
        }

        public void RemoveSection(string section)
        {
            if (string.IsNullOrEmpty(section)) return;
            lock (_sync)
            {
                _ini.RemoveSection(section);
                _dirty = true;
                _mutationVersion++;
            }
        }

        public IDisposable BeginBatch()
        {
            lock (_sync)
            {
                _batchDepth++;
            }
            return new BatchScope(this);
        }

        public void Save()
        {
            string resolvedPath = StoragePaths.ResolveUserFile(FilePath);
            if (!TryPrepareFlush(resolvedPath, "save"))
                return;

            _flushGate.Wait();
            try
            {
                if (!TryCaptureSnapshot(resolvedPath, out byte[] bytesToWrite, out long capturedVersion))
                    return;

                StoragePaths.AtomicWriteBytes(resolvedPath, bytesToWrite, durable: true);
                MarkFlushedIfUnchanged(capturedVersion);
            }
            finally
            {
                _flushGate.Release();
            }
        }

        public async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string resolvedPath = StoragePaths.ResolveUserFile(FilePath);
            if (!TryPrepareFlush(resolvedPath, "flush"))
                return;

            await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!TryCaptureSnapshot(resolvedPath, out byte[] bytesToWrite, out long capturedVersion))
                    return;

                await StoragePaths.AtomicWriteBytesAsync(resolvedPath, bytesToWrite, durable: true, cancellationToken).ConfigureAwait(false);
                MarkFlushedIfUnchanged(capturedVersion);
            }
            finally
            {
                _flushGate.Release();
            }
        }

        private bool TryPrepareFlush(string resolvedPath, string operation)
        {
            lock (_sync)
            {
                if (_batchDepth > 0)
                {
                    _deferredSaveInBatch = true;
                    return false;
                }

                if (!_dirty && File.Exists(resolvedPath))
                    return false;
            }

            string? directory = Path.GetDirectoryName(resolvedPath);
            if (string.IsNullOrEmpty(directory))
            {
                AppServices.Logger.Error($"Cannot {operation} settings, target directory does not exist: {FilePath}");
                return false;
            }

            Directory.CreateDirectory(directory);
            return true;
        }

        private bool TryCaptureSnapshot(string resolvedPath, out byte[] bytesToWrite, out long capturedVersion)
        {
            lock (_sync)
            {
                if (_batchDepth > 0)
                {
                    _deferredSaveInBatch = true;
                    bytesToWrite = Array.Empty<byte>();
                    capturedVersion = 0;
                    return false;
                }

                if (!_dirty && File.Exists(resolvedPath))
                {
                    bytesToWrite = Array.Empty<byte>();
                    capturedVersion = 0;
                    return false;
                }

                bytesToWrite = _ini.ToUtf8JsonBytes();
                capturedVersion = _mutationVersion;
                return true;
            }
        }

        private void MarkFlushedIfUnchanged(long capturedVersion)
        {
            lock (_sync)
            {
                if (_mutationVersion == capturedVersion)
                {
                    _dirty = false;
                    _deferredSaveInBatch = false;
                }
            }
            _onAtomicWrite?.Invoke();
        }

        private void EndBatch()
        {
            bool shouldSave = false;
            lock (_sync)
            {
                if (_batchDepth > 0)
                    _batchDepth--;

                if (_batchDepth == 0 && _deferredSaveInBatch)
                {
                    _deferredSaveInBatch = false;
                    shouldSave = true;
                }
            }

            if (shouldSave)
                Save();
        }

        public string ToJsonString() => _ini.ToJsonString();

        private sealed class BatchScope(SettingsDocument owner) : IDisposable
        {
            private SettingsDocument? _owner = owner;
            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndBatch();
        }
    }
}
