#nullable enable
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Windows.Input;

namespace Shiro.Services
{
    /// <summary>
    /// Thread-safe ring buffer of recent emitted notifications (max 30), tracking unread state,
    /// severity filtering, and Notification Center popover open/close coordination.
    /// </summary>
    public sealed class NotificationHistoryService : INotifyPropertyChanged
    {
        public const int MaxHistoryCount = 30;
        public static NotificationHistoryService Instance { get; } = new();

        private readonly Lock _lock = new();
        private bool _isCenterOpen;
        private bool _filterWarningsOnly;
        private int _unreadCount;

        public event PropertyChangedEventHandler? PropertyChanged;

        public ObservableCollection<ToastItem> HistoryItems { get; } = [];
        public ObservableCollection<ToastItem> FilteredHistoryItems { get; } = [];

        public ICommand MarkAllReadCommand { get; }
        public ICommand ClearHistoryCommand { get; }
        public ICommand SetFilterCommand { get; }
        public ICommand CloseCenterCommand { get; }

        private NotificationHistoryService()
        {
            MarkAllReadCommand = new HistoryCommand(_ => MarkAllRead());
            ClearHistoryCommand = new HistoryCommand(_ => ClearHistory());
            SetFilterCommand = new HistoryCommand(ApplyFilterParameter);
            CloseCenterCommand = new HistoryCommand(_ => IsCenterOpen = false);
        }

        public bool IsCenterOpen
        {
            get { lock (_lock) return _isCenterOpen; }
            set
            {
                bool changed;
                lock (_lock)
                {
                    changed = _isCenterOpen != value;
                    _isCenterOpen = value;
                }
                if (changed)
                    RaisePropertyChanged(nameof(IsCenterOpen));
            }
        }

        public bool FilterWarningsOnly
        {
            get { lock (_lock) return _filterWarningsOnly; }
            set
            {
                bool changed;
                lock (_lock)
                {
                    changed = _filterWarningsOnly != value;
                    _filterWarningsOnly = value;
                }
                if (changed)
                {
                    AppDispatcher.Post(() =>
                    {
                        RebuildFilteredOnUi();
                        RaiseFilterProperties();
                    });
                }
            }
        }

        public bool IsFilterAll => !FilterWarningsOnly;

        public int UnreadCount
        {
            get { lock (_lock) return _unreadCount; }
        }

        public bool HasUnread => UnreadCount > 0;
        public bool IsFilteredEmpty => FilteredHistoryItems.Count == 0;

        public void Record(ToastItem? item)
        {
            if (item == null) return;

            AppDispatcher.Post(() =>
            {
                bool warningsOnly;
                int unread;
                lock (_lock)
                {
                    HistoryItems.Insert(0, item);
                    while (HistoryItems.Count > MaxHistoryCount)
                        HistoryItems.RemoveAt(HistoryItems.Count - 1);

                    warningsOnly = _filterWarningsOnly;
                    SyncFilteredCollectionLocked(warningsOnly);
                    unread = CountUnreadLocked();
                    _unreadCount = unread;
                }
                RaiseNotificationStateProperties();
            });
        }

        public void MarkAllRead()
        {
            AppDispatcher.Post(() =>
            {
                lock (_lock)
                {
                    foreach (var item in HistoryItems)
                        item.IsRead = true;
                    _unreadCount = 0;
                }
                RaiseNotificationStateProperties();
            });
        }

        public void ClearHistory()
        {
            AppDispatcher.Post(() =>
            {
                lock (_lock)
                {
                    HistoryItems.Clear();
                    FilteredHistoryItems.Clear();
                    _unreadCount = 0;
                }
                RaiseNotificationStateProperties();
            });
        }

        internal void ResetForTesting()
        {
            void ApplyReset()
            {
                lock (_lock)
                {
                    HistoryItems.Clear();
                    FilteredHistoryItems.Clear();
                    _unreadCount = 0;
                    _filterWarningsOnly = false;
                    _isCenterOpen = false;
                }
                RaisePropertyChanged(nameof(IsCenterOpen));
                RaiseFilterProperties();
                RaiseNotificationStateProperties();
            }

            if (AppDispatcher.CustomDispatcher != null ||
                Avalonia.Application.Current == null ||
                AppDispatcher.HasUiThreadAccess())
            {
                ApplyReset();
                return;
            }

            lock (_lock)
            {
                _unreadCount = 0;
                _filterWarningsOnly = false;
                _isCenterOpen = false;
            }
            AppDispatcher.Post(ApplyReset);
        }

        private void ApplyFilterParameter(object? parameter)
        {
            if (parameter is bool b)
            {
                FilterWarningsOnly = b;
                return;
            }

            if (parameter is string s)
            {
                if (string.Equals(s, "alerts", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s, "true", StringComparison.OrdinalIgnoreCase))
                {
                    FilterWarningsOnly = true;
                }
                else if (string.Equals(s, "all", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(s, "false", StringComparison.OrdinalIgnoreCase))
                {
                    FilterWarningsOnly = false;
                }
            }
        }

        private void RebuildFilteredOnUi()
        {
            lock (_lock)
            {
                SyncFilteredCollectionLocked(_filterWarningsOnly);
            }
        }

        private void SyncFilteredCollectionLocked(bool warningsOnly)
        {
            FilteredHistoryItems.Clear();
            foreach (var item in HistoryItems)
            {
                if (!warningsOnly || item.Type is ToastType.Warning or ToastType.Error)
                    FilteredHistoryItems.Add(item);
            }
        }

        private int CountUnreadLocked() => HistoryItems.Count(t => !t.IsRead);

        private void RaiseFilterProperties()
        {
            RaisePropertyChanged(nameof(FilterWarningsOnly));
            RaisePropertyChanged(nameof(IsFilterAll));
            RaisePropertyChanged(nameof(IsFilteredEmpty));
        }

        private void RaiseNotificationStateProperties()
        {
            RaisePropertyChanged(nameof(UnreadCount));
            RaisePropertyChanged(nameof(HasUnread));
            RaisePropertyChanged(nameof(IsFilteredEmpty));
        }

        private void RaisePropertyChanged(string propertyName)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        private sealed class HistoryCommand(Action<object?> execute) : ICommand
        {
            public event EventHandler? CanExecuteChanged { add { } remove { } }
            public bool CanExecute(object? parameter) => true;
            public void Execute(object? parameter) => execute(parameter);
        }
    }
}
