#nullable enable
using System;
using System.IO;
using Avalonia.Controls;
using Shiro.Services;
using Shiro.ViewModels.Dialogs;

namespace Shiro.AvaloniaUI.Views
{
    public partial class UpdaterWindow : Window
    {
        private readonly UpdaterViewModel _vm;

        public UpdaterWindow() : this(new UpdaterViewModel())
        {
        }

        public UpdaterWindow(UpdaterViewModel viewModel)
        {
            InitializeComponent();
            _vm = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            DataContext = _vm;
            _vm.CloseRequested += OnCloseRequested;
            Loaded += (_, _) => _vm.StartCommand.Execute(null);
            Closing += OnWindowClosing;
        }

        internal static bool IsSafeArchiveEntryPath(string destinationRoot, string entryFullName, out string resolvedDestinationPath) =>
            UpdateArchiveInstaller.IsSafeArchiveEntryPath(destinationRoot, entryFullName, out resolvedDestinationPath);

        internal static void ApplyLocalUpdateArchive(string archivePath, string stagingDir, string targetBaseDir)
        {
            string currentUpdaterExe = Path.GetFileName(Environment.ProcessPath ?? "Auto Update.exe");
            UpdateArchiveInstaller.Apply(archivePath, stagingDir, targetBaseDir, currentUpdaterExe);
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
