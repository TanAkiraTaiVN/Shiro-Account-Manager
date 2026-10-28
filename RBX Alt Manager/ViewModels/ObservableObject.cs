#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Shiro.ViewModels
{
    /// <summary>
    /// Modern base class for observable ViewModel entities conforming to INotifyPropertyChanged.
    /// Caches PropertyChangedEventArgs instances for zero-allocation property notifications.
    /// </summary>
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        private static readonly PropertyChangedEventArgs AllPropertiesChangedArgs = new(string.Empty);
        private static readonly ConcurrentDictionary<string, PropertyChangedEventArgs> EventArgsCache = new(StringComparer.Ordinal);

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            var handler = PropertyChanged;
            if (handler == null) return;

            PropertyChangedEventArgs args = string.IsNullOrEmpty(propertyName)
                ? AllPropertiesChangedArgs
                : EventArgsCache.GetOrAdd(propertyName, static name => new PropertyChangedEventArgs(name));

            handler.Invoke(this, args);
        }

        protected virtual bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(storage, value))
                return false;

            storage = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected virtual bool SetProperty<T>(ref T storage, T value, Action? onChanged, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(storage, value))
                return false;

            storage = value;
            onChanged?.Invoke();
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
