#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Shiro.Services
{
    /// <summary>
    /// Thread-safe, bounded Least Recently Used (LRU) memory cache with lock-free reads.
    /// Supports opt-in disposal on eviction; UI bitmap caches pass <c>disposeOnEvict: false</c>
    /// so bitmaps still bound to active controls are never disposed while displayed.
    /// </summary>
    /// <typeparam name="TKey">The cache key type.</typeparam>
    /// <typeparam name="TValue">The disposable value type.</typeparam>
    public sealed class LruCache<TKey, TValue> : IDisposable where TKey : notnull where TValue : IDisposable
    {
        private readonly int _capacity;
        private readonly bool _disposeOnEvict;
        private readonly ConcurrentDictionary<TKey, CacheEntry> _entries;
        private readonly System.Threading.Lock _writeLock = new();
        private long _clock;
        private volatile bool _disposed;

        private sealed class CacheEntry
        {
            public required TKey Key { get; init; }
            public required TValue Value { get; set; }
            public long Stamp;
        }

        public LruCache(int capacity, bool disposeOnEvict = true)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be greater than zero.");

            _capacity = capacity;
            _disposeOnEvict = disposeOnEvict;
            _entries = new ConcurrentDictionary<TKey, CacheEntry>(Environment.ProcessorCount, capacity);
        }

        public int Count => _entries.Count;

        public int Capacity => _capacity;

        public TValue this[TKey key]
        {
            get => TryGetValue(key, out var val) ? val : throw new KeyNotFoundException($"Key '{key}' not found.");
            set => Set(key, value);
        }

        public bool TryGetValue(TKey key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TValue? value)
        {
            if (_disposed)
            {
                value = default;
                return false;
            }

            if (_entries.TryGetValue(key, out var entry))
            {
                Interlocked.Exchange(ref entry.Stamp, Interlocked.Increment(ref _clock));
                value = entry.Value!;
                return true;
            }

            value = default;
            return false;
        }

        public void Set(TKey key, TValue value)
        {
            List<TValue>? toDispose = null;

            lock (_writeLock)
            {
                if (_disposed)
                {
                    if (_disposeOnEvict)
                    {
                        SafeExecuteHelper.Try(() => value?.Dispose(), "LruBitmapCache.Set.Disposed");
                    }
                    return;
                }

                long stamp = Interlocked.Increment(ref _clock);
                if (_entries.TryGetValue(key, out var existingNode))
                {
                    TValue previous = existingNode.Value;
                    existingNode.Value = value;
                    Interlocked.Exchange(ref existingNode.Stamp, stamp);

                    if (_disposeOnEvict && !EqualityComparer<TValue>.Default.Equals(previous, value))
                    {
                        toDispose = new List<TValue>(1) { previous };
                    }
                }
                else
                {
                    while (_entries.Count >= _capacity)
                    {
                        CacheEntry? oldest = null;
                        long minStamp = long.MaxValue;

                        foreach (var kvp in _entries)
                        {
                            long s = Interlocked.Read(ref kvp.Value.Stamp);
                            if (s < minStamp)
                            {
                                minStamp = s;
                                oldest = kvp.Value;
                            }
                        }

                        if (oldest == null)
                            break;

                        if (_entries.TryRemove(oldest.Key, out var removed) && _disposeOnEvict && removed.Value != null)
                        {
                            toDispose ??= new List<TValue>(1);
                            toDispose.Add(removed.Value);
                        }
                    }

                    _entries[key] = new CacheEntry
                    {
                        Key = key,
                        Value = value,
                        Stamp = stamp
                    };
                }
            }

            if (toDispose != null)
            {
                foreach (var evicted in toDispose)
                {
                    var captured = evicted;
                    AppDispatcher.Post(() =>
                    {
                        SafeExecuteHelper.Try(() => captured.Dispose(), "LruBitmapCache.Set.Evict");
                    });
                }
            }
        }

        public bool TryRemove(TKey key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TValue? removed)
        {
            lock (_writeLock)
            {
                if (_entries.TryRemove(key, out var entry))
                {
                    removed = entry.Value!;
                    return true;
                }

                removed = default;
                return false;
            }
        }

        public void Clear()
        {
            List<TValue>? toDispose = null;
            lock (_writeLock)
            {
                if (_disposeOnEvict && !_entries.IsEmpty)
                {
                    toDispose = new List<TValue>(_entries.Count);
                    foreach (var kvp in _entries)
                    {
                        if (kvp.Value.Value != null)
                            toDispose.Add(kvp.Value.Value);
                    }
                }
                _entries.Clear();
            }

            if (toDispose != null)
            {
                foreach (var val in toDispose)
                {
                    var captured = val;
                    AppDispatcher.Post(() =>
                    {
                        SafeExecuteHelper.Try(() => captured?.Dispose(), "LruBitmapCache.Clear");
                    });
                }
            }
        }

        internal List<TValue> ClearAndDrain()
        {
            var drained = new List<TValue>();
            lock (_writeLock)
            {
                foreach (var kvp in _entries)
                {
                    if (kvp.Value.Value != null)
                        drained.Add(kvp.Value.Value);
                }
                _entries.Clear();
            }
            return drained;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Clear();
        }
    }
}
