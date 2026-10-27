using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using RBX_Alt_Manager.Services;

namespace RBX_Alt_Manager.AvaloniaUI
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

                bool isUpdater = desktop.Args != null && desktop.Args.Any(a => string.Equals(a, "-update", StringComparison.OrdinalIgnoreCase));
                if (isUpdater)
                {
                    desktop.MainWindow = new Views.UpdaterWindow();
                }
                else
                {
                    desktop.MainWindow = new Views.MainWindow();
                    desktop.Exit += (s, e) =>
                    {
                        Program.Closed = true;
                        try
                        {
                            if (desktop.MainWindow is Views.MainWindow mw && mw.ViewModel != null && !string.IsNullOrWhiteSpace(mw.ViewModel.PlaceId))
                            {
                                string pid = mw.ViewModel.PlaceId.Trim();
                                AccountManager.CurrentPlaceId = pid;
                                AccountManager.General?.Set("SavedPlaceId", pid);
                                AccountManager.SaveSettings();
                            }
                        }
                        catch { }
                        try { RobloxWindowService.ShowAllWindows(); } catch { }
                    };

                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        await System.Threading.Tasks.Task.Delay(350);
                        try
                        {
                            AccountManager.Instance?.InitializePostStartup();
                        }
                        catch (Exception ex)
                        {
                            Program.Logger?.Error($"Background post-startup failed: {ex}");
                        }
                    });
                }
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
