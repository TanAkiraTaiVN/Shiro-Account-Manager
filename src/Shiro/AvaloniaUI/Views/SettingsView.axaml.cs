#nullable enable
using Avalonia.Controls;
using Shiro.Services;
using Shiro.ViewModels;
using Shiro.ViewModels.Dialogs;

namespace Shiro.AvaloniaUI.Views
{
    public partial class SettingsView : UserControl
    {
        public SettingsViewModel ViewModel { get; }

        public SettingsView() : this(new SettingsViewModel())
        {
        }

        public SettingsView(SettingsViewModel viewModel)
        {
            SettingsSideEffectsService.NotifyDisplayNamesChangedCallback ??= () =>
            {
                var accountVms = MainWindow.Instance?.ViewModel?.Accounts;
                if (accountVms == null) return;
                foreach (var vm in accountVms)
                    vm?.NotifyDisplayNameChanged();
            };
            SettingsSideEffectsService.ClearAvatarCacheCallback ??= () =>
            {
                AccountViewModel.ClearAvatarMemoryCache();
                var accountVms = MainWindow.Instance?.ViewModel?.Accounts;
                if (accountVms == null) return;
                foreach (var accVm in accountVms)
                    accVm?.InvalidateAvatar();
            };
            ViewModel = viewModel;
            DataContext = ViewModel;
            InitializeComponent();
            bool hasLoadedForCurrentSession = false;
            void EnsureSessionLoaded()
            {
                if (hasLoadedForCurrentSession || !IsVisible)
                    return;
                hasLoadedForCurrentSession = true;
                ViewModel.Load();
            }
            void EndSession()
            {
                if (!hasLoadedForCurrentSession)
                    return;
                hasLoadedForCurrentSession = false;
                ViewModel.OnClosing();
            }

            Loaded += (_, _) => EnsureSessionLoaded();
            Unloaded += (_, _) => EndSession();
            PropertyChanged += (_, e) =>
            {
                if (e.Property == IsVisibleProperty)
                {
                    if (IsVisible)
                        EnsureSessionLoaded();
                    else
                        EndSession();
                }
            };
        }
    }
}
