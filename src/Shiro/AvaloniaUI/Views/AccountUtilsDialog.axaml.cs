#nullable enable
using System;
using Avalonia.Controls;
using Shiro.Services;
using Shiro.ViewModels.Dialogs;

namespace Shiro.AvaloniaUI.Views
{
    public partial class AccountUtilsDialog : Window
    {
        private readonly AccountUtilsViewModel _viewModel;

        public AccountUtilsDialog() : this(new Account { Username = "Account" })
        {
        }

        public AccountUtilsDialog(Account account)
            : this(new AccountUtilsViewModel(account ?? throw new ArgumentNullException(nameof(account))))
        {
        }

        public AccountUtilsDialog(AccountUtilsViewModel viewModel)
        {
            InitializeComponent();
            WindowBackdropService.ApplyDarkAndBackdrop(this);
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            DataContext = _viewModel;
            _viewModel.CloseRequested += (_, _) => Close();
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            _viewModel.OnClosing();
            base.OnClosing(e);
        }
    }
}
