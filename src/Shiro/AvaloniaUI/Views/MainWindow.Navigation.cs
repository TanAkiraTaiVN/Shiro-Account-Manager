#nullable enable
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Shiro.Services;
using Shiro.ViewModels;

namespace Shiro.AvaloniaUI.Views
{
    /// <summary>
    /// Sidebar navigation, Tool menu actions, Roblox Window Arranger triggers,
    /// and Ctrl+K Command Palette keyboard navigation for <see cref="MainWindow"/>.
    /// </summary>
    public partial class MainWindow
    {
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.K)
            {
                if (ViewModel != null)
                {
                    bool next = !ViewModel.IsCommandPaletteOpen;
                    ViewModel.IsCommandPaletteOpen = next;
                    if (next)
                        FocusCommandPaletteInput();
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Escape)
            {
                if (ViewModel != null && ViewModel.IsCommandPaletteOpen)
                {
                    ViewModel.IsCommandPaletteOpen = false;
                    e.Handled = true;
                }
            }
        }

        private void FocusCommandPaletteInput()
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (ViewModel?.IsCommandPaletteOpen == true && TxtCommandPalette != null)
                {
                    TxtCommandPalette.Focus();
                    TxtCommandPalette.SelectAll();
                }
            }, Avalonia.Threading.DispatcherPriority.Render);
        }

        private void OnCommandPaletteKeyDown(object? sender, KeyEventArgs e)
        {
            if (ViewModel == null || !ViewModel.IsCommandPaletteOpen)
                return;

            switch (e.Key)
            {
                case Key.Down:
                    ViewModel.MoveCommandPaletteSelection(1);
                    e.Handled = true;
                    break;
                case Key.Up:
                    ViewModel.MoveCommandPaletteSelection(-1);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    bool joinServer = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ||
                                      e.KeyModifiers.HasFlag(KeyModifiers.Control);
                    if (ViewModel.ExecuteCommandPaletteSelection(joinServer))
                    {
                        e.Handled = true;
                    }
                    break;
                case Key.Escape:
                    ViewModel.IsCommandPaletteOpen = false;
                    e.Handled = true;
                    break;
            }
        }

        private void OnCommandPaletteRowPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.Source is Visual v && (v is Button || v.FindAncestorOfType<Button>() != null))
                return;

            if (sender is Control ctrl && ctrl.DataContext is CommandPaletteItemViewModel item && ViewModel != null)
            {
                int idx = ViewModel.CommandPaletteItems.IndexOf(item);
                if (idx >= 0)
                    ViewModel.SetSelectedCommandPaletteIndex(idx);

                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                {
                    bool joinServer = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ||
                                      e.KeyModifiers.HasFlag(KeyModifiers.Control);
                    ViewModel.ExecuteCommandPaletteSelection(joinServer);
                    e.Handled = true;
                }
            }
        }

        private void SetActiveView(Control? activeView, Button? activeButton)
        {
            if (ViewDashboard != null) ViewDashboard.IsVisible = (activeView == ViewDashboard);
            if (ViewServerList != null) ViewServerList.IsVisible = (activeView == ViewServerList);
            if (ViewSettings != null) ViewSettings.IsVisible = (activeView == ViewSettings);
            if (ViewAbout != null) ViewAbout.IsVisible = (activeView == ViewAbout);

            ResetNavButtons();
            activeButton?.Classes.Add("active");
        }

        private void ResetNavButtons()
        {
            BtnNavDashboard?.Classes.Remove("active");
            BtnNavServerList?.Classes.Remove("active");
            BtnNavRecentGames?.Classes.Remove("active");
            BtnNavTools?.Classes.Remove("active");
            BtnNavSettings?.Classes.Remove("active");
            BtnNavAbout?.Classes.Remove("active");
        }

        private void OnNavDashboardClicked(object sender, RoutedEventArgs e)
            => SetActiveView(ViewDashboard, BtnNavDashboard);

        private void OnNavServerListClicked(object sender, RoutedEventArgs e)
        {
            if (ViewServerList != null && ViewDashboard?.ViewModel?.ServerListViewModel is { } slvm)
            {
                ViewServerList.DataContext = slvm;
                slvm.SelectedSubTabIndex = 0;
                slvm.EnsureServersLoadedForCurrentPlaceId();
            }
            SetActiveView(ViewServerList, BtnNavServerList);
        }

        private void OnNavRecentGamesClicked(object sender, RoutedEventArgs e)
        {
            ViewDashboard?.ViewModel?.ReloadRecentGames();
            if (ViewServerList != null && ViewDashboard?.ViewModel?.ServerListViewModel is { } slvm)
            {
                ViewServerList.DataContext = slvm;
                slvm.SelectedSubTabIndex = 2;
                slvm.NotifyRecentGamesChanged();
            }
            SetActiveView(ViewServerList, BtnNavRecentGames);
        }

        private void OnNavSettingsClicked(object sender, RoutedEventArgs e)
            => SetActiveView(ViewSettings, BtnNavSettings);

        private void OnNavAboutClicked(object sender, RoutedEventArgs e)
            => SetActiveView(ViewAbout, BtnNavAbout);

        private void OnSidebarCloseRobloxClicked(object sender, RoutedEventArgs e)
        {
            var selected = ViewDashboard?.GetSelectedAccounts();
            if (selected != null && selected.Count > 0)
            {
                if (selected.Count > 1)
                {
                    bool confirm = AppServices.Dialogs.ShowConfirmation(
                        Shiro.Services.LocalizationService.Format("MainWindow.ConfirmCloseRobloxSelectedPrompt", selected.Count),
                        Shiro.Services.LocalizationService.Get("MainWindow.ConfirmCloseRobloxBatchTitle"));
                    if (!confirm) return;
                }

                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    int closed = RobloxProcessService.CloseAccountRobloxProcesses(selected);
                    AppDispatcher.Post(() =>
                    {
                        if (closed > 0)
                            ToastService.ShowSuccess(Shiro.Services.LocalizationService.Format("MainWindow.ClosedRobloxForSelected", closed));
                        else
                            ToastService.ShowInfo(Shiro.Services.LocalizationService.Get("MainWindow.NoRobloxRunningForSelected"));
                        ViewModel?.RefreshLiveProcessStatus();
                    });
                });
            }
            else
            {
                bool confirm = AppServices.Dialogs.ShowConfirmation(
                    Shiro.Services.LocalizationService.Get("MainWindow.ConfirmKillAllRobloxPrompt"),
                    Shiro.Services.LocalizationService.Get("MainWindow.ConfirmKillAllRobloxTitle"));
                if (confirm)
                {
                    ViewModel?.KillAllClientsCommand.Execute(null);
                }
            }
        }

        private void OnToolAntiAfkClicked(object sender, RoutedEventArgs e)
            => ViewModel?.ToggleAntiAfkCommand.Execute(null);

        private void OnToolPotatoClicked(object sender, RoutedEventArgs e)
            => ViewModel?.TogglePotatoModeCommand.Execute(null);

        private void OnToolZeroGpuClicked(object sender, RoutedEventArgs e)
            => ViewModel?.ToggleWindowsCommand.Execute(null);

        private void OnToolOptimizeRamClicked(object sender, RoutedEventArgs e)
            => ViewModel?.AntiLagBoosterCommand.Execute(null);

        private void OnToolMuteAllClicked(object sender, RoutedEventArgs e)
        {
            bool wasUnmuted = RobloxAudioService.ToggleMuteAll();
            string msg = wasUnmuted
                ? Shiro.Services.LocalizationService.Get("MainWindow.MutedAllRobloxClients")
                : Shiro.Services.LocalizationService.Get("MainWindow.UnmutedAllRobloxClients");
            ToastService.ShowSuccess(msg);
        }

        private void OnToolSmartMuteClicked(object sender, RoutedEventArgs e)
            => ViewModel?.ToggleSmartMuteCommand.Execute(null);

        private void OnArrangeAutoClicked(object sender, RoutedEventArgs e)
            => ViewModel?.ArrangeRobloxWindowsCommand.Execute("Auto");

        private void OnArrange2x2Clicked(object sender, RoutedEventArgs e)
            => ViewModel?.ArrangeRobloxWindowsCommand.Execute("2x2");

        private void OnArrange3x2Clicked(object sender, RoutedEventArgs e)
            => ViewModel?.ArrangeRobloxWindowsCommand.Execute("3x2");

        private void OnArrange4x3Clicked(object sender, RoutedEventArgs e)
            => ViewModel?.ArrangeRobloxWindowsCommand.Execute("4x3");

        private void OnArrangeCascadeClicked(object sender, RoutedEventArgs e)
            => ViewModel?.ArrangeRobloxWindowsCommand.Execute("Cascade");

        private void OnToolNexusClicked(object sender, RoutedEventArgs e)
            => AppServices.Dialogs.OpenNexusWindow();

        private void OnToolProxyClicked(object sender, RoutedEventArgs e)
            => AppServices.Dialogs.OpenProxyManagerDialog();

        private void OnToolCleanTrayClicked(object sender, RoutedEventArgs e)
            => ViewModel?.CleanSystemTrayCommand.Execute(null);

        private void OnToolStreamerModeClicked(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
                ViewModel.IsStreamerModeEnabled = !ViewModel.IsStreamerModeEnabled;
            else
                StreamerModeService.IsEnabled = !StreamerModeService.IsEnabled;

            ToastService.ShowInfo("Streamer Mode", StreamerModeService.IsActive
                ? Shiro.Services.LocalizationService.Get("MainWindow.StreamerModeEnabled")
                : Shiro.Services.LocalizationService.Get("MainWindow.StreamerModeDisabled"));
        }

        private void OnToolAltGenClicked(object sender, RoutedEventArgs e)
            => AppServices.Dialogs.OpenAltGenDialog();

        private void OnCommandPaletteBarPressed(object sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            if (ViewModel != null)
            {
                ViewModel.IsCommandPaletteOpen = true;
                FocusCommandPaletteInput();
            }
        }

        private void OnCommandPaletteBarClicked(object? sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.IsCommandPaletteOpen = true;
                FocusCommandPaletteInput();
            }
        }

        private void OnCloseCommandPaletteClicked(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
                ViewModel.IsCommandPaletteOpen = false;
        }

        private void OnCommandPaletteBackdropPressed(object sender, PointerPressedEventArgs e)
        {
            if (ReferenceEquals(e.Source, sender) && ViewModel != null)
            {
                ViewModel.IsCommandPaletteOpen = false;
            }
        }
    }
}
