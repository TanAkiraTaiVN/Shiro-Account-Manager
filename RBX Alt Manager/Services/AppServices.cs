#nullable enable
using System;
using Microsoft.Extensions.DependencyInjection;

namespace RBX_Alt_Manager.Services
{
    /// <summary>
    /// Centralized Dependency Injection Container and Service Provider for Shiro Account Manager.
    /// Provides loosely-coupled, enterprise-grade DI architecture using Microsoft.Extensions.DependencyInjection.
    /// </summary>
    public static class AppServices
    {
        private static IServiceProvider? _serviceProvider;
        private static readonly System.Threading.Lock _initLock = new();

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

                // Core Domain Services
                services.AddSingleton<IAccountRepository>(Services.AccountRepository.Default);
                services.AddSingleton<IRobloxSessionService>(Services.RobloxSessionService.Default);
                services.AddSingleton<IRobloxLauncherService>(Services.RobloxLauncherService.Default);
                services.AddSingleton<IDialogService>(AvaloniaDialogService.Default);
                services.AddSingleton<AccountWorkflowService>(Services.AccountWorkflowService.Default);
                services.AddSingleton<IRobloxServerQueryService>(Services.RobloxServerQueryService.Default);
                services.AddSingleton<BrowserService>(Services.BrowserService.Default);
                services.AddSingleton<AppSettings>(AppSettings.Current);

                configureExtra?.Invoke(services);

                _serviceProvider = services.BuildServiceProvider();
            }
        }

        public static T? GetService<T>() where T : class => ServiceProvider.GetService<T>();

        public static T GetRequiredService<T>() where T : class => ServiceProvider.GetRequiredService<T>();

        // Strongly-typed static convenience accessors resolving directly from DI
        public static IAccountRepository AccountRepository
        {
            get => GetRequiredService<IAccountRepository>();
            set => ReplaceService(value);
        }

        public static IRobloxSessionService RobloxSessionService
        {
            get => GetRequiredService<IRobloxSessionService>();
            set => ReplaceService(value);
        }

        public static IRobloxLauncherService RobloxLauncherService
        {
            get => GetRequiredService<IRobloxLauncherService>();
            set => ReplaceService(value);
        }

        public static IDialogService DialogService
        {
            get => GetRequiredService<IDialogService>();
            set => ReplaceService(value);
        }

        public static IDialogService Dialogs => DialogService;

        public static AccountWorkflowService AccountWorkflowService
        {
            get => GetRequiredService<AccountWorkflowService>();
            set => ReplaceService(value);
        }

        public static IRobloxServerQueryService RobloxServerQueryService
        {
            get => GetRequiredService<IRobloxServerQueryService>();
            set => ReplaceService(value);
        }

        public static BrowserService BrowserService
        {
            get => GetRequiredService<BrowserService>();
            set => ReplaceService(value);
        }

        public static AppSettings Settings => GetRequiredService<AppSettings>();

        private static void ReplaceService<T>(T instance) where T : class
        {
            lock (_initLock)
            {
                var services = new ServiceCollection();

                services.AddSingleton<IAccountRepository>((instance as IAccountRepository) ?? AccountRepository);
                services.AddSingleton<IRobloxSessionService>((instance as IRobloxSessionService) ?? RobloxSessionService);
                services.AddSingleton<IRobloxLauncherService>((instance as IRobloxLauncherService) ?? RobloxLauncherService);
                services.AddSingleton<IDialogService>((instance as IDialogService) ?? DialogService);
                services.AddSingleton<AccountWorkflowService>((instance as AccountWorkflowService) ?? AccountWorkflowService);
                services.AddSingleton<IRobloxServerQueryService>((instance as IRobloxServerQueryService) ?? RobloxServerQueryService);
                services.AddSingleton<BrowserService>((instance as BrowserService) ?? BrowserService);
                services.AddSingleton<AppSettings>(AppSettings.Current);

                _serviceProvider = services.BuildServiceProvider();
            }
        }
    }
}
