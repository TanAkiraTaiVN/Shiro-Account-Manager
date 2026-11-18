#nullable enable
using System;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace Shiro.Services
{
    /// <summary>
    /// Pure Avalonia UI 11 high-performance UI Dispatcher abstraction.
    /// Bridges UI thread execution directly to Avalonia Dispatcher.UIThread.
    /// </summary>
    public static class AppDispatcher
    {
        public static Action<Action>? CustomDispatcher { get; set; }

        /// <summary>
        /// Executes an action safely on the Avalonia UI Thread.
        /// </summary>
        public static void Post(Action action) => PostWithPriority(action, DispatcherPriority.Normal);

        /// <summary>
        /// Queues a non-urgent UI update at Background priority so input and scroll rendering always take precedence.
        /// </summary>
        public static void PostBackground(Action action) => PostWithPriority(action, DispatcherPriority.Background);

        private static void PostWithPriority(Action action, DispatcherPriority priority)
        {
            if (action == null) return;

            if (CustomDispatcher != null)
            {
                CustomDispatcher(action);
                return;
            }

            bool hasActiveDispatcher = TryGetActiveDispatcher(out var avaloniaDispatcher);
            DispatchOrFallback(
                hasActiveDispatcher,
                dispatch: () =>
                {
                    if (priority == DispatcherPriority.Normal && avaloniaDispatcher!.CheckAccess())
                        action();
                    else
                        avaloniaDispatcher!.Post(action, priority);
                },
                fallback: action);
        }

        /// <summary>
        /// Asynchronously executes a function returning a value on the Avalonia UI Thread and awaits its result.
        /// </summary>
        public static async Task<T?> InvokeAsync<T>(Func<T> func)
        {
            if (func == null) return default;

            bool hasActiveDispatcher = TryGetActiveDispatcher(out var avaloniaDispatcher);
            return await DispatchOrFallbackAsync(
                hasActiveDispatcher,
                dispatch: async () =>
                {
                    if (avaloniaDispatcher!.CheckAccess())
                        return func();

                    return await avaloniaDispatcher.InvokeAsync(func);
                },
                fallback: () => Task.FromResult<T?>(func()));
        }

        /// <summary>
        /// Asynchronously executes an action on the Avalonia UI Thread and awaits its completion.
        /// </summary>
        public static async Task InvokeAsync(Action action)
        {
            if (action == null) return;

            bool hasActiveDispatcher = TryGetActiveDispatcher(out var avaloniaDispatcher);
            await DispatchOrFallbackAsync(
                hasActiveDispatcher,
                dispatch: async () =>
                {
                    if (avaloniaDispatcher!.CheckAccess())
                        action();
                    else
                        await avaloniaDispatcher.InvokeAsync(action);
                },
                fallback: () =>
                {
                    action();
                    return Task.CompletedTask;
                });
        }

        private static bool TryGetActiveDispatcher(out Dispatcher? dispatcher)
        {
            dispatcher = null;
            // Guard: Do not touch Dispatcher.UIThread if Avalonia Application is not initialized.
            // Touching Dispatcher.UIThread before Application startup binds a null controlled implementation,
            // causing Dispatcher.MainLoop to fail with PlatformNotSupportedException.
            if (Avalonia.Application.Current == null)
                return false;

            try
            {
                dispatcher = Dispatcher.UIThread;
                return dispatcher != null;
            }
            catch (PlatformNotSupportedException ex)
            {
                AppServices.Logger?.Debug($"AppDispatcher.TryGetActiveDispatcher failed: {ex.Message}");
                dispatcher = null;
                return false;
            }
        }

        internal static void DispatchOrFallback(bool hasActiveDispatcher, Action dispatch, Action fallback)
        {
            if (hasActiveDispatcher)
            {
                try
                {
                    dispatch();
                }
                catch (Exception ex)
                {
                    AppServices.Logger?.Debug($"AppDispatcher.UIThreadPost dispatch failed: {ex.Message}");
                }
                return;
            }

            fallback();
        }

        internal static Task DispatchOrFallbackAsync(
            bool hasActiveDispatcher,
            Func<Task> dispatch,
            Func<Task> fallback)
        {
            return hasActiveDispatcher ? dispatch() : fallback();
        }

        internal static Task<T?> DispatchOrFallbackAsync<T>(
            bool hasActiveDispatcher,
            Func<Task<T?>> dispatch,
            Func<Task<T?>> fallback)
        {
            return hasActiveDispatcher ? dispatch() : fallback();
        }
    }
}
