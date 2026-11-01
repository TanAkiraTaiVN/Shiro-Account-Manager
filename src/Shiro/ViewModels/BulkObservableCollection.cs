#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Shiro.ViewModels
{
    public class BulkObservableCollection<T> : ObservableCollection<T>
    {
        private bool _suppressNotification;

        public BulkObservableCollection() : base() { }

        public BulkObservableCollection(IEnumerable<T> collection) : base(collection) { }

        public BulkObservableCollection(List<T> list) : base(list) { }

        public void ReplaceAll(IEnumerable<T> items)
        {
            _suppressNotification = true;
            try
            {
                Items.Clear();
                if (items != null)
                {
                    foreach (var item in items)
                    {
                        Items.Add(item);
                    }
                }
            }
            finally
            {
                _suppressNotification = false;
            }

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        public void AddRange(IEnumerable<T> items)
        {
            if (items == null) return;
            _suppressNotification = true;
            try
            {
                foreach (var item in items)
                {
                    Items.Add(item);
                }
            }
            finally
            {
                _suppressNotification = false;
            }

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        public void InsertRange(int index, IEnumerable<T> items)
        {
            if (items == null) return;
            _suppressNotification = true;
            try
            {
                int curr = index;
                foreach (var item in items)
                {
                    Items.Insert(curr++, item);
                }
            }
            finally
            {
                _suppressNotification = false;
            }

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        public void Sync(IList<T> newItems)
        {
            if (newItems == null || newItems.Count == 0)
            {
                if (Count > 0)
                    Clear();
                return;
            }

            if (Count == 0)
            {
                ReplaceAll(newItems);
                return;
            }

            // Adaptive notification strategy:
            // If the differences between collections are large (e.g. re-sorting, filtering, or bulk replacements),
            // a single atomic Reset event via ReplaceAll is 100x faster and prevents Avalonia layout thrashing.
            // Fine-grained Replace/Remove/Add events are preserved only for small incremental deltas.
            int threshold = Math.Max(5, (int)(Count * 0.15));
            int sizeDiff = Math.Abs(Count - newItems.Count);
            int sharedCount = Math.Min(Count, newItems.Count);
            int diffCount = sizeDiff;

            if (diffCount > threshold)
            {
                ReplaceAll(newItems);
                return;
            }

            for (int i = 0; i < sharedCount; i++)
            {
                if (!EqualityComparer<T>.Default.Equals(this[i], newItems[i]))
                {
                    diffCount++;
                    if (diffCount > threshold)
                        break;
                }
            }

            if (diffCount > threshold)
            {
                ReplaceAll(newItems);
                return;
            }

            // Remove extra items from the tail
            while (Count > newItems.Count)
            {
                RemoveAt(Count - 1);
            }

            // In-place update existing items where different
            for (int i = 0; i < sharedCount; i++)
            {
                if (!EqualityComparer<T>.Default.Equals(this[i], newItems[i]))
                {
                    this[i] = newItems[i];
                }
            }

            // Append remaining new items
            for (int i = Count; i < newItems.Count; i++)
            {
                Add(newItems[i]);
            }
        }

        protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
        {
            if (!_suppressNotification)
                base.OnCollectionChanged(e);
        }

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            if (!_suppressNotification)
                base.OnPropertyChanged(e);
        }
    }
}
