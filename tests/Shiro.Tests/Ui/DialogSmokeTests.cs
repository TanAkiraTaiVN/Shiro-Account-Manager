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

    [Collection("SerialGlobalState")]
    public partial class DialogSmokeTests
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
                    UserID = 0,
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
        public void AboutView_WrapsLongVietnameseText_InsideCardBounds()
        {
            var prevLang = LocalizationService.CurrentLanguage;
            Window? window = null;
            try
            {
                LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);
                var view = new AboutView();
                window = new Window { Width = 620, Height = 520, Content = view };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                Border? cardBorder = view.GetVisualDescendants()
                    .OfType<Border>()
                    .FirstOrDefault(b => b.MaxWidth > 0);
                Assert.NotNull(cardBorder);
                double innerWidth = cardBorder!.Bounds.Width - cardBorder.Padding.Left - cardBorder.Padding.Right;
                Assert.True(innerWidth >= 380, $"Unexpected innerWidth={innerWidth}");

                var textBlocks = view.GetVisualDescendants().OfType<TextBlock>().ToList();
                Assert.True(textBlocks.Count >= 8);
                Assert.Contains(textBlocks, tb => tb.TextWrapping == TextWrapping.Wrap);

                foreach (TextBlock tb in textBlocks)
                {
                    Assert.True(
                        tb.Bounds.Width <= innerWidth + 1.0,
                        $"TextBlock '{tb.Text}' width {tb.Bounds.Width} exceeded inner card width {innerWidth}");
                }
            }
            finally
            {
                window?.Close();
                LocalizationService.SetLanguage(prevLang, saveSetting: false);
                Avalonia.Application.Current?.Resources.MergedDictionaries.Clear();
            }
        }

        [AvaloniaFact]
        public void MainWindow_MaximizeRestoreIcons_ToggleVisibility_WithLightStrokeBrush()
        {
            MainWindow? prevWindow = MainWindow.Instance;
            var bridge = AppServices.HostUiBridge as HostUiBridgeService;
            var prevRefreshAccounts = bridge?.RefreshAccountsOnUiHandler;
            var prevRefreshSingle = bridge?.RefreshSingleAccountOnUiHandler;
            var prevNotifyValidity = bridge?.NotifyAccountValidityOnUiHandler;
            var prevRefreshLive = bridge?.RefreshLiveProcessStatusOnUiHandler;
            var prevUpdateStatus = bridge?.UpdateWindowStatusOnUiHandler;
            var prevNotifyClosed = bridge?.NotifyRobloxProcessesClosedOnUiHandler;
            var prevGetSelected = bridge?.GetDashboardSelectedAccountsHandler;
            var prevGetPlace = bridge?.GetDashboardPlaceIdHandler;
            var prevGetJob = bridge?.GetDashboardJobIdHandler;

            var mw = new MainWindow();
            try
            {
                var iconMax = mw.FindControl<LucideAvalonia.Lucide>("IconMaximize");
                var iconRestore = mw.FindControl<LucideAvalonia.Lucide>("IconRestore");
                Assert.NotNull(iconMax);
                Assert.NotNull(iconRestore);
                Assert.True(iconMax!.IsVisible);
                Assert.False(iconRestore!.IsVisible);

                Color expectedStroke = Color.Parse("#A1A1AA");
                AssertLucideStrokeColor(iconMax, expectedStroke);
                AssertLucideStrokeColor(iconRestore, expectedStroke);

                mw.WindowState = WindowState.Maximized;
                Assert.False(iconMax.IsVisible);
                Assert.True(iconRestore.IsVisible);
                AssertLucideStrokeColor(iconRestore, expectedStroke);

                mw.WindowState = WindowState.Normal;
                Assert.True(iconMax.IsVisible);
                Assert.False(iconRestore.IsVisible);
            }
            finally
            {
                (mw.ViewModel as IDisposable)?.Dispose();
                mw.Close();
                typeof(MainWindow).GetProperty(nameof(MainWindow.Instance))?.SetValue(null, prevWindow);
                if (bridge != null)
                {
                    bridge.RefreshAccountsOnUiHandler = prevRefreshAccounts;
                    bridge.RefreshSingleAccountOnUiHandler = prevRefreshSingle;
                    bridge.NotifyAccountValidityOnUiHandler = prevNotifyValidity;
                    bridge.RefreshLiveProcessStatusOnUiHandler = prevRefreshLive;
                    bridge.UpdateWindowStatusOnUiHandler = prevUpdateStatus;
                    bridge.NotifyRobloxProcessesClosedOnUiHandler = prevNotifyClosed;
                    bridge.GetDashboardSelectedAccountsHandler = prevGetSelected;
                    bridge.GetDashboardPlaceIdHandler = prevGetPlace;
                    bridge.GetDashboardJobIdHandler = prevGetJob;
                }
            }
        }

        private static void AssertLucideStrokeColor(LucideAvalonia.Lucide icon, Color expectedColor)
        {
            var drawingImage = Assert.IsType<DrawingImage>(icon.IconSource);
            var group = Assert.IsType<DrawingGroup>(drawingImage.Drawing);
            var strokes = group.Children.OfType<GeometryDrawing>()
                .Select(g => g.Pen?.Brush as ISolidColorBrush)
                .Where(b => b != null)
                .ToList();
            Assert.NotEmpty(strokes);
            Assert.All(strokes, b => Assert.Equal(expectedColor, b!.Color));
        }
    }
}
