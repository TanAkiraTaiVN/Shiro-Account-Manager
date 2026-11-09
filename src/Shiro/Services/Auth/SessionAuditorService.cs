#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public enum AccountSessionStatus
    {
        Unknown,
        Valid,
        ExpiredCookie,
        VerificationRequired,
        Moderated,
        RateLimited
    }

    public sealed class AccountSessionAuditResult
    {
        public AccountSessionStatus Status { get; set; }
        public long RobuxBalance { get; set; }
        public long PendingRobux { get; set; }
        public DateTimeOffset AuditedAt { get; set; }
        public string? Message { get; set; }
    }

    public sealed class AuditProgress
    {
        public int CompletedAccounts { get; set; }
        public int TotalAccounts { get; set; }
        public Account? CurrentAccount { get; set; }
        public AccountSessionStatus CurrentStatus { get; set; }
    }

    /// <summary>
    /// Background health scanner and balance auditor for Roblox account sessions.
    /// Manages concurrency pacing, proxy-aware client resolution, and Robux balance inspection.
    /// </summary>
    public sealed class SessionAuditorService
    {
        private static readonly Lazy<SessionAuditorService> _defaultInstance = new(() => new SessionAuditorService());
        public static SessionAuditorService Default => _defaultInstance.Value;
        public static SessionAuditorService Instance => Default;

        private static IAppLogger _staticLogger = AppServices.Logger;
        public static IAppLogger Logger
        {
            get => _staticLogger;
            set => _staticLogger = value ?? throw new ArgumentNullException(nameof(value));
        }

        public SessionAuditorService() { }

        internal static Func<Account, HttpClient>? TestHttpClientFactory;

        private static HttpClient GetHttpClient(Account account, bool allowAutoRedirect = true)
        {
            if (TestHttpClientFactory != null)
                return TestHttpClientFactory(account);

            return account.GetSessionHttpClient(allowAutoRedirect);
        }

        /// <summary>
        /// Maps an HTTP response status code to its corresponding <see cref="AccountSessionStatus"/>.
        /// </summary>
        public static AccountSessionStatus MapStatusCodeToSessionStatus(HttpStatusCode statusCode)
        {
            return statusCode switch
            {
                HttpStatusCode.OK => AccountSessionStatus.Valid,
                HttpStatusCode.Unauthorized => AccountSessionStatus.ExpiredCookie,
                HttpStatusCode.Forbidden => AccountSessionStatus.VerificationRequired,
                (HttpStatusCode)429 => AccountSessionStatus.RateLimited,
                _ => AccountSessionStatus.Unknown
            };
        }

        private static bool IsModerationUrl(string url)
        {
            return url.Contains("/not-approved", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("/moderation", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("/banned", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("/account-status", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("/account-locked", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Audits a single account's session health and current Robux balance.
        /// </summary>
        public static async Task<AccountSessionAuditResult> AuditAccountAsync(Account account, CancellationToken ct = default)
        {
            if (account == null)
            {
                return new AccountSessionAuditResult
                {
                    Status = AccountSessionStatus.Unknown,
                    RobuxBalance = 0,
                    PendingRobux = 0,
                    AuditedAt = DateTimeOffset.UtcNow,
                    Message = "Account is null"
                };
            }

            if (string.IsNullOrWhiteSpace(account.SecurityToken))
            {
                account.Valid = false;
                return new AccountSessionAuditResult
                {
                    Status = AccountSessionStatus.ExpiredCookie,
                    RobuxBalance = account.RobuxBalance > 0 ? account.RobuxBalance : 0,
                    PendingRobux = account.PendingRobux,
                    AuditedAt = DateTimeOffset.UtcNow,
                    Message = "Missing security token"
                };
            }

            try
            {
                HttpClient client = GetHttpClient(account, allowAutoRedirect: true);

                // 1. Session health probe via /v1/users/authenticated
                using var request = account.CreateAuthenticatedGetRequest("https://users.roblox.com/v1/users/authenticated");
                using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);

                account.TryUpdateRotatedCookie(response);
                var status = MapStatusCodeToSessionStatus(response.StatusCode);

                string redirectLocation = response.Headers.Location?.ToString() ?? string.Empty;
                string requestUri = response.RequestMessage?.RequestUri?.AbsoluteUri ?? string.Empty;
                if ((!string.IsNullOrEmpty(redirectLocation) && IsModerationUrl(redirectLocation)) ||
                    (!string.IsNullOrEmpty(requestUri) && IsModerationUrl(requestUri)))
                {
                    status = AccountSessionStatus.Moderated;
                }

                string responseBody = string.Empty;
                if (status == AccountSessionStatus.VerificationRequired || status == AccountSessionStatus.Valid)
                {
                    try { responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
                    catch (Exception ex) { _staticLogger.Debug($"[SessionAuditor] Read response body failed: {ex.Message}"); }
                }

                bool alertAlreadyDispatched = false;
                if (status == AccountSessionStatus.VerificationRequired)
                {
                    string modCode = FastLoginCoordinator.ClassifyAuthOrModerationApiPayload(responseBody, (int)response.StatusCode);
                    if (!string.IsNullOrEmpty(modCode) && modCode != "UNAUTHENTICATED_MODERATION" && modCode != "WRONG_PASSWORD")
                    {
                        status = AccountSessionStatus.Moderated;
                        var (modBadge, _) = FastLoginCoordinator.MapDomIssueCode(modCode);
                        if (!string.IsNullOrEmpty(modBadge) && string.IsNullOrEmpty(account.AccountStatusIssue))
                            account.AccountStatusIssue = modBadge;
                    }

                    if (status != AccountSessionStatus.Moderated)
                    {
                        var (probedBan, probedBadge, _) = await RobloxModerationService.Instance
                            .CheckModerationStatusAsync(account, account.SecurityToken, ct)
                            .ConfigureAwait(false);
                        if (probedBan)
                        {
                            status = AccountSessionStatus.Moderated;
                            alertAlreadyDispatched = true;
                            if (!string.IsNullOrEmpty(probedBadge))
                                account.AccountStatusIssue = probedBadge;
                        }
                    }
                }

                if (status != AccountSessionStatus.Valid)
                {
                    string auditMessage = $"Audit returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase})";
                    if (status == AccountSessionStatus.ExpiredCookie)
                    {
                        account.Valid = false;
                    }
                    else if (status == AccountSessionStatus.Moderated)
                    {
                        account.Valid = false;
                        if (string.IsNullOrEmpty(account.AccountStatusIssue))
                            account.AccountStatusIssue = Shiro.Services.LocalizationService.Get("FastLogin.LockedOrBannedBadge");
                        string modDetails = account.AccountStatusIssue;
                        auditMessage = $"Account {account.Username} received moderation notice: {modDetails}";
                        if (!alertAlreadyDispatched)
                        {
                            WebhookNotificationService.EnqueueAlert(new WebhookAlert
                            {
                                EventType = WebhookEventType.ModerationWarning,
                                AccountUsername = account.Username,
                                Description = $"Account {account.Username} received moderation notice: {modDetails}",
                                Timestamp = DateTimeOffset.UtcNow
                            });
                        }
                    }

                    return new AccountSessionAuditResult
                    {
                        Status = status,
                        RobuxBalance = account.RobuxBalance > 0 ? account.RobuxBalance : 0,
                        PendingRobux = account.PendingRobux,
                        AuditedAt = DateTimeOffset.UtcNow,
                        Message = auditMessage
                    };
                }

                account.Valid = true;
                account.AccountStatusIssue = string.Empty;
                string userJson = responseBody;
                long userId = account.UserID;

                if (!string.IsNullOrWhiteSpace(userJson))
                {
                    try
                    {
                        var authUser = JsonSerializer.Deserialize(userJson, ShiroAppJson.Default.RobloxAuthenticatedUser);
                        if (authUser != null && authUser.Id > 0)
                        {
                            userId = authUser.Id;
                            if (account.UserID <= 0) account.UserID = authUser.Id;
                            if (string.IsNullOrEmpty(account.Username) && !string.IsNullOrEmpty(authUser.Name))
                                account.Username = authUser.Name;
                        }
                    }
                    catch (Exception ex)
                    {
                        _staticLogger.Debug($"[SessionAuditor] Failed to deserialize user info for {account.Username}: {ex.Message}");
                    }
                }

                long robuxBalance = account.RobuxBalance > 0 ? account.RobuxBalance : 0;
                long pendingRobux = account.PendingRobux;

                // 2. Fetch Robux Currency and Pending Robux concurrently
                if (userId > 0)
                {
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
                                if (!string.IsNullOrWhiteSpace(ecoJson))
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
                                if (!string.IsNullOrWhiteSpace(pendJson))
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
                        catch (Exception ex)
                        {
                            _staticLogger.Debug($"[SessionAuditor] Pending Robux query error for {account.Username}: {ex.Message}");
                        }
                    }

                    await Task.WhenAll(FetchCurrencyAsync(), FetchPendingRobuxAsync()).ConfigureAwait(false);
                }

                return new AccountSessionAuditResult
                {
                    Status = AccountSessionStatus.Valid,
                    RobuxBalance = robuxBalance,
                    PendingRobux = pendingRobux,
                    AuditedAt = DateTimeOffset.UtcNow,
                    Message = "Session valid and active"
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _staticLogger.Warn($"[SessionAuditor] Audit exception for {account.Username}: {ex.Message}");
                return new AccountSessionAuditResult
                {
                    Status = AccountSessionStatus.Unknown,
                    RobuxBalance = account.RobuxBalance > 0 ? account.RobuxBalance : 0,
                    PendingRobux = account.PendingRobux,
                    AuditedAt = DateTimeOffset.UtcNow,
                    Message = ex.Message
                };
            }
        }

        /// <summary>
        /// Audits all provided accounts with concurrency bounded to 3 parallel requests.
        /// </summary>
        public static async Task<IReadOnlyDictionary<Account, AccountSessionAuditResult>> AuditAllAccountsAsync(
            IEnumerable<Account> accounts,
            IProgress<AuditProgress>? progress = null,
            CancellationToken ct = default)
        {
            var results = new ConcurrentDictionary<Account, AccountSessionAuditResult>();
            if (accounts == null) return results;

            var list = accounts.Where(a => a != null).Distinct().ToList();
            if (list.Count == 0) return results;

            using var semaphore = new SemaphoreSlim(3, 3);
            int completed = 0;

            var tasks = list.Select(async acc =>
            {
                await semaphore.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var result = await AuditAccountAsync(acc, ct).ConfigureAwait(false);
                    results[acc] = result;
                    int comp = Interlocked.Increment(ref completed);
                    progress?.Report(new AuditProgress
                    {
                        CompletedAccounts = comp,
                        TotalAccounts = list.Count,
                        CurrentAccount = acc,
                        CurrentStatus = result.Status
                    });
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
            return results;
        }
    }
}
