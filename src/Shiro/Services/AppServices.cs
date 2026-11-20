#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace Shiro.Services
{
    /// <summary>
    /// Centralized Dependency Injection Container and Service Provider facade for Shiro Account Manager.
    /// Composition, keyed section adapters, and lifecycle disposal are owned by <see cref="AppBootstrap"/>.
    /// </summary>
    public static class AppServices
    {
        private static AppBootstrap? _bootstrap;
        private static IServiceProvider? _serviceProvider;
        private static IServiceCollection? _currentServices;
        private static readonly Lock _initLock = new();
        private static readonly Stack<OverrideScope> _overrideStack = new();
        private static readonly ISettingsService _fallbackGeneralSettings = new FallbackGeneralSettingsService();

        internal static AppBootstrap? CurrentBootstrap
        {
            get
            {
                lock (_initLock)
                    return _bootstrap;
            }
        }

        public static IServiceProvider ServiceProvider
        {
            get
            {
                var current = Volatile.Read(ref _serviceProvider);
                if (current == null)
                {
                    EnsureInitialized();
                    current = Volatile.Read(ref _serviceProvider);
                }
                return current!;
            }
        }

        public static void EnsureInitialized(Action<IServiceCollection>? configureExtra = null)
        {
            lock (_initLock)
            {
                if (_serviceProvider != null)
                    return;

                var bootstrap = new AppBootstrap();
                bootstrap.InitializeSync(configureExtra);
                _bootstrap = bootstrap;
                _currentServices = bootstrap.ServiceDescriptors;
                Volatile.Write(ref _serviceProvider, bootstrap.Services);
            }
        }

        internal static AppBootstrap InitializeWithBootstrap(
            Action<IServiceCollection>? configureServices = null,
            Action<IServiceProvider>? initializeHost = null,
            Action? shutdownHost = null)
        {
            AppBootstrap bootstrap;
            lock (_initLock)
            {
                if (_bootstrap == null)
                {
                    bootstrap = new AppBootstrap();
                    bootstrap.InitializeSync(configureServices);
                    _bootstrap = bootstrap;
                    _currentServices = bootstrap.ServiceDescriptors;
                    Volatile.Write(ref _serviceProvider, bootstrap.Services);
                }
                else
                {
                    bootstrap = _bootstrap;
                }
            }

            bootstrap.InitializeSync(configureServices: null, initializeHost: initializeHost, shutdownHost: shutdownHost);
            return bootstrap;
        }

        internal static void DetachBootstrap(AppBootstrap bootstrap)
        {
            lock (_initLock)
            {
                if (!ReferenceEquals(_bootstrap, bootstrap))
                    return;

                _overrideStack.Clear();
                _bootstrap = null;
                _currentServices = null;
                Volatile.Write(ref _serviceProvider, null);
            }
        }

        internal static IDisposable Override<T>(T instance) where T : class
        {
            ArgumentNullException.ThrowIfNull(instance);

            lock (_initLock)
            {
                if (_serviceProvider == null)
                    EnsureInitialized();

                var previousProvider = _serviceProvider!;
                var previousServices = _currentServices ?? new ServiceCollection();

                IServiceCollection newServices = new ServiceCollection();
                foreach (var descriptor in previousServices)
                {
                    if (!descriptor.IsKeyedService && descriptor.ServiceType == typeof(T))
                        continue;
                    newServices.Add(descriptor);
                }

                newServices.AddSingleton<T>(instance);

                ServiceProvider newProvider = newServices.BuildServiceProvider();
                var scope = new OverrideScope(previousProvider, previousServices, newProvider);
                _overrideStack.Push(scope);

                _currentServices = newServices;
                Volatile.Write(ref _serviceProvider, newProvider);

                return scope;
            }
        }

        private sealed class FallbackGeneralSettingsService : ISettingsService
        {
            private static ISettingsService Current => AppSettings.General ?? new IniSection("General");

            public string? Get(string name) => Current.Get(name);
            public T? Get<T>(string name, T? defaultValue = default) => Current.Get(name, defaultValue);
            public bool GetBool(string name, bool defaultValue = false) => Current.GetBool(name, defaultValue);
            public int GetInt(string name, int defaultValue = 0) => Current.GetInt(name, defaultValue);
            public decimal GetDecimal(string name, decimal defaultValue = 0m) => Current.GetDecimal(name, defaultValue);
            public bool Exists(string name) => Current.Exists(name);
            public void Set(string name, string? value, string? comment = null) => Current.Set(name, value, comment);
            public void RemoveProperty(string name) => Current.RemoveProperty(name);
        }

        private sealed class OverrideScope : IDisposable
        {
            private readonly IServiceProvider _previousProvider;
            private readonly IServiceCollection _previousServices;
            private readonly ServiceProvider _ownedProvider;
            private bool _disposed;

            public OverrideScope(
                IServiceProvider previousProvider,
                IServiceCollection previousServices,
                ServiceProvider ownedProvider)
            {
                _previousProvider = previousProvider;
                _previousServices = previousServices;
                _ownedProvider = ownedProvider;
            }

            public void Dispose()
            {
                lock (_initLock)
                {
                    if (_disposed) return;

                    if (_overrideStack.Count == 0 || _overrideStack.Peek() != this)
                    {
                        throw new InvalidOperationException("Service override scopes must be disposed in reverse order of creation (LIFO).");
                    }

                    _overrideStack.Pop();
                    Volatile.Write(ref _serviceProvider, _previousProvider);
                    _currentServices = _previousServices;
                    _disposed = true;
                }

                _ownedProvider.Dispose();
            }
        }

        public static T? GetService<T>() where T : class
        {
            var sp = ServiceProvider;
            try { return sp.GetService<T>(); }
            catch (ObjectDisposedException) { return ServiceProvider.GetService<T>(); }
        }

        public static T GetRequiredService<T>() where T : class
        {
            var sp = ServiceProvider;
            try { return sp.GetRequiredService<T>(); }
            catch (ObjectDisposedException) { return ServiceProvider.GetRequiredService<T>(); }
        }

        private static ISettingsService GetRequiredKeyedSettings(string key)
        {
            var sp = ServiceProvider;
            try { return sp.GetRequiredKeyedService<ISettingsService>(key); }
            catch (ObjectDisposedException) { return ServiceProvider.GetRequiredKeyedService<ISettingsService>(key); }
        }

        // Strongly-typed static convenience accessors resolving directly from DI
        public static AccountPersistenceService AccountPersistence => GetRequiredService<AccountPersistenceService>();
        public static AccountRegistrationService AccountRegistration => GetRequiredService<AccountRegistrationService>();
        public static IAccountRepository AccountRepository => GetRequiredService<IAccountRepository>();
        public static IRobloxApiClient RobloxApi => GetRequiredService<IRobloxApiClient>();
        public static IDialogService DialogService => GetRequiredService<IDialogService>();
        public static IDialogService Dialogs => DialogService;
        public static IUserPrompt Prompt => GetRequiredService<IUserPrompt>();
        public static AccountWorkflowService AccountWorkflowService => GetRequiredService<AccountWorkflowService>();
        public static IRobloxServerQueryService RobloxServerQueryService => GetRequiredService<IRobloxServerQueryService>();
        public static BrowserService BrowserService => GetRequiredService<BrowserService>();
        public static ISelectionStateService SelectionState => GetRequiredService<ISelectionStateService>();
        public static IServerBrowserStateService ServerBrowserState => GetRequiredService<IServerBrowserStateService>();
        public static IHostUiBridgeService HostUiBridge => GetRequiredService<IHostUiBridgeService>();
        public static IToastNotifier Toasts => GetRequiredService<IToastNotifier>();
        public static ISettingsSideEffects SettingsSideEffects => GetRequiredService<ISettingsSideEffects>();
        public static AccountSessionMaintenanceService AccountSessionMaintenance => GetRequiredService<AccountSessionMaintenanceService>();

        public static IAppLogger Logger
        {
            get
            {
                try { return Volatile.Read(ref _serviceProvider)?.GetService<IAppLogger>() ?? Program.Logger; }
                catch (ObjectDisposedException) { return Volatile.Read(ref _serviceProvider)?.GetService<IAppLogger>() ?? Program.Logger; }
            }
        }

        public static ISettingsService GeneralSettings
        {
            get
            {
                try { return Volatile.Read(ref _serviceProvider)?.GetService<ISettingsService>() ?? _fallbackGeneralSettings; }
                catch (ObjectDisposedException) { return Volatile.Read(ref _serviceProvider)?.GetService<ISettingsService>() ?? _fallbackGeneralSettings; }
            }
        }

        public static ISettingsService? WatcherSettings => AppSettings.Watcher;
        public static ISettingsService PromptsSettings => GetRequiredKeyedSettings("Prompts");
        public static ISettingsService DeveloperSettings => GetRequiredKeyedSettings("Developer");
        public static ISettingsService WebServerSettings => GetRequiredKeyedSettings("WebServer");
        public static void SaveSettings() => AppSettings.Save();
        public static void QueueDeferredSaveSettings() => AppSettings.QueueDeferredSave();
        public static AppSettings Settings => GetRequiredService<AppSettings>();

        public const int MinimumAccountJoinDelaySeconds = 1;
        public const int MaximumAccountJoinDelaySeconds = 3600;
    }
}
