#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using NSubstitute;
using Shiro.AvaloniaUI.Views;
using Shiro.Services;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests.Ui
{
    [Collection("SerialGlobalState")]
    public sealed class Phase2UiSafetyTests : IDisposable
    {
        private readonly bool _origStreamerEnabled = StreamerModeService.IsEnabled;
        private readonly bool _origStreamerAutoDetect = StreamerModeService.AutoDetectEnabled;
        private readonly string? _origStreamerSetting = AppServices.GeneralSettings.Get<string>(AppSettingsKeys.General.StreamerModeEnabled);
        private readonly string? _origHideUsernamesSetting = AppServices.GeneralSettings.Get<string>(AppSettingsKeys.General.HideUsernames);
        private readonly Action<Action>? _origDispatcher = AppDispatcher.CustomDispatcher;
        private readonly AppLanguage _origLanguage = LocalizationService.CurrentLanguage;

        public void Dispose()
        {
            AppDispatcher.CustomDispatcher = _origDispatcher;
            StreamerModeService.IsEnabled = _origStreamerEnabled;
            StreamerModeService.AutoDetectEnabled = _origStreamerAutoDetect;
            RestoreSetting(AppSettingsKeys.General.StreamerModeEnabled, _origStreamerSetting);
            RestoreSetting(AppSettingsKeys.General.HideUsernames, _origHideUsernamesSetting);
            LocalizationService.SetLanguage(_origLanguage, saveSetting: false);
            Application.Current?.Resources.MergedDictionaries.Clear();
        }

        private static void RestoreSetting(string key, string? previous)
        {
            if (previous is null)
                AppServices.GeneralSettings.RemoveProperty(key);
            else
                AppServices.GeneralSettings.Set(key, previous);
        }

        [AvaloniaFact]
        public async Task AccountFieldsDialog_StreamerModeChangedFromBackgroundThread_UpdatesHeaderWithoutCrossThreadCrash()
        {
            using var settingsScope = AppServices.Override<ISettingsService>(new IniFile()["General"]);
            var account = new Account { Username = "SensitiveFieldUser", UserID = 11223344, Valid = true };
            AccountFieldsDialog? dlg = null;

            try
            {
                StreamerModeService.AutoDetectEnabled = false;
                StreamerModeService.IsEnabled = false;

                dlg = new AccountFieldsDialog(account);
                dlg.Show();
                Dispatcher.UIThread.RunJobs();

                var txtHeader = dlg.FindControl<TextBlock>("TxtHeaderTitle");
                Assert.NotNull(txtHeader);
                Assert.Contains("SensitiveFieldUser", txtHeader!.Text ?? string.Empty, StringComparison.Ordinal);

                StreamerModeService.IsEnabled = true;
                Exception? bgException = await Record.ExceptionAsync(() => Task.Run(() => dlg.InvokeStreamerModeChangedForTest(true)));
                Dispatcher.UIThread.RunJobs();

                Assert.Null(bgException);
                Assert.DoesNotContain("SensitiveFieldUser", txtHeader.Text ?? string.Empty, StringComparison.Ordinal);
                Assert.Contains(StreamerModeService.MaskUsername("SensitiveFieldUser"), txtHeader.Text ?? string.Empty, StringComparison.Ordinal);

                dlg.Close();
                Dispatcher.UIThread.RunJobs();

                StreamerModeService.IsEnabled = false;
                Exception? closedBgException = await Record.ExceptionAsync(() => Task.Run(() => dlg.InvokeStreamerModeChangedForTest(false)));
                Dispatcher.UIThread.RunJobs();

                Assert.Null(closedBgException);
                Assert.DoesNotContain("SensitiveFieldUser", txtHeader.Text ?? string.Empty, StringComparison.Ordinal);
                dlg = null;
            }
            finally
            {
                if (dlg != null)
                {
                    try { dlg.Close(); }
                    catch (InvalidOperationException) { }
                    Dispatcher.UIThread.RunJobs();
                }
                StreamerModeService.IsEnabled = _origStreamerEnabled;
            }
        }

        [AvaloniaFact]
        public void DashboardView_GetItemFromMenu_UnattachedMenuItem_ReturnsNullInsteadOfFirstAccount()
        {
            var repo = Substitute.For<IAccountRepository>();
            var acc1 = new Account { Username = "MenuTargetOne", UserID = 101, Valid = true, SecurityToken = "tok1" };
            var acc2 = new Account { Username = "MenuTargetTwo", UserID = 102, Valid = true, SecurityToken = "tok2" };
            repo.GetAll().Returns(new List<Account> { acc1, acc2 });
            repo.Count.Returns(2);

            using var repoScope = AppServices.Override<IAccountRepository>(repo);
            using var settingsScope = AppServices.Override<ISettingsService>(new IniFile()["General"]);

            var view = new DashboardView();
            try
            {
                view.LoadDashboardData(new[] { acc1, acc2 });
                Assert.Equal(2, view.ViewModel.AccountsVm.FilteredAccounts.Count);

                var firstAccountVm = view.ViewModel.AccountsVm.FilteredAccounts[0];
                var secondAccountVm = view.ViewModel.AccountsVm.FilteredAccounts[1];
                firstAccountVm.IsSelected = true;
                secondAccountVm.IsSelected = false;

                Assert.Null(view.ResolveItemFromMenuForTest(new MenuItem()));
                Assert.Null(view.ResolveItemFromMenuForTest(null));
                Assert.Same(secondAccountVm, view.ResolveItemFromMenuForTest(new MenuItem { DataContext = secondAccountVm }));
            }
            finally
            {
                view.ViewModel.Dispose();
            }
        }
    }
}
