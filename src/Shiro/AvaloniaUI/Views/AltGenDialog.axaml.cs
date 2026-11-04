#nullable enable
using System;
using Avalonia.Controls;
using Shiro.Services;
using Shiro.ViewModels.Dialogs;

namespace Shiro.AvaloniaUI.Views
{
    public partial class AltGenDialog : Window
    {
        private readonly AltGenViewModel _vm;

        public AltGenDialog() : this(new AltGenViewModel())
        {
        }

        public AltGenDialog(AltGenViewModel viewModel)
        {
            InitializeComponent();
            WindowBackdropService.ApplyDarkAndBackdrop(this);

            _vm = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            DataContext = _vm;
            _vm.CloseRequested += OnCloseRequested;
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
