#nullable enable
using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Shiro.AvaloniaUI.Views;
using Shiro.Classes;
using Shiro.Services;
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
            Logger.Sink = sink;
            try
            {
                var dlg = new BulkImportDialog();
                dlg.Show();
                Assert.IsType<BulkImportViewModel>(dlg.DataContext);
                Assert.Empty(sink.Errors);
                dlg.Close();
            }
            finally
            {
                Logger.Sink = prevSink;
            }
        }

        [AvaloniaFact]
        public void ProxyManagerDialog_Opens_WithViewModelDataContext()
        {
            var sink = new BindingErrorSink();
            var prevSink = Logger.Sink;
            Logger.Sink = sink;
            try
            {
                var dlg = new ProxyManagerDialog();
                dlg.Show();
                Assert.IsType<ProxyManagerViewModel>(dlg.DataContext);
                Assert.Empty(sink.Errors);
                dlg.Close();
            }
            finally
            {
                Logger.Sink = prevSink;
            }
        }

        [AvaloniaFact]
        public void AccountUtilsDialog_Opens_WithViewModelDataContext()
        {
            var sink = new BindingErrorSink();
            var prevSink = Logger.Sink;
            Logger.Sink = sink;
            try
            {
                var dlg = new AccountUtilsDialog(new Account { Username = "TestSmoke" });
                dlg.Show();
                Assert.IsType<AccountUtilsViewModel>(dlg.DataContext);
                Assert.Empty(sink.Errors);
                dlg.Close();
            }
            finally
            {
                Logger.Sink = prevSink;
            }
        }

        [AvaloniaFact]
        public void AltGenDialog_Opens_WithViewModelDataContext()
        {
            var sink = new BindingErrorSink();
            var prevSink = Logger.Sink;
            Logger.Sink = sink;
            try
            {
                var dlg = new AltGenDialog();
                dlg.Show();
                Assert.IsType<AltGenViewModel>(dlg.DataContext);
                Assert.Empty(sink.Errors);
                dlg.Close();
            }
            finally
            {
                Logger.Sink = prevSink;
            }
        }

        [AvaloniaFact]
        public void MissingAssetsDialog_Opens_WithViewModelDataContext()
        {
            var sink = new BindingErrorSink();
            var prevSink = Logger.Sink;
            Logger.Sink = sink;
            try
            {
                var dlg = new MissingAssetsDialog(new Account { Username = "SmokeUser" }, new long[] { 12345 });
                dlg.Show();
                Assert.IsType<MissingAssetsViewModel>(dlg.DataContext);
                Assert.Empty(sink.Errors);
                dlg.Close();
            }
            finally
            {
                Logger.Sink = prevSink;
            }
        }

        [AvaloniaFact]
        public void UpdaterWindow_Opens_WithViewModelDataContext()
        {
            var sink = new BindingErrorSink();
            var prevSink = Logger.Sink;
            Logger.Sink = sink;
            try
            {
                var w = new UpdaterWindow();
                w.Show();
                Assert.IsType<UpdaterViewModel>(w.DataContext);
                Assert.Empty(sink.Errors);
                w.Close();
            }
            finally
            {
                Logger.Sink = prevSink;
            }
        }

        [AvaloniaFact]
        public void SettingsView_Loads_WithViewModelDataContext()
        {
            var sink = new BindingErrorSink();
            var prevSink = Logger.Sink;
            Logger.Sink = sink;
            try
            {
                var view = new SettingsView();
                var window = new Window { Content = view };
                window.Show();
                Assert.IsType<SettingsViewModel>(view.DataContext);
                Assert.Empty(sink.Errors);
                window.Close();
            }
            finally
            {
                Logger.Sink = prevSink;
            }
        }

        [AvaloniaFact]
        public void BulkImport_TypeAndClickStart_UpdatesStatus()
        {
            var dlg = new BulkImportDialog();
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

            dlg.Close();
        }
    }
}
