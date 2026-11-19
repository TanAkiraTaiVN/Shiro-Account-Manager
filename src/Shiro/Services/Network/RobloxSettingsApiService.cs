#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Http;
using Shiro.Services;

namespace Shiro.Services
{
    public sealed partial class RobloxSettingsApiService
    {
        private static readonly Lazy<RobloxSettingsApiService> _defaultInstance = new(() => new RobloxSettingsApiService(AppServices.Logger, AppServices.GeneralSettings));
        public static RobloxSettingsApiService Default => _defaultInstance.Value;
        public static RobloxSettingsApiService Instance => Default;

        private readonly IAppLogger _logger;
        private readonly ISettingsService _settings;

        public RobloxSettingsApiService() : this(AppServices.Logger, AppServices.GeneralSettings) { }

        public RobloxSettingsApiService(IAppLogger logger, ISettingsService? settings = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? AppServices.GeneralSettings;
        }

        [GeneratedRegex(@"Roblox\.GameLauncher\.joinPrivateGame\(\d+\,\s*'(\w+\-\w+\-\w+\-\w+\-\w+)'")]
        private static partial Regex JoinPrivateGameRegex();

        [GeneratedRegex(@"""(?:accessCode|gameInstanceId)""\s*:\s*""([a-zA-Z0-9\-]{20,})""", RegexOptions.IgnoreCase)]
        private static partial Regex ModernAccessCodeRegex();

        [GeneratedRegex(@"""joinScriptUrl"":\s*null")]
        private static partial Regex JoinScriptNullRegex();

        private bool HasValidSession(Account? account, [CallerMemberName] string caller = "")
        {
            if (account == null || string.IsNullOrWhiteSpace(account.SecurityToken))
            {
                if (account != null)
                {
                    _logger.Warn($"[{caller}] {account.Username}: Invalid Account Session.");
                }
                return false;
            }
            return true;
        }

        public async Task<bool> SetFollowPrivacyAsync(Account account, int privacy, CancellationToken cancellationToken = default)
        {
            if (!HasValidSession(account)) return false;
            var csrf = await account.GetCSRFTokenAsync(cancellationToken).ConfigureAwait(false);
            if (!csrf.Success) return false;

            string modernSetting = privacy switch
            {
                0 => "AllUsers",
                1 => "FriendsFollowingAndFollowers",
                2 => "FriendsAndFollowing",
                3 => "Friends",
                _ => "NoOne"
            };

            RestResponse? modernResponse = await RobloxCsrfRetryHelper.ExecuteWithCsrfRetryAsync(
                account,
                AppServices.RobloxApi.Api,
                "https://apis.roblox.com/",
                csrf.Token,
                tok => account.MakeRequest("user-settings-api/v1/user-settings", Method.Post)
                    .AddHeader("Referer", "https://www.roblox.com/")
                    .AddHeader("X-CSRF-TOKEN", tok)
                    .AddJsonBody(new FollowPrivacySettingPayload(modernSetting)),
                cancellationToken).ConfigureAwait(false);
            if (modernResponse != null && modernResponse.IsSuccessful)
                return true;

            string legacyFollowValue = privacy switch
            {
                0 => "All",
                1 => "Followers",
                2 => "Following",
                3 => "Friends",
                4 => "NoOne",
                _ => string.Empty
            };

            RestResponse? response = await RobloxCsrfRetryHelper.ExecuteWithCsrfRetryAsync(
                account,
                AppServices.RobloxApi.Main,
                "https://www.roblox.com/",
                initialCsrfToken: null,
                tok =>
                {
                    RestRequest req = account.MakeRequest("account/settings/follow-me-privacy", Method.Post)
                        .AddHeader("Referer", "https://www.roblox.com/my/account")
                        .AddHeader("X-CSRF-TOKEN", tok)
                        .AddHeader("Content-Type", "application/x-www-form-urlencoded");
                    if (!string.IsNullOrEmpty(legacyFollowValue))
                        req.AddParameter("FollowMePrivacy", legacyFollowValue);
                    return req;
                },
                cancellationToken).ConfigureAwait(false);
            if (response == null || !response.IsSuccessful || response.StatusCode != HttpStatusCode.OK)
            {
                _logger.Warn($"[SetFollowPrivacyAsync] {account.Username}: Failed to set follow privacy ({response?.StatusCode}).");
                return false;
            }
            return true;
        }

        public async Task<bool> ChangePasswordAsync(Account account, string currentPassword, string newPassword, CancellationToken cancellationToken = default)
        {
            if (!HasValidSession(account)) return false;
            var csrf = await account.GetCSRFTokenAsync(cancellationToken).ConfigureAwait(false);
            if (!csrf.Success) return false;

            int rotationBefore = account.CookieRotationVersion;
            RestResponse? response = await RobloxCsrfRetryHelper.ExecuteWithCsrfRetryAsync(
                account,
                AppServices.RobloxApi.Auth,
                "https://auth.roblox.com/",
                csrf.Token,
                tok => account.MakeRequest("v2/user/passwords/change", Method.Post)
                    .AddHeader("Referer", "https://www.roblox.com/")
                    .AddHeader("X-CSRF-TOKEN", tok)
                    .AddHeader("Content-Type", "application/x-www-form-urlencoded")
                    .AddParameter("currentPassword", currentPassword)
                    .AddParameter("newPassword", newPassword),
                cancellationToken).ConfigureAwait(false);

            if (response != null && response.IsSuccessful && response.StatusCode == HttpStatusCode.OK)
            {
                bool rotated = account.CookieRotationVersion != rotationBefore || account.TryUpdateRotatedCookie(response);
                if (rotated)
                {
                    account.Password = newPassword;
                    await AppServices.AccountRepository.SaveAsync(true).ConfigureAwait(false);
                    _logger.Info($"[ChangePasswordAsync] {account.Username}: Password changed successfully.");
                    return true;
                }

                _logger.Warn($"[ChangePasswordAsync] {account.Username}: Password change skipped/failed because no replacement .ROBLOSECURITY cookie was captured.");
                return false;
            }

            _logger.Warn($"[ChangePasswordAsync] {account.Username}: Failed to change password ({response?.StatusCode}).");
            return false;
        }

        public async Task<bool> ChangeEmailAsync(Account account, string password, string newEmail, CancellationToken cancellationToken = default)
        {
            if (!HasValidSession(account)) return false;
            var csrf = await account.GetCSRFTokenAsync(cancellationToken).ConfigureAwait(false);
            if (!csrf.Success) return false;

            RestResponse? response = await RobloxCsrfRetryHelper.ExecuteWithCsrfRetryAsync(
                account,
                AppServices.RobloxApi.Account,
                "https://accountsettings.roblox.com/",
                csrf.Token,
                tok => account.MakeRequest("v1/email", Method.Post)
                    .AddHeader("Referer", "https://www.roblox.com/")
                    .AddHeader("X-CSRF-TOKEN", tok)
                    .AddHeader("Content-Type", "application/x-www-form-urlencoded")
                    .AddParameter("password", password)
                    .AddParameter("emailAddress", newEmail),
                cancellationToken).ConfigureAwait(false);

            if (response != null && response.IsSuccessful && response.StatusCode == HttpStatusCode.OK)
            {
                _logger.Info($"[ChangeEmailAsync] {account.Username}: Email changed successfully.");
                return true;
            }

            _logger.Warn($"[ChangeEmailAsync] {account.Username}: Failed to change email ({response?.StatusCode}).");
            return false;
        }

        public async Task<bool> LogOutOfOtherSessionsAsync(Account account, bool Internal = false, CancellationToken cancellationToken = default)
        {
            if (!HasValidSession(account)) return false;
            var csrf = await account.GetCSRFTokenAsync(cancellationToken).ConfigureAwait(false);
            if (!csrf.Success) return false;

            int rotationBefore = account.CookieRotationVersion;
            RestResponse? response = await RobloxCsrfRetryHelper.ExecuteWithCsrfRetryAsync(
                account,
                AppServices.RobloxApi.Auth,
                "https://auth.roblox.com/",
                csrf.Token,
                tok => account.MakeRequest("v1/logoutfromallsessionsandreauthenticate", Method.Post)
                    .AddHeader("Referer", "https://www.roblox.com/")
                    .AddHeader("X-CSRF-TOKEN", tok)
                    .AddJsonBody(new EmptyJsonObjectPayload()),
                cancellationToken).ConfigureAwait(false);

            if (response == null || !response.IsSuccessful)
            {
                response = await RobloxCsrfRetryHelper.ExecuteWithCsrfRetryAsync(
                    account,
                    AppServices.RobloxApi.Main,
                    "https://www.roblox.com/",
                    initialCsrfToken: null,
                    tok => account.MakeRequest("authentication/signoutfromallsessionsandreauthenticate", Method.Post)
                        .AddHeader("Referer", "https://www.roblox.com/")
                        .AddHeader("X-CSRF-TOKEN", tok)
                        .AddHeader("Content-Type", "application/x-www-form-urlencoded"),
                    cancellationToken).ConfigureAwait(false);
            }

            if (response != null && response.IsSuccessful && response.StatusCode == HttpStatusCode.OK)
            {
                bool rotated = account.CookieRotationVersion != rotationBefore || account.TryUpdateRotatedCookie(response);
                if (rotated)
                {
                    await AppServices.AccountRepository.SaveAsync(true).ConfigureAwait(false);
                    _logger.Info($"[LogOutOfOtherSessionsAsync] {account.Username}: Signed out of all other sessions.");
                    return true;
                }
                else
                {
                    _logger.Warn($"[LogOutOfOtherSessionsAsync] Roblox did not issue a replacement .ROBLOSECURITY cookie for {account.Username} during session signout.");
                    return false;
                }
            }

            _logger.Warn($"[LogOutOfOtherSessionsAsync] {account.Username}: Failed to log out of other sessions ({response?.StatusCode}).");
            return false;
        }

        public static bool ParseAccessCode(RestResponse response, out string code)
        {
            code = "";
            if (string.IsNullOrWhiteSpace(response?.Content)) return false;

            Match match = JoinPrivateGameRegex().Match(response.Content);
            if (match.Success && match.Groups.Count == 2)
            {
                code = match.Groups[1]?.Value ?? string.Empty;
                return true;
            }

            Match modernMatch = ModernAccessCodeRegex().Match(response.Content);
            if (modernMatch.Success && modernMatch.Groups.Count == 2)
            {
                code = modernMatch.Groups[1]?.Value ?? string.Empty;
                return true;
            }

            return false;
        }

        public async Task<(bool Successful, string Message)> SetServerAsync(Account account, long placeId, string jobId, CancellationToken cancellationToken = default)
        {
            if (account == null) return (false, "Account is null");
            var csrf = await account.GetCSRFTokenAsync(cancellationToken).ConfigureAwait(false);
            if (!csrf.Success || string.IsNullOrEmpty(csrf.Token))
                return (false, $"ERROR: Account Session Expired, re-add the account or try again. (Invalid X-CSRF-Token)\n{csrf.Error}");

            RestResponse? response = await RobloxCsrfRetryHelper.ExecuteWithCsrfRetryAsync(
                account,
                AppServices.RobloxApi.GameJoin,
                "https://gamejoin.roblox.com/",
                csrf.Token,
                tok => account.MakeRequest("v1/join-game-instance", Method.Post)
                    .AddHeader("Content-Type", "application/json")
                    .AddHeader("X-CSRF-TOKEN", tok)
                    .AddJsonBody(new JoinGameInstancePayload(jobId, placeId)),
                cancellationToken).ConfigureAwait(false);

            if (response != null && response.StatusCode == HttpStatusCode.OK)
            {
                string msg = !string.IsNullOrEmpty(response.Content) && JoinScriptNullRegex().IsMatch(response.Content) ? response.Content : "Success";
                return (true, msg);
            }
            else
                return (false, $"Failed {response?.StatusCode}: {response?.Content} {response?.ErrorMessage}");
        }

        public async Task SetDisplayNameAsync(Account account, string displayName, CancellationToken cancellationToken = default)
        {
            if (!HasValidSession(account)) return;
            var csrf = await account.GetCSRFTokenAsync(cancellationToken).ConfigureAwait(false);
            if (!csrf.Success) throw new InvalidOperationException(csrf.Error ?? "Failed to acquire CSRF token");

            RestResponse? dpResponse = await RobloxCsrfRetryHelper.ExecuteWithCsrfRetryAsync(
                account,
                AppServices.RobloxApi.Users,
                "https://users.roblox.com/",
                csrf.Token,
                tok => account.MakeRequest($"/v1/users/{account.UserID}/display-names", Method.Patch)
                    .AddHeader("X-CSRF-TOKEN", tok)
                    .AddJsonBody(new SetDisplayNamePayload(displayName)),
                cancellationToken).ConfigureAwait(false);

            if (dpResponse == null || dpResponse.StatusCode != HttpStatusCode.OK)
            {
                string? errMsg = null;
                try
                {
                    using var errDoc = JsonDocument.Parse(dpResponse?.Content ?? "{}");
                    if (errDoc.RootElement.TryGetProperty("errors", out var errArr) && errArr.GetArrayLength() > 0)
                        errMsg = errArr[0].TryGetProperty("message", out var msgProp) ? msgProp.GetString() : null;
                }
                catch (JsonException ex)
                {
                    _logger.Debug($"Parse error message JSON failed: {ex.Message}");
                }
                throw new Exception(errMsg ?? $"Something went wrong\n{dpResponse?.StatusCode}: {dpResponse?.Content}");
            }
        }

        public async Task<bool> SetAvatarAsync(Account account, string avatarJsonData, CancellationToken cancellationToken = default)
        {
            if (!HasValidSession(account) || string.IsNullOrWhiteSpace(avatarJsonData)) return false;

            if (!AvatarV4PayloadMapper.TryMapPayload(avatarJsonData, out AvatarV4PatchRequest patchRequest, out string? parseError))
            {
                if (parseError != null)
                {
                    _logger.Warn($"SetAvatarAsync: invalid JSON payload: {parseError}");
                }
                return false;
            }

            var csrf = await account.GetCSRFTokenAsync(cancellationToken).ConfigureAwait(false);
            if (!csrf.Success)
            {
                _logger.Warn($"[SetAvatarAsync] {account.Username}: Failed to acquire CSRF token: {csrf.Error}");
                return false;
            }

            string requestJson = JsonSerializer.Serialize(patchRequest, ShiroAppJson.Default.AvatarV4PatchRequest);
            RestResponse? response = await RobloxCsrfRetryHelper.ExecuteWithCsrfRetryAsync(
                account,
                AppServices.RobloxApi.Avatar,
                "https://avatar.roblox.com/",
                csrf.Token,
                tok => account.MakeRequest("v4/avatar", Method.Patch)
                    .AddHeader("X-CSRF-TOKEN", tok)
                    .AddStringBody(requestJson, "application/json"),
                cancellationToken).ConfigureAwait(false);
            bool succeeded = response is { IsSuccessful: true };
            if (response != null)
            {
                account.TryUpdateRotatedCookie(response);
                long[] missingIds = AvatarV4PayloadMapper.ExtractInvalidAssetIds(response.Content);
                if (missingIds.Length > 0)
                {
                    AppServices.Dialogs.OpenMissingAssetsDialog(account, missingIds);
                }
            }

            if (!succeeded)
                _logger.Warn($"[SetAvatarAsync] {account.Username}: Avatar update failed ({response?.StatusCode}).");

            return succeeded;
        }

        internal async Task<bool> EnsurePrivateServerInvitesAllowedAsync(
            Account account,
            string csrfToken,
            Func<Task<bool>> confirmPrivacyUpdateAsync,
            CancellationToken cancellationToken = default)
        {
            var request = account.MakeRequest("user-settings-api/v1/user-settings", Method.Get)
                .AddHeader("X-CSRF-TOKEN", csrfToken)
                .AddHeader("Referer", "https://www.roblox.com/my/account");

            RestResponse? result = await account.ExecuteAccountRestAsync(AppServices.RobloxApi.Api, "https://apis.roblox.com/", request, cancellationToken).ConfigureAwait(false);
            if (!NeedsPrivateServerInviteUpdate(result))
                return true;

            if (!await confirmPrivacyUpdateAsync().ConfigureAwait(false))
                return true;

            RestResponse? setResponse = await RobloxCsrfRetryHelper.ExecuteWithCsrfRetryAsync(
                account,
                AppServices.RobloxApi.Api,
                "https://apis.roblox.com/",
                csrfToken,
                tok => account.MakeRequest("user-settings-api/v1/user-settings", Method.Post)
                    .AddHeader("X-CSRF-TOKEN", tok)
                    .AddHeader("Referer", "https://www.roblox.com/my/account")
                    .AddJsonBody(new PrivateServerInviteSettingPayload("AllUsers")),
                cancellationToken).ConfigureAwait(false);
            return setResponse != null && setResponse.IsSuccessful;
        }

        private bool NeedsPrivateServerInviteUpdate(RestResponse? result)
        {
            if (result == null || !result.IsSuccessful || string.IsNullOrEmpty(result.Content))
                return false;

            try
            {
                using var doc = JsonDocument.Parse(result.Content);
                return doc.RootElement.TryGetProperty("whoCanInviteMeToPrivateServers", out var prop)
                    && prop.ValueKind == JsonValueKind.String
                    && !string.Equals(prop.GetString(), "AllUsers", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _logger.Debug($"Parse private server privacy settings failed: {ex.Message}");
                return false;
            }
        }
    }
}
