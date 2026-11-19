#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public sealed partial class SessionAuditorService
    {
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

                    if (ecoResp.IsSuccessStatusCode)
                    {
                        string ecoJson = await ecoResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(ecoJson) && !IsHtmlResponse(ecoJson))
                        {
                            var currency = JsonSerializer.Deserialize(ecoJson, ShiroAppJson.Default.RobloxCurrencyResponse);
                            if (currency != null)
                            {
                                robuxBalance = currency.Robux;
                                account.RobuxBalance = currency.Robux;
                                account.LastEconomyCheck = DateTime.UtcNow;
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
            CancellationToken cancellationToken)
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
