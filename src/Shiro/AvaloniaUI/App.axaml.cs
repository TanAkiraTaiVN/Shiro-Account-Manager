#nullable enable
using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Shiro.Services;
using Shiro.ViewModels;

namespace Shiro.AvaloniaUI
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AppDispatcher.RegisterUiThread();
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            AsyncRelayCommand.DefaultUnhandledErrorHandler ??= HandleUnhandledAsyncCommandError;

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                AppServices.EnsureInitialized();
                LocalizationService.Initialize(this);
                LocalizationService.SetLanguage(LocalizationService.CurrentLanguage, false);
                AccentThemeService.Initialize();
                StreamerModeService.Initialize();

                desktop.MainWindow = new Views.MainWindow();
                ForegroundWindowService.Start();
                UpdateCheckService.StartBackgroundCheck();
                desktop.Exit += (s, e) => ExecuteDesktopExitCleanup(desktop);

                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(350);
                    try
                    {
                        AppSettings.InitializePostStartup();
                    }
                    catch (Exception ex)
                    {
                        Program.Logger?.Error($"Background post-startup failed: {ex}");
                    }
                });
            }

            base.OnFrameworkInitializationCompleted();
        }

        internal static void ExecuteDesktopExitCleanup(IClassicDesktopStyleApplicationLifetime? desktop)
        {
            Program.Closed = true;
            ForegroundWindowService.Stop();
            SafeExecuteHelper.Try(() => AccountRuntimeStateStore.Flush(), "App.Exit.FlushRuntimeState");
            try
            {
                if (desktop?.MainWindow is Views.MainWindow mw && mw.ViewModel != null && !string.IsNullOrWhiteSpace(mw.ViewModel.PlaceId))
                {
                    string pid = mw.ViewModel.PlaceId.Trim();
                    AppSettings.CurrentPlaceId = pid;
                    AppSettings.General?.Set("SavedPlaceId", pid);
                    AppSettings.Save();
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"Saving PlaceId on exit threw: {ex.Message}");
            }

            SafeExecuteHelper.Try(() => AppServices.HostUiBridge.ShutdownBackend(), "App.Exit.ShutdownBackend");
            SafeExecuteHelper.Try(() => AppServices.CurrentBootstrap?.CancelDeferredSettingsSave(), "App.Exit.CancelDeferredSave");
            try { RobloxWindowService.ShowAllWindows(); }
            catch (Exception ex) { Program.Logger?.Debug($"ShowAllWindows on exit threw: {ex.Message}"); }
        }

        internal static void HandleUnhandledAsyncCommandError(Exception ex)
        {
            if (ex == null) return;
            try
            {
                AppServices.Logger?.Error($"[UI.AsyncCommand] Unhandled error: {ex.GetType().Name}: {ex.Message}", ex);
                string title = LocalizationService.Get("Status_Error", "Error");
                string message = string.IsNullOrWhiteSpace(ex.Message)
                    ? LocalizationService.Get("Status_Error", "Unexpected error")
                    : LocalizationService.Format("Common.ErrorFormat", ex.Message);
                ToastService.ShowError(title, message);
            }
            catch (Exception toastEx)
            {
                AppServices.Logger?.Debug($"[App] HandleUnhandledAsyncCommandError failed: {toastEx.Message}");
            }
        }
    }
}
