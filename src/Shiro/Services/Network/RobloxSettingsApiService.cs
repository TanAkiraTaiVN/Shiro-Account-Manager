#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;
using Shiro.Http;

namespace Shiro
{
    public class RobloxPinInfo
    {
        [JsonPropertyName("isEnabled")]
        public bool isEnabled { get; set; }

        [JsonPropertyName("unlockedUntil")]
        public int? unlockedUntil { get; set; }
    }
}

namespace Shiro.Services
{
    public sealed partial class RobloxSettingsApiService
    {
        public static RobloxSettingsApiService Instance { get; } = new();

        [GeneratedRegex(@"Roblox\.GameLauncher\.joinPrivateGame\(\d+\,\s*'(\w+\-\w+\-\w+\-\w+\-\w+)'")]
        private static partial Regex JoinPrivateGameRegex();

        [GeneratedRegex(@"""(?:accessCode|gameInstanceId)""\s*:\s*""([a-zA-Z0-9\-]{20,})""", RegexOptions.IgnoreCase)]
        private static partial Regex ModernAccessCodeRegex();

        [GeneratedRegex(@"""joinScriptUrl"":[%s+]?null")]
        private static partial Regex JoinScriptNullRegex();

        public async Task<bool> CheckPinAsync(Account account, bool Internal = false, CancellationToken cancellationToken = default)
        {
            if (account == null) return false;
            if (DateTime.Now < account.PinUnlocked)
                return true;

            if (string.IsNullOrWhiteSpace(account.SecurityToken))
            {
                if (!Internal)
                    Program.Logger.Warn($"[CheckPinAsync] {account.Username}: Invalid Account Session.");
                return false;
            }

            RestRequest request = account.MakeRequest("v1/account/pin/", Method.Get).AddHeader("Referer", "https://www.roblox.com/");
            RestResponse? response = await account.ExecuteAccountRestAsync(AppServices.RobloxApi.Auth, "https://auth.roblox.com/", request, cancellationToken).ConfigureAwait(false);

            if (response != null && response.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (!Internal)
                    Program.Logger.Warn($"[CheckPinAsync] {account.Username}: Invalid Account Session.");
                return false;
            }

            if (response != null && response.IsSuccessful && response.StatusCode == HttpStatusCode.OK &&
                response.Content != null &&
                Utilities.FastJson.TryDeserialize<RobloxPinInfo>(response.Content, out var pinInfo) && pinInfo != null)
            {
                bool isEnabled = pinInfo.isEnabled;
                int unlockedUntil = pinInfo.unlockedUntil ?? 0;

                if (isEnabled && unlockedUntil <= 0)
                {
                    if (!Internal)
                    {
                        Program.Logger.Warn($"[CheckPinAsync] {account.Username}: Pin required for operation.");
                    }
                    return false;
                }

                if (!isEnabled)
                {
                    account.PinUnlocked = DateTime.Now.AddMinutes(15);
                }
                else if (unlockedUntil > 0)
                {
                    account.PinUnlocked = DateTime.Now.AddSeconds(unlockedUntil);
                }
            }

            return true;
        }

        public async Task<bool> UnlockPinAsync(Account account, string pin, CancellationToken cancellationToken = default)
        {
            if (account == null || pin.Length != 4) return false;
            if (await CheckPinAsync(account, true, cancellationToken).ConfigureAwait(false)) return true;

            var csrf = await account.GetCSRFTokenAsync(cancellationToken).ConfigureAwait(false);
            if (!csrf.Success) return false;

            RestRequest request = account.MakeRequest("v1/account/pin/unlock", Method.Post)
                .AddHeader("Referer", "https://www.roblox.com/")
                .AddHeader("X-CSRF-TOKEN", csrf.Token)
                .AddJsonBody(new { pin });

            RestResponse? response = await account.ExecuteAccountRestAsync(AppServices.RobloxApi.Auth, "https://auth.roblox.com/", request, cancellationToken).ConfigureAwait(false);

            if (response != null && response.IsSuccessful && response.StatusCode == HttpStatusCode.OK &&
                response.Content != null &&
                Utilities.FastJson.TryDeserialize<RobloxPinInfo>(response.Content, out var pinInfo) && pinInfo != null)
            {
                bool isEnabled = pinInfo.isEnabled;
                int unlockedUntil = pinInfo.unlockedUntil ?? 0;

                if (isEnabled && unlockedUntil > 0)
                    account.PinUnlocked = DateTime.Now.AddSeconds(unlockedUntil);
                else if (!isEnabled)
                    account.PinUnlocked = DateTime.Now.AddMinutes(15);

                if (account.PinUnlocked > DateTime.Now)
                {
                    Program.Logger?.Info($"[UnlockPinAsync] {account.Username}: Pin unlocked for 5 minutes.");
                    return true;
                }
            }

            return false;
        }

        public async Task<bool> SetFollowPrivacyAsync(Account account, int privacy, CancellationToken cancellationToken = default)
        {
            if (account == null || !await CheckPinAsync(account, false, cancellationToken).ConfigureAwait(false)) return false;
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

            RestRequest modernRequest = account.MakeRequest("user-settings-api/v1/user-settings", Method.Post)
                .AddHeader("Referer", "https://www.roblox.com/")
                .AddHeader("X-CSRF-TOKEN", csrf.Token)
                .AddJsonBody(new { whoCanJoinMeInExperiences = modernSetting });

            RestResponse? modernResponse = await account.ExecuteAccountRestAsync(AppServices.RobloxApi.Api, "https://apis.roblox.com/", modernRequest, cancellationToken).ConfigureAwait(false);
            if (modernResponse != null && modernResponse.IsSuccessful)
                return true;

            RestRequest request = account.MakeRequest("account/settings/follow-me-privacy", Method.Post)
                .AddHeader("Referer", "https://www.roblox.com/my/account")
                .AddHeader("X-CSRF-TOKEN", csrf.Token)
                .AddHeader("Content-Type", "application/x-www-form-urlencoded");

            switch (privacy)
            {
                case 0:
                    request.AddParameter("FollowMePrivacy", "All");
                    break;
                case 1:
                    request.AddParameter("FollowMePrivacy", "Followers");
                    break;
                case 2:
                    request.AddParameter("FollowMePrivacy", "Following");
                    break;
                case 3:
                    request.AddParameter("FollowMePrivacy", "Friends");
                    break;
                case 4:
                    request.AddParameter("FollowMePrivacy", "NoOne");
                    break;
            }

            RestResponse? response = await account.ExecuteAccountRestAsync(AppServices.RobloxApi.Main, "https://www.roblox.com/", request, cancellationToken).ConfigureAwait(false);
            return response?.IsSuccessful == true && response.StatusCode == HttpStatusCode.OK;
        }

        public async Task<bool> ChangePasswordAsync(Account account, string currentPassword, string newPassword, CancellationToken cancellationToken = default)
        {
            if (account == null || !await CheckPinAsync(account, false, cancellationToken).ConfigureAwait(false)) return false;
            var csrf = await account.GetCSRFTokenAsync(cancellationToken).ConfigureAwait(false);
            if (!csrf.Success) return false;

            RestRequest request = account.MakeRequest("v2/user/passwords/change", Method.Post)
                .AddHeader("Referer", "https://www.roblox.com/")
                .AddHeader("X-CSRF-TOKEN", csrf.Token)
                .AddHeader("Content-Type", "application/x-www-form-urlencoded")
                .AddParameter("currentPassword", currentPassword)
                .AddParameter("newPassword", newPassword);

            int rotationBefore = account.CookieRotationVersion;
            RestResponse? response = await account.ExecuteAccountRestAsync(AppServices.RobloxApi.Auth, "https://auth.roblox.com/", request, cancellationToken).ConfigureAwait(false);

            if (response != null && response.IsSuccessful && response.StatusCode == HttpStatusCode.OK)
            {
                account.Password = newPassword;

                bool rotated = account.CookieRotationVersion != rotationBefore || account.TryUpdateRotatedCookie(response);
                if (rotated)
                {
                    await AppServices.AccountRepository.SaveAsync(true).ConfigureAwait(false);
                }
                else
                {
                    Program.Logger?.Warn($"[ChangePasswordAsync] {account.Username}: Password changed, but no new .ROBLOSECURITY was captured.");
                }

                Program.Logger?.Info($"[ChangePasswordAsync] {account.Username}: Password changed successfully.");
                return true;
            }

            Program.Logger?.Warn($"[ChangePasswordAsync] {account.Username}: Failed to change password ({response?.StatusCode}).");
            return false;
        }

        public async Task<bool> ChangeEmailAsync(Account account, string password, string newEmail, CancellationToken cancellationToken = default)
        {
            if (account == null || !await CheckPinAsync(account, false, cancellationToken).ConfigureAwait(false)) return false;
            var csrf = await account.GetCSRFTokenAsync(cancellationToken).ConfigureAwait(false);
            if (!csrf.Success) return false;

            RestRequest request = account.MakeRequest("v1/email", Method.Post)
                .AddHeader("Referer", "https://www.roblox.com/")
                .AddHeader("X-CSRF-TOKEN", csrf.Token)
                .AddHeader("Content-Type", "application/x-www-form-urlencoded")
                .AddParameter("password", password)
                .AddParameter("emailAddress", newEmail);

            RestResponse? response = await account.ExecuteAccountRestAsync(AppServices.RobloxApi.Account, "https://accountsettings.roblox.com/", request, cancellationToken).ConfigureAwait(false);

            if (response != null && response.IsSuccessful && response.StatusCode == HttpStatusCode.OK)
            {
                Program.Logger?.Info($"[ChangeEmailAsync] {account.Username}: Email changed successfully.");
                return true;
            }

            Program.Logger?.Warn($"[ChangeEmailAsync] {account.Username}: Failed to change email ({response?.StatusCode}).");
            return false;
        }

        public async Task<bool> LogOutOfOtherSessionsAsync(Account account, bool Internal = false, CancellationToken cancellationToken = default)
        {
            if (account == null || !await CheckPinAsync(account, Internal, cancellationToken).ConfigureAwait(false)) return false;
            var csrf = await account.GetCSRFTokenAsync(cancellationToken).ConfigureAwait(false);
            if (!csrf.Success) return false;

            int rotationBefore = account.CookieRotationVersion;
            RestRequest request = account.MakeRequest("v1/logoutfromallsessionsandreauthenticate", Method.Post)
                .AddHeader("Referer", "https://www.roblox.com/")
                .AddHeader("X-CSRF-TOKEN", csrf.Token)
                .AddJsonBody(new { });

            RestResponse? response = await account.ExecuteAccountRestAsync(AppServices.RobloxApi.Auth, "https://auth.roblox.com/", request, cancellationToken).ConfigureAwait(false);

            if (response == null || !response.IsSuccessful)
            {
                RestRequest fallbackReq = account.MakeRequest("authentication/signoutfromallsessionsandreauthenticate", Method.Post)
                    .AddHeader("Referer", "https://www.roblox.com/")
                    .AddHeader("X-CSRF-TOKEN", csrf.Token)
                    .AddHeader("Content-Type", "application/x-www-form-urlencoded");

                response = await account.ExecuteAccountRestAsync(AppServices.RobloxApi.Main, "https://www.roblox.com/", fallbackReq, cancellationToken).ConfigureAwait(false);
            }

            if (response != null && response.IsSuccessful && response.StatusCode == HttpStatusCode.OK)
            {
                bool rotated = account.CookieRotationVersion != rotationBefore || account.TryUpdateRotatedCookie(response);
                if (rotated)
                {
                    await AppServices.AccountRepository.SaveAsync(true).ConfigureAwait(false);
                    Program.Logger?.Info($"[LogOutOfOtherSessionsAsync] {account.Username}: Signed out of all other sessions.");
                    return true;
                }
                else
                {
                    Program.Logger?.Warn($"[LogOutOfOtherSessionsAsync] Roblox did not issue a replacement .ROBLOSECURITY cookie for {account.Username} during session signout.");
                    return false;
                }
            }

            Program.Logger?.Warn($"[LogOutOfOtherSessionsAsync] {account.Username}: Failed to log out of other sessions ({response?.StatusCode}).");
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

            RestRequest request = account.MakeRequest("v1/join-game-instance", Method.Post).AddHeader("Content-Type", "application/json").AddJsonBody(new { gameId = jobId, placeId });
            RestResponse? response = await account.ExecuteAccountRestAsync(AppServices.RobloxApi.GameJoin, "https://gamejoin.roblox.com/", request, cancellationToken).ConfigureAwait(false);

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
            if (account == null) return;
            var csrf = await account.GetCSRFTokenAsync(cancellationToken).ConfigureAwait(false);
            if (!csrf.Success) throw new InvalidOperationException(csrf.Error ?? "Failed to acquire CSRF token");

            RestRequest dpRequest = account.MakeRequest($"/v1/users/{account.UserID}/display-names", Method.Patch).AddHeader("X-CSRF-TOKEN", csrf.Token).AddJsonBody(new { newDisplayName = displayName });
            RestResponse? dpResponse = await account.ExecuteAccountRestAsync(AppServices.RobloxApi.Users, "https://users.roblox.com/", dpRequest, cancellationToken).ConfigureAwait(false);

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
                    Program.Logger?.Debug($"Parse error message JSON failed: {ex.Message}");
                }
                throw new Exception(errMsg ?? $"Something went wrong\n{dpResponse?.StatusCode}: {dpResponse?.Content}");
            }
        }

        public async Task SetAvatarAsync(Account account, string avatarJsonData, CancellationToken cancellationToken = default)
        {
            if (account == null || string.IsNullOrWhiteSpace(avatarJsonData)) return;
            JsonDocument avatarDoc;
            try
            {
                avatarDoc = JsonDocument.Parse(avatarJsonData);
            }
            catch (JsonException ex)
            {
                Program.Logger?.Warn($"SetAvatarAsync: invalid JSON payload: {ex.Message}");
                return;
            }

            using (avatarDoc)
            {
                var root = avatarDoc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;

                var csrf = await account.GetCSRFTokenAsync(cancellationToken).ConfigureAwait(false);
                if (!csrf.Success)
                {
                    Program.Logger.Warn($"[SetAvatarAsync] {account.Username}: Failed to acquire CSRF token: {csrf.Error}");
                    return;
                }

                var attributeTasks = new List<Task<RestResponse?>>(3);

                if (root.TryGetProperty("playerAvatarType", out var patElem) && patElem.ValueKind != JsonValueKind.Null && patElem.ValueKind != JsonValueKind.Undefined)
                {
                    string patVal = (patElem.ValueKind == JsonValueKind.String ? patElem.GetString() : patElem.ToString()) ?? string.Empty;
                    var patRequest = account.MakeRequest("v1/avatar/set-player-avatar-type", Method.Post)
                        .AddHeader("X-CSRF-TOKEN", csrf.Token)
                        .AddJsonBody(new { playerAvatarType = patVal });

                    attributeTasks.Add(account.ExecuteAccountRestAsync(AppServices.RobloxApi.Avatar, "https://avatar.roblox.com/", patRequest, cancellationToken));
                }

                if ((root.TryGetProperty("scales", out var scaleElem) || root.TryGetProperty("scale", out scaleElem)) &&
                    scaleElem.ValueKind == JsonValueKind.Object)
                {
                    var scaleRequest = account.MakeRequest("v1/avatar/set-scales", Method.Post)
                        .AddHeader("X-CSRF-TOKEN", csrf.Token)
                        .AddStringBody(scaleElem.GetRawText(), "application/json");

                    attributeTasks.Add(account.ExecuteAccountRestAsync(AppServices.RobloxApi.Avatar, "https://avatar.roblox.com/", scaleRequest, cancellationToken));
                }

                if (root.TryGetProperty("bodyColors", out var bcElem) && bcElem.ValueKind == JsonValueKind.Object)
                {
                    var bcRequest = account.MakeRequest("v1/avatar/set-body-colors", Method.Post)
                        .AddHeader("X-CSRF-TOKEN", csrf.Token)
                        .AddStringBody(bcElem.GetRawText(), "application/json");

                    attributeTasks.Add(account.ExecuteAccountRestAsync(AppServices.RobloxApi.Avatar, "https://avatar.roblox.com/", bcRequest, cancellationToken));
                }

                if (attributeTasks.Count > 0)
                {
                    await Task.WhenAll(attributeTasks).ConfigureAwait(false);
                }

                if (root.TryGetProperty("assets", out var assetsElem) && assetsElem.ValueKind == JsonValueKind.Array)
                {
                    var assetsRequest = account.MakeRequest("v2/avatar/set-wearing-assets", Method.Post)
                        .AddHeader("X-CSRF-TOKEN", csrf.Token)
                        .AddStringBody($"{{\"assets\":{assetsElem.GetRawText()}}}", "application/json");

                    RestResponse? response = await account.ExecuteAccountRestAsync(AppServices.RobloxApi.Avatar, "https://avatar.roblox.com/", assetsRequest, cancellationToken).ConfigureAwait(false);

                    if (response != null && response.IsSuccessful && !string.IsNullOrWhiteSpace(response.Content))
                    {
                        try
                        {
                            using var resDoc = JsonDocument.Parse(response.Content);
                            if (resDoc.RootElement.TryGetProperty("invalidAssetIds", out var invalidArr) && invalidArr.ValueKind == JsonValueKind.Array)
                            {
                                long[] assetIds = invalidArr.EnumerateArray().Select(asset => asset.GetInt64()).ToArray();
                                AppServices.Dialogs.OpenMissingAssetsDialog(account, assetIds);
                            }
                        }
                        catch (JsonException ex)
                        {
                            Program.Logger?.Debug($"Parse invalidAssetIds JSON failed: {ex.Message}");
                        }
                    }
                }
            }
        }
    }
}
