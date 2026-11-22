#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Shiro.Services;
using NSubstitute;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class AppSettingsBehaviorTests : IDisposable
    {
        private readonly string _tempFile;
        private readonly IniFile? _prevIni;

        public AppSettingsBehaviorTests()
        {
            _prevIni = AppSettings.ActiveIniFile;
            _tempFile = Path.Combine(Path.GetTempPath(), $"Shiro_Settings_Test_{Guid.NewGuid():N}.ini");
            AppSettings.ActiveIniFile = new IniFile(_tempFile);
        }

        public void Dispose()
        {
            AppSettings.ResetOverrides();
            AppSettings.ActiveIniFile = _prevIni;
            if (File.Exists(_tempFile))
            {
                try { File.Delete(_tempFile); }
                catch (IOException) { }
            }
        }

        [Fact]
        public void AppSettings_CurrentInstance_IsNotNull()
        {
            var current = AppSettings.Current;
            Assert.NotNull(current);
            Assert.Same(current, AppSettings.Current);
        }

        [Fact]
        public void AppSettings_GeneralProperties_ReadAndWriteCorrectly()
        {
            var settings = AppSettings.Current;

            // EnableMultiRbx
            settings.EnableMultiRbx = true;
            Assert.True(settings.EnableMultiRbx);
            settings.EnableMultiRbx = false;
            Assert.False(settings.EnableMultiRbx);

            // AccountJoinDelay (clamped 1-3600)
            settings.AccountJoinDelay = 15;
            Assert.Equal(15, settings.AccountJoinDelay);
            settings.AccountJoinDelay = 0;
            Assert.Equal(1, settings.AccountJoinDelay);
            settings.AccountJoinDelay = 5000;
            Assert.Equal(3600, settings.AccountJoinDelay);

            // SavePasswords
            settings.SavePasswords = true;
            Assert.True(settings.SavePasswords);
            settings.SavePasswords = false;
            Assert.False(settings.SavePasswords);

            // AutoCookieRefresh
            settings.AutoCookieRefresh = true;
            Assert.True(settings.AutoCookieRefresh);
            settings.AutoCookieRefresh = false;
            Assert.False(settings.AutoCookieRefresh);

            // ShowPresence
            settings.ShowPresence = true;
            Assert.True(settings.ShowPresence);
            settings.ShowPresence = false;
            Assert.False(settings.ShowPresence);

            // PresenceUpdateRate (clamped min 1)
            settings.PresenceUpdateRate = 10;
            Assert.Equal(10, settings.PresenceUpdateRate);
            settings.PresenceUpdateRate = -5;
            Assert.Equal(1, settings.PresenceUpdateRate);

            // UnlockFPS & MaxFPSValue
            settings.UnlockFPS = true;
            Assert.True(settings.UnlockFPS);
            settings.MaxFPSValue = 144;
            Assert.Equal(144, settings.MaxFPSValue);

            // EnablePotatoMode
            settings.EnablePotatoMode = true;
            Assert.True(settings.EnablePotatoMode);
            settings.EnablePotatoMode = false;
            Assert.False(settings.EnablePotatoMode);

            // AccentTheme
            settings.AccentTheme = "Emerald";
            Assert.Equal("Emerald", settings.AccentTheme);

            // SavedPlaceId
            settings.SavedPlaceId = "998877";
            Assert.Equal("998877", settings.SavedPlaceId);

            // AsyncJoin
            settings.AsyncJoin = true;
            Assert.True(settings.AsyncJoin);
            settings.AsyncJoin = false;
            Assert.False(settings.AsyncJoin);

            // CheckForUpdates
            settings.CheckForUpdates = true;
            Assert.True(settings.CheckForUpdates);
            settings.CheckForUpdates = false;
            Assert.False(settings.CheckForUpdates);

            // UseProxies, ProxyTimeout, ProxyTestLimit
            settings.UseProxies = true;
            Assert.True(settings.UseProxies);
            settings.ProxyTimeout = 4000;
            Assert.Equal(4000, settings.ProxyTimeout);
            settings.ProxyTestLimit = 25;
            Assert.Equal(25, settings.ProxyTestLimit);
        }

        [Fact]
        public void AppSettings_DeveloperAndWebServerProperties_ReadAndWriteCorrectly()
        {
            var settings = AppSettings.Current;

            settings.DevMode = true;
            Assert.True(settings.DevMode);
            settings.DevMode = false;
            Assert.False(settings.DevMode);

            settings.EnableWebServer = true;
            Assert.True(settings.EnableWebServer);
            settings.EnableWebServer = false;
            Assert.False(settings.EnableWebServer);

            settings.WebServerPort = 8089;
            Assert.Equal(8089, settings.WebServerPort);
        }

        [Fact]
        public void AppSettings_RuntimeBrowserStateAndSelections_RoundTrip()
        {
            AppSettings.WebSocketPassword = "secret-pass-test";
            Assert.Equal("secret-pass-test", AppSettings.WebSocketPassword);

            AppSettings.CurrentPlaceId = "1234567";
            Assert.Equal("1234567", AppSettings.CurrentPlaceId);

            AppSettings.CurrentJobId = "job-abc";
            Assert.Equal("job-abc", AppSettings.CurrentJobId);

            AppSettings.ShuffleJobId = true;
            Assert.True(AppSettings.ShuffleJobId);
            AppSettings.ShuffleJobId = false;
            Assert.False(AppSettings.ShuffleJobId);

            AppSettings.CurrentClientVersion = "version-xyz";
            Assert.Equal("version-xyz", AppSettings.CurrentClientVersion);

            var acc = new Account { Username = "SettingsAcc", UserID = 771122 };
            AppSettings.SelectedAccount = acc;
            Assert.Same(acc, AppSettings.SelectedAccount);

            AppSettings.LastValidAccount = acc;
            Assert.Same(acc, AppSettings.LastValidAccount);

            var list = new List<Account> { acc };
            AppSettings.SelectedAccounts = list;
            Assert.Single(AppSettings.SelectedAccounts);
            Assert.Equal("SettingsAcc", AppSettings.SelectedAccounts[0].Username);

            Assert.Equal(1, AppSettings.MinimumAccountJoinDelaySeconds);
            Assert.Equal(3600, AppSettings.MaximumAccountJoinDelaySeconds);
        }

        [Fact]
        public async Task AppSettings_HostUiBridges_ExecuteCleanly()
        {
            var acc = new Account { Username = "BridgeAcc", UserID = 445566, Valid = true };
            bool origSingletonEnabled = RobloxSingletonService.IsEnabled;
            string tempDir = Path.Combine(Path.GetTempPath(), $"Shiro_BridgeTest_{Guid.NewGuid():N}");
            try
            {
                using var dirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);

                var ex = await Record.ExceptionAsync(async () =>
                {
                    AppSettings.Save();
                    AppSettings.QueueDeferredSave();
                    AppSettings.RefreshAccountView(acc);
                    AppSettings.UpdateAccountValidityOnUi(acc, true);
                    AppSettings.SchedulePresenceRefresh(acc);
                    AppSettings.SchedulePresenceRefresh(null);
                    AppSettings.SignalNextLaunchAccount();
                    AppSettings.CancelLaunching();
                    AppSettings.UpdateMultiRoblox();
                    AppSettings.CloseAllRobloxFromUi();
                    AppSettings.SetDashboardStatus("Testing dashboard");
                    AppSettings.EnsureBackendInitialized();
                    AppSettings.InitializePostStartup();
                    AppSettings.SetAutoCookieRefreshEnabled(true);
                    AppSettings.SetAutoCookieRefreshEnabled(false);
                    await AppSettings.RestartWebServerAsync(7991, false);
                    await AppSettings.StopWebServerAsync();
                    await AppSettings.ResetEncryptionAsync(prompt: false);
                    AntiAfkService.Stop();
                    MemoryOptimizer.ConfigureAutoTrim(false);
                    RobloxAudioService.ConfigureSmartMute(false);
                });

                Assert.Null(ex);
            }
            finally
            {
                RobloxSingletonService.ResetForTest(origSingletonEnabled);
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch (IOException) { }
            }
        }

        [Fact]
        public void AppSettingsGeneral_WritesThroughToActiveIniFileSection()
        {
            var temp = Path.Combine(Path.GetTempPath(), $"Shiro_WriteThrough_{Guid.NewGuid():N}.ini");
            try
            {
                var ini = new IniFile(temp);
                AppSettings.ActiveIniFile = ini;
                AppSettings.General = null!; // clear override
                AppSettings.General.Set("SaveThrottleMs", "1234");
                Assert.Equal("1234", ini.Section("General").Get("SaveThrottleMs"));
            }
            finally
            {
                if (File.Exists(temp))
                {
                    try { File.Delete(temp); }
                    catch (IOException) { }
                }
            }
        }

        [Fact]
        public void SelectionStateOverride_IsObservedByAppSettingsFacade()
        {
            var fake = Substitute.For<ISelectionStateService>();
            var acc = new Account { Username = "OverrideSelected", UserID = 12345 };
            fake.SelectedAccount.Returns(acc);
            using (AppServices.Override<ISelectionStateService>(fake))
                Assert.Same(acc, AppSettings.SelectedAccount);
        }

        [Fact]
        public void SectionSettings_ReflectActiveIniFileChanges()
        {
            var prev = AppSettings.ActiveIniFile;
            try
            {
                var tempPath = Path.Combine(Path.GetTempPath(), $"shiro-sec-{Guid.NewGuid():N}.ini");
                AppSettings.ActiveIniFile = new IniFile(tempPath);
                AppServices.PromptsSettings.Set("TestPromptKey", "true");
                Assert.True(AppSettings.Prompts.Get<bool>("TestPromptKey"));
                File.Delete(tempPath);
            }
            finally
            {
                AppSettings.ActiveIniFile = prev;
            }
        }
    }
}
