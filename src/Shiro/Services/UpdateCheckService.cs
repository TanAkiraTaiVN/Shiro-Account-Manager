#nullable enable
using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    /// <summary>
    /// Lightweight non-blocking background update detector.
    /// Checks GitHub latest release once on startup when CheckForUpdates is enabled.
    /// </summary>
    public static class UpdateCheckService
    {
        private const string ReleaseApiUrl = "https://api.github.com/repos/X-Shiro-X/Shiro-Account-Manager/releases/latest";
        private static int _checkInitiated = 0;

        public static void StartBackgroundCheck()
        {
            if (Interlocked.Exchange(ref _checkInitiated, 1) == 1)
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    // Delay 3 seconds after app launch to allow UI and initial account load to finish smoothly
                    await Task.Delay(3000).ConfigureAwait(false);
                    await CheckForUpdatesInternalAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"[UpdateCheckService] Background update check error: {ex.Message}");
                }
            });
        }

        public static async Task<bool> CheckForUpdatesInternalAsync(CancellationToken cancellationToken)
        {
            bool checkEnabled = AppSettings.General == null ||
                                !AppSettings.General.Exists(AppSettingsKeys.General.CheckForUpdates) ||
                                AppSettings.General.Get<bool>(AppSettingsKeys.General.CheckForUpdates, true);

            if (!checkEnabled)
                return false;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseApiUrl);
            request.Headers.Add("User-Agent", "Shiro-Account-Manager");
            request.Headers.Add("Accept", "application/vnd.github.v3+json");

            var response = await SharedHttpClient.Get().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return false;

            string json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("tag_name", out var tagElem))
                return false;

            string? tagName = tagElem.GetString();
            if (string.IsNullOrWhiteSpace(tagName))
                return false;

            var currentVer = typeof(UpdateCheckService).Assembly.GetName().Version ?? new Version(4, 2, 0);
            if (IsRemoteVersionNewer(currentVer.ToString(), tagName))
            {
                AppDispatcher.Post(() =>
                {
                    ToastService.ShowInfo(
                        "Bản cập nhật mới",
                        $"Đã có phiên bản {tagName}! Khởi động lại với '-update' để nâng cấp.");
                });
                return true;
            }

            return false;
        }

        public static bool IsRemoteVersionNewer(string currentVerStr, string remoteTagStr)
        {
            if (string.IsNullOrWhiteSpace(currentVerStr) || string.IsNullOrWhiteSpace(remoteTagStr))
                return false;

            string cleanedRemote = remoteTagStr.TrimStart('v', 'V').Trim();
            string cleanedCurrent = currentVerStr.TrimStart('v', 'V').Trim();

            if (!TryParseNormalizedVersion(cleanedCurrent, out var currentVer) ||
                !TryParseNormalizedVersion(cleanedRemote, out var remoteVer))
            {
                return false;
            }

            return remoteVer > currentVer;
        }

        private static bool TryParseNormalizedVersion(string input, out Version version)
        {
            string[] parts = input.Split('.');
            if (parts.Length == 1 && int.TryParse(parts[0], out int major))
            {
                version = new Version(major, 0);
                return true;
            }
            if (parts.Length == 2 && int.TryParse(parts[0], out major) && int.TryParse(parts[1], out int minor))
            {
                version = new Version(major, minor);
                return true;
            }
            if (parts.Length == 3 && int.TryParse(parts[0], out major) && int.TryParse(parts[1], out minor) && int.TryParse(parts[2], out int build))
            {
                version = new Version(major, minor, build);
                return true;
            }

            return Version.TryParse(input, out version!);
        }
    }
}
