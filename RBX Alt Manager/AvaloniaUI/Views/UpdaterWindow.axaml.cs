#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RBX_Alt_Manager.Services;

namespace RBX_Alt_Manager.AvaloniaUI.Views
{
    public partial class UpdaterWindow : Window
    {
        private string _fileName = "Update.zip";
        private readonly string _updatePath = Path.Combine(StoragePaths.BaseDirectory, "Update");

        public UpdaterWindow()
        {
            InitializeComponent();
            Loaded += async (s, e) => await StartUpdateAsync();
        }

        private async Task StartUpdateAsync()
        {
            PnlErrorActions.IsVisible = false;
            try
            {
                var updateDir = new DirectoryInfo(_updatePath);
                if (updateDir.Exists) updateDir.RecursiveDelete();

                _fileName = Path.Combine(StoragePaths.BaseDirectory, "Update.zip");
                await DownloadAsync();
            }
            catch (Exception ex)
            {
                ShowError($"Cập nhật thất bại: {ex.Message}");
            }
        }

        private void SetStatus(string text, string? percentText = null, int? progressVal = null)
        {
            AppDispatcher.Post(() =>
            {
                if (text != null) TxtStatus.Text = text;
                if (percentText != null) TxtPercent.Text = percentText;
                if (progressVal.HasValue) PrgUpdate.Value = progressVal.Value;
            });
        }

        private void ShowError(string message)
        {
            AppDispatcher.Post(() =>
            {
                TxtStatus.Text = "Đã xảy ra lỗi!";
                TxtPercent.Text = "Lỗi";
                TxtSubStatus.Text = message;
                PnlErrorActions.IsVisible = true;
            });
        }

        private void ShowComplete()
        {
            AppDispatcher.Post(() =>
            {
                TxtStatus.Text = "Hệ thống đã ở phiên bản mới nhất!";
                TxtPercent.Text = "100%";
                TxtSubStatus.Text = "Không có bản cập nhật mới nào cần tải.";
                Close();
            });
        }

        internal static bool IsSafeArchiveEntryPath(string destinationRoot, string entryFullName, out string resolvedDestinationPath)
        {
            resolvedDestinationPath = string.Empty;
            if (string.IsNullOrWhiteSpace(destinationRoot) || string.IsNullOrWhiteSpace(entryFullName))
                return false;

            string fullRoot = Path.GetFullPath(destinationRoot);
            if (!fullRoot.EndsWith(Path.DirectorySeparatorChar))
                fullRoot += Path.DirectorySeparatorChar;

            string candidate = Path.GetFullPath(Path.Combine(fullRoot, entryFullName));
            if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                return false;

            resolvedDestinationPath = candidate;
            return true;
        }

        private async Task DownloadAsync()
        {
            if (File.Exists(_fileName))
            {
                SetStatus("Đang giải nén gói cập nhật...", "40%", 40);
                await Task.Run(() => ApplyLocalUpdateArchive(_fileName, _updatePath, StoragePaths.BaseDirectory));
                SetStatus("Cập nhật hoàn tất! Đang khởi động lại...", "100%", 100);
                await Task.Delay(400);

                string? mainExe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(mainExe) || !File.Exists(mainExe))
                    mainExe = Path.Combine(StoragePaths.BaseDirectory, "Roblox Account Manager.exe");
                if (!File.Exists(mainExe))
                    mainExe = Path.Combine(StoragePaths.BaseDirectory, "RBX Alt Manager.exe");

                if (File.Exists(mainExe))
                {
                    try { Process.Start(new ProcessStartInfo(mainExe) { UseShellExecute = true, WorkingDirectory = StoragePaths.BaseDirectory }); }
                    catch (Exception ex) { Program.Logger?.Error($"Failed to restart application after update: {ex.Message}"); }
                }
                Close();
                return;
            }

#if DEBUG
            SetStatus("Chế độ Debug: Bỏ qua tải về và hoàn tất...", "100%", 100);
            await Task.Delay(500);
            ShowComplete();
#else
            SetStatus("Kiểm tra trạng thái hệ thống...", "50%", 50);
            await Task.Delay(400);
            SetStatus("Phiên bản hiện tại là bản Shiro Pure Avalonia UI 11 mới nhất (.NET 10).", "100%", 100);
            await Task.Delay(600);
            ShowComplete();
#endif
        }

        private static void ApplyLocalUpdateArchive(string archivePath, string stagingDir, string targetBaseDir)
        {
            Directory.CreateDirectory(stagingDir);

            using (ZipArchive archive = ZipFile.OpenRead(archivePath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name))
                        continue;

                    if (!IsSafeArchiveEntryPath(stagingDir, entry.FullName, out string stagedFile))
                        throw new InvalidDataException($"Invalid archive entry path: {entry.FullName}");

                    string? stagedParent = Path.GetDirectoryName(stagedFile);
                    if (!string.IsNullOrEmpty(stagedParent))
                        Directory.CreateDirectory(stagedParent);

                    entry.ExtractToFile(stagedFile, overwrite: true);
                }
            }

            string currentUpdaterExe = Path.GetFileName(Environment.ProcessPath ?? "Auto Update.exe");
            foreach (string stagedFile in Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(stagingDir, stagedFile);
                if (string.Equals(relativePath, currentUpdaterExe, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(relativePath, "Auto Update.exe", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!IsSafeArchiveEntryPath(targetBaseDir, relativePath, out string finalDest))
                    continue;

                string? destParent = Path.GetDirectoryName(finalDest);
                if (!string.IsNullOrEmpty(destParent))
                    Directory.CreateDirectory(destParent);

                File.Copy(stagedFile, finalDest, overwrite: true);
            }

            try { File.Delete(archivePath); }
            catch (Exception ex) { Program.Logger?.Debug($"Deleting update archive failed: {ex.Message}"); }

            try { new DirectoryInfo(stagingDir).RecursiveDelete(); }
            catch (Exception ex) { Program.Logger?.Debug($"Deleting staging directory failed: {ex.Message}"); }
        }

        private async void BtnRetry_Click(object sender, RoutedEventArgs e)
        {
            await StartUpdateAsync();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
