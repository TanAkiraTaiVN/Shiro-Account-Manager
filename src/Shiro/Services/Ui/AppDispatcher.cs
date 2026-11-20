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
        private static volatile int _uiThreadId;
        private static volatile bool _isUiShuttingDown;
        private static readonly EventHandler s_onDispatcherShutdown = OnDispatcherShutdown;

        public static Action<Action>? CustomDispatcher { get; set; }

        internal static void RegisterUiThread()
        {
            _uiThreadId = Environment.CurrentManagedThreadId;
            _isUiShuttingDown = false;
            var dispatcher = Dispatcher.UIThread;
            dispatcher.ShutdownStarted -= s_onDispatcherShutdown;
            dispatcher.ShutdownStarted += s_onDispatcherShutdown;
            dispatcher.ShutdownFinished -= s_onDispatcherShutdown;
            dispatcher.ShutdownFinished += s_onDispatcherShutdown;
        }

        private static void OnDispatcherShutdown(object? sender, EventArgs e)
            => _isUiShuttingDown = true;

        internal static bool HasUiThreadAccess()
        {
            if (CustomDispatcher != null || Avalonia.Application.Current == null || _isUiShuttingDown)
                return false;

            int uiThreadId = _uiThreadId;
            if (uiThreadId != 0 && uiThreadId != Environment.CurrentManagedThreadId)
                return false;

            return Dispatcher.UIThread.CheckAccess();
        }

        internal static bool CanMutateUiResources()
        {
            if (Avalonia.Application.Current == null || _isUiShuttingDown)
                return false;

            int uiThreadId = _uiThreadId;
            return uiThreadId == 0 || uiThreadId == Environment.CurrentManagedThreadId;
        }

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
                    if (priority == DispatcherPriority.Normal &&
                        (_uiThreadId == 0 || _uiThreadId == Environment.CurrentManagedThreadId) &&
                        avaloniaDispatcher!.CheckAccess())
                    {
                        action();
                    }
                    else
                    {
                        avaloniaDispatcher!.Post(action, priority);
                    }
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
            // Guard: Do not touch Dispatcher.UIThread if Avalonia Application is not initialized or shutting down.
            // Touching Dispatcher.UIThread before Application startup binds a null controlled implementation,
            // causing Dispatcher.MainLoop to fail with PlatformNotSupportedException.
            if (Avalonia.Application.Current == null || _isUiShuttingDown)
                return false;

            try
            {
                dispatcher = Dispatcher.UIThread;
                return dispatcher != null && !_isUiShuttingDown;
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
