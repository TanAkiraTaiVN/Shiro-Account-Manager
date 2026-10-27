#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace RBX_Alt_Manager.Services
{
    /// <summary>
    /// Thread-safe, bounded Least Recently Used (LRU) memory cache designed for UI resources
    /// such as Avalonia Bitmap images. Automatically disposes evicted entries to prevent memory leaks.
    /// </summary>
    /// <typeparam name="TKey">The cache key type.</typeparam>
    /// <typeparam name="TValue">The disposable value type.</typeparam>
    public sealed class LruCache<TKey, TValue> : IDisposable where TKey : notnull where TValue : IDisposable
    {
        private readonly int _capacity;
        private readonly Dictionary<TKey, LinkedListNode<CacheEntry>> _index;
        private readonly LinkedList<CacheEntry> _lruList = new LinkedList<CacheEntry>();
        private readonly System.Threading.Lock _lock = new();
        private bool _disposed;

        private sealed class CacheEntry
        {
            public required TKey Key { get; init; }
            public required TValue Value { get; set; }
        }

        public LruCache(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be greater than zero.");

            _capacity = capacity;
            _index = new Dictionary<TKey, LinkedListNode<CacheEntry>>(capacity);
        }

        public int Count
        {
            get
            {
                lock (_lock) return _index.Count;
            }
        }

        public int Capacity => _capacity;

        public TValue this[TKey key]
        {
            get => TryGetValue(key, out var val) ? val : throw new KeyNotFoundException($"Key '{key}' not found.");
            set => Set(key, value);
        }

        public bool TryGetValue(TKey key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TValue? value)
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    value = default;
                    return false;
                }

                if (_index.TryGetValue(key, out var node))
                {
                    _lruList.Remove(node);
                    _lruList.AddFirst(node);
                    value = node.Value.Value!;
                    return true;
                }

                value = default;
                return false;
            }
        }

        public void Set(TKey key, TValue value)
        {
            TValue? evicted = default;
            bool shouldDispose = false;

            lock (_lock)
            {
                if (_disposed)
                {
                    try { value?.Dispose(); } catch (Exception) { }
                    return;
                }

                if (_index.TryGetValue(key, out var existingNode))
                {
                    evicted = existingNode.Value.Value;
                    shouldDispose = !EqualityComparer<TValue>.Default.Equals(evicted, value);
                    existingNode.Value.Value = value;
                    _lruList.Remove(existingNode);
                    _lruList.AddFirst(existingNode);
                }
                else
                {
                    if (_index.Count >= _capacity)
                    {
                        var oldest = _lruList.Last;
                        if (oldest != null)
                        {
                            _lruList.RemoveLast();
                            _index.Remove(oldest.Value.Key);
                            evicted = oldest.Value.Value;
                            shouldDispose = true;
                        }
                    }

                    var newEntry = new CacheEntry { Key = key, Value = value };
                    var newNode = new LinkedListNode<CacheEntry>(newEntry);
                    _lruList.AddFirst(newNode);
                    _index[key] = newNode;
                }
            }

            if (shouldDispose && evicted != null)
            {
                try { evicted.Dispose(); } catch (Exception) { }
            }
        }

        public bool TryRemove(TKey key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TValue? removed)
        {
            lock (_lock)
            {
                if (_index.TryGetValue(key, out var node))
                {
                    _lruList.Remove(node);
                    _index.Remove(key);
                    removed = node.Value.Value!;
                    return true;
                }

                removed = default;
                return false;
            }
        }

        public void Clear()
        {
            List<TValue> toDispose;
            lock (_lock)
            {
                toDispose = _lruList.Select(n => n.Value).ToList();
                _lruList.Clear();
                _index.Clear();
            }

            foreach (var val in toDispose)
            {
                try { val?.Dispose(); } catch (Exception) { }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Clear();
        }
    }
}
