#nullable enable
using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Shiro.Services;

namespace Shiro.AvaloniaUI
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                LocalizationService.Initialize(this);
                LocalizationService.SetLanguage(LocalizationService.CurrentLanguage, false);
                AccentThemeService.Initialize();

                desktop.MainWindow = new Views.MainWindow();
                ForegroundWindowService.Start();
                UpdateCheckService.StartBackgroundCheck();
                desktop.Exit += (s, e) =>
                {
                    Program.Closed = true;
                    ForegroundWindowService.Stop();
                    SafeExecuteHelper.Try(() => AccountRuntimeStateStore.Flush(), "App.Exit.FlushRuntimeState");
                    try
                    {
                        if (desktop.MainWindow is Views.MainWindow mw && mw.ViewModel != null && !string.IsNullOrWhiteSpace(mw.ViewModel.PlaceId))
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
                    try { RobloxWindowService.ShowAllWindows(); }
                    catch (Exception ex) { Program.Logger?.Debug($"ShowAllWindows on exit threw: {ex.Message}"); }
                };

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
    }
}
