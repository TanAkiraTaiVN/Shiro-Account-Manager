#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
    public sealed partial class SessionAuditorService
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

        private static readonly AsyncLocal<Func<Account, HttpClient>?> _testHttpClientFactory = new();

        internal static Func<Account, HttpClient>? TestHttpClientFactory
        {
            get => _testHttpClientFactory.Value;
            set => _testHttpClientFactory.Value = value;
        }

        private static HttpClient GetHttpClient(Account account, bool allowAutoRedirect = true)
        {
            if (TestHttpClientFactory != null)
                return TestHttpClientFactory(account);

            return AppServices.AccountSessionClient.GetSessionHttpClient(account, allowAutoRedirect);
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

        /// <summary>
        /// Audits a single account's session health and current Robux balance.
        /// </summary>
        public static async Task<AccountSessionAuditResult> AuditAccountAsync(Account account, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

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
                bool validityChanged = false;
                AccountSessionAuditResult missingResult = await account.RunWithSessionGateAsync(() =>
                {
                    if (account.Valid)
                    {
                        account.Valid = false;
                        if (!Account.IsSuppressingDeferredSave)
                            account.IsDirty = true;
                        validityChanged = true;
                    }
                    return Task.FromResult(BuildAuditResult(account, AccountSessionStatus.ExpiredCookie, "Missing security token"));
                }, ct).ConfigureAwait(false);

                if (validityChanged && !Account.IsSuppressingDeferredSave)
                {
                    AppServices.AccountRepository.QueueDeferredSave();
                    AppServices.HostUiBridge.UpdateAccountValidityOnUi(account, false);
                }
                return missingResult;
            }

            string proxyScope = RobloxRateLimitCoordinator.ResolveProxyScope(account);
            return await RobloxRateLimitCoordinator.Instance.ExecuteWithPacingAsync(
                RateLimitCategory.SessionCheck,
                async () =>
                {
                    var sideEffects = new AuditSideEffects();
                    AccountSessionAuditResult result = await account.RunWithSessionGateAsync(
                        () => AuditAccountUnderGateAsync(account, sideEffects, ct),
                        ct).ConfigureAwait(false);
                    DispatchAuditSideEffects(account, proxyScope, sideEffects);
                    return result;
                },
                proxyScope,
                minIntervalMs: 100,
                cancellationToken: ct).ConfigureAwait(false);
        }

        private static async Task<AccountSessionAuditResult> AuditAccountUnderGateAsync(
            Account account,
            AuditSideEffects sideEffects,
            CancellationToken ct)
        {
            bool prevValid = account.Valid;
            string prevIssue = account.AccountStatusIssue;
            long prevUserId = account.UserID;
            string prevUsername = account.Username;
            long prevRobux = account.RobuxBalance;
            long prevPending = account.PendingRobux;

            try
            {
                HttpClient client = GetHttpClient(account, allowAutoRedirect: false);

                // 1. Session health probe via /v1/users/authenticated
                using var request = AppServices.AccountSessionClient.CreateAuthenticatedGetRequest(account, "https://users.roblox.com/v1/users/authenticated");
                using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);

                AppServices.AccountSessionClient.TryUpdateRotatedCookie(account, response);
                var status = MapStatusCodeToSessionStatus(response.StatusCode);
                if (status == AccountSessionStatus.RateLimited)
                    sideEffects.RecordPrimaryRateLimit(response.Headers.RetryAfter);

                string redirectLocation = response.Headers.Location?.ToString() ?? string.Empty;
                string requestUri = response.RequestMessage?.RequestUri?.AbsoluteUri ?? string.Empty;
                if ((!string.IsNullOrEmpty(redirectLocation) && IsModerationUrl(redirectLocation)) ||
                    (!string.IsNullOrEmpty(requestUri) && IsModerationUrl(requestUri)))
                {
                    status = AccountSessionStatus.Moderated;
                }
                else if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400 &&
                         (redirectLocation.Contains("/login", StringComparison.OrdinalIgnoreCase) ||
                          redirectLocation.Contains("NewLogin", StringComparison.OrdinalIgnoreCase)))
                {
                    status = AccountSessionStatus.ExpiredCookie;
                }

                string responseBody = string.Empty;
                if (status == AccountSessionStatus.VerificationRequired || status == AccountSessionStatus.Valid)
                {
                    try { responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) { _staticLogger.Debug($"[SessionAuditor] Read response body failed: {ex.Message}"); }
                }

                string? moderationReason = null;
                bool confirmedBySecondary = false;
                if (status == AccountSessionStatus.ExpiredCookie)
                {
                    if (await ValidateSecondaryWithRotationAsync(account, () => client, ct, sideEffects.RecordSecondaryRateLimit).ConfigureAwait(false))
                    {
                        status = AccountSessionStatus.Valid;
                        confirmedBySecondary = true;
                    }
                    else
                    {
                        (status, moderationReason) = await ProbeModerationStatusAsync(account, client, status, ct).ConfigureAwait(false);
                    }
                }
                else if (status == AccountSessionStatus.VerificationRequired)
                {
                    string modCode = FastLoginCoordinator.ClassifyAuthOrModerationApiPayload(responseBody, (int)response.StatusCode);
                    if (!string.IsNullOrEmpty(modCode) && modCode != "UNAUTHENTICATED_MODERATION" && modCode != "WRONG_PASSWORD")
                    {
                        status = AccountSessionStatus.Moderated;
                        var (modBadge, modReason) = FastLoginCoordinator.MapDomIssueCode(modCode);
                        if (!string.IsNullOrEmpty(modBadge) && string.IsNullOrEmpty(account.AccountStatusIssue))
                            account.AccountStatusIssue = modBadge;
                        moderationReason = modReason;
                    }

                    if (status != AccountSessionStatus.Moderated)
                    {
                        (status, moderationReason) = await ProbeModerationStatusAsync(account, client, status, ct).ConfigureAwait(false);
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
                        sideEffects.PendingModerationAlert = RobloxModerationService.BuildModerationAlert(
                            account,
                            modDetails,
                            moderationReason);
                    }

                    return BuildAuditResult(account, status, auditMessage);
                }

                long userId = account.UserID;
                RobloxAuthenticatedUser? authUser = null;
                if (!confirmedBySecondary)
                {
                    if (IsHtmlResponse(responseBody))
                    {
                        if (RobloxModerationService.IsLoginPageHtml(responseBody))
                        {
                            _staticLogger.Warn($"[SessionAuditor] {account.Username}: Authenticated response returned login page HTML. Marking expired.");
                            account.Valid = false;
                            return BuildAuditResult(account, AccountSessionStatus.ExpiredCookie, "Authenticated response returned login page HTML");
                        }

                        _staticLogger.Warn($"[SessionAuditor] {account.Username}: Received HTML block/captive page instead of JSON for 200 OK. Preserving validity: {account.Valid}");
                        return BuildAuditResult(account, AccountSessionStatus.Unknown, "Received HTML block or captive page instead of JSON");
                    }

                    if (!string.IsNullOrWhiteSpace(responseBody))
                    {
                        try
                        {
                            authUser = JsonSerializer.Deserialize(responseBody, ShiroAppJson.Default.RobloxAuthenticatedUser);
                        }
                        catch (Exception ex)
                        {
                            _staticLogger.Debug($"[SessionAuditor] Failed to deserialize user info for {account.Username}: {ex.Message}");
                        }
                    }

                    if (authUser == null || authUser.Id <= 0)
                    {
                        account.Valid = false;
                        return BuildAuditResult(account, AccountSessionStatus.ExpiredCookie, "Authenticated response was not valid JSON");
                    }

                    if (account.UserID > 0 && authUser.Id != account.UserID)
                    {
                        _staticLogger.Warn($"[SessionAuditor] {account.Username}: UserID mismatch (Expected: {account.UserID}, Got: {authUser.Id}). Marking invalid.");
                        account.Valid = false;
                        return BuildAuditResult(account, AccountSessionStatus.ExpiredCookie, $"UserID mismatch (expected {account.UserID}, got {authUser.Id})");
                    }
                }

                if (authUser != null && authUser.Id > 0)
                {
                    userId = authUser.Id;
                    if (account.UserID <= 0) account.UserID = authUser.Id;
                    if (string.IsNullOrEmpty(account.Username) && !string.IsNullOrEmpty(authUser.Name))
                        account.Username = authUser.Name;
                }

                account.Valid = true;
                account.AccountStatusIssue = string.Empty;
                sideEffects.SessionCheckSuccess = true;

                long robuxBalance = account.RobuxBalance > 0 ? account.RobuxBalance : 0;
                long pendingRobux = account.PendingRobux;

                // 2. Fetch Robux Currency and Pending Robux concurrently
                if (userId > 0)
                {
                    (robuxBalance, pendingRobux) = await FetchEconomyBalancesAsync(
                        account, client, userId, robuxBalance, pendingRobux, sideEffects, ct).ConfigureAwait(false);
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
                return BuildAuditResult(account, AccountSessionStatus.Unknown, ex.Message);
            }
            finally
            {
                CaptureAuditMutations(account, prevValid, prevIssue, prevUserId, prevUsername, prevRobux, prevPending, sideEffects);
            }
        }

        private static async Task<(AccountSessionStatus Status, string? ModerationReason)> ProbeModerationStatusAsync(
            Account account,
            HttpClient client,
            AccountSessionStatus currentStatus,
            CancellationToken ct)
        {
            // Suppress immediate webhook dispatch while account._sessionGate is held;
            // AuditAccountAsync enqueues sideEffects.PendingModerationAlert after releasing the gate.
            var (probedBan, probedBadge, probedReason) = await RobloxModerationService.Instance
                .CheckModerationStatusAsync(
                    account,
                    account.SecurityToken,
                    TestHttpClientFactory != null ? client : null,
                    ct,
                    dispatchAlert: false)
                .ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!probedBan)
                return (currentStatus, null);

            if (!string.IsNullOrEmpty(probedBadge))
                account.AccountStatusIssue = probedBadge;
            return (AccountSessionStatus.Moderated, probedReason);
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
