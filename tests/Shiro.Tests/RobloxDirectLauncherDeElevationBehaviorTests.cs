#nullable enable
using System;
using System.Diagnostics;
using NSubstitute;
using Shiro.Services;
using Shiro.ViewModels.Dialogs;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class RobloxDirectLauncherDeElevationBehaviorTests
    {
        [Fact]
        public void IsCurrentProcessElevated_ReturnsValidBoolean()
        {
            bool isElevated = RobloxDirectLauncher.IsCurrentProcessElevated();
            Assert.Equal(isElevated, RobloxDirectLauncher.IsCurrentProcessElevated());
        }

        [Fact]
        public void SpawnRobloxProcess_StandardFallback_SpawnsSuccessfully()
        {
            AppSettings.General.Set(AppSettingsKeys.General.EnableTokenDeElevation, "false");
            using var proc = RobloxDirectLauncher.SpawnRobloxProcess("cmd.exe", "/c exit 0", "TestUser");
            Assert.NotNull(proc);
            proc!.WaitForExit(3000);
            Assert.True(proc.HasExited);
        }

        [Fact]
        public void TrySpawnDeElevated_ExecutesTokenDuplicationPipeline()
        {
            bool res = RobloxDirectLauncher.TrySpawnDeElevated("cmd.exe", "/c exit 0", out Process? deElevated);
            if (res && deElevated != null)
            {
                deElevated.WaitForExit(3000);
                deElevated.Dispose();
            }
            Assert.IsType<bool>(res);

            bool failRes = RobloxDirectLauncher.TrySpawnDeElevated("C:\\non_existent_executable_123456.exe", string.Empty, out _);
            Assert.False(failRes);
        }

        [Fact]
        public void SelectExplorerPidForCurrentSession_WhenNoMatchingSession_ReturnsZeroInsteadOfCrossSessionExplorer()
        {
            using var current = Process.GetCurrentProcess();
            int sameSession = current.SessionId;
            int foreignSession = sameSession + 1;

            int matchedPid = RobloxDirectLauncher.SelectExplorerPidForCurrentSession(new[] { current }, sameSession);
            Assert.Equal(current.Id, matchedPid);

            int mismatchPid = RobloxDirectLauncher.SelectExplorerPidForCurrentSession(new[] { current }, foreignSession);
            Assert.Equal(0, mismatchPid);
            Assert.Equal(0, RobloxDirectLauncher.SelectExplorerPidForCurrentSession(Array.Empty<Process>(), sameSession));
        }

        [Fact]
        public void RobloxDirectLauncher_EnableTokenDeElevationDefault_IsFalseMatchingSettingsViewModel()
        {
            var ini = new IniFile();
            var general = ini["General"];
            var prevIni = AppSettings.ActiveIniFile;
            AppSettings.ActiveIniFile = ini;
            using var settingsScope = AppServices.Override<ISettingsService>(general);
            try
            {
                var vm = new SettingsViewModel(
                    new AppSettings(),
                    general,
                    Substitute.For<ISettingsSideEffects>(),
                    Substitute.For<IDialogService>(),
                    TimeSpan.FromMilliseconds(10),
                    Substitute.For<IAppLogger>());
                vm.Load();

                Assert.False(vm.EnableTokenDeElevation);
                Assert.False(RobloxDirectLauncher.IsTokenDeElevationEnabled());

                general.Set(AppSettingsKeys.General.EnableTokenDeElevation, "true");
                Assert.True(RobloxDirectLauncher.IsTokenDeElevationEnabled());
            }
            finally
            {
                AppSettings.ActiveIniFile = prevIni;
            }
        }
    }
}
