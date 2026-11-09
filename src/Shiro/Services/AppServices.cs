#nullable enable
using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;

namespace Shiro.Services
{
    /// <summary>
    /// Centralized Dependency Injection Container and Service Provider for Shiro Account Manager.
    /// Provides loosely-coupled, enterprise-grade DI architecture using Microsoft.Extensions.DependencyInjection.
    /// </summary>
    public static class AppServices
    {
        private static IServiceProvider? _serviceProvider;
        private static IServiceCollection? _currentServices;
        private static readonly System.Threading.Lock _initLock = new();
        private static readonly Stack<OverrideScope> _overrideStack = new();
        private static readonly ISettingsService _defaultGeneralSettings = new DelegatingSettingsService();

        public static IServiceProvider ServiceProvider
        {
            get
            {
                if (_serviceProvider == null)
                    EnsureInitialized();
                return _serviceProvider!;
            }
        }

        public static void EnsureInitialized(Action<IServiceCollection>? configureExtra = null)
        {
            lock (_initLock)
            {
                if (_serviceProvider != null) return;

                var services = new ServiceCollection();

                // Core Infrastructure & Configuration Services
                services.AddSingleton<IAppLogger>(Program.Logger);
                services.AddSingleton<ISettingsService>(_defaultGeneralSettings);

                // Core Domain Services
                services.AddSingleton(AccountPersistenceService.Default);
                services.AddSingleton(AccountRegistrationService.Default);
                services.AddSingleton(AccountSessionMaintenanceService.Default);
                services.AddSingleton<IAccountRepository>(Services.AccountRepository.Default);
                services.AddSingleton<IRobloxApiClient>(Services.RobloxApiClient.Default);
                services.AddSingleton<IRobloxSessionService>(Services.RobloxSessionService.Default);
                services.AddSingleton<IRobloxLauncherService>(Services.RobloxLauncherService.Default);
                services.AddSingleton<IDialogService>(AvaloniaDialogService.Default);
                services.AddSingleton<IUserPrompt>(AvaloniaDialogService.Default);
                services.AddSingleton<AccountWorkflowService>(Services.AccountWorkflowService.Default);
                services.AddSingleton<IRobloxServerQueryService>(Services.RobloxServerQueryService.Default);
                services.AddSingleton<BrowserService>(Services.BrowserService.Default);
                services.AddSingleton<ISelectionStateService>(SelectionStateService.Default);
                services.AddSingleton<IServerBrowserStateService>(ServerBrowserStateService.Default);
                services.AddSingleton<IHostUiBridgeService>(HostUiBridgeService.Default);
                services.AddSingleton<IBloxGenService>(BloxGenService.Default);
                services.AddSingleton<IToastNotifier>(ToastNotifierService.Default);
                services.AddSingleton<AppSettings>(AppSettings.Current);

                configureExtra?.Invoke(services);

                _currentServices = services;
                _serviceProvider = services.BuildServiceProvider();
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
                    if (descriptor.ServiceType == typeof(T))
                        continue;
                    newServices.Add(descriptor);
                }

                newServices.AddSingleton<T>(instance);

                ServiceProvider newProvider = newServices.BuildServiceProvider();
                var scope = new OverrideScope(previousProvider, previousServices, newProvider);
                _overrideStack.Push(scope);

                _currentServices = newServices;
                _serviceProvider = newProvider;

                return scope;
            }
        }

        private sealed class DelegatingSettingsService : ISettingsService
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

        private sealed class DelegatingSectionSettingsService : ISettingsService
        {
            private readonly Func<ISettingsService?> _resolver;

            public DelegatingSectionSettingsService(Func<ISettingsService?> resolver)
            {
                _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            }

            private ISettingsService Current => _resolver() ?? new IniSection(string.Empty);

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
                    _serviceProvider = _previousProvider;
                    _currentServices = _previousServices;
                    _disposed = true;
                }

                _ownedProvider.Dispose();
            }
        }

        public static T? GetService<T>() where T : class => ServiceProvider.GetService<T>();

        public static T GetRequiredService<T>() where T : class => ServiceProvider.GetRequiredService<T>();

        // Strongly-typed static convenience accessors resolving directly from DI
        public static AccountPersistenceService AccountPersistence => GetRequiredService<AccountPersistenceService>();
        public static AccountRegistrationService AccountRegistration => GetRequiredService<AccountRegistrationService>();
        public static IAccountRepository AccountRepository => GetRequiredService<IAccountRepository>();
        public static IRobloxApiClient RobloxApi => GetRequiredService<IRobloxApiClient>();
        public static IRobloxSessionService RobloxSessionService => GetRequiredService<IRobloxSessionService>();
        public static IRobloxLauncherService RobloxLauncherService => GetRequiredService<IRobloxLauncherService>();
        public static IDialogService DialogService => GetRequiredService<IDialogService>();
        public static IDialogService Dialogs => DialogService;
        public static IUserPrompt Prompt => GetRequiredService<IUserPrompt>();
        public static AccountWorkflowService AccountWorkflowService => GetRequiredService<AccountWorkflowService>();
        public static IRobloxServerQueryService RobloxServerQueryService => GetRequiredService<IRobloxServerQueryService>();
        public static BrowserService BrowserService => GetRequiredService<BrowserService>();
        public static ISelectionStateService SelectionState => GetRequiredService<ISelectionStateService>();
        public static IServerBrowserStateService ServerBrowserState => GetRequiredService<IServerBrowserStateService>();
        public static IHostUiBridgeService HostUiBridge => GetRequiredService<IHostUiBridgeService>();
        public static IBloxGenService BloxGen => GetRequiredService<IBloxGenService>();
        public static IToastNotifier Toasts => GetRequiredService<IToastNotifier>();
        public static AccountSessionMaintenanceService AccountSessionMaintenance => GetRequiredService<AccountSessionMaintenanceService>();
        public static IAppLogger Logger => _serviceProvider?.GetService<IAppLogger>() ?? Program.Logger;
        public static ISettingsService GeneralSettings => _serviceProvider?.GetService<ISettingsService>() ?? _defaultGeneralSettings;
        public static ISettingsService? WatcherSettings => AppSettings.Watcher;
        public static ISettingsService PromptsSettings => new DelegatingSectionSettingsService(() => AppSettings.Prompts);
        public static ISettingsService DeveloperSettings => new DelegatingSectionSettingsService(() => AppSettings.Developer);
        public static ISettingsService WebServerSettings => new DelegatingSectionSettingsService(() => AppSettings.WebServer);
        public static ISettingsService AccountControlSettings => new DelegatingSectionSettingsService(() => AppSettings.AccountControl);
        public static void SaveSettings() => AppSettings.Save();
        public static void QueueDeferredSaveSettings() => AppSettings.QueueDeferredSave();
        public static AppSettings Settings => GetRequiredService<AppSettings>();

        public const int MinimumAccountJoinDelaySeconds = 1;
        public const int MaximumAccountJoinDelaySeconds = 3600;
    }
}
