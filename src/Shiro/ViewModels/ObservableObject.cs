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

    internal static class WeakEventSubscription
    {
        public static EventHandler Create<TTarget>(
            TTarget target,
            Action<TTarget, object?, EventArgs> onEvent,
            Action<EventHandler> detach)
            where TTarget : class
        {
            var weakRef = new WeakReference<TTarget>(target);
            EventHandler? handler = null;
            handler = (sender, args) =>
            {
                if (weakRef.TryGetTarget(out var live))
                    onEvent(live, sender, args);
                else if (handler != null)
                    detach(handler);
            };
            return handler;
        }

        public static EventHandler<TArgs> Create<TTarget, TArgs>(
            TTarget target,
            Action<TTarget, object?, TArgs> onEvent,
            Action<EventHandler<TArgs>> detach)
            where TTarget : class
        {
            var weakRef = new WeakReference<TTarget>(target);
            EventHandler<TArgs>? handler = null;
            handler = (sender, args) =>
            {
                if (weakRef.TryGetTarget(out var live))
                    onEvent(live, sender, args);
                else if (handler != null)
                    detach(handler);
            };
            return handler;
        }

        public static Action<TArg> CreateAction<TTarget, TArg>(
            TTarget target,
            Action<TTarget, TArg> onEvent,
            Action<Action<TArg>> detach)
            where TTarget : class
        {
            var weakRef = new WeakReference<TTarget>(target);
            Action<TArg>? handler = null;
            handler = arg =>
            {
                if (weakRef.TryGetTarget(out var live))
                    onEvent(live, arg);
                else if (handler != null)
                    detach(handler);
            };
            return handler;
        }

        public static Action CreateAction<TTarget>(
            TTarget target,
            Action<TTarget> onEvent,
            Action<Action> detach)
            where TTarget : class
        {
            var weakRef = new WeakReference<TTarget>(target);
            Action? handler = null;
            handler = () =>
            {
                if (weakRef.TryGetTarget(out var live))
                    onEvent(live);
                else if (handler != null)
                    detach(handler);
            };
            return handler;
        }
    }
}
