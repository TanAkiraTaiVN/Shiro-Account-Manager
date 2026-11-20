#nullable enable
using Avalonia.Controls;
using Shiro.Services;
using Shiro.ViewModels.Dialogs;

namespace Shiro.AvaloniaUI.Views
{
    public partial class ProxyManagerDialog : Window
    {
        private readonly ProxyManagerViewModel _viewModel;

        public ProxyManagerDialog() : this(new ProxyManagerViewModel())
        {
        }

        public ProxyManagerDialog(ProxyManagerViewModel viewModel)
        {
            InitializeComponent();
            WindowBackdropService.ApplyDarkAndBackdrop(this);
            _viewModel = viewModel;
            DataContext = _viewModel;
            _viewModel.CloseRequested += (_, result) => Close(result ?? true);
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            _viewModel.OnClosing();
            base.OnClosing(e);
        }
    }
}
