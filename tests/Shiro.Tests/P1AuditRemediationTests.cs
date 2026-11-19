#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using NSubstitute;
using Shiro.AvaloniaUI.Views;
using Shiro.Services;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class P1AuditRemediationTests
    {
        [Fact]
        public void MainViewModel_LanguageSwitch_EnToViToEn_ResetsDefaultReadyStatusSymmetrically()
        {
            var prevLang = LocalizationService.CurrentLanguage;
            var prevDispatcher = AppDispatcher.CustomDispatcher;
            AppDispatcher.CustomDispatcher = static action => action();
            try
            {
                var isolatedSettings = Substitute.For<ISettingsService>();
                isolatedSettings.Exists("EnablePotatoMode").Returns(true);
                isolatedSettings.Get<bool>("EnablePotatoMode").Returns(true);

                LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
                using var vm = new MainViewModel(null, isolatedSettings);
                Assert.True(vm.IsPotatoMode);
                string enReady = LocalizationService.Get("Status_ReadyDefault");
                Assert.Equal(enReady, vm.StatusMessage);

                LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);
                string viReady = LocalizationService.Get("Status_ReadyDefault");
                Assert.NotEqual(enReady, viReady);
                Assert.Equal(viReady, vm.StatusMessage);

                LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
                Assert.Equal(enReady, vm.StatusMessage);

                vm.StatusMessage = "Custom toast status";
                LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);
                LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
                Assert.Equal("Custom toast status", vm.StatusMessage);
            }
            finally
            {
                AppDispatcher.CustomDispatcher = prevDispatcher;
                LocalizationService.SetLanguage(prevLang, saveSetting: false);
            }
        }

        [Fact]
        public void ServerListViewModel_LanguageSwitch_EnToViToEn_ResetsDefaultReadyStatusSymmetrically()
        {
            var prevLang = LocalizationService.CurrentLanguage;
            var prevDispatcher = AppDispatcher.CustomDispatcher;
            AppDispatcher.CustomDispatcher = static action => action();
            try
            {
                LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
                using var vm = new ServerListViewModel();
                string enReady = LocalizationService.Get("Status_Ready");
                Assert.Equal(enReady, vm.LoadingStatus);

                LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);
                string viReady = LocalizationService.Get("Status_Ready");
                Assert.NotEqual(enReady, viReady);
                Assert.Equal(viReady, vm.LoadingStatus);

                LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
                Assert.Equal(enReady, vm.LoadingStatus);

                vm.LoadingStatus = "Custom server status";
                LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);
                LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
                Assert.Equal("Custom server status", vm.LoadingStatus);
            }
            finally
            {
                AppDispatcher.CustomDispatcher = prevDispatcher;
                LocalizationService.SetLanguage(prevLang, saveSetting: false);
            }
        }

        [Fact]
        public void CommandPaletteCatalog_GetBuiltinCommands_ReturnsSameCachedInstance()
        {
            var first = CommandPaletteCatalog.GetBuiltinCommands();
            var second = CommandPaletteCatalog.GetBuiltinCommands();

            Assert.NotEmpty(first);
            Assert.Same(first, second);
        }

        [AvaloniaFact]
        public void SettingsView_InitialShow_LoadsViewModelOnce_AndReloadsWhenReopened()
        {
            var innerGeneral = AppServices.GeneralSettings;
            var generalSpy = Substitute.For<ISettingsService>();
            int loadProbeCount = 0;

            generalSpy.Exists(Arg.Any<string>()).Returns(ci =>
            {
                string key = ci.Arg<string>();
                if (string.Equals(key, AppSettingsKeys.General.AggressiveRamTrimming, StringComparison.Ordinal))
                    loadProbeCount++;
                return innerGeneral.Exists(key);
            });
            generalSpy.Get(Arg.Any<string>()).Returns(ci => innerGeneral.Get(ci.Arg<string>()));
            generalSpy.Get(Arg.Any<string>(), Arg.Any<bool>()).Returns(ci => innerGeneral.Get(ci.Arg<string>(), ci.Arg<bool>()));
            generalSpy.Get<bool>(Arg.Any<string>()).Returns(ci => innerGeneral.Get<bool>(ci.Arg<string>()));
            generalSpy.Get(Arg.Any<string>(), Arg.Any<int>()).Returns(ci => innerGeneral.Get(ci.Arg<string>(), ci.Arg<int>()));
            generalSpy.Get<int>(Arg.Any<string>()).Returns(ci => innerGeneral.Get<int>(ci.Arg<string>()));
            generalSpy.Get(Arg.Any<string>(), Arg.Any<double>()).Returns(ci => innerGeneral.Get(ci.Arg<string>(), ci.Arg<double>()));
            generalSpy.When(s => s.Set(Arg.Any<string>(), Arg.Any<string?>()))
                .Do(ci => innerGeneral.Set(ci.ArgAt<string>(0), ci.ArgAt<string?>(1)));

            using var _ = AppServices.Override<ISettingsService>(generalSpy);
            Window? window = null;
            try
            {
                var hiddenView = new SettingsView { IsVisible = false };
                window = new Window { Width = 900, Height = 700, Content = hiddenView };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(0, loadProbeCount);

                hiddenView.IsVisible = true;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(1, loadProbeCount);

                hiddenView.IsVisible = false;
                Dispatcher.UIThread.RunJobs();
                hiddenView.IsVisible = true;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(2, loadProbeCount);
            }
            finally
            {
                window?.Close();
            }

            loadProbeCount = 0;
            Window? visibleWindow = null;
            try
            {
                var visibleView = new SettingsView();
                visibleWindow = new Window { Width = 900, Height = 700, Content = visibleView };
                visibleWindow.Show();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(1, loadProbeCount);
            }
            finally
            {
                visibleWindow?.Close();
            }
        }

        [Fact]
        public void LocalizationService_GetWithDictionaries_FallsBackToEnglishOnlyWhenNotEnglishPrimary()
        {
            var en = new Dictionary<string, string>(StringComparer.Ordinal) { ["MissingInVi"] = "English Fallback" };
            var vi = new Dictionary<string, string>(StringComparer.Ordinal) { ["OnlyInVi"] = "Chi co trong Vi" };

            Assert.Equal("English Fallback", LocalizationService.GetWithDictionaries("MissingInVi", null, vi, en));
            Assert.Equal("custom_fb", LocalizationService.GetWithDictionaries("MissingInBoth", "custom_fb", vi, en));
            Assert.Equal("MissingInBoth", LocalizationService.GetWithDictionaries("MissingInBoth", null, vi, en));
            Assert.Equal("OnlyInVi", LocalizationService.GetWithDictionaries("OnlyInVi", "OnlyInVi", en, en));
        }

        [Fact]
        public void DiscordRpcService_WorkerGenerationGuard_PreventsStaleWorkerCleanup()
        {
            Assert.False(DiscordRpcService.ShouldCleanupWorkerSession(workerGeneration: 1, activeGeneration: 2));
            Assert.True(DiscordRpcService.ShouldCleanupWorkerSession(workerGeneration: 2, activeGeneration: 2));

            var logger = Substitute.For<IAppLogger>();
            var settings = Substitute.For<ISettingsService>();
            settings.Get(AppSettingsKeys.General.DiscordRpcEnabled, Arg.Any<bool>()).Returns(true);
            settings.Get<bool>(AppSettingsKeys.General.DiscordRpcEnabled, Arg.Any<bool>()).Returns(true);

            using var rpc = new DiscordRpcService(logger, settings);
            long genBefore = rpc.WorkerGenerationForTest;

            rpc.ApplyConfigurationChanged(true, reconnect: true);
            rpc.ApplyConfigurationChanged(true, reconnect: true);
            long genAfterReconnects = rpc.WorkerGenerationForTest;

            rpc.Stop();
            long genAfterStop = rpc.WorkerGenerationForTest;

            Assert.True(genAfterReconnects > genBefore);
            Assert.True(genAfterStop > genAfterReconnects);
        }

        [Fact]
        public void StreamerModeService_Shutdown_StopsAndAllowsCleanReinitialize()
        {
            var ex = Record.Exception(() =>
            {
                StreamerModeService.InitializeForTest();
                StreamerModeService.Shutdown();
                StreamerModeService.Shutdown();
                StreamerModeService.InitializeForTest();
                StreamerModeService.Shutdown();
            });
            Assert.Null(ex);
            Assert.False(StreamerModeService.IsStreamingAppDetected);
        }
    }
}
