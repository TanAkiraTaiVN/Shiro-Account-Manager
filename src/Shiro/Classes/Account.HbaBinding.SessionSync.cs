#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;
using Shiro.Services;

namespace Shiro
{
    public partial class Account
    {
        /// <summary>
        /// Synchronizes and rotates the account's <c>.ROBLOSECURITY</c> session cookie onto the current network IP
        /// (e.g., when disabling VPN/WARP back to normal Vietnam IP) before or during authentication ticket negotiation.
        /// </summary>
        public async Task<bool> TryRebindSessionToCurrentNetworkAsync(bool force = false, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(GetCurrentSecurityToken()))
                return false;

            string currentNetSig = RobloxHbaService.GetLocalNetworkSignature();
            string previousSig = LastBoundIpSignature;

            if (!force)
            {
                if (currentNetSig is "offline" or "unknown")
                    return false;

                if (string.IsNullOrEmpty(previousSig))
                {
                    LastBoundIpSignature = currentNetSig;
                    AppServices.AccountRepository.QueueDeferredSave();
                    return true;
                }

                if (string.Equals(previousSig, currentNetSig, StringComparison.Ordinal))
                    return true;
            }

            try
            {
                // Stage 1: Non-destructive users/authenticated probe.
                // If Roblox responds with Set-Cookie rotating .ROBLOSECURITY, we have successfully migrated to current IP.
                var (rotatedViaUsers, usersAuthenticatedOk, usersStatusCode) =
                    await TryProbeUsersEndpointForCookieRotationAsync(cancellationToken).ConfigureAwait(false);

                if (rotatedViaUsers)
                {
                    CommitBoundNetworkSignature(currentNetSig, rotated: true, "users/authenticated");
                    return true;
                }

                // Stage 2: Canonical Roblox ticket negotiation and redemption.
                // Acquire an authentication ticket and exchange it at auth.roblox.com/v1/authentication-ticket/redeem.
                // Roblox issues a fresh .ROBLOSECURITY Set-Cookie bound to the current network IP.
                var (ticketOk, ticket) = await GetAuthTicketCoreAsync(allowIpSyncRetry: false, cancellationToken).ConfigureAwait(false);
                if (ticketOk && !string.IsNullOrWhiteSpace(ticket))
                {
                    if (await TryRedeemTicketWithHbaAsync(ticket, cancellationToken).ConfigureAwait(false))
                    {
                        CommitBoundNetworkSignature(currentNetSig, rotated: true, "v1/authentication-ticket/redeem");
                        return true;
                    }

                    // Ticket succeeded directly without rotation - session is recognized on this network interface.
                    CommitBoundNetworkSignature(currentNetSig, rotated: false, "authentication-ticket");
                    return true;
                }

                // Stage 3: If users/authenticated returned 200 OK directly, do NOT overwrite LastBoundIpSignature
                // when Stage 2 (authentication-ticket) failed (otherwise subsequent ticket requests falsely think
                // they are on the same network and strip HBA, which revokes the cookie).
                if (usersAuthenticatedOk)
                {
                    return true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[HbaRebind] Fail-soft during network session rebind for {Username}: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Self-healing IP-transition synchronization invoked when <c>GetAuthTicketAsync</c> encounters an initial 401
        /// (for example when external/ISP/router IP changed even though local NIC addresses stayed constant).
        /// </summary>
        internal async Task<bool> TrySynchronizeSessionForIpChangeAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(GetCurrentSecurityToken()))
                return false;

            string currentNetSig = RobloxHbaService.GetLocalNetworkSignature();
            int versionBefore = CookieRotationVersion;

            var (rotatedViaUsers, _, _) =
                await TryProbeUsersEndpointForCookieRotationAsync(cancellationToken).ConfigureAwait(false);

            if (rotatedViaUsers || CookieRotationVersion != versionBefore)
            {
                CommitBoundNetworkSignature(currentNetSig, rotated: true, "ip-sync users/authenticated");
                return true;
            }

            return CookieRotationVersion != versionBefore;
        }

        private async Task<(bool Rotated, bool AuthenticatedUserOk, HttpStatusCode StatusCode)> TryProbeUsersEndpointForCookieRotationAsync(
            CancellationToken cancellationToken)
        {
            HttpClient httpClient = GetSessionHttpClient();
            bool anyRotated = false;
            HttpStatusCode lastStatus = 0;

            for (int attempt = 0; attempt < 2; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RobloxRateLimitCoordinator.Instance.WaitForBackoffAsync(cancellationToken).ConfigureAwait(false);

                int versionBefore = CookieRotationVersion;
                using var request = CreateAuthenticatedGetRequest("https://users.roblox.com/v1/users/authenticated");
                using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (response == null)
                    break;

                lastStatus = response.StatusCode;
                if (TryUpdateRotatedCookie(response) || CookieRotationVersion != versionBefore)
                    anyRotated = true;

                if (attempt == 0 && anyRotated &&
                    (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden))
                {
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    string trimmed = body?.TrimStart() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(trimmed) && !trimmed.StartsWith("<", StringComparison.Ordinal))
                    {
                        if (Utilities.FastJson.TryDeserialize<RobloxAuthenticatedUser>(trimmed, out var parsed) && parsed?.Id > 0)
                            return (anyRotated, true, lastStatus);

                        try
                        {
                            using var doc = JsonDocument.Parse(trimmed);
                            if (doc.RootElement.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out long uid) && uid > 0)
                                return (anyRotated, true, lastStatus);
                        }
                        catch (Exception ex)
                        {
                            Program.Logger?.Debug($"[HbaRebind] Non-user JSON payload during users/authenticated probe: {ex.Message}");
                        }
                    }
                }

                break;
            }

            return (anyRotated, false, lastStatus);
        }

        private async Task<bool> TryRedeemTicketWithHbaAsync(string ticket, CancellationToken cancellationToken)
        {
            HttpClient httpClient = GetSessionHttpClient();
            string payloadJson = JsonSerializer.Serialize(new AuthenticationTicketPayload(ticket), ShiroAppJson.Default.AuthenticationTicketPayload);

            string currentCsrf;
            lock (CookieSyncRoot)
            {
                currentCsrf = CSRFToken ?? string.Empty;
            }

            for (int attempt = 0; attempt < 2; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int versionBefore = CookieRotationVersion;
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://auth.roblox.com/v1/authentication-ticket/redeem");
                ApplySessionFingerprintAndHba(request, payloadJson);
                if (!string.IsNullOrEmpty(currentCsrf))
                    request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", currentCsrf);
                request.Headers.TryAddWithoutValidation("Origin", "https://www.roblox.com");
                request.Headers.TryAddWithoutValidation("Referer", "https://www.roblox.com/");
                request.Headers.TryAddWithoutValidation("rbxauthenticationnegotiation", "1");
                request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

                using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (response == null)
                    return false;

                bool rotated = TryUpdateRotatedCookie(response) || CookieRotationVersion != versionBefore;
                UpdateCsrfFromHeadersIfPresent(response, ref currentCsrf);

                if (response.StatusCode == HttpStatusCode.Forbidden && attempt == 0 && !string.IsNullOrEmpty(currentCsrf))
                    continue;

                if (rotated || response.IsSuccessStatusCode)
                    return true;

                break;
            }

            return false;
        }

        private void UpdateCsrfFromHeadersIfPresent(HttpResponseMessage response, ref string currentCsrf)
        {
            if (response.Headers.TryGetValues("x-csrf-token", out var csrfVals))
            {
                string? freshCsrf = csrfVals.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(freshCsrf))
                {
                    currentCsrf = freshCsrf;
                    lock (CookieSyncRoot)
                    {
                        CSRFToken = freshCsrf;
                        TokenSet = DateTime.Now;
                    }
                }
            }
        }

        private void CommitBoundNetworkSignature(string currentNetSig, bool rotated, string source)
        {
            if (currentNetSig is not ("offline" or "unknown"))
                LastBoundIpSignature = currentNetSig;
            AppServices.AccountRepository.QueueDeferredSave();
            Program.Logger?.Info($"[HbaRebind] Session synchronized to current network via {source} for {Username} (rotated={rotated}).");
        }
    }
}
