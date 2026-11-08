using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;
using Shiro.Services;

namespace Shiro
{
    public partial class AccountManager
    {
        public void UpdateAccountValidity(Account account, bool isValid)
        {
            if (account == null) return;
            Registration.NotifyAccountValidity(account, isValid);
        }

        public void UpdateAccountView(Account? account = null) =>
            HostUiBridgeService.Default.NotifyLiveProcessStatusChanged();

        internal async Task RefreshStaleCookiesAsync(CancellationToken cancellationToken)
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
                        .Where(a => a != null && !string.IsNullOrWhiteSpace(a.SecurityToken))
                        .Where(a => a.GetField("NoCookieRefresh") != "true")
                        .Where(a => !protectCrossNetwork ||
                                    string.IsNullOrWhiteSpace(a.LastBoundIpSignature) ||
                                    localNetSig is "offline" or "unknown" ||
                                    string.Equals(a.LastBoundIpSignature, RobloxHbaService.GetNetworkSignatureForAccount(a, localNetSig),
                                        StringComparison.Ordinal))
                        .Where(a => (DateTime.Now - a.LastAttemptedRefresh).TotalHours >= 24 || a.LastAttemptedRefresh == DateTime.MinValue)
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
                            bool valid = await RobloxRateLimitCoordinator.Instance.ExecuteWithPacingAsync(
                                RateLimitCategory.SessionCheck,
                                () => account.ValidateSessionAsync(cancellationToken),
                                minIntervalMs: 350,
                                cancellationToken: cancellationToken).ConfigureAwait(false);
                            if (valid && !account.Valid)
                            {
                                account.Valid = true;
                                anyChanged = true;
                                UpdateAccountValidity(account, true);
                            }
                        }
                        catch (Exception ex)
                        {
                            AppServices.Logger.Warn($"[RefreshStaleCookiesAsync] Error refreshing {account.Username}: {ex.Message}");
                        }
                    }

                    if (anyChanged)
                    {
                        Persistence.SaveAccounts(bypassRateLimit: true);
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

        internal async Task ImportBulkUserPassAsync(string combos, Action<string>? statusCallback = null, CancellationToken cancellationToken = default)
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
                if (cancellationToken.IsCancellationRequested || lifetimeCancellation.IsCancellationRequested)
                {
                    string cancelMsg = Shiro.Services.LocalizationService.Get("AccountManager.BulkLoginStopped");
                    SetDashboardStatus(cancelMsg);
                    statusCallback?.Invoke(cancelMsg);
                    break;
                }

                string combo = comboList[i].Trim();
                if (string.IsNullOrWhiteSpace(combo)) continue;

                int separator = combo.IndexOf(':');
                if (separator <= 0) separator = combo.IndexOf('|');
                if (separator <= 0) separator = combo.IndexOf('\t');

                if (separator <= 0 || separator >= combo.Length - 1)
                    continue;

                string username = combo.Substring(0, separator).Trim();
                string password = combo.Substring(separator + 1).Trim();
                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                    continue;

                validCount++;
                int windowSlot = activeSlotTasks[0] == null ? 0 : 1;
                activeSlotTasks[windowSlot] = RunBulkUserPassLoginAsync(username, password, windowSlot, cancellationToken);
                string msg = Shiro.Services.LocalizationService.Format("AccountManager.OpeningBrowser", username, validCount, comboList.Length);
                SetDashboardStatus(msg);
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
                string msg = Shiro.Services.LocalizationService.Get("AccountManager.NoValidCredentials");
                SetDashboardStatus(msg);
                statusCallback?.Invoke(msg);
                return;
            }

            if (cancellationToken.IsCancellationRequested)
                return;

            if (failedAccounts.Count > 0)
            {
                string failedNames = string.Join(", ", failedAccounts.Select(f => f.Username));
                string msg = Shiro.Services.LocalizationService.Format("AccountManager.BulkLoginSummary", validCount - failedAccounts.Count, validCount, failedNames);
                SetDashboardStatus(msg);
                statusCallback?.Invoke(msg);
                AppServices.Logger.Warn($"Bulk sign-in completed with {failedAccounts.Count} failures: {failedNames}");
            }
            else
            {
                string msg = Shiro.Services.LocalizationService.Format("AccountManager.BulkLoginCompleted", validCount);
                SetDashboardStatus(msg);
                statusCallback?.Invoke(msg);
            }
        }

        private async Task<(bool Success, string Username, string Error, int WindowSlot)> RunBulkUserPassLoginAsync(string username, string password, int windowSlot, CancellationToken cancellationToken = default)
        {
            try
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token, cancellationToken);
                await browserService.SignInAsync(
                    username,
                    password,
                    windowSlot,
                    submitCredentials: true,
                    cancellationToken: linkedCts.Token);
                return (true, username, string.Empty, windowSlot);
            }
            catch (OperationCanceledException)
            {
                return (false, username, Shiro.Services.LocalizationService.Get("AccountManager.CancelledByUser"), windowSlot);
            }
            catch (Exception exception)
            {
                AppServices.Logger.Error($"Bulk user:pass login failed for {username}: {exception.Message}");
                return (false, username, exception.Message, windowSlot);
            }
        }

        private static readonly System.Buffers.SearchValues<char> InvalidFileNameChars =
            System.Buffers.SearchValues.Create(Path.GetInvalidFileNameChars());

        internal static string MakeSafeFileName(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;

            ReadOnlySpan<char> trimmed = value.AsSpan().Trim();
            if (trimmed.IndexOfAny(InvalidFileNameChars) < 0)
            {
                ReadOnlySpan<char> clean = trimmed.TrimEnd('.');
                if (clean.IsEmpty || clean.IsWhiteSpace())
                    return fallback;
                return clean.Length == value.Length ? value : clean.ToString();
            }

            Span<char> buffer = trimmed.Length <= 256
                ? stackalloc char[trimmed.Length]
                : new char[trimmed.Length];

            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                buffer[i] = InvalidFileNameChars.Contains(c) ? '_' : c;
            }

            ReadOnlySpan<char> resultSpan = ((ReadOnlySpan<char>)buffer).Trim().TrimEnd('.');
            return resultSpan.IsEmpty || resultSpan.IsWhiteSpace() ? fallback : resultSpan.ToString();
        }
    }
}
