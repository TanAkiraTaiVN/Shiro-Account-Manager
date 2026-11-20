using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class MvvmFoundationTests
    {
        private class TestObservable : ObservableObject
        {
            private string _name = string.Empty;
            public bool ChangedCallbackFired { get; private set; }

            public string Name
            {
                get => _name;
                set => SetProperty(ref _name, value, () => ChangedCallbackFired = true);
            }
        }

        [Fact]
        public void ObservableObject_SetProperty_WithCallback_ExecutesCallback()
        {
            var model = new TestObservable();
            Assert.False(model.ChangedCallbackFired);

            model.Name = "ShiroModern";
            Assert.True(model.ChangedCallbackFired);
            Assert.Equal("ShiroModern", model.Name);
        }

        [Fact]
        public void RelayCommand_Generic_ExecutesWithTypedParameter()
        {
            string? received = null;
            var command = new RelayCommand<string>(param => received = param);

            Assert.True(command.CanExecute("test-string"));
            command.Execute("test-string");

            Assert.Equal("test-string", received);
        }

        [Fact]
        public async Task AsyncRelayCommand_Generic_ExecutesWithTypedParameter()
        {
            int receivedNumber = 0;
            var command = new AsyncRelayCommand<int>(async number =>
            {
                await Task.Yield();
                receivedNumber = number;
            });

            Assert.True(command.CanExecute(42));
            command.Execute(42);

            // Small delay to allow async completion
            await Task.Delay(50);
            Assert.Equal(42, receivedNumber);
        }

        [Fact]
        public async Task AsyncRelayCommand_ConcurrentExecute_RunsOnce()
        {
            int count = 0;
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var command = new AsyncRelayCommand(async () =>
            {
                await gate.Task;
                Interlocked.Increment(ref count);
            });

            Parallel.For(0, 8, _ =>
            {
                command.Execute(null);
            });

            gate.SetResult(true);
            await Task.Delay(200);

            Assert.Equal(1, count);
        }

        [Fact]
        public async Task AsyncRelayCommand_Generic_ConcurrentExecute_RunsOnce()
        {
            int count = 0;
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var command = new AsyncRelayCommand<string>(async _ =>
            {
                await gate.Task;
                Interlocked.Increment(ref count);
            });

            Parallel.For(0, 8, _ =>
            {
                command.Execute("test");
            });

            gate.SetResult(true);
            await Task.Delay(200);

            Assert.Equal(1, count);
        }

        [Fact]
        public void AppServices_ServiceProvider_ResolvesAllRegisteredServices()
        {
            var repo = AppServices.GetRequiredService<IAccountRepository>();
            Assert.Same(AppServices.AccountRepository, repo);

            var browser = AppServices.GetRequiredService<BrowserService>();
            Assert.IsType<BrowserService>(browser);
        }

        [Fact]
        public void StoragePaths_DefaultSettingsFile_IsJson()
        {
            string settingsPath = StoragePaths.SettingsFile;
            Assert.EndsWith(".json", settingsPath, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void AvaloniaUI_AppBuilder_BuildsSuccessfully()
        {
            var builder = Program.BuildAvaloniaApp();
            Assert.IsType<Avalonia.AppBuilder>(builder);
        }

        [Fact]
        public void AppDispatcher_Post_ExecutesDirectlyWhenNoDispatcherActive()
        {
            bool executed = false;
            AppDispatcher.Post(() => executed = true);
            Assert.True(executed);
        }

        [Fact]
        public void NaturalStringComparer_SortsAlphanumericNaturallyWithoutWin32()
        {
            var comparer = Shiro.NaturalStringComparer.Instance;

            // Basic alphanumeric ordering (2 comes before 10)
            Assert.True(comparer.Compare("AD_Jackpot1", "AD_Jackpot2") < 0);
            Assert.True(comparer.Compare("AD_Jackpot2", "AD_Jackpot10") < 0);
            Assert.True(comparer.Compare("AD_Jackpot10", "AD_Jackpot11") < 0);

            // Case-insensitive
            Assert.True(comparer.Compare("account2", "Account10") < 0);
            Assert.True(comparer.Compare("Account1", "account1") == 0);

            // Null handling
            Assert.True(comparer.Compare(null, "Test") < 0);
            Assert.True(comparer.Compare("Test", null) > 0);
            Assert.True(comparer.Compare(null, null) == 0);

            // Large numeric values exceeding long.MaxValue
            string huge1 = "Place_9999999999999999999999999999991";
            string huge2 = "Place_9999999999999999999999999999992";
            Assert.True(comparer.Compare(huge1, huge2) < 0);
        }

        [Fact]
        public async Task KestrelWebServer_StartsServesRequestAndStopsGracefully()
        {
            int testPort = 7998;
            var server = new KestrelWebServer(testPort, false, async context =>
            {
                await Task.Yield();
                return "{\"status\":\"ok\"}";
            });

            try
            {
                await server.StartAsync();
                Assert.True(server.IsRunning);

                using var client = new System.Net.Http.HttpClient();
                string response = await client.GetStringAsync($"http://localhost:{testPort}/test");
                Assert.Equal("{\"status\":\"ok\"}", response);
            }
            finally
            {
                await server.StopAsync();
                Assert.False(server.IsRunning);
            }
        }

        [Fact]
        public void AvaloniaUI_DialogTypes_AreRegisteredAndDiscoverable()
        {
            Assert.True(typeof(Avalonia.Controls.Window).IsAssignableFrom(typeof(Shiro.AvaloniaUI.Views.FastLoginDialog)));
            Assert.True(typeof(Avalonia.Controls.Window).IsAssignableFrom(typeof(Shiro.AvaloniaUI.Views.BulkImportDialog)));
            Assert.True(typeof(Avalonia.Controls.Window).IsAssignableFrom(typeof(Shiro.AvaloniaUI.Views.ProxyManagerDialog)));
        }

        [Fact]
        public async Task ClipboardHelper_SetClipboardTextAsync_HandlesSafelyWithoutBlocking()
        {
            var ex = await Record.ExceptionAsync(() => ClipboardHelper.SetClipboardTextAsync("ShiroUniversalClipboard"));
            Assert.Null(ex);
            bool fallback = ClipboardHelper.SetText(null!);
            Assert.False(fallback);
        }

        [Fact]
        public async Task MainViewModel_SearchText_DebouncesFilterAsync()
        {
            var vm = new MainViewModel();
            vm.SearchText = "TestQuery";
            await Task.Delay(160);
            Assert.Equal("TestQuery", vm.SearchText);
        }

        [Fact]
        public void AntiAfkService_StartsStopsAndTogglesState()
        {
            // Ensure stopped initially
            if (AntiAfkService.IsEnabled) AntiAfkService.Stop();
            Assert.False(AntiAfkService.IsEnabled);

            AntiAfkService.Start();
            Assert.True(AntiAfkService.IsEnabled);

            AntiAfkService.Toggle();
            Assert.False(AntiAfkService.IsEnabled);
        }

        [Fact]
        public void MainViewModel_Dispose_UnsubscribesAllStaticEvents_AndIsIdempotent()
        {
            var prevLang = LocalizationService.CurrentLanguage;
            var prevTheme = AccentThemeService.CurrentTheme;
            try
            {
                var vm = new MainViewModel();
                var serverVm = vm.ServerListViewModel;
                Assert.IsAssignableFrom<IDisposable>(vm);
                Assert.IsAssignableFrom<IDisposable>(serverVm);

                vm.Dispose();
                vm.Dispose(); // idempotent second call

                int mainPropChanges = 0;
                int serverPropChanges = 0;
                vm.PropertyChanged += (_, _) => mainPropChanges++;
                serverVm.PropertyChanged += (_, _) => serverPropChanges++;

                LocalizationService.SetLanguage(prevLang == AppLanguage.Vietnamese ? AppLanguage.English : AppLanguage.Vietnamese);
                AccentThemeService.ApplyTheme(prevTheme == AccentTheme.Cyan ? AccentTheme.Emerald : AccentTheme.Cyan);
                StreamerModeService.IsEnabled = !StreamerModeService.IsEnabled;
                vm.RunningRobloxProcessCount = 5;
                vm.RefreshLiveProcessStatus();
                StreamerModeService.IsEnabled = !StreamerModeService.IsEnabled;
                vm.RunningRobloxProcessCount = 5;
                vm.RefreshLiveProcessStatus();

                Assert.Equal(0, mainPropChanges);
                Assert.Equal(0, serverPropChanges);
            }
            finally
            {
                LocalizationService.SetLanguage(prevLang);
                AccentThemeService.ApplyTheme(prevTheme);
            }
        }

        [Fact]
        public void Commands_RaiseCanExecuteChanged_MarshalsViaAppDispatcherPost()
        {
            var prevDispatcher = AppDispatcher.CustomDispatcher;
            int postedCount = 0;
            int invokedCount = 0;
            AppDispatcher.CustomDispatcher = action =>
            {
                postedCount++;
                action();
            };

            try
            {
                var syncCmd = new RelayCommand(() => { });
                var genericSyncCmd = new RelayCommand<string>(_ => { });
                var asyncCmd = new AsyncRelayCommand(() => Task.CompletedTask);
                var genericAsyncCmd = new AsyncRelayCommand<int>(_ => Task.CompletedTask);

                syncCmd.CanExecuteChanged += (_, _) => invokedCount++;
                genericSyncCmd.CanExecuteChanged += (_, _) => invokedCount++;
                asyncCmd.CanExecuteChanged += (_, _) => invokedCount++;
                genericAsyncCmd.CanExecuteChanged += (_, _) => invokedCount++;

                syncCmd.RaiseCanExecuteChanged();
                genericSyncCmd.RaiseCanExecuteChanged();
                asyncCmd.RaiseCanExecuteChanged();
                genericAsyncCmd.RaiseCanExecuteChanged();

                Assert.Equal(4, postedCount);
                Assert.Equal(4, invokedCount);
            }
            finally
            {
                AppDispatcher.CustomDispatcher = prevDispatcher;
            }
        }

        [Fact]
        public async Task AsyncRelayCommand_UnhandledException_InvokesDefaultUnhandledErrorHandlerOnce()
        {
            int defaultCount = 0;
            Exception? captured = null;
            var prev = AsyncRelayCommand.DefaultUnhandledErrorHandler;
            AsyncRelayCommand.DefaultUnhandledErrorHandler = ex =>
            {
                Interlocked.Increment(ref defaultCount);
                captured = ex;
            };
            try
            {
                var cmd = new AsyncRelayCommand(() => Task.FromException(new InvalidOperationException("boom")));
                cmd.Execute(null);
                await Task.Delay(50);

                Assert.Equal(1, defaultCount);
                Assert.IsType<InvalidOperationException>(captured);

                var genericCmd = new AsyncRelayCommand<int>(_ => Task.FromException(new InvalidOperationException("boom-generic")));
                genericCmd.Execute(7);
                await Task.Delay(50);

                Assert.Equal(2, defaultCount);
                Assert.Equal("boom-generic", captured?.Message);
            }
            finally
            {
                AsyncRelayCommand.DefaultUnhandledErrorHandler = prev;
            }
        }

        [Fact]
        public async Task AsyncRelayCommand_LocalCatchOrInstanceOnError_DoesNotInvokeDefaultHandler()
        {
            int defaultCount = 0;
            int instanceCount = 0;
            var prev = AsyncRelayCommand.DefaultUnhandledErrorHandler;
            AsyncRelayCommand.DefaultUnhandledErrorHandler = _ => Interlocked.Increment(ref defaultCount);
            try
            {
                var instanceCmd = new AsyncRelayCommand(
                    () => Task.FromException(new InvalidOperationException("handled-by-instance")),
                    canExecute: null,
                    onError: _ => Interlocked.Increment(ref instanceCount));
                await instanceCmd.ExecuteAsync();

                var localCatchCmd = new AsyncRelayCommand(async () =>
                {
                    try
                    {
                        await Task.FromException(new InvalidOperationException("caught-locally"));
                    }
                    catch (InvalidOperationException)
                    {
                        // Handled locally by command body
                    }
                });
                await localCatchCmd.ExecuteAsync();

                Assert.Equal(1, instanceCount);
                Assert.Equal(0, defaultCount);
            }
            finally
            {
                AsyncRelayCommand.DefaultUnhandledErrorHandler = prev;
            }
        }
    }
}
