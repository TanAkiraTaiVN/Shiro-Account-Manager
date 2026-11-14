#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Logging;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using Shiro.AvaloniaUI.Views;
using Shiro.Services;
using Shiro.ViewModels;
using Shiro.ViewModels.Dialogs;
using Xunit;

namespace Shiro.Tests.Ui
{
    public partial class DialogSmokeTests
    {
        [AvaloniaFact]
        public void FastLoginDialog_StartInCookieMode_CompactsHeight_HidesEmptyPreviewName_AndLocalizesWatermarks()
        {
            var sink = new BindingErrorSink();
            var prevSink = Logger.Sink;
            var prevLang = LocalizationService.CurrentLanguage;
            FastLoginDialog? dlg = null;
            Logger.Sink = sink;
            try
            {
                LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
                dlg = new FastLoginDialog(null, startInCookieMode: true);
                dlg.Show();
                Dispatcher.UIThread.RunJobs();

                Assert.True(dlg.IsCookieModeActive);
                Assert.Equal(FastLoginDialog.CookieModeHeight, dlg.Height);
                Assert.Equal(FastLoginDialog.CookieModeMinHeight, dlg.MinHeight);

                var pnlInApp = dlg.FindControl<Grid>("PnlInApp");
                var pnlCookie = dlg.FindControl<ScrollViewer>("PnlCookie");
                var btnVerify = dlg.FindControl<Button>("BtnVerify");
                var btnAddCookie = dlg.FindControl<Button>("BtnAddCookie");
                var txtTip = dlg.FindControl<TextBlock>("TxtTip");
                var txtPreviewUsername = dlg.FindControl<TextBlock>("TxtPreviewUsername");
                var txtAlias = dlg.FindControl<TextBox>("TxtCookieAlias");
                var txtGroup = dlg.FindControl<TextBox>("TxtCookieGroup");

                Assert.NotNull(pnlInApp);
                Assert.NotNull(pnlCookie);
                Assert.NotNull(btnVerify);
                Assert.NotNull(btnAddCookie);
                Assert.NotNull(txtTip);
                Assert.NotNull(txtPreviewUsername);
                Assert.NotNull(txtAlias);
                Assert.NotNull(txtGroup);

                Assert.False(pnlInApp.IsVisible);
                Assert.True(pnlCookie.IsVisible);
                Assert.True(btnVerify.IsVisible);
                Assert.True(btnAddCookie.IsVisible);
                Assert.False(txtTip.IsVisible);
                Assert.False(txtPreviewUsername.IsVisible);

                string enAliasWatermark = LocalizationService.Get("FastLogin_AliasWatermark");
                string enGroupWatermark = LocalizationService.Get("FastLogin_GroupWatermark");
                Assert.Equal(enAliasWatermark, txtAlias.Watermark);
                Assert.Equal(enGroupWatermark, txtGroup.Watermark);

                LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);
                Dispatcher.UIThread.RunJobs();

                string viAliasWatermark = LocalizationService.Get("FastLogin_AliasWatermark");
                string viGroupWatermark = LocalizationService.Get("FastLogin_GroupWatermark");
                Assert.NotEqual(enAliasWatermark, viAliasWatermark);
                Assert.NotEqual(enGroupWatermark, viGroupWatermark);
                Assert.Equal(viAliasWatermark, txtAlias.Watermark);
                Assert.Equal(viGroupWatermark, txtGroup.Watermark);

                dlg.SelectLoginMode(isCookieMode: false);
                Dispatcher.UIThread.RunJobs();
                Assert.False(dlg.IsCookieModeActive);
                Assert.Equal(FastLoginDialog.InAppModeHeight, dlg.Height);
                Assert.Equal(FastLoginDialog.InAppModeMinHeight, dlg.MinHeight);
                Assert.True(pnlInApp.IsVisible);
                Assert.False(pnlCookie.IsVisible);

                dlg.SelectLoginMode(isCookieMode: true);
                Dispatcher.UIThread.RunJobs();
                Assert.True(dlg.IsCookieModeActive);
                Assert.Equal(FastLoginDialog.CookieModeHeight, dlg.Height);
                Assert.Empty(sink.Errors);
            }
            finally
            {
                dlg?.Close();
                LocalizationService.SetLanguage(prevLang, saveSetting: false);
                Avalonia.Application.Current?.Resources.MergedDictionaries.Clear();
                Logger.Sink = prevSink;
            }
        }

        [AvaloniaFact]
        public void AccountFieldsDialog_Opens_FiltersInternalFields_AndHasZeroBindingErrors()
        {
            var sink = new BindingErrorSink();
            var prevSink = Logger.Sink;
            var prevLang = LocalizationService.CurrentLanguage;
            AccountFieldsDialog? dlg = null;
            Logger.Sink = sink;
            try
            {
                var repo = Substitute.For<IAccountRepository>();
                using var repoScope = AppServices.Override<IAccountRepository>(repo);

                var account = new Account { Username = "FieldFilterUser" };
                account.Fields["PublicTag"] = "MainGroup";
                account.Fields["FarmTier"] = "Gold";
                account.Fields["HbaPrivateKey"] = "pkcs8-secret";
                account.Fields["HbaPublicKey"] = "spki-public";
                account.Fields["HbaTemplate"] = "template-secret";
                account.Fields["SessionUserAgent"] = "Roblox/WinInet";
                account.Fields["LastBoundIpSig"] = "ip-sig";

                LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
                dlg = new AccountFieldsDialog(account);
                dlg.Show();
                Dispatcher.UIThread.RunJobs();

                var listBox = dlg.FindControl<ListBox>("FieldsListBox");
                Assert.NotNull(listBox);
                var items = listBox.Items.OfType<AccountFieldItem>().ToList();
                Assert.Equal(2, items.Count);
                Assert.Contains(items, x => x.Key == "PublicTag" && x.Value == "MainGroup");
                Assert.Contains(items, x => x.Key == "FarmTier" && x.Value == "Gold");
                Assert.DoesNotContain(items, x => x.Key.StartsWith("Hba", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(items, x => x.Key == "SessionUserAgent" || x.Key == "LastBoundIpSig");

                var txtHeader = dlg.FindControl<TextBlock>("TxtHeaderTitle");
                var txtNewKey = dlg.FindControl<TextBox>("TxtNewKey");
                Assert.NotNull(txtHeader);
                Assert.NotNull(txtNewKey);
                Assert.Equal(LocalizationService.Format("AccountFields.TitleFormat", "FieldFilterUser"), txtHeader.Text);

                string enKeyWatermark = LocalizationService.Get("AccountFields_KeyWatermark");
                Assert.Equal(enKeyWatermark, txtNewKey.Watermark);

                LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);
                Dispatcher.UIThread.RunJobs();

                string viKeyWatermark = LocalizationService.Get("AccountFields_KeyWatermark");
                Assert.NotEqual(enKeyWatermark, viKeyWatermark);
                Assert.Equal(viKeyWatermark, txtNewKey.Watermark);
                Assert.Equal(LocalizationService.Get("AccountFields_WindowTitle"), dlg.Title);
                Assert.Empty(sink.Errors);
            }
            finally
            {
                dlg?.Close();
                LocalizationService.SetLanguage(prevLang, saveSetting: false);
                Avalonia.Application.Current?.Resources.MergedDictionaries.Clear();
                Logger.Sink = prevSink;
            }
        }

        [AvaloniaFact]
        public void InputDialog_And_MessageBoxDialog_Open_WithZeroBindingErrors()
        {
            var sink = new BindingErrorSink();
            var prevSink = Logger.Sink;
            var prevLang = LocalizationService.CurrentLanguage;
            InputDialog? inputDlg = null;
            MessageBoxDialog? msgDlg = null;
            Logger.Sink = sink;
            try
            {
                LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);

                inputDlg = new InputDialog();
                inputDlg.Show();
                msgDlg = new MessageBoxDialog();
                msgDlg.Show();
                Dispatcher.UIThread.RunJobs();

                var inputTitle = inputDlg.FindControl<TextBlock>("TxtTitle");
                var inputBox = inputDlg.FindControl<TextBox>("TxtInput");
                var inputOk = inputDlg.FindControl<Button>("BtnOK");
                Assert.NotNull(inputTitle);
                Assert.NotNull(inputBox);
                Assert.NotNull(inputOk);

                string enInputTitle = LocalizationService.Get("InputDialog_DefaultTitle");
                Assert.Equal(enInputTitle, inputTitle.Text);

                var msgTitle = msgDlg.FindControl<TextBlock>("TxtTitle");
                var msgOk = msgDlg.FindControl<Button>("BtnOK");
                var msgCancel = msgDlg.FindControl<Button>("BtnCancel");
                Assert.NotNull(msgTitle);
                Assert.NotNull(msgOk);
                Assert.NotNull(msgCancel);

                string enMsgTitle = LocalizationService.Get("Msg_InfoTitle");
                Assert.Equal(enMsgTitle, msgTitle.Text);
                Assert.True(msgOk.IsVisible);
                Assert.False(msgCancel.IsVisible);

                LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);
                Dispatcher.UIThread.RunJobs();

                string viInputTitle = LocalizationService.Get("InputDialog_DefaultTitle");
                string viMsgTitle = LocalizationService.Get("Msg_InfoTitle");
                Assert.NotEqual(enInputTitle, viInputTitle);
                Assert.NotEqual(enMsgTitle, viMsgTitle);
                Assert.Equal(viInputTitle, inputTitle.Text);
                Assert.Equal(viMsgTitle, msgTitle.Text);

                inputBox.Text = "HeadlessInputResult";
                inputOk.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                msgOk.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();

                Assert.False(inputDlg.IsVisible);
                Assert.False(msgDlg.IsVisible);
                Assert.Empty(sink.Errors);
            }
            finally
            {
                inputDlg?.Close();
                msgDlg?.Close();
                LocalizationService.SetLanguage(prevLang, saveSetting: false);
                Avalonia.Application.Current?.Resources.MergedDictionaries.Clear();
                Logger.Sink = prevSink;
            }
        }

        [AvaloniaFact]
        public void ServerListView_Loads_WithServerListViewModel_AndZeroBindingErrors()
        {
            var sink = new BindingErrorSink();
            var prevSink = Logger.Sink;
            var prevLang = LocalizationService.CurrentLanguage;
            Window? window = null;
            ServerListViewModel? vm = null;
            Logger.Sink = sink;
            try
            {
                var browserState = Substitute.For<IServerBrowserStateService>();
                browserState.CurrentPlaceId.Returns(string.Empty);
                using var browserScope = AppServices.Override<IServerBrowserStateService>(browserState);

                LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
                var logger = Substitute.For<IAppLogger>();
                var settings = Substitute.For<ISettingsService>();
                vm = new ServerListViewModel(null, logger, settings);
                vm.SetRawServers(new[]
                {
                    new ServerItemViewModel
                    {
                        JobId = "job-smoke-01",
                        Type = "Public",
                        Playing = 8,
                        MaxPlayers = 12,
                        Ping = 35,
                        Region = "SG"
                    }
                });

                var view = new ServerListView { DataContext = vm };
                window = new Window { Width = 1000, Height = 700, Content = view };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                Assert.Same(vm, view.ViewModel);
                Assert.Equal(1, vm.TotalServersCount);

                var txtPlaceId = view.FindControl<TextBox>("TxtPlaceId");
                Assert.NotNull(txtPlaceId);
                string enPlaceholder = LocalizationService.Get("Srv_PlaceIdPlaceholder");
                Assert.Equal(enPlaceholder, txtPlaceId.Watermark);

                var rowTexts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToList();
                Assert.Contains("job-smoke-01", rowTexts);

                vm.SelectedSubTabIndex = 2;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(2, vm.SelectedSubTabIndex);

                vm.SelectedSubTabIndex = 3;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(3, vm.SelectedSubTabIndex);

                vm.SelectedSubTabIndex = 0;
                Dispatcher.UIThread.RunJobs();

                LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);
                Dispatcher.UIThread.RunJobs();

                string viPlaceholder = LocalizationService.Get("Srv_PlaceIdPlaceholder");
                Assert.NotEqual(enPlaceholder, viPlaceholder);
                Assert.Equal(viPlaceholder, txtPlaceId.Watermark);
                Assert.Equal(LocalizationService.Get("Status_Ready"), vm.LoadingStatus);
                Assert.Empty(sink.Errors);
            }
            finally
            {
                window?.Close();
                vm?.Dispose();
                LocalizationService.SetLanguage(prevLang, saveSetting: false);
                Avalonia.Application.Current?.Resources.MergedDictionaries.Clear();
                Logger.Sink = prevSink;
            }
        }

        [AvaloniaFact]
        public void DashboardView_Loads_SwitchesLanguage_AndDisposesCleanly_WithZeroBindingErrors()
        {
            var sink = new BindingErrorSink();
            var prevSink = Logger.Sink;
            var prevLang = LocalizationService.CurrentLanguage;
            string prevPlaceId = AppSettings.CurrentPlaceId;
            Window? window = null;
            DashboardView? view = null;
            Logger.Sink = sink;
            try
            {
                using var suppressSave = Account.BeginSuppressDeferredSaveScope();
                AppSettings.CurrentPlaceId = string.Empty;

                var repo = Substitute.For<IAccountRepository>();
                var acc1 = new Account
                {
                    Username = "DashSmokeOne",
                    UserID = 0,
                    Valid = true,
                    SecurityToken = "smoke_tok_1",
                    RobuxBalance = 500
                };
                var acc2 = new Account
                {
                    Username = "DashSmokeTwo",
                    UserID = 0,
                    Valid = false
                };
                repo.GetAll().Returns(new List<Account> { acc1, acc2 });
                repo.Count.Returns(2);

                var settings = Substitute.For<ISettingsService>();
                var browserState = Substitute.For<IServerBrowserStateService>();
                browserState.CurrentPlaceId.Returns(string.Empty);

                using var repoScope = AppServices.Override<IAccountRepository>(repo);
                using var settingsScope = AppServices.Override<ISettingsService>(settings);
                using var browserScope = AppServices.Override<IServerBrowserStateService>(browserState);

                LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
                view = new DashboardView();
                window = new Window { Width = 1200, Height = 800, Content = view };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                Assert.IsType<MainViewModel>(view.DataContext);
                Assert.Equal(2, view.ViewModel.TotalCount);
                Assert.Equal(1, view.ViewModel.ReadyCount);
                Assert.Equal(1, view.ViewModel.ExpiredCount);

                var txtSearch = view.FindControl<TextBox>("TxtSearch");
                Assert.NotNull(txtSearch);
                string enSearchWatermark = LocalizationService.Get("Dash_SearchPlaceholder");
                Assert.Equal(enSearchWatermark, txtSearch.Watermark);

                string enTabAllExpected = LocalizationService.Format("Main.TabAllCount", 2);
                var enTexts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToList();
                Assert.Contains(enTabAllExpected, enTexts);

                LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);
                Dispatcher.UIThread.RunJobs();

                string viSearchWatermark = LocalizationService.Get("Dash_SearchPlaceholder");
                Assert.NotEqual(enSearchWatermark, viSearchWatermark);
                Assert.Equal(viSearchWatermark, txtSearch.Watermark);

                string viTabAllExpected = LocalizationService.Format("Main.TabAllCount", 2);
                Assert.NotEqual(enTabAllExpected, viTabAllExpected);
                var viTexts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToList();
                Assert.Contains(viTabAllExpected, viTexts);

                int postDisposeNotifications = 0;
                view.ViewModel.PropertyChanged += (_, _) => postDisposeNotifications++;
                view.ViewModel.Dispose();

                LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(0, postDisposeNotifications);
                Assert.Empty(sink.Errors);
            }
            finally
            {
                window?.Close();
                view?.ViewModel.Dispose();
                AppSettings.CurrentPlaceId = prevPlaceId;
                LocalizationService.SetLanguage(prevLang, saveSetting: false);
                Avalonia.Application.Current?.Resources.MergedDictionaries.Clear();
                Logger.Sink = prevSink;
            }
        }
    }
}
