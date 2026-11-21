#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public sealed partial class RobloxAccountSessionClient
    {
        public async Task<bool> ValidateSessionAsync(Account account, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(account);
            ct.ThrowIfCancellationRequested();
            return await account.RunWithSessionGateAsync(
                () => ValidateSessionCoreAsync(account, ct),
                ct).ConfigureAwait(false);
        }

        private async Task<bool> ValidateSessionCoreAsync(Account account, CancellationToken ct)
        {
            string initialToken = account.GetCurrentSecurityToken();
            if (string.IsNullOrWhiteSpace(initialToken))
            {
                if (!account.IsBannedOrLocked)
                    account.AccountStatusIssue = string.Empty;
                account.Valid = false;
                return false;
            }

            for (int attempt = 0; attempt < 2; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    int versionBefore = account.CookieRotationVersion;
                    HttpClient httpClient = GetSessionHttpClient(account);
                    using var request = CreateAuthenticatedGetRequest(account, "https://users.roblox.com/v1/users/authenticated");

                    await RobloxRateLimitCoordinator.Instance.WaitForBackoffAsync(RateLimitCategory.SessionCheck, ct).ConfigureAwait(false);

                    using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    if (response == null)
                        return account.Valid;

                    bool rotatedOnPrimary = TryUpdateRotatedCookie(account, response) || account.CookieRotationVersion != versionBefore;
                    if (attempt == 0 && rotatedOnPrimary &&
                        (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden))
                    {
                        continue;
                    }

                    if (response.RequestMessage?.RequestUri != null)
                    {
                        string absUri = response.RequestMessage.RequestUri.AbsoluteUri;
                        string host = response.RequestMessage.RequestUri.Host;
                        bool isRobloxHost = host.Equals("roblox.com", StringComparison.OrdinalIgnoreCase) ||
                                            host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase);
                        if (!isRobloxHost)
                        {
                            Logger?.Warn($"[ValidateSessionAsync] {account.Username}: ISP redirected API to external host ({absUri}). Preserving session validity: {account.Valid}");
                            return account.Valid;
                        }

                        if (absUri.IndexOf("/login", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            absUri.IndexOf("NewLogin", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            Logger?.Warn($"[ValidateSessionAsync] {account.Username}: Redirected to login ({absUri}). Cookie expired.");
                            if (!account.IsBannedOrLocked)
                                account.AccountStatusIssue = string.Empty;
                            account.Valid = false;
                            return false;
                        }
                    }

                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        if (await ValidateViaSecondaryEndpointAsync(account, ct).ConfigureAwait(false))
                        {
                            Logger?.Debug($"[ValidateSessionAsync] {account.Username}: users/authenticated returned 401 but secondary endpoint confirmed the session. Keeping it valid.");
                            account.AccountStatusIssue = string.Empty;
                            account.Valid = true;
                            return true;
                        }

                        var (modBanned, modBadge, _) = await RobloxModerationService.Instance
                            .CheckModerationStatusAsync(account, account.GetCurrentSecurityToken(), ct)
                            .ConfigureAwait(false);
                        if (modBanned)
                        {
                            account.AccountStatusIssue = modBadge ?? LocalizationService.Get("FastLogin.LockedOrBannedBadge");
                            account.Valid = false;
                            return false;
                        }

                        Logger?.Warn($"[ValidateSessionAsync] {account.Username}: Received 401 Unauthorized. Cookie expired.");
                        if (!account.IsBannedOrLocked)
                            account.AccountStatusIssue = string.Empty;
                        account.Valid = false;
                        return false;
                    }

                    if (response.StatusCode == HttpStatusCode.OK)
                        return await HandleOkValidationResponseAsync(account, response, ct).ConfigureAwait(false);

                    if ((int)response.StatusCode == 429)
                    {
                        RobloxRateLimitCoordinator.Instance.RecordRateLimitHit(RateLimitCategory.SessionCheck, response);
                        if (attempt == 0)
                        {
                            await RobloxRateLimitCoordinator.Instance.WaitForBackoffAsync(RateLimitCategory.SessionCheck, ct).ConfigureAwait(false);
                            continue;
                        }
                        if (await ValidateViaSecondaryEndpointAsync(account, ct).ConfigureAwait(false))
                        {
                            account.AccountStatusIssue = string.Empty;
                            account.Valid = true;
                            account.IsDirty = true;
                            return true;
                        }
                        Logger?.Warn($"[ValidateSessionAsync] {account.Username}: Rate limited (HTTP 429). Preserving existing session validity: {account.Valid}");
                        return account.Valid;
                    }

                    if (response.StatusCode == HttpStatusCode.Forbidden)
                        return await HandleForbiddenValidationResponseAsync(account, response, ct).ConfigureAwait(false);

                    if ((int)response.StatusCode >= 500 ||
                        response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired ||
                        response.StatusCode == HttpStatusCode.RequestTimeout)
                    {
                        Logger?.Warn($"[ValidateSessionAsync] {account.Username}: Server returned HTTP {(int)response.StatusCode} {response.StatusCode}. Roblox service is temporarily degraded. Preserving session validity: {account.Valid}");
                        return account.Valid;
                    }

                    if (!account.IsBannedOrLocked)
                        account.AccountStatusIssue = string.Empty;
                    account.Valid = false;
                    return false;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    return account.Valid;
                }
                catch (Exception ex)
                {
                    Logger?.Warn($"[ValidateSessionAsync] {account.Username}: Network/connection error: {ex.Message}");
                    if (attempt == 0)
                    {
                        await Task.Delay(500, ct).ConfigureAwait(false);
                        continue;
                    }
                    Logger?.Warn($"[ValidateSessionAsync] {account.Username}: Network unreachable after retries. Preserving session validity: {account.Valid}");
                    return account.Valid;
                }
            }

            return account.Valid;
        }

        private async Task<bool> HandleOkValidationResponseAsync(Account account, HttpResponseMessage response, CancellationToken ct)
        {
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            string trimmedBody = body?.TrimStart() ?? string.Empty;
            if (trimmedBody.StartsWith("<", StringComparison.Ordinal))
            {
                Logger?.Warn($"[ValidateSessionAsync] {account.Username}: Received HTML block/captive page instead of JSON for 200 OK. Preserving validity: {account.Valid}");
                return account.Valid;
            }
            if (string.IsNullOrWhiteSpace(trimmedBody))
            {
                if (!account.IsBannedOrLocked)
                    account.AccountStatusIssue = string.Empty;
                account.Valid = false;
                return false;
            }

            RobloxAuthenticatedUser? authUser = null;
            if (FastJson.TryDeserialize<RobloxAuthenticatedUser>(trimmedBody, out var parsedUser) && parsedUser?.Id > 0)
            {
                authUser = parsedUser;
            }
            else if (!string.IsNullOrWhiteSpace(trimmedBody))
            {
                try
                {
                    using var uDoc = JsonDocument.Parse(trimmedBody);
                    if (uDoc.RootElement.TryGetProperty("id", out var idProp))
                    {
                        authUser = new RobloxAuthenticatedUser
                        {
                            Id = idProp.GetInt64(),
                            Name = (uDoc.RootElement.TryGetProperty("name", out var nProp) ? nProp.GetString() : null) ?? string.Empty
                        };
                    }
                }
                catch (Exception ex)
                {
                    Logger?.Debug($"Parse authenticated user JSON failed: {ex.Message}");
                }
            }

            if (authUser != null && authUser.Id > 0)
            {
                long authId = authUser.Id;
                if (account.UserID > 0 && authId != account.UserID)
                {
                    Logger?.Warn($"[ValidateSessionAsync] {account.Username}: UserID mismatch (Expected: {account.UserID}, Got: {authId}). Cookie expired or belongs to different user.");
                    account.AccountStatusIssue = string.Empty;
                    account.Valid = false;
                    return false;
                }

                if (account.UserID <= 0)
                    account.UserID = authId;

                if (!string.IsNullOrEmpty(authUser.Name) && string.IsNullOrEmpty(account.Username))
                    account.Username = authUser.Name;

                var liveSessionTask = VerifyLiveSessionStoreAsync(account, ct);
                var moderationTask = RobloxModerationService.Instance
                    .CheckModerationStatusAsync(account, account.GetCurrentSecurityToken(), ct);
                await Task.WhenAll(liveSessionTask, moderationTask).ConfigureAwait(false);

                var (isBanned, issueBadge, reason) = await moderationTask.ConfigureAwait(false);
                if (isBanned)
                {
                    string badge = issueBadge ?? LocalizationService.Get("FastLogin.LockedOrBannedBadge");
                    Logger?.Warn($"[ValidateSessionAsync] {account.Username}: Active moderation detected: {badge} ({reason}). Marking invalid.");
                    account.AccountStatusIssue = badge;
                    account.Valid = false;
                    return false;
                }

                bool liveSessionOk = await liveSessionTask.ConfigureAwait(false);
                if (!liveSessionOk)
                {
                    liveSessionOk = await ValidateViaSecondaryEndpointAsync(account, ct).ConfigureAwait(false);
                    if (liveSessionOk)
                        Logger?.Debug($"[ValidateSessionAsync] {account.Username}: accountsettings returned 401 but secondary endpoint confirmed the session. Keeping it valid.");
                }
                if (!liveSessionOk)
                {
                    Logger?.Warn($"[ValidateSessionAsync] {account.Username}: Live session store and secondary endpoint both returned 401. Session expired or IP-locked.");
                    if (!account.IsBannedOrLocked)
                        account.AccountStatusIssue = string.Empty;
                    account.Valid = false;
                    return false;
                }

                account.AccountStatusIssue = string.Empty;
                account.Valid = true;
                RobloxRateLimitCoordinator.Instance.RecordSuccess(RateLimitCategory.SessionCheck);
                return true;
            }

            if (!account.IsBannedOrLocked)
                account.AccountStatusIssue = string.Empty;
            account.Valid = false;
            return false;
        }

        private async Task<bool> HandleForbiddenValidationResponseAsync(Account account, HttpResponseMessage response, CancellationToken ct)
        {
            string forbiddenBody = string.Empty;
            try
            {
                forbiddenBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger?.Debug($"[ValidateSessionAsync] Reading 403 body failed: {ex.Message}");
            }

            var (modBanned, modBadge, modReason) = await RobloxModerationService.Instance
                .CheckModerationStatusAsync(account, account.GetCurrentSecurityToken(), ct)
                .ConfigureAwait(false);
            if (modBanned)
            {
                string badge = modBadge ?? LocalizationService.Get("FastLogin.LockedOrBannedBadge");
                Logger?.Warn($"[ValidateSessionAsync] {account.Username}: Active moderation detected on 403: {badge} ({modReason}). Marking invalid.");
                account.AccountStatusIssue = badge;
                account.Valid = false;
                return false;
            }

            if (!string.IsNullOrWhiteSpace(forbiddenBody))
            {
                string forbiddenCode = FastLoginCoordinator.ClassifyAuthOrModerationApiPayload(forbiddenBody, 403);
                if (!string.IsNullOrEmpty(forbiddenCode) &&
                    forbiddenCode != "UNAUTHENTICATED_MODERATION" &&
                    forbiddenCode != "WRONG_PASSWORD")
                {
                    var (fbBadge, fbReason) = FastLoginCoordinator.MapDomIssueCode(forbiddenCode);
                    string resolvedBadge = !string.IsNullOrEmpty(account.AccountStatusIssue)
                        ? account.AccountStatusIssue
                        : (fbBadge ?? LocalizationService.Get("FastLogin.LockedOrBannedBadge"));
                    Logger?.Warn($"[ValidateSessionAsync] {account.Username}: 403 payload indicates moderation ({resolvedBadge}: {fbReason}). Marking invalid.");
                    account.AccountStatusIssue = resolvedBadge;
                    account.Valid = false;
                    return false;
                }
            }

            if (await ValidateViaSecondaryEndpointAsync(account, ct).ConfigureAwait(false))
            {
                account.AccountStatusIssue = string.Empty;
                account.Valid = true;
                account.IsDirty = true;
                return true;
            }
            Logger?.Warn($"[ValidateSessionAsync] {account.Username}: HTTP 403 Forbidden received. Preserving session validity: {account.Valid}");
            return account.Valid;
        }

        private async Task<bool> VerifyLiveSessionStoreAsync(Account account, CancellationToken ct)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    linkedCts.CancelAfter(TimeSpan.FromSeconds(5));

                    int versionBefore = account.CookieRotationVersion;
                    HttpClient httpClient = GetSessionHttpClient(account);
                    using var request = CreateAuthenticatedGetRequest(account, "https://accountsettings.roblox.com/v1/email");

                    using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token).ConfigureAwait(false);
                    if (response == null)
                        return true;

                    TryUpdateRotatedCookie(account, response);

                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        if (attempt == 0 && account.CookieRotationVersion != versionBefore) continue;
                        return false;
                    }
                    return true;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return true; }
                catch (Exception ex) { Logger?.Debug("[RobloxAccountSessionClient] VerifyLiveSessionStore failed", ex); return true; }
            }
            return false;
        }

        private Task<bool> ValidateViaSecondaryEndpointAsync(Account account, CancellationToken ct) =>
            SessionAuditorService.ValidateSecondaryWithRotationAsync(account, () => GetSessionHttpClient(account), ct);
    }
}
