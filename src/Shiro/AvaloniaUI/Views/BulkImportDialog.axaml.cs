#nullable enable
using Avalonia.Controls;
using Shiro.Services;
using Shiro.ViewModels.Dialogs;

namespace Shiro.AvaloniaUI.Views
{
    public partial class BulkImportDialog : Window
    {
        private readonly BulkImportViewModel _viewModel;

        public int ImportedCount => _viewModel.ImportedCount;

        public BulkImportDialog() : this(new BulkImportViewModel())
        {
        }

        public BulkImportDialog(BulkImportViewModel viewModel)
        {
            InitializeComponent();
            WindowBackdropService.ApplyDarkAndBackdrop(this);
            _viewModel = viewModel;
            DataContext = _viewModel;
            _viewModel.CloseRequested += (_, result) => Close(result ?? (_viewModel.ImportedCount > 0));
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            _viewModel.OnClosing();
            base.OnClosing(e);
        }

        public static (string? cookie, string? password) ParseAccountLine(string? line)
            => AccountLineParser.ParseAccountLine(line);

        public static string? CleanCookie(string? line)
            => AccountLineParser.CleanCookie(line);
    }
}
