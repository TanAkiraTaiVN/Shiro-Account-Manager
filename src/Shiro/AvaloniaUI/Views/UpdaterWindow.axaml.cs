#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Shiro.Services;

namespace Shiro.AvaloniaUI.Views
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
                ShowError(Shiro.Services.LocalizationService.Format("Updater.UpdateFailed", ex.Message));
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
                TxtStatus.Text = Shiro.Services.LocalizationService.Get("Updater.ErrorOccurredTitle");
                TxtPercent.Text = Shiro.Services.LocalizationService.Get("Status_Error");
                TxtSubStatus.Text = message;
                PnlErrorActions.IsVisible = true;
            });
        }

        private void ShowComplete()
        {
            AppDispatcher.Post(() =>
            {
                TxtStatus.Text = Shiro.Services.LocalizationService.Get("Updater.AlreadyLatestVersion");
                TxtPercent.Text = "100%";
                TxtSubStatus.Text = Shiro.Services.LocalizationService.Get("Updater.NoNewUpdates");
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
                SetStatus(Shiro.Services.LocalizationService.Get("Updater.ExtractingPackage"), "40%", 40);
                await Task.Run(() => ApplyLocalUpdateArchive(_fileName, _updatePath, StoragePaths.BaseDirectory));
                SetStatus(Shiro.Services.LocalizationService.Get("Updater.CompletedRestarting"), "100%", 100);
                await Task.Delay(400);

                string? mainExe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(mainExe) || !File.Exists(mainExe))
                    mainExe = Path.Combine(StoragePaths.BaseDirectory, "Shiro.exe");
                if (!File.Exists(mainExe))
                    mainExe = Path.Combine(StoragePaths.BaseDirectory, "Roblox Account Manager.exe");

                if (File.Exists(mainExe))
                {
                    try { Process.Start(new ProcessStartInfo(mainExe) { UseShellExecute = true, WorkingDirectory = StoragePaths.BaseDirectory }); }
                    catch (Exception ex) { Program.Logger?.Error($"Failed to restart application after update: {ex.Message}"); }
                }
                Close();
                return;
            }

            await CheckAndDownloadReleaseAsync();
        }

        private async Task CheckAndDownloadReleaseAsync()
        {
            SetStatus(Shiro.Services.LocalizationService.Get("Updater.CheckingGitHub"), "20%", 20);
            try
            {
                var client = SharedHttpClient.Get();
                string releaseUrl = "https://api.github.com/repos/X-Shiro-X/Shiro-Account-Manager/releases/latest";
                using var req = new HttpRequestMessage(HttpMethod.Get, releaseUrl);
                req.Headers.TryAddWithoutValidation("User-Agent", "Shiro-Account-Manager");
                using var resp = await client.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    AppDispatcher.Post(() =>
                    {
                        SetStatus(Shiro.Services.LocalizationService.Get("Updater.CurrentIsLatest"), "100%", 100);
                        ShowComplete();
                    });
                    return;
                }

                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string? tagName = root.TryGetProperty("tag_name", out var tagElem) ? tagElem.GetString() : null;
                if (!string.IsNullOrWhiteSpace(tagName))
                {
                    string cleanTag = tagName.TrimStart('v', 'V').Trim();
                    var currentVer = typeof(UpdaterWindow).Assembly.GetName().Version ?? new Version(4, 2, 0);
                    if (Version.TryParse(cleanTag, out var remoteVer) && remoteVer > currentVer)
                    {
                        if (root.TryGetProperty("assets", out var assetsElem) && assetsElem.ValueKind == JsonValueKind.Array)
                        {
                            string? downloadUrl = null;
                            long expectedSize = 0;
                            foreach (var asset in assetsElem.EnumerateArray())
                            {
                                string? name = asset.TryGetProperty("name", out var nElem) ? nElem.GetString() : null;
                                if (!string.IsNullOrEmpty(name) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                                {
                                    downloadUrl = asset.TryGetProperty("browser_download_url", out var dlElem) ? dlElem.GetString() : null;
                                    expectedSize = asset.TryGetProperty("size", out var sElem) && sElem.TryGetInt64(out long sVal) ? sVal : 0;
                                    break;
                                }
                            }

                            if (!string.IsNullOrEmpty(downloadUrl))
                            {
                                SetStatus(Shiro.Services.LocalizationService.Format("Updater.DownloadingVersion", cleanTag), "30%", 30);
                                using var downloadClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                                downloadClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Shiro-Account-Manager");
                                using var getResp = await downloadClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                                getResp.EnsureSuccessStatusCode();

                                await using (var fileStream = new FileStream(_fileName, FileMode.Create, FileAccess.Write, FileShare.None))
                                await using (var netStream = await getResp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                                {
                                    byte[] buffer = new byte[81920];
                                    long totalRead = 0;
                                    int bytesRead;
                                    while ((bytesRead = await netStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                                    {
                                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead)).ConfigureAwait(false);
                                        totalRead += bytesRead;
                                        if (expectedSize > 0)
                                        {
                                            int pct = 30 + (int)((totalRead / (double)expectedSize) * 40.0);
                                            SetStatus(Shiro.Services.LocalizationService.Format("Updater.DownloadingSize", totalRead / 1048576), $"{pct}%", pct);
                                        }
                                    }
                                }

                                SetStatus(Shiro.Services.LocalizationService.Get("Updater.ExtractingPackage"), "75%", 75);
                                await Task.Run(() => ApplyLocalUpdateArchive(_fileName, _updatePath, StoragePaths.BaseDirectory));
                                SetStatus(Shiro.Services.LocalizationService.Get("Updater.CompletedRestarting"), "100%", 100);
                                await Task.Delay(400);

                                string? mainExe = Environment.ProcessPath;
                                if (string.IsNullOrEmpty(mainExe) || !File.Exists(mainExe))
                                    mainExe = Path.Combine(StoragePaths.BaseDirectory, "Shiro.exe");

                                if (File.Exists(mainExe))
                                {
                                    try { Process.Start(new ProcessStartInfo(mainExe) { UseShellExecute = true, WorkingDirectory = StoragePaths.BaseDirectory }); }
                                    catch (Exception ex) { Program.Logger?.Error($"Failed to restart after update: {ex.Message}"); }
                                }
                                AppDispatcher.Post(Close);
                                return;
                            }
                        }
                    }
                }

                AppDispatcher.Post(() =>
                {
                    SetStatus(Shiro.Services.LocalizationService.Get("Updater.AlreadyLatestVersion"), "100%", 100);
                    ShowComplete();
                });
            }
            catch (Exception ex)
            {
                Program.Logger?.Warn($"Update check failed: {ex.Message}");
                AppDispatcher.Post(() =>
                {
                    SetStatus(Shiro.Services.LocalizationService.Get("Updater.CannotConnectToServer"), "100%", 100);
                    ShowComplete();
                });
            }
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
            try
            {
                await StartUpdateAsync();
            }
            catch (Exception ex)
            {
                Program.Logger?.Error($"[UpdaterWindow.BtnRetry] {ex.Message}");
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
