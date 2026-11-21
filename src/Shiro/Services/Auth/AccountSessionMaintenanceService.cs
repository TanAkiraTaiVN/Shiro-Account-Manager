#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public sealed class AccountSessionMaintenanceService
    {
        public static AccountSessionMaintenanceService Default { get; } = new();

        private static readonly SearchValues<char> InvalidFileNameChars =
            SearchValues.Create(Path.GetInvalidFileNameChars());

        private static readonly SearchValues<char> ComboSeparators =
            SearchValues.Create(":|\t");

        private static readonly HashSet<string> ReservedWindowsDeviceNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        internal async Task RefreshStaleCookiesAsync(
            SemaphoreSlim cookieRefreshGate,
            CancellationToken cancellationToken)
        {
            if (RobloxHbaService.IsInteractiveLoginActive)
                return;

            try
            {
                if (!await cookieRefreshGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                    return;

                try
                {
                    IReadOnlyList<Account> snapshot = AppServices.AccountRepository.GetAll();
                    if (snapshot.Count == 0) return;

                    bool protectCrossNetwork = RobloxHbaService.IsVpnOrTunnelInterfaceActive() ||
                                               RobloxHbaService.IsWithinPostInteractiveLoginGracePeriod(TimeSpan.FromMinutes(5));
                    string localNetSig = protectCrossNetwork ? RobloxHbaService.GetLocalNetworkSignature() : string.Empty;

                    var candidates = snapshot
                        .Where(a => a != null && a.HasSecurityToken)
                        .Where(a => (DateTime.Now - a.LastAttemptedRefresh).TotalHours >= 24 || a.LastAttemptedRefresh == DateTime.MinValue)
                        .Where(a => a.GetField("NoCookieRefresh") != "true")
                        .Where(a => !protectCrossNetwork ||
                                    string.IsNullOrWhiteSpace(a.LastBoundIpSignature) ||
                                    localNetSig is "offline" or "unknown" ||
                                    string.Equals(a.LastBoundIpSignature, RobloxHbaService.GetNetworkSignatureForAccount(a, localNetSig),
                                        StringComparison.Ordinal))
                        .OrderByDescending(a => a.Valid)
                        .ThenBy(a => a.LastAttemptedRefresh)
                        .Take(4)
                        .ToList();

                    if (candidates.Count == 0)
                        return;

                    bool anyChanged = false;
                    foreach (Account account in candidates)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        account.LastAttemptedRefresh = DateTime.Now;

                        try
                        {
                            bool wasValid = account.Valid;
                            bool valid = await RobloxRateLimitCoordinator.Instance.ExecuteWithPacingAsync(
                                RateLimitCategory.SessionCheck,
                                () => account.ValidateSessionAsync(cancellationToken),
                                minIntervalMs: 350,
                                cancellationToken: cancellationToken).ConfigureAwait(false);
                            if (valid != wasValid)
                            {
                                account.Valid = valid;
                                anyChanged = true;
                                AppServices.AccountRegistration.NotifyAccountValidity(account, valid);
                            }
                        }
                        catch (Exception ex)
                        {
                            AppServices.Logger.Warn($"[RefreshStaleCookiesAsync] Error refreshing {account.Username}: {ex.Message}");
                        }
                    }

                    if (anyChanged)
                    {
                        AppServices.AccountPersistence.SaveAccounts(bypassRateLimit: true);
                    }
                }
                finally
                {
                    cookieRefreshGate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                /* canceled */
            }
            catch (Exception exception)
            {
                AppServices.Logger.Warn($"Automatic cookie refresh stopped early: {exception.Message}");
            }
        }

        internal async Task ImportBulkUserPassAsync(
            BrowserService browserService,
            string combos,
            Action<string>? statusCallback,
            CancellationToken lifetimeToken,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(combos) || combos == "/UC")
                return;

            string[] comboList = combos
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);

            var activeSlotTasks = new Task<(bool Success, string Username, string Error, int WindowSlot)>?[2];
            var failedAccounts = new List<(string Username, string Error)>();
            int validCount = 0;

            for (int i = 0; i < comboList.Length; i++)
            {
                if (cancellationToken.IsCancellationRequested || lifetimeToken.IsCancellationRequested)
                {
                    string cancelMsg = LocalizationService.Get("AccountManager.BulkLoginStopped");
                    AppServices.HostUiBridge.NotifyWindowStatus(cancelMsg);
                    statusCallback?.Invoke(cancelMsg);
                    break;
                }

                string combo = comboList[i].Trim();
                if (string.IsNullOrWhiteSpace(combo)) continue;

                var (username, password) = ParseUserPassCombo(combo);
                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                    continue;

                validCount++;
                int windowSlot = activeSlotTasks[0] == null ? 0 : 1;
                activeSlotTasks[windowSlot] = RunBulkUserPassLoginAsync(browserService, username, password, windowSlot, lifetimeToken, cancellationToken);
                string msg = LocalizationService.Format("AccountManager.OpeningBrowser", username, validCount, comboList.Length);
                AppServices.HostUiBridge.NotifyWindowStatus(msg);
                statusCallback?.Invoke(msg);

                if (activeSlotTasks[0] != null && activeSlotTasks[1] != null)
                {
                    var completedTask = await Task.WhenAny(activeSlotTasks[0]!, activeSlotTasks[1]!);
                    var res = await completedTask;
                    activeSlotTasks[res.WindowSlot] = null;
                    if (!res.Success)
                        failedAccounts.Add((res.Username, res.Error));
                }
            }

            var remaining = activeSlotTasks.Where(t => t != null).Select(t => t!).ToArray();
            if (remaining.Length > 0)
            {
                var results = await Task.WhenAll(remaining);
                foreach (var res in results)
                {
                    if (!res.Success)
                        failedAccounts.Add((res.Username, res.Error));
                }
            }

            if (validCount == 0)
            {
                string msg = LocalizationService.Get("AccountManager.NoValidCredentials");
                AppServices.HostUiBridge.NotifyWindowStatus(msg);
                statusCallback?.Invoke(msg);
                return;
            }

            if (cancellationToken.IsCancellationRequested)
                return;

            if (failedAccounts.Count > 0)
            {
                string failedNames = string.Join(", ", failedAccounts.Select(f => f.Username));
                string msg = LocalizationService.Format("AccountManager.BulkLoginSummary", validCount - failedAccounts.Count, validCount, failedNames);
                AppServices.HostUiBridge.NotifyWindowStatus(msg);
                statusCallback?.Invoke(msg);
                AppServices.Logger.Warn($"Bulk sign-in completed with {failedAccounts.Count} failures: {failedNames}");
            }
            else
            {
                string msg = LocalizationService.Format("AccountManager.BulkLoginCompleted", validCount);
                AppServices.HostUiBridge.NotifyWindowStatus(msg);
                statusCallback?.Invoke(msg);
            }
        }

        private async Task<(bool Success, string Username, string Error, int WindowSlot)> RunBulkUserPassLoginAsync(
            BrowserService browserService,
            string username,
            string password,
            int windowSlot,
            CancellationToken lifetimeToken,
            CancellationToken cancellationToken = default)
        {
            try
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken, cancellationToken);
                Account? signedIn = await browserService.SignInAsync(
                    username,
                    password,
                    windowSlot,
                    submitCredentials: true,
                    cancellationToken: linkedCts.Token);
                if (signedIn == null || !signedIn.Valid)
                    return (false, username, LocalizationService.Get("AccountManager.CancelledByUser"), windowSlot);
                return (true, username, string.Empty, windowSlot);
            }
            catch (OperationCanceledException)
            {
                return (false, username, LocalizationService.Get("AccountManager.CancelledByUser"), windowSlot);
            }
            catch (Exception exception)
            {
                AppServices.Logger.Error($"Bulk user:pass login failed for {username}: {exception.Message}");
                return (false, username, exception.Message, windowSlot);
            }
        }

        internal static (string Username, string Password) ParseUserPassCombo(string combo)
        {
            if (string.IsNullOrWhiteSpace(combo))
                return (string.Empty, string.Empty);

            int separator = combo.AsSpan().IndexOfAny(ComboSeparators);
            if (separator <= 0 || separator >= combo.Length - 1)
                return (string.Empty, string.Empty);

            return (combo.Substring(0, separator).Trim(), combo.Substring(separator + 1).Trim());
        }

        internal static string MakeSafeFileName(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;

            ReadOnlySpan<char> trimmed = value.AsSpan().Trim();
            if (trimmed.IndexOfAny(InvalidFileNameChars) < 0)
                return FinalizeSafeFileName(trimmed, value, fallback);

            Span<char> buffer = trimmed.Length <= 256
                ? stackalloc char[trimmed.Length]
                : new char[trimmed.Length];

            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                buffer[i] = InvalidFileNameChars.Contains(c) ? '_' : c;
            }

            return FinalizeSafeFileName(buffer, null, fallback);
        }

        private static string FinalizeSafeFileName(ReadOnlySpan<char> candidate, string? unchangedSource, string fallback)
        {
            ReadOnlySpan<char> clean = candidate.Trim().TrimEnd(". ");
            if (clean.IsEmpty || clean.IsWhiteSpace())
                return fallback;

            int dotIndex = clean.IndexOf('.');
            ReadOnlySpan<char> stem = (dotIndex >= 0 ? clean[..dotIndex] : clean).TrimEnd(' ');
            if (ReservedWindowsDeviceNames.GetAlternateLookup<ReadOnlySpan<char>>().Contains(stem))
                return string.Concat("_".AsSpan(), clean);

            return unchangedSource != null && clean.Length == unchangedSource.Length
                ? unchangedSource
                : clean.ToString();
        }
    }
}
