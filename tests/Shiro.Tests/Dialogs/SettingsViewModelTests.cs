#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using Shiro.Classes;
using Shiro.Services;
using Shiro.ViewModels.Dialogs;
using Xunit;

namespace Shiro.Tests.Dialogs
{
    [Collection("SerialGlobalState")]
    public sealed class SettingsViewModelTests
    {
        private sealed class FakeSettingsSideEffects : ISettingsSideEffects
        {
            public int ApplyThemeCount { get; private set; }
            public AccentTheme LastAppliedTheme { get; private set; }
            public int SetLanguageCount { get; private set; }
            public AppLanguage LastLanguage { get; private set; }
            public int PatchClientSettingsCount { get; private set; }
            public bool LastAntiAfk { get; private set; }
            public bool LastSmartMute { get; private set; }
            public bool LastAutoTrim { get; private set; }
            public int LastCpuRateLimit { get; private set; }
            public int RestartWebServerCount { get; private set; }
            public int StopWebServerCount { get; private set; }
            public int NotifyDisplayNamesChangedCount { get; private set; }
            public int QueueDeferredSaveCount { get; private set; }
            public Exception? ThrowOnQueueSave { get; set; }

            public void ApplyTheme(AccentTheme theme, bool save)
            {
                ApplyThemeCount++;
                LastAppliedTheme = theme;
            }

            public void SetLanguage(AppLanguage lang)
            {
                SetLanguageCount++;
                LastLanguage = lang;
            }

            public void PatchClientSettings() => PatchClientSettingsCount++;
            public void SetAntiAfk(bool on) => LastAntiAfk = on;
            public void ConfigureSmartMute(bool on) => LastSmartMute = on;
            public void ConfigureAutoTrim(bool on) => LastAutoTrim = on;
            public void SetCpuRateLimit(int percent) => LastCpuRateLimit = percent;

            public Task RestartWebServerAsync(int port, bool allowExternal)
            {
                RestartWebServerCount++;
                return Task.CompletedTask;
            }

            public Task StopWebServerAsync()
            {
                StopWebServerCount++;
                return Task.CompletedTask;
            }

            public void SetAutoCookieRefresh(bool on) { }
            public void NotifyDisplayNamesChanged() => NotifyDisplayNamesChangedCount++;

            public void QueueDeferredSave()
            {
                if (ThrowOnQueueSave != null) throw ThrowOnQueueSave;
                QueueDeferredSaveCount++;
            }

            public void CleanNotificationArea() { }
            public int ClearAvatarCache() => 0;
            public Task ResetEncryptionAsync() => Task.CompletedTask;
        }

        private static (AppSettings Settings, IniFile? PrevIni, string TempPath) CreateIsolatedAppSettings()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), $"shiro-settings-test-{Guid.NewGuid():N}.ini");
            var prev = AppSettings.ActiveIniFile;
            var ini = new IniFile(tempFile);
            AppSettings.ActiveIniFile = ini;
            return (new AppSettings(), prev, tempFile);
        }

        private static void CleanupIsolatedAppSettings(IniFile? prevIni, string tempPath)
        {
            AppSettings.ActiveIniFile = prevIni;
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch (IOException) { }
            }
        }

        [Theory]
        [InlineData(false, 80, true, 80, WebServerAction.Start)]
        [InlineData(true, 80, false, 80, WebServerAction.Stop)]
        [InlineData(true, 80, true, 81, WebServerAction.Restart)]
        [InlineData(true, 80, true, 80, WebServerAction.None)]
        [InlineData(false, 80, false, 81, WebServerAction.None)]
        public void ComputeWebServerAction_MatchesExpectedTransition(
            bool wasOn, int oldPort, bool isOn, int newPort, WebServerAction expected)
        {
            Assert.Equal(expected, SettingsViewModel.ComputeWebServerAction(wasOn, oldPort, isOn, newPort));
        }

        [Fact]
        public void Load_AppliesDocumentedDefaults_WhenKeysMissing()
        {
            var (settings, prevIni, tempPath) = CreateIsolatedAppSettings();
            try
            {
                settings.MaxFPSValue = 0;
                var general = new IniSection("General");
                var effects = new FakeSettingsSideEffects();
                var vm = new SettingsViewModel(settings, general, effects);

                vm.LoadCommand.Execute(null);

                Assert.False(vm.AutoMute);
                Assert.True(vm.AggressiveRamTrim);
                Assert.True(vm.SmartFastLaunch);
                Assert.Equal(144m, vm.TargetFps);
                Assert.Equal(0m, vm.JobCpuCap);
            }
            finally
            {
                CleanupIsolatedAppSettings(prevIni, tempPath);
            }
        }

        [Fact]
        public void Load_ClampsCpuCap()
        {
            var (settings, prevIni, tempPath) = CreateIsolatedAppSettings();
            try
            {
                var general = new IniSection("General");
                general.Set(AppSettingsKeys.General.MaxTotalRobloxCpuPercent, "250");
                var effects = new FakeSettingsSideEffects();
                var vm = new SettingsViewModel(settings, general, effects);

                vm.LoadCommand.Execute(null);

                Assert.Equal(100m, vm.JobCpuCap);
            }
            finally
            {
                CleanupIsolatedAppSettings(prevIni, tempPath);
            }
        }

        [Fact]
        public void Save_UnlockFpsChanged_PatchesClientSettings()
        {
            var (settings, prevIni, tempPath) = CreateIsolatedAppSettings();
            try
            {
                settings.UnlockFPS = false;
                var general = new IniSection("General");
                var effects = new FakeSettingsSideEffects();
                var vm = new SettingsViewModel(settings, general, effects);
                vm.LoadCommand.Execute(null);

                vm.UnlockFps = true;
                vm.SaveCommand.Execute(null);

                Assert.Equal(1, effects.PatchClientSettingsCount);
                Assert.True(settings.UnlockFPS);
            }
            finally
            {
                CleanupIsolatedAppSettings(prevIni, tempPath);
            }
        }

        [Fact]
        public void Save_NothingChanged_DoesNotRestartWebServer()
        {
            var (settings, prevIni, tempPath) = CreateIsolatedAppSettings();
            try
            {
                settings.EnableWebServer = false;
                settings.WebServerPort = 7963;
                var general = new IniSection("General");
                var effects = new FakeSettingsSideEffects();
                var vm = new SettingsViewModel(settings, general, effects);
                vm.LoadCommand.Execute(null);

                vm.SaveCommand.Execute(null);

                Assert.Equal(0, effects.RestartWebServerCount);
                Assert.Equal(0, effects.StopWebServerCount);
            }
            finally
            {
                CleanupIsolatedAppSettings(prevIni, tempPath);
            }
        }

        [Fact]
        public void Save_WritesHideUsernames_AndNotifiesDisplayNames()
        {
            var (settings, prevIni, tempPath) = CreateIsolatedAppSettings();
            try
            {
                var general = new IniSection("General");
                var effects = new FakeSettingsSideEffects();
                var vm = new SettingsViewModel(settings, general, effects);
                vm.LoadCommand.Execute(null);

                vm.HideUsernames = true;
                vm.SaveCommand.Execute(null);

                Assert.Equal("true", general.Get("HideUsernames"));
                Assert.Equal(1, effects.NotifyDisplayNamesChangedCount);
            }
            finally
            {
                CleanupIsolatedAppSettings(prevIni, tempPath);
            }
        }

        [Fact]
        public void Save_Throws_ShowsSaveError()
        {
            var (settings, prevIni, tempPath) = CreateIsolatedAppSettings();
            try
            {
                var general = new IniSection("General");
                var effects = new FakeSettingsSideEffects
                {
                    ThrowOnQueueSave = new InvalidOperationException("disk full")
                };
                var vm = new SettingsViewModel(settings, general, effects);
                vm.LoadCommand.Execute(null);

                vm.SaveCommand.Execute(null);

                Assert.Equal(LocalizationService.Format("Settings.SaveError", "disk full"), vm.SaveFeedback);
            }
            finally
            {
                CleanupIsolatedAppSettings(prevIni, tempPath);
            }
        }

        [Fact]
        public async Task SaveFeedback_ClearsAfterDuration()
        {
            var (settings, prevIni, tempPath) = CreateIsolatedAppSettings();
            try
            {
                var general = new IniSection("General");
                var effects = new FakeSettingsSideEffects();
                var vm = new SettingsViewModel(settings, general, effects, feedbackDuration: TimeSpan.FromMilliseconds(20));
                vm.LoadCommand.Execute(null);

                vm.SaveCommand.Execute(null);
                Assert.Equal(LocalizationService.Get("Settings.SaveSuccess"), vm.SaveFeedback);

                await Task.Delay(80);
                Assert.Equal(string.Empty, vm.SaveFeedback);
            }
            finally
            {
                CleanupIsolatedAppSettings(prevIni, tempPath);
            }
        }

        [Fact]
        public async Task Save_Twice_RestartsFeedbackTimer()
        {
            var (settings, prevIni, tempPath) = CreateIsolatedAppSettings();
            try
            {
                var general = new IniSection("General");
                var effects = new FakeSettingsSideEffects();
                var vm = new SettingsViewModel(settings, general, effects, feedbackDuration: TimeSpan.FromMilliseconds(80));
                vm.LoadCommand.Execute(null);

                vm.SaveCommand.Execute(null);
                await Task.Delay(45);
                vm.SaveCommand.Execute(null);
                await Task.Delay(45);

                Assert.Equal(LocalizationService.Get("Settings.SaveSuccess"), vm.SaveFeedback);

                await Task.Delay(80);
                Assert.Equal(string.Empty, vm.SaveFeedback);
            }
            finally
            {
                CleanupIsolatedAppSettings(prevIni, tempPath);
            }
        }
    }
}
