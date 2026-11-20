#nullable enable
using System;
using Avalonia.Controls;
using Shiro.Services;
using Shiro.ViewModels.Dialogs;

namespace Shiro.AvaloniaUI.Views
{
    public partial class MissingAssetsDialog : Window
    {
        private readonly MissingAssetsViewModel _vm;

        public MissingAssetsDialog() : this(new MissingAssetsViewModel())
        {
        }

        public MissingAssetsDialog(Account account, long[] assetIds)
            : this(new MissingAssetsViewModel(
                account ?? throw new ArgumentNullException(nameof(account)),
                assetIds ?? Array.Empty<long>()))
        {
        }

        public MissingAssetsDialog(MissingAssetsViewModel viewModel)
        {
            InitializeComponent();
            WindowBackdropService.ApplyDarkAndBackdrop(this);
            _vm = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            DataContext = _vm;
            _vm.CloseRequested += OnCloseRequested;
            Loaded += (_, _) => _vm.LoadCommand.Execute(null);
            Closing += OnWindowClosing;
        }

        private void OnCloseRequested(object? sender, bool? result)
        {
            Close(result);
        }

        private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
        {
            _vm.CloseRequested -= OnCloseRequested;
            _vm.OnClosing();
        }
    }
}
