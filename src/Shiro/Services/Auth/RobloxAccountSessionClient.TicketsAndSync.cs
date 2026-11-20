#nullable enable
using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public sealed partial class RobloxAccountSessionClient
    {
        /// <summary>
        /// Asynchronously fetches an X-CSRF-Token from Roblox.
        /// Uses v1/authentication-ticket first (fast, non-login endpoint) before falling back to v2/login,
        /// which reliably returns HTTP 403 Forbidden containing the x-csrf-token response header.
        /// </summary>
        public async Task<(bool Success, string Token, string Error)> GetCsrfTokenAsync(Account account, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(account);
            ct.ThrowIfCancellationRequested();

            string cachedCsrf = account.GetFreshCsrfToken(TimeSpan.FromMinutes(4));
            if (!string.IsNullOrEmpty(cachedCsrf))
                return (true, cachedCsrf, string.Empty);

            string[] endpoints =
            {
                "https://auth.roblox.com/v1/authentication-ticket",
                "https://auth.roblox.com/v2/login"
            };
            HttpClient httpClient = GetSessionHttpClient(account);

            foreach (string endpoint in endpoints)
            {
                ct.ThrowIfCancellationRequested();
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                ApiService.ApplySessionFingerprintAndHba(account, request, "{}");
                request.Headers.TryAddWithoutValidation("Origin", "https://www.roblox.com");
                request.Headers.TryAddWithoutValidation("Referer", "https://www.roblox.com");
                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

                try
                {
                    using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    if (response == null) continue;

                    TryUpdateRotatedCookie(account, response);

                    if ((int)response.StatusCode == 429)
                    {
                        account.LastAuthTicketRateLimited = true;
                        MarkNativeAuthRateLimited(60, account.Username, "CSRF token", response: response);
                        return (false, string.Empty, LocalizationService.Get("Account.RateLimited"));
                    }

                    if (response.Headers.TryGetValues("x-csrf-token", out var vals))
                    {
                        string? token = vals.FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(token))
                        {
                            account.RecordCsrfTokenAndLastUse(token, DateTime.Now);
                            AppServices.SelectionState.LastValidAccount = account;
                            return (true, token, string.Empty);
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Logger?.Warn($"[GetCSRFTokenAsync] Error on {endpoint}: {ex.Message}");
                }
            }

            return (false, string.Empty, "Roblox did not issue an X-CSRF-Token.");
        }

        /// <summary>
        /// 1-step optimistic authentication ticket negotiation.
        /// Posts directly to v1/authentication-ticket/ using any cached CSRF token (or empty on first call),
        /// captures the x-csrf-token directly from the 403 Forbidden challenge response, and self-heals
        /// IP-transition 401s by synchronizing/rotating .ROBLOSECURITY onto the current IP.
        /// </summary>
        public Task<(bool Success, string Ticket)> GetAuthTicketAsync(Account account, CancellationToken ct = default) =>
            GetAuthTicketCoreAsync(account, allowIpSyncRetry: true, ct);

        internal async Task<(bool Success, string Ticket)> GetAuthTicketCoreAsync(
            Account account,
            bool allowIpSyncRetry,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(account);
            account.LastAuthTicketRateLimited = false;
            HttpClient httpClient = GetSessionHttpClient(account);
            bool attemptedIpSync = false;
            string currentSigAtStart = RobloxHbaService.GetNetworkSignatureForAccount(account);
            bool wasSameNetworkAtStart =
                !string.IsNullOrEmpty(account.LastBoundIpSignature) &&
                string.Equals(account.LastBoundIpSignature, currentSigAtStart, StringComparison.Ordinal);

            bool useHba = account.HasHbaBinding;
            if (allowIpSyncRetry)
            {
                if (string.IsNullOrEmpty(account.LastBoundIpSignature) &&
                    currentSigAtStart is not ("offline" or "unknown"))
                {
                    if (!useHba)
                    {
                        account.LastBoundIpSignature = currentSigAtStart;
                        AppServices.AccountRepository.QueueDeferredSave();
                        wasSameNetworkAtStart = true;
                    }
                }
                else if (useHba &&
                    !string.IsNullOrEmpty(account.LastBoundIpSignature) &&
                    !wasSameNetworkAtStart &&
                    currentSigAtStart is not ("offline" or "unknown"))
                {
                    Logger?.Info($"[GetAuthTicketAsync] Network signature changed from '{account.LastBoundIpSignature}' to '{currentSigAtStart}' for {account.Username}. Proactively synchronizing session to current network...");
                    await TryRebindSessionToCurrentNetworkAsync(account, force: true, ct).ConfigureAwait(false);
                }
            }

            string currentCsrf = account.GetFreshCsrfToken(TimeSpan.FromMinutes(4));
            for (int attempt = 0; attempt < AuthTicketMaxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                await RobloxRateLimitCoordinator.Instance.WaitForBackoffAsync(RateLimitCategory.Authentication, ct).ConfigureAwait(false);
                string ticketBody = await AccountLaunchService.FetchClientAssertionBodyAsync(account, httpClient, includeHba: useHba, ct).ConfigureAwait(false);
                int rotationBefore = account.CookieRotationVersion;
                using var request = AccountLaunchService.CreateTicketRequest(account, currentCsrf, body: ticketBody, includeHba: useHba);
                try
                {
                    using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    if (response == null) return (false, string.Empty);

                    bool rotatedOnTicketCall = TryUpdateRotatedCookie(account, response) || account.CookieRotationVersion != rotationBefore;
                    bool updatedCsrfFromHeader = false;

                    if (response.Headers.TryGetValues("x-csrf-token", out var csrfVals))
                    {
                        string? freshCsrf = csrfVals.FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(freshCsrf))
                        {
                            updatedCsrfFromHeader = true;
                            currentCsrf = freshCsrf;
                            account.RecordCsrfTokenAndLastUse(freshCsrf, DateTime.Now);
                            AppServices.SelectionState.LastValidAccount = account;
                        }
                    }

                    if (response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        if (!updatedCsrfFromHeader)
                        {
                            currentCsrf = string.Empty;
                            account.SetCsrfToken(null);
                            var fallbackCsrf = await GetCsrfTokenAsync(account, ct).ConfigureAwait(false);
                            if (fallbackCsrf.Success)
                                currentCsrf = fallbackCsrf.Token;
                        }
                        continue;
                    }

                    if ((int)response.StatusCode == 429)
                    {
                        account.LastAuthTicketRateLimited = true;

                        if (attempt + 1 < AuthTicketMaxAttempts)
                        {
                            RobloxRateLimitCoordinator.Instance.RecordRateLimitHit(RateLimitCategory.Authentication, response, defaultBackoffSeconds: 3);
                            var backoff = RobloxRateLimitCoordinator.Instance.GetRemainingBackoff(RateLimitCategory.Authentication);
                            Logger?.Warn($"Roblox authentication-ticket request was rate limited (HTTP 429) for {account.Username}. Backing off for {backoff.TotalSeconds:F1}s before retry (attempt {attempt + 1}/{AuthTicketMaxAttempts})...");
                            continue;
                        }

                        MarkNativeAuthRateLimited(30, account.Username, "authentication-ticket", response: response);
                        Logger?.Warn($"Roblox authentication-ticket request was rate limited (HTTP 429) for {account.Username}; exhausted all {AuthTicketMaxAttempts} attempts.");
                        return (false, string.Empty);
                    }

                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        Logger?.Warn($"Roblox authentication-ticket request returned HTTP 401 Unauthorized for {account.Username} (hba={useHba}). Preserving stored session.");
                        currentCsrf = string.Empty;
                        account.SetCsrfToken(null);

                        if (rotatedOnTicketCall && attempt + 1 < AuthTicketMaxAttempts)
                            continue;

                        if (allowIpSyncRetry && !attemptedIpSync && attempt + 1 < AuthTicketMaxAttempts)
                        {
                            attemptedIpSync = true;
                            if (await TrySynchronizeSessionForIpChangeAsync(account, ct).ConfigureAwait(false))
                            {
                                currentCsrf = account.GetFreshCsrfToken(TimeSpan.FromMinutes(4));
                                continue;
                            }
                        }

                        if (useHba && attempt + 1 < AuthTicketMaxAttempts &&
                            wasSameNetworkAtStart &&
                            !Account.IsShortLivedCookie(account.GetCurrentSecurityToken()))
                        {
                            useHba = false;
                            Logger?.Info($"[GetAuthTicketAsync] Retrying authentication-ticket for {account.Username} without HBA header on same network...");
                            continue;
                        }

                        return (false, string.Empty);
                    }

                    if (response.Headers.TryGetValues("rbx-authentication-ticket", out var ticketVals))
                    {
                        string ticket = ticketVals.FirstOrDefault() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(ticket))
                        {
                            account.LastAuthTicketRateLimited = false;
                            RobloxRateLimitCoordinator.Instance.RecordSuccess(RateLimitCategory.Authentication);
                            AppServices.AccountRepository.QueueDeferredSave();
                            return (true, ticket);
                        }
                    }

                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Logger?.Warn($"[GetAuthTicketAsync] Error on attempt {attempt}: {ex.Message}");
                    if (attempt + 1 < AuthTicketMaxAttempts)
                        await Task.Delay(350, ct).ConfigureAwait(false);
                }
            }

            return (false, string.Empty);
        }

        /// <summary>
        /// Synchronizes and rotates the account's <c>.ROBLOSECURITY</c> session cookie onto the current network IP
        /// before or during authentication ticket negotiation.
        /// </summary>
        public async Task<bool> TryRebindSessionToCurrentNetworkAsync(Account account, bool force = false, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(account);
            if (string.IsNullOrWhiteSpace(account.GetCurrentSecurityToken()))
                return false;

            string currentNetSig = RobloxHbaService.GetNetworkSignatureForAccount(account);
            string previousSig = account.LastBoundIpSignature;

            if (!force)
            {
                if (currentNetSig is "offline" or "unknown")
                    return false;

                if (string.IsNullOrEmpty(previousSig))
                {
                    account.LastBoundIpSignature = currentNetSig;
                    AppServices.AccountRepository.QueueDeferredSave();
                    return true;
                }

                if (string.Equals(previousSig, currentNetSig, StringComparison.Ordinal))
                    return true;
            }

            long rebindStartTicks = Stopwatch.GetTimestamp();
            try
            {
                // Stage 1: Non-destructive users/authenticated probe.
                var (rotatedViaUsers, usersAuthenticatedOk, _) =
                    await TryProbeUsersEndpointForCookieRotationAsync(account, ct).ConfigureAwait(false);

                if (rotatedViaUsers)
                {
                    CommitBoundNetworkSignature(account, currentNetSig, rotated: true, "users/authenticated");
                    return true;
                }

                // Stage 2: Canonical Roblox ticket negotiation and redemption.
                var (ticketOk, ticket) = await GetAuthTicketCoreAsync(account, allowIpSyncRetry: false, ct).ConfigureAwait(false);
                if (ticketOk && !string.IsNullOrWhiteSpace(ticket))
                {
                    if (await TryRedeemTicketWithHbaAsync(account, ticket, ct).ConfigureAwait(false))
                    {
                        CommitBoundNetworkSignature(account, currentNetSig, rotated: true, "v1/authentication-ticket/redeem");
                        return true;
                    }

                    CommitBoundNetworkSignature(account, currentNetSig, rotated: false, "authentication-ticket");
                    return true;
                }

                if (usersAuthenticatedOk)
                {
                    return true;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger?.Debug($"[HbaRebind] Fail-soft during network session rebind for {account.Username}: {ex.Message}");
            }
            finally
            {
                LaunchAttemptContext? attemptCtx = LaunchTimingService.CurrentAttempt;
                if (attemptCtx != null)
                {
                    attemptCtx.RebindPerformed = true;
                    attemptCtx.RebindMs = (long)Stopwatch.GetElapsedTime(rebindStartTicks).TotalMilliseconds;
                }
            }

            return false;
        }

        internal async Task<bool> TrySynchronizeSessionForIpChangeAsync(Account account, CancellationToken ct = default)
        {
            if (account == null || string.IsNullOrWhiteSpace(account.GetCurrentSecurityToken()))
                return false;

            string currentNetSig = RobloxHbaService.GetNetworkSignatureForAccount(account);
            int versionBefore = account.CookieRotationVersion;

            var (rotatedViaUsers, _, _) =
                await TryProbeUsersEndpointForCookieRotationAsync(account, ct).ConfigureAwait(false);

            if (rotatedViaUsers || account.CookieRotationVersion != versionBefore)
            {
                CommitBoundNetworkSignature(account, currentNetSig, rotated: true, "ip-sync users/authenticated");
                return true;
            }

            return account.CookieRotationVersion != versionBefore;
        }

        private async Task<(bool Rotated, bool AuthenticatedUserOk, HttpStatusCode StatusCode)> TryProbeUsersEndpointForCookieRotationAsync(
            Account account,
            CancellationToken ct)
        {
            HttpClient httpClient = GetSessionHttpClient(account);
            bool anyRotated = false;
            HttpStatusCode lastStatus = 0;

            for (int attempt = 0; attempt < 2; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                await RobloxRateLimitCoordinator.Instance.WaitForBackoffAsync(RateLimitCategory.SessionCheck, ct).ConfigureAwait(false);

                int versionBefore = account.CookieRotationVersion;
                using var request = CreateAuthenticatedGetRequest(account, "https://users.roblox.com/v1/users/authenticated");
                using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (response == null)
                    break;

                lastStatus = response.StatusCode;
                if (TryUpdateRotatedCookie(account, response) || account.CookieRotationVersion != versionBefore)
                    anyRotated = true;

                if (attempt == 0 && anyRotated &&
                    (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden))
                {
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    string trimmed = body?.TrimStart() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(trimmed) && !trimmed.StartsWith("<", StringComparison.Ordinal))
                    {
                        if (FastJson.TryDeserialize<RobloxAuthenticatedUser>(trimmed, out var parsed) && parsed?.Id > 0)
                            return (anyRotated, true, lastStatus);

                        try
                        {
                            using var doc = JsonDocument.Parse(trimmed);
                            if (doc.RootElement.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out long uid) && uid > 0)
                                return (anyRotated, true, lastStatus);
                        }
                        catch (Exception ex)
                        {
                            Logger?.Debug($"[HbaRebind] Non-user JSON payload during users/authenticated probe: {ex.Message}");
                        }
                    }
                }

                break;
            }

            return (anyRotated, false, lastStatus);
        }

        private async Task<bool> TryRedeemTicketWithHbaAsync(Account account, string ticket, CancellationToken ct)
        {
            HttpClient httpClient = GetSessionHttpClient(account);
            string payloadJson = JsonSerializer.Serialize(new AuthenticationTicketPayload(ticket), ShiroAppJson.Default.AuthenticationTicketPayload);
            string currentCsrf = account.CSRFToken ?? string.Empty;

            for (int attempt = 0; attempt < 2; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                int versionBefore = account.CookieRotationVersion;
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://auth.roblox.com/v1/authentication-ticket/redeem");
                ApiService.ApplySessionFingerprintAndHba(account, request, payloadJson);
                if (!string.IsNullOrEmpty(currentCsrf))
                    request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", currentCsrf);
                request.Headers.TryAddWithoutValidation("Origin", "https://www.roblox.com");
                request.Headers.TryAddWithoutValidation("Referer", "https://www.roblox.com/");
                request.Headers.TryAddWithoutValidation("rbxauthenticationnegotiation", "1");
                request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

                using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (response == null)
                    return false;

                bool rotated = TryUpdateRotatedCookie(account, response) || account.CookieRotationVersion != versionBefore;
                UpdateCsrfFromHeadersIfPresent(account, response, ref currentCsrf);

                if (response.StatusCode == HttpStatusCode.Forbidden && attempt == 0 && !string.IsNullOrEmpty(currentCsrf))
                    continue;

                if (rotated || response.IsSuccessStatusCode)
                    return true;

                break;
            }

            return false;
        }

        private static void UpdateCsrfFromHeadersIfPresent(Account account, HttpResponseMessage response, ref string currentCsrf)
        {
            if (response.Headers.TryGetValues("x-csrf-token", out var csrfVals))
            {
                string? freshCsrf = csrfVals.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(freshCsrf))
                {
                    currentCsrf = freshCsrf;
                    account.SetCsrfToken(freshCsrf);
                }
            }
        }

        private void CommitBoundNetworkSignature(Account account, string currentNetSig, bool rotated, string source)
        {
            if (currentNetSig is not ("offline" or "unknown"))
                account.LastBoundIpSignature = currentNetSig;
            AppServices.AccountRepository.QueueDeferredSave();
            Logger?.Info($"[HbaRebind] Session synchronized to current network via {source} for {account.Username} (rotated={rotated}).");
        }
    }
}
