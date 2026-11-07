#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Shiro.Classes;
using Shiro.Services;

namespace Shiro.ViewModels.Dialogs;

public sealed class UpdaterViewModel : DialogViewModelBase
{
    private const string ReleaseUrl = "https://api.github.com/repos/X-Shiro-X/Shiro-Account-Manager/releases/latest";

    private readonly HttpClient _http;
    private readonly Func<Version> _currentVersion;
    private readonly string _baseDirectory;
    private readonly Action<string> _restart;
    private readonly IAppLogger _logger;

    private string _percentText = "0%";
    private int _progress;
    private string _subStatusText = string.Empty;
    private bool _hasError;

    public UpdaterViewModel(
        HttpClient? http = null,
        Func<Version>? currentVersion = null,
        string? baseDirectory = null,
        Action<string>? restart = null,
        IAppLogger? logger = null)
    {
        _http = http ?? SharedHttpClient.Get();
        _currentVersion = currentVersion ?? DefaultCurrentVersion;
        _baseDirectory = baseDirectory ?? StoragePaths.BaseDirectory;
        _restart = restart ?? DefaultRestart;
        _logger = logger ?? AppServices.Logger;

        SetStatus("Đang kiểm tra phiên bản mới nhất...", StatusKind.Info);

        StartCommand = TrackCommand(new AsyncRelayCommand(StartUpdateAsync, () => !IsBusy));
        RetryCommand = TrackCommand(new AsyncRelayCommand(StartUpdateAsync, () => !IsBusy));
        CloseCommand = new RelayCommand(() => RequestClose(null));
    }

    public string PercentText
    {
        get => _percentText;
        private set => SetProperty(ref _percentText, value);
    }

    public int Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public string SubStatusText
    {
        get => _subStatusText;
        private set => SetProperty(ref _subStatusText, value);
    }

    public bool HasError
    {
        get => _hasError;
        private set => SetProperty(ref _hasError, value);
    }

    public IAsyncRelayCommand StartCommand { get; }
    public IAsyncRelayCommand RetryCommand { get; }
    public ICommand CloseCommand { get; }

    private async Task StartUpdateAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        HasError = false;
        SubStatusText = string.Empty;
        CancellationToken ct = BeginOperation();

        try
        {
            string updatePath = Path.Combine(_baseDirectory, "Update");
            var updateDir = new DirectoryInfo(updatePath);
            if (updateDir.Exists)
                updateDir.RecursiveDelete();

            string archivePath = Path.Combine(_baseDirectory, "Update.zip");
            if (File.Exists(archivePath))
            {
                SafeExecuteHelper.Try(() => File.Delete(archivePath), "Updater.CleanStaleArchive");
            }

            await CheckAndDownloadReleaseAsync(archivePath, updatePath, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Cancelled
        }
        catch (Exception ex)
        {
            ShowError(LocalizationService.Format("Updater.UpdateFailed", ex.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CheckAndDownloadReleaseAsync(string archivePath, string updatePath, CancellationToken ct)
    {
        UpdateProgress(LocalizationService.Get("Updater.CheckingGitHub"), "20%", 20);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ReleaseUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", "Shiro-Account-Manager");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(true);

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                ShowComplete(LocalizationService.Get("Updater.CurrentIsLatest"));
                return;
            }

            if (!resp.IsSuccessStatusCode)
            {
                string errMsg = $"{LocalizationService.Get("Updater.CannotConnectToServer")} ({(int)resp.StatusCode})";
                ShowError(errMsg);
                return;
            }

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(true);
            ReleaseInfo? release = ReleaseInfoParser.TryGetNewer(json, _currentVersion());
            if (release == null)
            {
                ShowComplete(LocalizationService.Get("Updater.AlreadyLatestVersion"));
                return;
            }

            UpdateProgress(LocalizationService.Format("Updater.DownloadingVersion", release.Tag), "30%", 30);
            using var dlReq = new HttpRequestMessage(HttpMethod.Get, release.ZipUrl);
            dlReq.Headers.TryAddWithoutValidation("User-Agent", "Shiro-Account-Manager");
            using var dlResp = await _http.SendAsync(dlReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(true);
            dlResp.EnsureSuccessStatusCode();

            await using (var fileStream = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None))
            await using (var netStream = await dlResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(true))
            {
                byte[] buffer = new byte[81920];
                long totalRead = 0;
                int bytesRead;
                while ((bytesRead = await netStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(true)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(true);
                    totalRead += bytesRead;
                    if (release.Size > 0)
                    {
                        int pct = UpdateArchiveInstaller.ComputeDownloadPercent(totalRead, release.Size);
                        UpdateProgress(LocalizationService.Format("Updater.DownloadingSize", totalRead / 1048576), $"{pct}%", pct);
                    }
                }
            }

            string? expectedHash = release.ExpectedSha256;
            if (string.IsNullOrWhiteSpace(expectedHash) && !string.IsNullOrWhiteSpace(release.ChecksumsUrl))
            {
                using var sumsReq = new HttpRequestMessage(HttpMethod.Get, release.ChecksumsUrl);
                sumsReq.Headers.TryAddWithoutValidation("User-Agent", "Shiro-Account-Manager");
                using var sumsResp = await _http.SendAsync(sumsReq, ct).ConfigureAwait(true);
                if (sumsResp.IsSuccessStatusCode)
                {
                    string sumsBody = await sumsResp.Content.ReadAsStringAsync(ct).ConfigureAwait(true);
                    expectedHash = UpdateArchiveInstaller.TryParseSha256Sums(sumsBody, release.AssetName);
                }
            }

            if (string.IsNullOrWhiteSpace(expectedHash) || !UpdateArchiveInstaller.VerifyFileSha256(archivePath, expectedHash))
            {
                SafeExecuteHelper.Try(() => File.Delete(archivePath), "Updater.DeleteUnverifiedArchive");
                _logger.Warn($"Updater rejected archive '{release.AssetName}': SHA-256 integrity verification failed.");
                ShowError("SHA-256 integrity verification failed.");
                return;
            }

            await ApplyAndRestartAsync(archivePath, updatePath, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Update check failed: {ex.Message}");
            ShowError($"{LocalizationService.Get("Updater.CannotConnectToServer")} ({ex.Message})");
        }
    }

    private async Task ApplyAndRestartAsync(string archivePath, string updatePath, CancellationToken ct)
    {
        UpdateProgress(LocalizationService.Get("Updater.ExtractingPackage"), "75%", 75);
        string currentExeName = Path.GetFileName(Environment.ProcessPath ?? "Auto Update.exe");
        await Task.Run(() => UpdateArchiveInstaller.Apply(archivePath, updatePath, _baseDirectory, currentExeName), ct).ConfigureAwait(true);

        UpdateProgress(LocalizationService.Get("Updater.CompletedRestarting"), "100%", 100);

        string? mainExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(mainExe) || !File.Exists(mainExe))
            mainExe = Path.Combine(_baseDirectory, "Shiro.exe");
        if (!File.Exists(mainExe))
            mainExe = Path.Combine(_baseDirectory, "Roblox Account Manager.exe");

        if (File.Exists(mainExe))
        {
            try
            {
                _restart(mainExe);
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to restart application after update: {ex.Message}");
            }
        }

        RequestClose(true);
    }

    private void UpdateProgress(string status, string percentText, int progress)
    {
        SetStatus(status, StatusKind.Info);
        PercentText = percentText;
        Progress = progress;
    }

    private void ShowError(string message)
    {
        SetStatus(LocalizationService.Get("Updater.ErrorOccurredTitle"), StatusKind.Error);
        PercentText = LocalizationService.Get("Status_Error");
        SubStatusText = message;
        HasError = true;
    }

    private void ShowComplete(string status)
    {
        SetStatus(status, StatusKind.Success);
        PercentText = "100%";
        Progress = 100;
        SubStatusText = LocalizationService.Get("Updater.NoNewUpdates");
        RequestClose(true);
    }

    private static Version DefaultCurrentVersion() =>
        typeof(UpdaterViewModel).Assembly.GetName().Version ?? new Version(4, 2, 0);

    private void DefaultRestart(string exePath)
    {
        Process.Start(new ProcessStartInfo(exePath)
        {
            UseShellExecute = true,
            WorkingDirectory = _baseDirectory
        });
    }
}
