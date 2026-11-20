#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Shiro.Services
{
    /// <summary>
    /// Explicit composition root for Shiro services, keyed section settings adapters,
    /// deferred settings persistence timer, and deterministic async shutdown.
    /// </summary>
    public sealed class AppBootstrap : IAsyncDisposable
    {
        private readonly System.Threading.Lock _gate = new();
        private readonly System.Threading.Lock _timerLock = new();
        private readonly List<Action> _shutdownCallbacks = new();
        private readonly List<Func<ValueTask>> _asyncShutdownCallbacks = new();
        private readonly ISettingsService _generalSettingsAdapter = new DelegatingSettingsService();
        private readonly ISettingsService _promptsSettingsAdapter = new DelegatingSectionSettingsService(() => AppSettings.Prompts);
        private readonly ISettingsService _developerSettingsAdapter = new DelegatingSectionSettingsService(() => AppSettings.Developer);
        private readonly ISettingsService _webServerSettingsAdapter = new DelegatingSectionSettingsService(() => AppSettings.WebServer);

        private IServiceCollection? _serviceDescriptors;
        private ServiceProvider? _serviceProvider;
        private Timer? _deferredSettingsSaveTimer;
        private Action? _pendingDeferredSaveAction;
        private bool _hostInitialized;
        private int _disposed;

        public AppBootstrap(Action<IServiceCollection>? configureExtra = null)
        {
            if (configureExtra != null)
            {
                BuildContainerLocked(configureExtra);
            }
        }

        public IServiceProvider Services
        {
            get
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                    return _serviceProvider ?? BuildContainerLocked(null);
                }
            }
        }

        internal IServiceCollection ServiceDescriptors
        {
            get
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                    if (_serviceProvider == null)
                    {
                        BuildContainerLocked(null);
                    }
                    return _serviceDescriptors!;
                }
            }
        }

        internal ISettingsService GeneralSettingsAdapter => _generalSettingsAdapter;

        public async Task InitializeAsync(
            Action<IServiceCollection>? configureServices = null,
            Func<IServiceProvider, CancellationToken, Task>? initializeHostAsync = null,
            Func<ValueTask>? shutdownHostAsync = null,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            IServiceProvider provider;
            bool shouldInitializeHost = false;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                if (_serviceProvider == null)
                {
                    provider = BuildContainerLocked(configureServices);
                }
                else
                {
                    if (configureServices != null)
                        throw new InvalidOperationException("Services have already been built for this AppBootstrap instance.");
                    provider = _serviceProvider;
                }

                if (shutdownHostAsync != null)
                    _asyncShutdownCallbacks.Add(shutdownHostAsync);

                if (!_hostInitialized)
                {
                    _hostInitialized = true;
                    shouldInitializeHost = true;
                }
            }

            if (shouldInitializeHost && initializeHostAsync != null)
            {
                await initializeHostAsync(provider, cancellationToken).ConfigureAwait(false);
            }
        }

        internal void InitializeSync(
            Action<IServiceCollection>? configureServices = null,
            Action<IServiceProvider>? initializeHost = null,
            Action? shutdownHost = null)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            IServiceProvider provider;
            bool shouldInitializeHost = false;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                if (_serviceProvider == null)
                {
                    provider = BuildContainerLocked(configureServices);
                }
                else
                {
                    if (configureServices != null)
                        throw new InvalidOperationException("Services have already been built for this AppBootstrap instance.");
                    provider = _serviceProvider;
                }

                if (shutdownHost != null)
                    _shutdownCallbacks.Add(shutdownHost);

                if (!_hostInitialized)
                {
                    _hostInitialized = true;
                    shouldInitializeHost = true;
                }
            }

            if (shouldInitializeHost)
            {
                initializeHost?.Invoke(provider);
            }
        }

        public void RegisterShutdownCallback(Action callback)
        {
            ArgumentNullException.ThrowIfNull(callback);
            if (Volatile.Read(ref _disposed) != 0) return;

            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) == 0)
                    _shutdownCallbacks.Add(callback);
            }
        }

        public void QueueDeferredSettingsSave(Action saveAction, int delayMs = 500)
        {
            ArgumentNullException.ThrowIfNull(saveAction);
            if (Volatile.Read(ref _disposed) != 0)
                return;

            int dueMs = Math.Max(50, delayMs);
            lock (_timerLock)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    return;

                _pendingDeferredSaveAction = saveAction;
                _deferredSettingsSaveTimer ??= new Timer(
                    static state => ((AppBootstrap)state!).InvokeDeferredSettingsSave(),
                    this,
                    Timeout.Infinite,
                    Timeout.Infinite);

                _deferredSettingsSaveTimer.Change(dueMs, Timeout.Infinite);
            }
        }

        public void CancelDeferredSettingsSave()
        {
            lock (_timerLock)
            {
                SafeExecuteHelper.Try(
                    () => _deferredSettingsSaveTimer?.Change(Timeout.Infinite, Timeout.Infinite),
                    "AppBootstrap.CancelDeferredSettingsSave");
            }
        }

        private void InvokeDeferredSettingsSave()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            Action? action;
            lock (_timerLock)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    return;
                action = _pendingDeferredSaveAction;
            }

            if (action == null)
                return;

            try
            {
                action();
            }
            catch (Exception ex)
            {
                AppServices.Logger.Error($"[DeferredSettingsSave] Error saving settings: {ex.Message}");
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            Timer? timer = DetachDeferredTimer();
            if (timer != null)
            {
                SafeExecuteHelper.Try(() => timer.Change(Timeout.Infinite, Timeout.Infinite), "AppBootstrap.CancelTimerOnDispose");
                await timer.DisposeAsync().ConfigureAwait(false);
            }

            List<Action> syncCallbacks;
            List<Func<ValueTask>> asyncCallbacks;
            ServiceProvider? provider;
            lock (_gate)
            {
                syncCallbacks = new List<Action>(_shutdownCallbacks);
                asyncCallbacks = new List<Func<ValueTask>>(_asyncShutdownCallbacks);
                _shutdownCallbacks.Clear();
                _asyncShutdownCallbacks.Clear();
                provider = _serviceProvider;
                _serviceProvider = null;
                _serviceDescriptors = null;
            }

            for (int i = syncCallbacks.Count - 1; i >= 0; i--)
            {
                SafeExecuteHelper.Try(syncCallbacks[i], "AppBootstrap.DisposeAsync.SyncCallback");
            }

            for (int i = asyncCallbacks.Count - 1; i >= 0; i--)
            {
                try
                {
                    await asyncCallbacks[i]().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AppServices.Logger.Debug($"[AppBootstrap] Shutdown callback threw: {ex.Message}");
                }
            }

            if (provider != null)
            {
                await provider.DisposeAsync().ConfigureAwait(false);
            }

            AppServices.DetachBootstrap(this);
        }

        internal void DisposeSynchronously()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            Timer? timer = DetachDeferredTimer();
            if (timer != null)
            {
                SafeExecuteHelper.Try(() => timer.Change(Timeout.Infinite, Timeout.Infinite), "AppBootstrap.CancelTimerOnSyncDispose");
                SafeExecuteHelper.Try(timer.Dispose, "AppBootstrap.DisposeTimerSync");
            }

            List<Action> syncCallbacks;
            ServiceProvider? provider;
            lock (_gate)
            {
                syncCallbacks = new List<Action>(_shutdownCallbacks);
                _shutdownCallbacks.Clear();
                _asyncShutdownCallbacks.Clear();
                provider = _serviceProvider;
                _serviceProvider = null;
                _serviceDescriptors = null;
            }

            for (int i = syncCallbacks.Count - 1; i >= 0; i--)
            {
                SafeExecuteHelper.Try(syncCallbacks[i], "AppBootstrap.DisposeSync.Callback");
            }

            provider?.Dispose();
            AppServices.DetachBootstrap(this);
        }

        private Timer? DetachDeferredTimer()
        {
            lock (_timerLock)
            {
                _pendingDeferredSaveAction = null;
                Timer? timer = _deferredSettingsSaveTimer;
                _deferredSettingsSaveTimer = null;
                return timer;
            }
        }

        private ServiceProvider BuildContainerLocked(Action<IServiceCollection>? configureExtra)
        {
            var services = new ServiceCollection();

            // Core Infrastructure & Configuration Services
            services.AddSingleton<IAppLogger>(Program.Logger);
            services.AddSingleton<ISettingsService>(_generalSettingsAdapter);
            services.AddKeyedSingleton<ISettingsService>("Prompts", _promptsSettingsAdapter);
            services.AddKeyedSingleton<ISettingsService>("Developer", _developerSettingsAdapter);
            services.AddKeyedSingleton<ISettingsService>("WebServer", _webServerSettingsAdapter);

            // Core Domain & State Services (wired once at the composition root)
            var accountRepository = AccountRepository.Default;
            services.AddSingleton<IAccountRepository>(accountRepository);
            services.AddSingleton(accountRepository.PersistenceInstance);
            services.AddSingleton(accountRepository.RegistrationInstance);
            services.AddSingleton(AccountSessionMaintenanceService.Default);
            services.AddSingleton<IRobloxApiClient>(RobloxApiClient.Default);
            services.AddSingleton<IDialogService>(AvaloniaDialogService.Default);
            services.AddSingleton<IUserPrompt>(AvaloniaDialogService.Default);
            services.AddSingleton<AccountWorkflowService>(AccountWorkflowService.Default);
            services.AddSingleton<IRobloxServerQueryService>(RobloxServerQueryService.Default);
            services.AddSingleton<BrowserService>(BrowserService.Default);
            services.AddSingleton<ISelectionStateService>(SelectionStateService.Default);
            services.AddSingleton<IServerBrowserStateService>(ServerBrowserStateService.Default);
            services.AddSingleton<IHostUiBridgeService>(HostUiBridgeService.Default);
            services.AddSingleton<IToastNotifier>(ToastNotifierService.Default);
            services.AddSingleton<ISettingsSideEffects>(SettingsSideEffectsService.Default);
            services.AddSingleton<AppSettings>(AppSettings.Current);

            configureExtra?.Invoke(services);

            var provider = services.BuildServiceProvider();
            _serviceDescriptors = services;
            _serviceProvider = provider;
            return provider;
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
    }
}
