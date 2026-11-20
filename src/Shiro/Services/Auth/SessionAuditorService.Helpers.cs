#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public sealed partial class SessionAuditorService
    {
        private sealed class AuditSideEffects
        {
            public bool SawPrimary429;
            public RetryConditionHeaderValue? Primary429RetryAfter;
            public bool SawSecondary429;
            public RetryConditionHeaderValue? Secondary429RetryAfter;
            public bool SawCurrency429;
            public RetryConditionHeaderValue? Currency429RetryAfter;
            public bool SawPending429;
            public RetryConditionHeaderValue? Pending429RetryAfter;
            public bool SessionCheckSuccess;
            public bool EconomySuccess;
            public WebhookAlert? PendingModerationAlert;
            public bool ValidityChanged;
            public bool StateChanged;

            public void RecordPrimaryRateLimit(RetryConditionHeaderValue? retryAfter)
            {
                SawPrimary429 = true;
                Primary429RetryAfter = retryAfter;
            }

            public void RecordSecondaryRateLimit(RetryConditionHeaderValue? retryAfter)
            {
                SawSecondary429 = true;
                Secondary429RetryAfter = retryAfter;
            }
        }

        private static void CaptureAuditMutations(
            Account account,
            bool prevValid,
            string prevIssue,
            long prevUserId,
            string prevUsername,
            long prevRobux,
            long prevPending,
            AuditSideEffects sideEffects)
        {
            bool validityChanged = account.Valid != prevValid;
            bool stateChanged = validityChanged ||
                !string.Equals(account.AccountStatusIssue, prevIssue, StringComparison.Ordinal) ||
                account.UserID != prevUserId ||
                !string.Equals(account.Username, prevUsername, StringComparison.Ordinal) ||
                account.RobuxBalance != prevRobux ||
                account.PendingRobux != prevPending;

            sideEffects.ValidityChanged = validityChanged;
            sideEffects.StateChanged = stateChanged;
            if (stateChanged && !Account.IsSuppressingDeferredSave)
                account.IsDirty = true;
        }

        private static void DispatchAuditSideEffects(Account account, string proxyScope, AuditSideEffects sideEffects)
        {
            var coordinator = RobloxRateLimitCoordinator.Instance;

            if (sideEffects.SawPrimary429)
                coordinator.RecordRateLimitHitWithRetryAfter(RateLimitCategory.SessionCheck, sideEffects.Primary429RetryAfter, 4, proxyScope);
            if (sideEffects.SawSecondary429)
                coordinator.RecordRateLimitHitWithRetryAfter(RateLimitCategory.SessionCheck, sideEffects.Secondary429RetryAfter, 4, proxyScope);
            if (sideEffects.SessionCheckSuccess && !sideEffects.SawPrimary429 && !sideEffects.SawSecondary429)
                coordinator.RecordSuccess(RateLimitCategory.SessionCheck, proxyScope);

            if (sideEffects.SawCurrency429)
                coordinator.RecordRateLimitHitWithRetryAfter(RateLimitCategory.General, sideEffects.Currency429RetryAfter, 4, proxyScope);
            if (sideEffects.SawPending429)
                coordinator.RecordRateLimitHitWithRetryAfter(RateLimitCategory.General, sideEffects.Pending429RetryAfter, 4, proxyScope);
            if (sideEffects.EconomySuccess && !sideEffects.SawCurrency429 && !sideEffects.SawPending429)
                coordinator.RecordSuccess(RateLimitCategory.General, proxyScope);

            if (sideEffects.PendingModerationAlert != null)
                WebhookNotificationService.EnqueueAlert(sideEffects.PendingModerationAlert);

            if (sideEffects.StateChanged && !Account.IsSuppressingDeferredSave)
                AppServices.AccountRepository.QueueDeferredSave();

            if (sideEffects.ValidityChanged)
                AppServices.HostUiBridge.UpdateAccountValidityOnUi(account, account.Valid);
            else if (sideEffects.StateChanged)
                AppServices.HostUiBridge.RefreshAccountView(account);
        }
        private static bool IsModerationUrl(string url) =>
            RobloxModerationHelper.IsModerationOrLockedUrl(url);

        private static bool IsHtmlResponse(string? content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return false;

            if (RobloxModerationService.IsLoginPageHtml(content))
                return true;

            return content.AsSpan().TrimStart().StartsWith("<".AsSpan(), StringComparison.Ordinal);
        }

        private static bool TryParseFriendsCountPayload(string? body)
        {
            if (string.IsNullOrWhiteSpace(body) || IsHtmlResponse(body))
                return false;

            try
            {
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.ValueKind == JsonValueKind.Object &&
                       doc.RootElement.TryGetProperty("count", out var countProp) &&
                       countProp.ValueKind == JsonValueKind.Number &&
                       countProp.TryGetInt64(out long count) &&
                       count >= 0;
            }
            catch (Exception ex)
            {
                _staticLogger.Debug($"Parse friend count JSON failed: {ex.Message}");
                return false;
            }
        }

        private static AccountSessionAuditResult BuildAuditResult(Account account, AccountSessionStatus status, string message)
        {
            return new AccountSessionAuditResult
            {
                Status = status,
                RobuxBalance = account.RobuxBalance > 0 ? account.RobuxBalance : 0,
                PendingRobux = account.PendingRobux,
                AuditedAt = DateTimeOffset.UtcNow,
                Message = message
            };
        }

        private static async Task<(long RobuxBalance, long PendingRobux)> FetchEconomyBalancesAsync(
            Account account,
            HttpClient client,
            long userId,
            long initialRobux,
            long initialPending,
            AuditSideEffects sideEffects,
            CancellationToken ct)
        {
            long robuxBalance = initialRobux;
            long pendingRobux = initialPending;

            async Task FetchCurrencyAsync()
            {
                try
                {
                    using var ecoReq = account.CreateAuthenticatedGetRequest($"https://economy.roblox.com/v1/users/{userId}/currency");
                    using var ecoResp = await client.SendAsync(ecoReq, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                    account.TryUpdateRotatedCookie(ecoResp);

                    if ((int)ecoResp.StatusCode == 429)
                    {
                        sideEffects.SawCurrency429 = true;
                        sideEffects.Currency429RetryAfter = ecoResp.Headers.RetryAfter;
                        return;
                    }

                    if (ecoResp.IsSuccessStatusCode)
                    {
                        string ecoJson = await ecoResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        if (RobloxEconomyService.TryParseCurrencyBalance(ecoJson, out long parsedRobux))
                        {
                            robuxBalance = parsedRobux;
                            account.RobuxBalance = parsedRobux;
                            account.LastEconomyCheck = DateTime.UtcNow;
                            sideEffects.EconomySuccess = true;
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _staticLogger.Debug($"[SessionAuditor] Economy currency query error for {account.Username}: {ex.Message}");
                }
            }

            async Task FetchPendingRobuxAsync()
            {
                try
                {
                    using var pendReq = account.CreateAuthenticatedGetRequest($"https://economy.roblox.com/v2/users/{userId}/transaction-totals?timeFrame=Month&transactionType=summary");
                    using var pendResp = await client.SendAsync(pendReq, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                    account.TryUpdateRotatedCookie(pendResp);

                    if ((int)pendResp.StatusCode == 429)
                    {
                        sideEffects.SawPending429 = true;
                        sideEffects.Pending429RetryAfter = pendResp.Headers.RetryAfter;
                        return;
                    }

                    if (pendResp.IsSuccessStatusCode)
                    {
                        string pendJson = await pendResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(pendJson) && !IsHtmlResponse(pendJson))
                        {
                            using var doc = JsonDocument.Parse(pendJson);
                            if (doc.RootElement.TryGetProperty("pendingRobuxTotal", out var prtProp) && prtProp.TryGetInt64(out long prt))
                            {
                                pendingRobux = prt;
                                account.PendingRobux = prt;
                                sideEffects.EconomySuccess = true;
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _staticLogger.Debug($"[SessionAuditor] Pending Robux query error for {account.Username}: {ex.Message}");
                }
            }

            await Task.WhenAll(FetchCurrencyAsync(), FetchPendingRobuxAsync()).ConfigureAwait(false);
            return (robuxBalance, pendingRobux);
        }

        internal static async Task<bool> ValidateSecondaryWithRotationAsync(
            Account account,
            Func<HttpClient> clientFactory,
            CancellationToken cancellationToken,
            Action<RetryConditionHeaderValue?>? onRateLimitHit = null)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    int versionBefore = account.CookieRotationVersion;
                    HttpClient httpClient = clientFactory();
                    using var request = account.CreateAuthenticatedGetRequest("https://friends.roblox.com/v1/my/friends/count");
                    using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    account.TryUpdateRotatedCookie(response);

                    if ((int)response.StatusCode == 429)
                    {
                        if (onRateLimitHit != null)
                        {
                            onRateLimitHit(response.Headers.RetryAfter);
                        }
                        else
                        {
                            RobloxRateLimitCoordinator.Instance.RecordRateLimitHit(
                                RateLimitCategory.SessionCheck,
                                response,
                                proxyScope: RobloxRateLimitCoordinator.ResolveProxyScope(account));
                        }
                        return false;
                    }

                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        if (attempt == 0 && account.CookieRotationVersion != versionBefore) continue;
                        _staticLogger.Warn($"[ValidateViaSecondaryEndpointAsync] {account.Username}: Received 401 on friends.roblox.com. Cookie expired.");
                        return false;
                    }

                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                        if (TryParseFriendsCountPayload(body))
                            return true;
                    }
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _staticLogger.Warn($"CheckFriendsCountAsync failed: {ex.Message}");
                    break;
                }
            }

            return false;
        }
    }
}
