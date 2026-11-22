#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Services;
using Shiro.ViewModels;
using Shiro.ViewModels.Dialogs;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class P2CleanupAndUiWiringRegressionTests
    {
        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Shiro.sln")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            throw new InvalidOperationException("Shiro.sln not found");
        }

        [Fact]
        public void DeadCodeAndUnusedConfig_ShouldBeCleanedUp()
        {
            string root = FindRepoRoot();
            Assert.False(File.Exists(Path.Combine(root, "src", "Shiro", "Services", "Ui", "DomainServiceContracts.cs")));

            string appAxaml = File.ReadAllText(Path.Combine(root, "src", "Shiro", "AvaloniaUI", "App.axaml"));
            foreach (string deadKey in new[]
            {
                "PurpleBrush", "SubtleBorderBrush", "CardBackgroundBrush", "SurfaceBackgroundBrush", "GeoCheck", "GeoInfo",
                "GreenBrush", "RedBrush", "AmberBrush", "BlueBrush", "MutedTextBrush"
            })
                Assert.DoesNotContain(deadKey, appAxaml, StringComparison.Ordinal);

            var removedFields = new[]
            {
                (typeof(AppSettingsKeys.WebServer), "AllowRemoteAccess"),
                (typeof(AppSettingsKeys.Watcher), "CloseRbxWindowTitle"),
            };
            foreach (var (t, field) in removedFields)
                Assert.Null(t.GetField(field, BindingFlags.Public | BindingFlags.Static));
            Assert.Null(typeof(AppSettingsKeys).GetNestedType("AccountControl"));

            var progType = typeof(AccountManager).Assembly.GetType("Shiro.Program");
            Assert.Null(progType?.GetField("Elevated", BindingFlags.Public | BindingFlags.Static));

            string enJson = File.ReadAllText(Path.Combine(root, "src", "Shiro", "Resources", "Strings", "en.json"));
            string viJson = File.ReadAllText(Path.Combine(root, "src", "Shiro", "Resources", "Strings", "vi.json"));
            Assert.DoesNotContain("-update", enJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("-update", viJson, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void UpdateCheckService_IsRemoteVersionNewer_Normalizes3And4PartVersions()
        {
            Assert.False(UpdateCheckService.IsRemoteVersionNewer("5.0.0.0", "v5.0.0"));
            Assert.False(UpdateCheckService.IsRemoteVersionNewer("5.0.0", "v5.0.0.0"));
            Assert.True(UpdateCheckService.IsRemoteVersionNewer("5.0.0.0", "v5.0.1"));
        }

        [Fact]
        public void DiscordRpcAndPlaceHelper_UseInjectedSettingsAndSafeMath()
        {
            var ini = new IniFile();
            var customSettings = ini["General"];
            customSettings.Set(AppSettingsKeys.General.DiscordRpcClientId, "987654321012345678");
            using var rpc = new DiscordRpcService(Substitute.For<IAppLogger>(), customSettings);
            Assert.Equal("987654321012345678", rpc.GetConfiguredClientId());

            Assert.Equal(42.0, RobloxPlaceHelper.MapValue(5, 10, 10, 42, 99));

            var localDt = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Local);
            Assert.Equal(localDt.ToUniversalTime().ToRobloxTick(), localDt.ToRobloxTick());
        }

        [Fact]
        public void SettingsViewModel_LoadsAndSaves_AdvancedSecurityWebhookRpcAndWatcherSettings()
        {
            var ini = new IniFile();
            ini["WebServer"].Set(AppSettingsKeys.WebServer.AllowExternalConnections, "true");
            ini["WebServer"].Set(AppSettingsKeys.WebServer.EveryRequestRequiresPassword, "true");
            ini["WebServer"].Set(AppSettingsKeys.WebServer.AllowGetAccounts, "false");
            ini["WebServer"].Set(AppSettingsKeys.WebServer.AllowGetCookie, "true");
            ini["WebServer"].Set(AppSettingsKeys.WebServer.AllowAccountEditing, "true");
            ini["WebServer"].Set(AppSettingsKeys.WebServer.AllowLaunchAccount, "false");
            ini["General"].Set(AppSettingsKeys.General.DiscordWebhookUrl, "https://discord.com/api/webhooks/123/abc");
            ini["General"].Set(AppSettingsKeys.General.TelegramBotToken, "123456:ABC-DEF");
            ini["General"].Set(AppSettingsKeys.General.TelegramChatId, "-100999");
            ini["General"].Set(AppSettingsKeys.General.NotifyOnDisconnect, "true");
            ini["General"].Set(AppSettingsKeys.General.NotifyOnModeration, "false");
            ini["General"].Set(AppSettingsKeys.General.DiscordRpcEnabled, "false");
            ini["General"].Set(AppSettingsKeys.General.DiscordRpcClientId, "123456789012345678");
            ini["General"].Set(AppSettingsKeys.General.DiscordRpcShowGame, "false");
            ini["General"].Set(AppSettingsKeys.General.DiscordRpcJoinCurrentServer, "false");
            ini["General"].Set(AppSettingsKeys.General.EnableTokenDeElevation, "true");
            ini["General"].Set(AppSettingsKeys.General.StreamerModeAutoDetect, "true");
            ini["General"].Set("AntiAfkForegroundFallback", "true");
            ini["General"].Set("AutoReconnect", "true");
            ini["Watcher"].Set("CloseIfWindowTitle", "true");
            ini["Watcher"].Set(AppSettingsKeys.Watcher.ExpectedWindowTitle, "Roblox");
            ini["Watcher"].Set("NoConnectionTimeout", "45");

            var prevIni = AppSettings.ActiveIniFile;
            bool prevStreamerEnabled = StreamerModeService.IsEnabled;
            bool prevStreamerAutoDetect = StreamerModeService.AutoDetectEnabled;
            bool prevAutoReconnect = AutoReconnectService.Instance.IsEnabled;
            bool prevCloseIfWindowTitle = RobloxWatcher.CloseIfWindowTitle;
            string? prevExpectedWindowTitle = RobloxWatcher.ExpectedWindowTitle;
            AppSettings.ActiveIniFile = ini;
            try
            {
                using var bridgeScope = AppServices.Override<IHostUiBridgeService>(new HostUiBridgeService());
                var vm = new SettingsViewModel(
                    new AppSettings(),
                    ini["General"],
                    Substitute.For<ISettingsSideEffects>(),
                    Substitute.For<IDialogService>(),
                    TimeSpan.FromMilliseconds(10),
                    Substitute.For<IAppLogger>());
                vm.Load();

                Assert.True(vm.AllowExternalConnections);
                Assert.True(vm.EveryRequestRequiresPassword);
                Assert.False(vm.AllowGetAccounts);
                Assert.True(vm.AllowGetCookie);
                Assert.True(vm.AllowAccountEditing);
                Assert.False(vm.AllowLaunchAccount);
                Assert.Equal("https://discord.com/api/webhooks/123/abc", vm.DiscordWebhookUrl);
                Assert.Equal("123456:ABC-DEF", vm.TelegramBotToken);
                Assert.Equal("-100999", vm.TelegramChatId);
                Assert.True(vm.NotifyOnDisconnect);
                Assert.False(vm.NotifyOnModeration);
                Assert.False(vm.DiscordRpcEnabled);
                Assert.Equal("123456789012345678", vm.DiscordRpcClientId);
                Assert.False(vm.DiscordRpcShowGame);
                Assert.False(vm.DiscordRpcJoinCurrentServer);
                Assert.True(vm.EnableTokenDeElevation);
                Assert.True(vm.StreamerModeAutoDetect);
                Assert.True(vm.AntiAfkForegroundFallback);
                Assert.True(vm.EnableAutoReconnect);
                Assert.True(vm.CloseIfWindowTitle);
                Assert.Equal("Roblox", vm.ExpectedWindowTitle);
                Assert.Equal(45m, vm.NoConnectionTimeout);

                vm.NotifyOnModeration = true;
                vm.EnableAutoReconnect = false;
                vm.Save();

                Assert.Equal("true", ini["General"].Get<string>(AppSettingsKeys.General.NotifyOnModeration));
                Assert.Equal("false", ini["General"].Get<string>("AutoReconnect"));
            }
            finally
            {
                StreamerModeService.IsEnabled = prevStreamerEnabled;
                StreamerModeService.AutoDetectEnabled = prevStreamerAutoDetect;
                AutoReconnectService.Instance.IsEnabled = prevAutoReconnect;
                RobloxWatcher.CloseIfWindowTitle = prevCloseIfWindowTitle;
                RobloxWatcher.ExpectedWindowTitle = prevExpectedWindowTitle;
                AppSettings.ActiveIniFile = prevIni;
            }
        }

        [Fact]
        public async Task SettingsAndMainViewModel_WireVaultExportImportAndBatchAuditCommands()
        {
            var effects = Substitute.For<ISettingsSideEffects>();
            effects.ExportVaultAsync().Returns(Task.FromResult(true));
            effects.ImportVaultAsync().Returns(Task.FromResult(true));

            var vm = new SettingsViewModel(
                new AppSettings(),
                new IniFile()["General"],
                effects,
                Substitute.For<IDialogService>(),
                TimeSpan.FromMilliseconds(10),
                Substitute.For<IAppLogger>());

            await vm.ExportVaultCommand.ExecuteAsync(null);
            await vm.ImportVaultCommand.ExecuteAsync(null);
            await effects.Received(1).ExportVaultAsync();
            await effects.Received(1).ImportVaultAsync();

            using var repoScope = AppServices.Override(Substitute.For<IAccountRepository>());
            using var mainVm = new MainViewModel();
            Assert.NotNull(mainVm.AuditAllAccountsCommand);
            Assert.Contains(CommandPaletteCatalog.GetBuiltinCommands(), c => c.Id == "accounts.audit");
        }

        [Fact]
        public void AboutView_AndDocs_AreSynchronizedToCurrentArchitecture()
        {
            string root = FindRepoRoot();
            string aboutAxaml = File.ReadAllText(Path.Combine(root, "src", "Shiro", "AvaloniaUI", "Views", "AboutView.axaml"));
            Assert.Contains("Avalonia UI 12", aboutAxaml, StringComparison.Ordinal);
            Assert.DoesNotContain("Avalonia UI 11", aboutAxaml, StringComparison.Ordinal);
            Assert.DoesNotContain("C# 13", aboutAxaml, StringComparison.Ordinal);
            Assert.Contains("About_Project", aboutAxaml, StringComparison.Ordinal);

            string archMd = File.ReadAllText(Path.Combine(root, "docs", "architecture.md"));
            Assert.DoesNotContain("Direct DPAPI Injection (Preferred)", archMd, StringComparison.Ordinal);

            string threatMd = File.ReadAllText(Path.Combine(root, "docs", "threat-model.md"));
            Assert.DoesNotContain("NexusWebSocketServer.cs", threatMd, StringComparison.Ordinal);
            Assert.DoesNotContain("AutoUpdaterService.cs", threatMd, StringComparison.Ordinal);
            Assert.DoesNotContain("VcRedistInstaller.cs", threatMd, StringComparison.Ordinal);
            Assert.DoesNotContain("Classes/Cryptography.cs", threatMd, StringComparison.Ordinal);
        }
    }
}
