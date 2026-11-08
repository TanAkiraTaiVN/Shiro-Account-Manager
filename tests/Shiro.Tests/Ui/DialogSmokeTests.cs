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
using Shiro.Classes;
using Shiro.Nexus;
using Shiro.Services;
using Shiro.ViewModels;
using Shiro.ViewModels.Dialogs;
using Xunit;

namespace Shiro.Tests.Ui
{
    internal sealed class BindingErrorSink : ILogSink
    {
        public List<string> Errors { get; } = new();

        public bool IsEnabled(LogEventLevel level, string area) =>
            level >= LogEventLevel.Warning && area == LogArea.Binding;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
        {
            if (IsEnabled(level, area))
            {
                Errors.Add(messageTemplate);
            }
        }

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        {
            if (IsEnabled(level, area))
            {
                try
                {
                    Errors.Add(string.Format(messageTemplate, propertyValues));
                }
                catch
                {
                    Errors.Add(messageTemplate);
                }
            }
        }
    }

    [Collection("StaticSeams")]
    public class DialogSmokeTests
    {
        [AvaloniaFact]
        public void BulkImportDialog_Opens_WithViewModelDataContext()
        {
            var sink = new BindingErrorSink();
            var prevSink = Logger.Sink;
            var prevLang = LocalizationService.CurrentLanguage;
            BulkImportDialog? dlg = null;
            Logger.Sink = sink;
            try
            {
                dlg = new BulkImportDialog();
                dlg.Show();
                Assert.IsType<BulkImportViewModel>(dlg.DataContext);
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
        public void ProxyManagerDialog_Opens_WithViewModelDataContext()
        {
            var sink = new BindingErrorSink();
            var prevSink = Logger.Sink;
            var prevLang = LocalizationService.CurrentLanguage;
            ProxyManagerDialog? dlg = null;
            Logger.Sink = sink;
            try
            {
                dlg = new ProxyManagerDialog();
                dlg.Show();
                Assert.IsType<ProxyManagerViewModel>(dlg.DataContext);
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
        public void AccountUtilsDialog_Opens_WithViewModelDataContext()
        {
            var sink = new BindingErrorSink();
            var prevSink = Logger.Sink;
            var prevLang = LocalizationService.CurrentLanguage;
            AccountUtilsDialog? dlg = null;
            Logger.Sink = sink;
            try
            {
                dlg = new AccountUtilsDialog(new Account { Username = "TestSmoke" });
                dlg.Show();
                Assert.IsType<AccountUtilsViewModel>(dlg.DataContext);
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
        public void AltGenDialog_Opens_WithViewModelDataContext()
        {
            var sink = new BindingErrorSink();
            var prevSink = Logger.Sink;
            var prevLang = LocalizationService.CurrentLanguage;
            AltGenDialog? dlg = null;
            Logger.Sink = sink;
            try
            {
                dlg = new AltGenDialog();
                dlg.Show();
                Assert.IsType<AltGenViewModel>(dlg.DataContext);
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
        public void MissingAssetsDialog_Opens_WithViewModelDataContext()
        {
            var sink = new BindingErrorSink();
            var prevSink = Logger.Sink;
            var prevLang = LocalizationService.CurrentLanguage;
            MissingAssetsDialog? dlg = null;
            Logger.Sink = sink;
            try
            {
                dlg = new MissingAssetsDialog(new Account { Username = "SmokeUser" }, new long[] { 12345 });
                dlg.Show();
                Assert.IsType<MissingAssetsViewModel>(dlg.DataContext);
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
        public void UpdaterWindow_Opens_WithViewModelDataContext()
        {
            var sink = new BindingErrorSink();
            var prevSink = Logger.Sink;
            var prevLang = LocalizationService.CurrentLanguage;
            UpdaterWindow? w = null;
            Logger.Sink = sink;
            try
            {
                w = new UpdaterWindow();
                w.Show();
                Assert.IsType<UpdaterViewModel>(w.DataContext);
                Assert.Empty(sink.Errors);
            }
            finally
            {
                w?.Close();
                LocalizationService.SetLanguage(prevLang, saveSetting: false);
                Avalonia.Application.Current?.Resources.MergedDictionaries.Clear();
                Logger.Sink = prevSink;
            }
        }

        [AvaloniaFact]
        public void SettingsView_Loads_WithViewModelDataContext()
        {
            var sink = new BindingErrorSink();
            var prevSink = Logger.Sink;
            var prevLang = LocalizationService.CurrentLanguage;
            Window? window = null;
            Logger.Sink = sink;
            try
            {
                var view = new SettingsView();
                window = new Window { Content = view };
                window.Show();
                Assert.IsType<SettingsViewModel>(view.DataContext);
                Assert.Empty(sink.Errors);
            }
            finally
            {
                window?.Close();
                LocalizationService.SetLanguage(prevLang, saveSetting: false);
                Avalonia.Application.Current?.Resources.MergedDictionaries.Clear();
                Logger.Sink = prevSink;
            }
        }

        [AvaloniaFact]
        public void BulkImport_TypeAndClickStart_UpdatesStatus()
        {
            var sink = new BindingErrorSink();
            var prevSink = Logger.Sink;
            var prevLang = LocalizationService.CurrentLanguage;
            BulkImportDialog? dlg = null;
            Logger.Sink = sink;
            try
            {
                dlg = new BulkImportDialog();
                dlg.Show();

                var txtImport = dlg.FindControl<TextBox>("TxtImportData");
                Assert.NotNull(txtImport);

                var btnStart = dlg.FindControl<Button>("BtnStartImport");
                Assert.NotNull(btnStart);

                var txtStatus = dlg.FindControl<TextBlock>("TxtStatus");
                Assert.NotNull(txtStatus);

                txtImport.Text = "   ";
                btnStart.Command?.Execute(null);

                string expectedPrompt = LocalizationService.Get("BulkImport.PasteAccountListPrompt");
                Assert.Equal(expectedPrompt, txtStatus.Text);

                var btnClear = dlg.FindControl<Button>("BtnClear");
                Assert.NotNull(btnClear);
                btnClear.Command?.Execute(null);

                string expectedCleared = LocalizationService.Get("BulkImport.ContentCleared");
                Assert.Equal(expectedCleared, txtStatus.Text);
                Assert.Equal(string.Empty, txtImport.Text);
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
        public void AboutDialog_Opens_WithPopulatedRuntimeAndMemoryMetrics_AndZeroBindingErrors()
        {
            var sink = new BindingErrorSink();
            var prevSink = Logger.Sink;
            var prevLang = LocalizationService.CurrentLanguage;
            AboutDialog? dlg = null;
            Logger.Sink = sink;
            try
            {
                var repo = Substitute.For<IAccountRepository>();
                repo.Count.Returns(5);
                using var repoScope = AppServices.Override<IAccountRepository>(repo);

                LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
                dlg = new AboutDialog();
                dlg.Show();
                Dispatcher.UIThread.RunJobs();

                string enTitle = LocalizationService.Get("About_WindowTitle");
                Assert.Equal(enTitle, dlg.Title);

                var txtRuntime = dlg.FindControl<TextBlock>("TxtRuntime");
                var txtMemory = dlg.FindControl<TextBlock>("TxtMemory");
                var txtAccountCount = dlg.FindControl<TextBlock>("TxtAccountCount");
                Assert.NotNull(txtRuntime);
                Assert.NotNull(txtMemory);
                Assert.NotNull(txtAccountCount);

                string expectedRuntime = string.Concat(".NET ", Environment.Version.Major.ToString(), ".0 (x64)");
                Assert.Equal(expectedRuntime, txtRuntime.Text);
                Assert.EndsWith(" MB (Working Set)", txtMemory.Text, StringComparison.Ordinal);
                Assert.DoesNotContain("--", txtMemory.Text, StringComparison.Ordinal);
                Assert.Equal(LocalizationService.Format("About.AccountsCount", 5), txtAccountCount.Text);

                LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);
                Dispatcher.UIThread.RunJobs();

                string viTitle = LocalizationService.Get("About_WindowTitle");
                Assert.NotEqual(enTitle, viTitle);
                Assert.Equal(viTitle, dlg.Title);
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
        public void AboutView_Loads_InBothLanguages_WithZeroBindingErrors()
        {
            var sink = new BindingErrorSink();
            var prevSink = Logger.Sink;
            var prevLang = LocalizationService.CurrentLanguage;
            Window? window = null;
            Logger.Sink = sink;
            try
            {
                LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
                var view = new AboutView();
                window = new Window { Width = 600, Height = 500, Content = view };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                Assert.Equal("en", LocalizationService.CurrentLanguageCode);
                var enTexts = view.GetVisualDescendants().OfType<TextBlock>().Select(tb => tb.Text ?? string.Empty).ToList();
                Assert.Contains("Shiro Account Manager", enTexts);
                Assert.Contains(enTexts, t => t.Contains("v5.0.0", StringComparison.Ordinal));

                LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);
                Dispatcher.UIThread.RunJobs();

                Assert.Equal("vi", LocalizationService.CurrentLanguageCode);
                var viTexts = view.GetVisualDescendants().OfType<TextBlock>().Select(tb => tb.Text ?? string.Empty).ToList();
                Assert.Contains("Shiro Account Manager", viTexts);
                var images = view.GetVisualDescendants().OfType<Image>().ToList();
                Assert.Equal(4, images.Count);
                Assert.All(images, img => Assert.NotNull(img.Source));
                Assert.Empty(sink.Errors);
            }
            finally
            {
                window?.Close();
                LocalizationService.SetLanguage(prevLang, saveSetting: false);
                Avalonia.Application.Current?.Resources.MergedDictionaries.Clear();
                Logger.Sink = prevSink;
            }
        }

        [AvaloniaFact]
        public void AccountCardView_BindsAccountViewModel_WithZeroBindingErrors()
        {
            var sink = new BindingErrorSink();
            var prevSink = Logger.Sink;
            var prevLang = LocalizationService.CurrentLanguage;
            Window? window = null;
            Logger.Sink = sink;
            try
            {
                using var suppressSave = Account.BeginSuppressDeferredSaveScope();
                var logger = Substitute.For<IAppLogger>();
                var settings = Substitute.For<ISettingsService>();
                var account = new Account
                {
                    Username = "CardSmokeUser",
                    UserID = 123456,
                    Valid = true,
                    SecurityToken = "valid_smoke_cookie",
                    RobuxBalance = 2500,
                    Pinned = true
                };

                LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
                var vm = new AccountViewModel(account, logger, settings);
                var card = new AccountCardView { DataContext = vm };
                window = new Window { Width = 380, Height = 200, Content = card };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                Assert.Same(vm, card.DataContext);
                string enGroup = LocalizationService.Get("Converters.DefaultGroup");
                string enPlay = LocalizationService.Get("Srv_Play");
                var enTexts = card.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToList();
                Assert.Contains("CardSmokeUser", enTexts);
                Assert.Contains(enGroup, enTexts);
                Assert.Contains(enPlay, enTexts);

                var chk = card.FindControl<CheckBox>("ChkSelect");
                Assert.NotNull(chk);
                Assert.Equal(false, chk.IsChecked);

                vm.IsSelected = true;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(true, chk.IsChecked);

                LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);
                vm.NotifyLanguageChanged();
                Dispatcher.UIThread.RunJobs();

                string viGroup = LocalizationService.Get("Converters.DefaultGroup");
                string viPlay = LocalizationService.Get("Srv_Play");
                Assert.NotEqual(enGroup, viGroup);
                var viTexts = card.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToList();
                Assert.Contains(viGroup, viTexts);
                Assert.Contains(viPlay, viTexts);
                Assert.Empty(sink.Errors);
            }
            finally
            {
                window?.Close();
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
                    UserID = 1001,
                    Valid = true,
                    SecurityToken = "smoke_tok_1",
                    RobuxBalance = 500
                };
                var acc2 = new Account
                {
                    Username = "DashSmokeTwo",
                    UserID = 1002,
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

        [AvaloniaFact]
        public void NexusWindow_Opens_TogglesVisualState_AndHides_WithZeroBindingErrors()
        {
            var sink = new BindingErrorSink();
            var prevSink = Logger.Sink;
            var prevLang = LocalizationService.CurrentLanguage;
            var prevNexusHost = NexusHost.Current;
            string acFilePath = StoragePaths.GetUserFile("AccountControlData.json");
            bool acExisted = File.Exists(acFilePath);
            string? prevAcContent = acExisted ? File.ReadAllText(acFilePath) : null;
            var createdServers = new List<NexusWebSocketServer>();
            NexusWindow? nexusWindow = null;
            Logger.Sink = sink;
            try
            {
                using var suppressSave = Account.BeginSuppressDeferredSaveScope();
                var repo = Substitute.For<IAccountRepository>();
                var acc = new Account { Username = "NexusSmokeAcc", UserID = 9001 };
                repo.GetAll().Returns(new List<Account> { acc });

                var accountControl = Substitute.For<ISettingsService>();
                accountControl.Get("NexusPort").Returns("15242");
                accountControl.Get<bool>("AllowExternalConnections").Returns(false);
                accountControl.Get<bool>("StartOnLaunch").Returns(false);

                LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);

                var vm = new NexusViewModel(
                    repo,
                    accountControl,
                    (ip, p) =>
                    {
                        var srv = new NexusWebSocketServer(ip, p);
                        createdServers.Add(srv);
                        return srv;
                    },
                    _ => Task.CompletedTask,
                    logger: Substitute.For<IAppLogger>());

                nexusWindow = new NexusWindow(vm);
                nexusWindow.Show();
                Dispatcher.UIThread.RunJobs();

                Assert.Same(vm, nexusWindow.DataContext);
                Assert.Single(nexusWindow.Accounts);
                Assert.Equal("NexusSmokeAcc", nexusWindow.Accounts[0].Username);

                var badge = nexusWindow.FindControl<TextBlock>("TxtServerStatusBadge");
                var btnToggle = nexusWindow.FindControl<Button>("BtnToggleServer");
                var txtScript = nexusWindow.FindControl<TextBox>("TxtScriptInput");
                Assert.NotNull(badge);
                Assert.NotNull(btnToggle);
                Assert.NotNull(txtScript);

                Assert.Equal(Color.Parse("#94A3B8"), Assert.IsAssignableFrom<ISolidColorBrush>(badge.Foreground).Color);
                Assert.Equal(Color.Parse("#0284C7"), Assert.IsAssignableFrom<ISolidColorBrush>(btnToggle.Background).Color);

                string enScriptWatermark = LocalizationService.Get("Nexus_ScriptWatermark");
                Assert.Equal(enScriptWatermark, txtScript.Watermark);

                vm.ToggleServerCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();

                Assert.True(vm.IsServerRunning);
                Assert.Equal(Color.Parse("#38BDF8"), Assert.IsAssignableFrom<ISolidColorBrush>(badge.Foreground).Color);
                Assert.Equal(Color.Parse("#DC2626"), Assert.IsAssignableFrom<ISolidColorBrush>(btnToggle.Background).Color);

                vm.ToggleServerCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();

                Assert.False(vm.IsServerRunning);
                Assert.Equal(Color.Parse("#94A3B8"), Assert.IsAssignableFrom<ISolidColorBrush>(badge.Foreground).Color);
                Assert.Equal(Color.Parse("#0284C7"), Assert.IsAssignableFrom<ISolidColorBrush>(btnToggle.Background).Color);

                LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);
                Dispatcher.UIThread.RunJobs();

                string viScriptWatermark = LocalizationService.Get("Nexus_ScriptWatermark");
                Assert.NotEqual(enScriptWatermark, viScriptWatermark);
                Assert.Equal(viScriptWatermark, txtScript.Watermark);

                Assert.True(nexusWindow.IsVisible);
                nexusWindow.Close();
                Dispatcher.UIThread.RunJobs();
                Assert.False(nexusWindow.IsVisible);
                Assert.Empty(sink.Errors);
            }
            finally
            {
                nexusWindow?.Hide();
                foreach (var srv in createdServers)
                {
                    srv.Dispose();
                }

                if (acExisted && prevAcContent != null)
                {
                    File.WriteAllText(acFilePath, prevAcContent);
                }
                else if (!acExisted && File.Exists(acFilePath))
                {
                    File.Delete(acFilePath);
                }

                NexusHost.Current = prevNexusHost;
                LocalizationService.SetLanguage(prevLang, saveSetting: false);
                Avalonia.Application.Current?.Resources.MergedDictionaries.Clear();
                Logger.Sink = prevSink;
            }
        }
    }
}
