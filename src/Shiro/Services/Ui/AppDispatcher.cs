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

            try
            {
                // Guard: Do not touch Dispatcher.UIThread if Avalonia Application is not initialized.
                // Touching Dispatcher.UIThread before Application startup binds a null controlled implementation,
                // causing Dispatcher.MainLoop to fail with PlatformNotSupportedException.
                if (Avalonia.Application.Current != null)
                {
                    var avaloniaDispatcher = Dispatcher.UIThread;
                    if (avaloniaDispatcher != null)
                    {
                        if (priority == DispatcherPriority.Normal && avaloniaDispatcher.CheckAccess())
                            action();
                        else
                            avaloniaDispatcher.Post(action, priority);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"AppDispatcher.UIThreadPost dispatch failed: {ex.Message}");
            }

            // Direct invocation if no UI dispatcher is active (e.g. headless tests)
            action();
        }

        /// <summary>
        /// Asynchronously executes a function returning a value on the Avalonia UI Thread and awaits its result.
        /// </summary>
        public static async Task<T?> InvokeAsync<T>(Func<T> func)
        {
            if (func == null) return default;

            try
            {
                if (Avalonia.Application.Current != null)
                {
                    var avaloniaDispatcher = Dispatcher.UIThread;
                    if (avaloniaDispatcher != null)
                    {
                        if (avaloniaDispatcher.CheckAccess())
                            return func();
                        else
                            return await avaloniaDispatcher.InvokeAsync(func);
                    }
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"AppDispatcher.UIThreadPost dispatch failed: {ex.Message}");
            }

            return func();
        }

        /// <summary>
        /// Asynchronously executes an action on the Avalonia UI Thread and awaits its completion.
        /// </summary>
        public static async Task InvokeAsync(Action action)
        {
            if (action == null) return;

            try
            {
                if (Avalonia.Application.Current != null)
                {
                    var avaloniaDispatcher = Dispatcher.UIThread;
                    if (avaloniaDispatcher != null)
                    {
                        if (avaloniaDispatcher.CheckAccess())
                            action();
                        else
                            await avaloniaDispatcher.InvokeAsync(action);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"AppDispatcher.UIThreadPost dispatch failed: {ex.Message}");
            }

            action();
        }
    }
}
