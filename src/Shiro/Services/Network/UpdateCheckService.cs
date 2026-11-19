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
        private static IAppLogger _logger = AppServices.Logger;
        public static IAppLogger Logger
        {
            get => _logger;
            set => _logger = value ?? throw new ArgumentNullException(nameof(value));
        }

        private static ISettingsService _settings = AppServices.GeneralSettings;
        public static ISettingsService Settings
        {
            get => _settings;
            set => _settings = value ?? throw new ArgumentNullException(nameof(value));
        }

        private const string ReleaseApiUrl = "https://api.github.com/repos/X-Shiro-X/Shiro-Account-Manager/releases/latest";
        private static int _checkInitiated = 0;
        internal static readonly Version DefaultFallbackVersion = new(5, 0, 0);

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
                    _logger.Debug($"[UpdateCheckService] Background update check error: {ex.Message}");
                }
            });
        }

        public static async Task<bool> CheckForUpdatesInternalAsync(CancellationToken cancellationToken)
        {
            bool checkEnabled = _settings == null ||
                                !_settings.Exists(AppSettingsKeys.General.CheckForUpdates) ||
                                _settings.Get<bool>(AppSettingsKeys.General.CheckForUpdates, true);

            if (!checkEnabled)
                return false;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseApiUrl);
            request.Headers.Add("User-Agent", "Shiro-Account-Manager");
            request.Headers.Add("Accept", "application/vnd.github.v3+json");

            using var response = await SharedHttpClient.Get().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return false;

            string json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("tag_name", out var tagElem))
                return false;

            string? tagName = tagElem.GetString();
            if (string.IsNullOrWhiteSpace(tagName))
                return false;

            var currentVer = typeof(UpdateCheckService).Assembly.GetName().Version ?? DefaultFallbackVersion;
            if (IsRemoteVersionNewer(currentVer.ToString(), tagName))
            {
                AppDispatcher.Post(() =>
                {
                    ToastService.ShowInfo(
                        Shiro.Services.LocalizationService.Get("UpdateCheck.NewUpdateTitle"),
                        Shiro.Services.LocalizationService.Format("UpdateCheck.NewVersionAvailable", tagName));
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

            if (!TryParseNormalizedVersion(cleanedCurrent, out var currentVer, out string? currentPre) ||
                !TryParseNormalizedVersion(cleanedRemote, out var remoteVer, out string? remotePre))
            {
                return false;
            }

            int numericCompare = remoteVer.CompareTo(currentVer);
            if (numericCompare != 0)
                return numericCompare > 0;

            if (string.IsNullOrEmpty(currentPre) != string.IsNullOrEmpty(remotePre))
                return string.IsNullOrEmpty(remotePre);

            if (!string.IsNullOrEmpty(remotePre) && !string.IsNullOrEmpty(currentPre))
                return string.Compare(remotePre, currentPre, StringComparison.OrdinalIgnoreCase) > 0;

            return false;
        }

        private static bool TryParseNormalizedVersion(string input, out Version version, out string? prerelease)
        {
            version = new Version(0, 0, 0, 0);
            prerelease = null;
            if (string.IsNullOrWhiteSpace(input))
                return false;

            string trimmed = input.Trim();
            int plusIdx = trimmed.IndexOf('+');
            if (plusIdx >= 0)
                trimmed = trimmed[..plusIdx];

            int dashIdx = trimmed.IndexOf('-');
            if (dashIdx >= 0)
            {
                prerelease = trimmed[(dashIdx + 1)..];
                trimmed = trimmed[..dashIdx];
            }

            if (!Version.TryParse(trimmed, out var parsed) || parsed == null)
                return false;

            version = new Version(
                parsed.Major,
                parsed.Minor,
                parsed.Build < 0 ? 0 : parsed.Build,
                parsed.Revision < 0 ? 0 : parsed.Revision);
            return true;
        }
    }
}
