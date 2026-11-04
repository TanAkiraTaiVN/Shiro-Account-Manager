#nullable enable
using Avalonia.Controls;
using Shiro.Services;
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
            SettingsSideEffectsService.AccountViewModelsProvider ??= () => MainWindow.Instance?.ViewModel?.Accounts;
            ViewModel = viewModel;
            DataContext = ViewModel;
            InitializeComponent();
            Loaded += (_, _) => ViewModel.Load();
            Unloaded += (_, _) => ViewModel.OnClosing();
        }
    }
}
