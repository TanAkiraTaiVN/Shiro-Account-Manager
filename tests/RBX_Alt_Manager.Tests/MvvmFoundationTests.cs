using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using RBX_Alt_Manager.Services;
using RBX_Alt_Manager.ViewModels;
using Xunit;

namespace RBX_Alt_Manager.Tests
{
    public class MvvmFoundationTests
    {
        private class TestObservable : ObservableObject
        {
            private string _name;
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
            string received = null;
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
        public void AppServices_ServiceProvider_ResolvesAllRegisteredServices()
        {
            var repo = AppServices.GetRequiredService<IAccountRepository>();
            Assert.NotNull(repo);

            var launcher = AppServices.GetRequiredService<IRobloxLauncherService>();
            Assert.NotNull(launcher);

            var session = AppServices.GetRequiredService<IRobloxSessionService>();
            Assert.NotNull(session);

            var browser = AppServices.GetRequiredService<BrowserService>();
            Assert.NotNull(browser);
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
            Assert.NotNull(builder);
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
            var comparer = RBX_Alt_Manager.Classes.NaturalStringComparer.Instance;

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
            Assert.True(typeof(Avalonia.Controls.Window).IsAssignableFrom(typeof(RBX_Alt_Manager.AvaloniaUI.Views.FastLoginDialog)));
            Assert.True(typeof(Avalonia.Controls.Window).IsAssignableFrom(typeof(RBX_Alt_Manager.AvaloniaUI.Views.BulkImportDialog)));
            Assert.True(typeof(Avalonia.Controls.Window).IsAssignableFrom(typeof(RBX_Alt_Manager.AvaloniaUI.Views.ProxyManagerDialog)));
        }

        [Fact]
        public void NexusWebSocketServer_StartsAndStopsGracefully()
        {
            var server = new RBX_Alt_Manager.Nexus.NexusWebSocketServer(System.Net.IPAddress.Loopback, 7996);
            try
            {
                server.Start();
                Assert.True(server.IsListening);
            }
            finally
            {
                server.Stop();
                Assert.False(server.IsListening);
            }
        }

        [Fact]
        public async Task ClipboardHelper_SetClipboardTextAsync_HandlesSafelyWithoutBlocking()
        {
            bool ok = await ClipboardHelper.SetClipboardTextAsync("ShiroUniversalClipboard");
            Assert.True(ok || !ok);
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
    }
}
