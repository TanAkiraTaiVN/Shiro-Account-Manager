using System;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Http;
using RBX_Alt_Manager.Services;

namespace RBX_Alt_Manager
{
    public partial class Account : IComparable<Account>
    {
        [GeneratedRegex(@"Roblox\.GameLauncher\.joinPrivateGame\(\d+\,\s*'(\w+\-\w+\-\w+\-\w+\-\w+)'")]
        private static partial Regex JoinPrivateGameRegex();

        [GeneratedRegex(@"""(?:accessCode|gameInstanceId)""\s*:\s*""([a-zA-Z0-9\-]{20,})""", RegexOptions.IgnoreCase)]
        private static partial Regex ModernAccessCodeRegex();

        [GeneratedRegex(@"""joinScriptUrl"":[%s+]?null")]
        private static partial Regex JoinScriptNullRegex();

        public async Task<bool> CheckPinAsync(bool Internal = false, CancellationToken cancellationToken = default)
        {
            if (DateTime.Now < PinUnlocked)
                return true;

            if (string.IsNullOrWhiteSpace(SecurityToken))
            {
                if (!Internal)
                    Program.Logger.Warn($"[CheckPinAsync] {Username}: Invalid Account Session.");
                return false;
            }

            RestRequest request = MakeRequest("v1/account/pin/", Method.Get).AddHeader("Referer", "https://www.roblox.com/");

            RestResponse? response = await ExecuteAccountRestAsync(AccountManager.AuthClient, "https://auth.roblox.com/", request, cancellationToken).ConfigureAwait(false);

            if (response != null && response.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (!Internal)
                    Program.Logger.Warn($"[CheckPinAsync] {Username}: Invalid Account Session.");
                return false;
            }

            // Roblox deprecated the legacy 4-digit Account PIN endpoint.
            // Only treat the PIN as locked when the endpoint explicitly confirms isEnabled == true and unlockedUntil <= 0.
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
                        Program.Logger.Warn($"[CheckPinAsync] {Username}: Pin required for operation.");
                    }
                    return false;
                }

                if (!isEnabled)
                {
                    PinUnlocked = DateTime.Now.AddMinutes(15);
                }
                else if (unlockedUntil > 0)
                {
                    PinUnlocked = DateTime.Now.AddSeconds(unlockedUntil);
                }
            }

            return true;
        }

        public async Task<bool> UnlockPinAsync(string Pin, CancellationToken cancellationToken = default)
        {
            if (Pin.Length != 4) return false;
            if (await CheckPinAsync(true, cancellationToken).ConfigureAwait(false)) return true;

            var csrf = await GetCSRFTokenAsync(cancellationToken).ConfigureAwait(false);
            if (!csrf.Success) return false;

            RestRequest request = MakeRequest("v1/account/pin/unlock", Method.Post)
                .AddHeader("Referer", "https://www.roblox.com/")
                .AddHeader("X-CSRF-TOKEN", csrf.Token)
                .AddHeader("Content-Type", "application/x-www-form-urlencoded")
                .AddParameter("pin", Pin);

            RestResponse? response = await ExecuteAccountRestAsync(AccountManager.AuthClient, "https://auth.roblox.com/", request, cancellationToken).ConfigureAwait(false);

            if (response != null && response.IsSuccessful && response.StatusCode == HttpStatusCode.OK &&
                response.Content != null &&
                Utilities.FastJson.TryDeserialize<RobloxPinInfo>(response.Content, out var pinInfo) && pinInfo != null)
            {
                bool isEnabled = pinInfo.isEnabled;
                int unlockedUntil = pinInfo.unlockedUntil ?? 0;

                if (isEnabled && unlockedUntil > 0)
                    PinUnlocked = DateTime.Now.AddSeconds(unlockedUntil);

                if (PinUnlocked > DateTime.Now)
                {
                    Program.Logger?.Info($"[UnlockPinAsync] {Username}: Pin unlocked for 5 minutes.");
                    return true;
                }
            }

            return false;
        }

        public async Task<bool> SetFollowPrivacyAsync(int Privacy, CancellationToken cancellationToken = default)
        {
            if (!await CheckPinAsync(false, cancellationToken).ConfigureAwait(false)) return false;
            var csrf = await GetCSRFTokenAsync(cancellationToken).ConfigureAwait(false);
            if (!csrf.Success) return false;

            string modernSetting = Privacy switch
            {
                0 => "AllUsers",
                1 => "FriendsFollowingAndFollowers",
                2 => "FriendsAndFollowing",
                3 => "Friends",
                _ => "NoOne"
            };

            // Use modern Roblox user-settings API first
            RestRequest modernRequest = MakeRequest("user-settings-api/v1/user-settings", Method.Post)
                .AddHeader("Referer", "https://www.roblox.com/")
                .AddHeader("X-CSRF-TOKEN", csrf.Token)
                .AddJsonBody(new { whoCanJoinMeInExperiences = modernSetting });

            RestResponse? modernResponse = await ExecuteAccountRestAsync(AccountManager.ApiClient, "https://apis.roblox.com/", modernRequest, cancellationToken).ConfigureAwait(false);
            if (modernResponse != null && modernResponse.IsSuccessful)
                return true;

            // Fallback to legacy endpoint if needed
            RestRequest request = MakeRequest("account/settings/follow-me-privacy", Method.Post)
                .AddHeader("Referer", "https://www.roblox.com/my/account")
                .AddHeader("X-CSRF-TOKEN", csrf.Token)
                .AddHeader("Content-Type", "application/x-www-form-urlencoded");

            switch (Privacy)
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

            RestResponse? response = await ExecuteAccountRestAsync(AccountManager.MainClient, "https://www.roblox.com/", request, cancellationToken).ConfigureAwait(false);

            return response?.IsSuccessful == true && response.StatusCode == HttpStatusCode.OK;
        }

        public async Task<bool> ChangePasswordAsync(string Current, string New, CancellationToken cancellationToken = default)
        {
            if (!await CheckPinAsync(false, cancellationToken).ConfigureAwait(false)) return false;
            var csrf = await GetCSRFTokenAsync(cancellationToken).ConfigureAwait(false);
            if (!csrf.Success) return false;

            RestRequest request = MakeRequest("v2/user/passwords/change", Method.Post)
                .AddHeader("Referer", "https://www.roblox.com/")
                .AddHeader("X-CSRF-TOKEN", csrf.Token)
                .AddHeader("Content-Type", "application/x-www-form-urlencoded")
                .AddParameter("currentPassword", Current)
                .AddParameter("newPassword", New);

            RestResponse? response = await ExecuteAccountRestAsync(AccountManager.AuthClient, "https://auth.roblox.com/", request, cancellationToken).ConfigureAwait(false);

            if (response != null && response.IsSuccessful && response.StatusCode == HttpStatusCode.OK)
            {
                Password = New;

                bool rotated = TryUpdateRotatedCookie(response);
                if (rotated)
                {
                    await AccountManager.SaveAccountsAsync(true).ConfigureAwait(false);
                }
                else
                {
                    Program.Logger?.Warn($"[ChangePasswordAsync] {Username}: Password changed, but no new .ROBLOSECURITY was captured.");
                }

                Program.Logger?.Info($"[ChangePasswordAsync] {Username}: Password changed successfully.");
                return true;
            }

            Program.Logger?.Warn($"[ChangePasswordAsync] {Username}: Failed to change password ({response?.StatusCode}).");
            return false;
        }

        public async Task<bool> ChangeEmailAsync(string Password, string NewEmail, CancellationToken cancellationToken = default)
        {
            if (!await CheckPinAsync(false, cancellationToken).ConfigureAwait(false)) return false;
            var csrf = await GetCSRFTokenAsync(cancellationToken).ConfigureAwait(false);
            if (!csrf.Success) return false;

            RestRequest request = MakeRequest("v1/email", Method.Post)
                .AddHeader("Referer", "https://www.roblox.com/")
                .AddHeader("X-CSRF-TOKEN", csrf.Token)
                .AddHeader("Content-Type", "application/x-www-form-urlencoded")
                .AddParameter("password", Password)
                .AddParameter("emailAddress", NewEmail);

            RestResponse? response = await ExecuteAccountRestAsync(AccountManager.AccountClient, "https://accountsettings.roblox.com/", request, cancellationToken).ConfigureAwait(false);

            if (response != null && response.IsSuccessful && response.StatusCode == HttpStatusCode.OK)
            {
                Program.Logger?.Info($"[ChangeEmailAsync] {Username}: Email changed successfully.");
                return true;
            }

            Program.Logger?.Warn($"[ChangeEmailAsync] {Username}: Failed to change email ({response?.StatusCode}).");
            return false;
        }

        public async Task<bool> LogOutOfOtherSessionsAsync(bool Internal = false, CancellationToken cancellationToken = default)
        {
            if (!await CheckPinAsync(Internal, cancellationToken).ConfigureAwait(false)) return false;
            var csrf = await GetCSRFTokenAsync(cancellationToken).ConfigureAwait(false);
            if (!csrf.Success) return false;

            RestRequest request = MakeRequest("v1/logoutfromallsessionsandreauthenticate", Method.Post)
                .AddHeader("Referer", "https://www.roblox.com/")
                .AddHeader("X-CSRF-TOKEN", csrf.Token)
                .AddJsonBody(new { });

            RestResponse? response = await ExecuteAccountRestAsync(AccountManager.AuthClient, "https://auth.roblox.com/", request, cancellationToken).ConfigureAwait(false);

            if (response == null || !response.IsSuccessful)
            {
                RestRequest fallbackReq = MakeRequest("authentication/signoutfromallsessionsandreauthenticate", Method.Post)
                    .AddHeader("Referer", "https://www.roblox.com/")
                    .AddHeader("X-CSRF-TOKEN", csrf.Token)
                    .AddHeader("Content-Type", "application/x-www-form-urlencoded");

                response = await ExecuteAccountRestAsync(AccountManager.MainClient, "https://www.roblox.com/", fallbackReq, cancellationToken).ConfigureAwait(false);
            }

            if (response != null && response.IsSuccessful && response.StatusCode == HttpStatusCode.OK)
            {
                bool rotated = TryUpdateRotatedCookie(response);
                if (rotated)
                {
                    await AccountManager.SaveAccountsAsync(true).ConfigureAwait(false);
                    Program.Logger?.Info($"[LogOutOfOtherSessionsAsync] {Username}: Signed out of all other sessions.");
                    return true;
                }
                else
                {
                    Program.Logger?.Warn($"[LogOutOfOtherSessionsAsync] Roblox did not issue a replacement .ROBLOSECURITY cookie for {Username} during session signout.");
                    return false;
                }
            }

            Program.Logger?.Warn($"[LogOutOfOtherSessionsAsync] {Username}: Failed to log out of other sessions ({response?.StatusCode}).");
            return false;
        }

        public async Task<(bool Success, bool Unblocked)> TogglePlayerBlockedAsync(string Username, CancellationToken cancellationToken = default)
        {
            if (!await CheckPinAsync(true, cancellationToken).ConfigureAwait(false)) throw new Exception("Pin is Locked!");
            var userRes = await AccountManager.GetUserIDAsync(Username, cancellationToken).ConfigureAwait(false);
            if (!userRes.Success) throw new Exception($"Failed to obtain UserId of {Username}!");

            RestResponse? BlockedResponse = await GetBlockedListAsync(cancellationToken).ConfigureAwait(false);

            if (BlockedResponse == null || !BlockedResponse.IsSuccessful) throw new Exception("Failed to obtain blocked users list!");

            string BlockedUsers = BlockedResponse.Content ?? string.Empty;
            long BlockeeID = userRes.UserId;
            bool isBlocked = IsUserIdInBlockedListJson(BlockedUsers, BlockeeID);

            if (!isBlocked)
            {
                var blockRes = await BlockUserIdAsync($"{BlockeeID}", false, false, cancellationToken).ConfigureAwait(false);
                return (blockRes?.IsSuccessful == true, false);
            }

            var unblockRes = await BlockUserIdAsync($"{BlockeeID}", false, true, cancellationToken).ConfigureAwait(false);
            return (unblockRes?.IsSuccessful == true, true);
        }

        internal static bool IsUserIdInBlockedListJson(string json, long targetUserId)
        {
            if (string.IsNullOrWhiteSpace(json) || targetUserId <= 0)
                return false;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                bool ContainsInArray(JsonElement arrayElem)
                {
                    foreach (var item in arrayElem.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out long rawId) && rawId == targetUserId)
                        {
                            return true;
                        }
                        if (item.ValueKind == JsonValueKind.Object)
                        {
                            if (item.TryGetProperty("userId", out var uProp) && uProp.TryGetInt64(out long uid1) && uid1 == targetUserId)
                                return true;
                            if (item.TryGetProperty("id", out var idProp) && idProp.TryGetInt64(out long uid2) && uid2 == targetUserId)
                                return true;
                        }
                    }
                    return false;
                }

                if (root.ValueKind == JsonValueKind.Array)
                    return ContainsInArray(root);

                if (root.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in root.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Array && ContainsInArray(prop.Value))
                            return true;
                        if (prop.Value.ValueKind == JsonValueKind.Object &&
                            prop.Value.TryGetProperty("blockedUsers", out var nestedArr) &&
                            nestedArr.ValueKind == JsonValueKind.Array &&
                            ContainsInArray(nestedArr))
                        {
                            return true;
                        }
                    }
                }
            }
            catch (JsonException ex)
            {
                Program.Logger?.Debug($"Parse blocked users json failed: {ex.Message}");
            }

            return false;
        }

        internal static System.Collections.Generic.List<long> ExtractBlockedUserIdsFromJson(string json)
        {
            var result = new System.Collections.Generic.List<long>();
            if (string.IsNullOrWhiteSpace(json))
                return result;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                void CollectFromArray(JsonElement arrayElem)
                {
                    foreach (var item in arrayElem.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out long rawId) && rawId > 0)
                        {
                            result.Add(rawId);
                        }
                        else if (item.ValueKind == JsonValueKind.Object)
                        {
                            if (item.TryGetProperty("userId", out var uProp) && uProp.TryGetInt64(out long uid1) && uid1 > 0)
                                result.Add(uid1);
                            else if (item.TryGetProperty("id", out var idProp) && idProp.TryGetInt64(out long uid2) && uid2 > 0)
                                result.Add(uid2);
                        }
                    }
                }

                if (root.ValueKind == JsonValueKind.Array)
                {
                    CollectFromArray(root);
                }
                else if (root.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in root.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Array)
                        {
                            CollectFromArray(prop.Value);
                        }
                        else if (prop.Value.ValueKind == JsonValueKind.Object &&
                                 prop.Value.TryGetProperty("blockedUsers", out var nestedArr) &&
                                 nestedArr.ValueKind == JsonValueKind.Array)
                        {
                            CollectFromArray(nestedArr);
                        }
                    }
                }
            }
            catch (JsonException ex)
            {
                Program.Logger?.Debug($"Extract blocked user ids json failed: {ex.Message}");
            }

            return result;
        }

        public async Task<RestResponse?> BlockUserIdAsync(string UserID, bool SkipPinCheck = false, bool Unblock = false, CancellationToken cancellationToken = default)
        {
            if (!SkipPinCheck && !await CheckPinAsync(true, cancellationToken).ConfigureAwait(false)) throw new Exception("Pin Locked");
            var csrf = await GetCSRFTokenAsync(cancellationToken).ConfigureAwait(false);
            if (!csrf.Success) throw new Exception("Invalid X-CSRF-Token");
            string Token = csrf.Token;

            string action = Unblock ? "unblock-user" : "block-user";
            RestRequest blockReq = MakeRequest($"user-blocking-api/v1/users/{UserID}/{action}", Method.Post)
                .AddHeader("X-CSRF-TOKEN", Token)
                .AddJsonBody(new { });

            RestResponse? blockRes = await ExecuteAccountRestAsync(AccountManager.ApiClient, "https://apis.roblox.com/", blockReq, cancellationToken).ConfigureAwait(false);

            Program.Logger?.Info($"Block Response for {UserID} | Unblocking: {Unblock}: [{blockRes?.StatusCode}] {blockRes?.Content}");

            return blockRes;
        }

        public Task<RestResponse?> UnblockUserIdAsync(string UserID, bool SkipPinCheck = false, CancellationToken cancellationToken = default)
            => BlockUserIdAsync(UserID, SkipPinCheck, Unblock: true, cancellationToken);

        public async Task<RestResponse?> GetBlockedListAsync(CancellationToken cancellationToken = default)
        {
            if (!await CheckPinAsync(true, cancellationToken).ConfigureAwait(false)) throw new Exception("Pin is Locked");

            RestRequest request = MakeRequest("user-blocking-api/v1/users/get-blocked-users", Method.Get);

            RestResponse? response = await ExecuteAccountRestAsync(AccountManager.ApiClient, "https://apis.roblox.com/", request, cancellationToken).ConfigureAwait(false);

            return response;
        }

        public bool ParseAccessCode(RestResponse response, out string Code)
        {
            Code = "";
            if (string.IsNullOrWhiteSpace(response?.Content)) return false;

            Match match = JoinPrivateGameRegex().Match(response.Content);

            if (match.Success && match.Groups.Count == 2)
            {
                Code = match.Groups[1]?.Value ?? string.Empty;
                return true;
            }

            Match modernMatch = ModernAccessCodeRegex().Match(response.Content);
            if (modernMatch.Success && modernMatch.Groups.Count == 2)
            {
                Code = modernMatch.Groups[1]?.Value ?? string.Empty;
                return true;
            }

            return false;
        }

        public async Task<(bool Successful, string Message)> SetServerAsync(long PlaceID, string JobID, CancellationToken cancellationToken = default)
        {
            var csrf = await GetCSRFTokenAsync(cancellationToken).ConfigureAwait(false);
            if (!csrf.Success || string.IsNullOrEmpty(csrf.Token))
                return (false, $"ERROR: Account Session Expired, re-add the account or try again. (Invalid X-CSRF-Token)\n{csrf.Error}");

            RestRequest request = MakeRequest("v1/join-game-instance", Method.Post).AddHeader("Content-Type", "application/json").AddJsonBody(new { gameId = JobID, placeId = PlaceID });

            RestResponse? response = await ExecuteAccountRestAsync(AccountManager.GameJoinClient, "https://gamejoin.roblox.com/", request, cancellationToken).ConfigureAwait(false);

            if (response != null && response.StatusCode == HttpStatusCode.OK)
            {
                string msg = !string.IsNullOrEmpty(response.Content) && JoinScriptNullRegex().IsMatch(response.Content) ? response.Content : "Success";
                return (true, msg);
            }
            else
                return (false, $"Failed {response?.StatusCode}: {response?.Content} {response?.ErrorMessage}");
        }

        public async Task<bool> SendFriendRequestAsync(string Username, CancellationToken cancellationToken = default)
        {
            var userRes = await AccountManager.GetUserIDAsync(Username, cancellationToken).ConfigureAwait(false);
            if (!userRes.Success) return false;
            var csrf = await GetCSRFTokenAsync(cancellationToken).ConfigureAwait(false);
            if (!csrf.Success) return false;

            RestRequest friendRequest = MakeRequest($"/v1/users/{userRes.UserId}/request-friendship", Method.Post).AddHeader("X-CSRF-TOKEN", csrf.Token);

            RestResponse? response = await ExecuteAccountRestAsync(AccountManager.FriendsClient, "https://friends.roblox.com/", friendRequest, cancellationToken).ConfigureAwait(false);

            return response != null && response.IsSuccessful && response.StatusCode == HttpStatusCode.OK;
        }

        public async Task SetDisplayNameAsync(string DisplayName, CancellationToken cancellationToken = default)
        {
            var csrf = await GetCSRFTokenAsync(cancellationToken).ConfigureAwait(false);
            if (!csrf.Success) throw new InvalidOperationException(csrf.Error ?? "Failed to acquire CSRF token");

            RestRequest dpRequest = MakeRequest($"/v1/users/{UserID}/display-names", Method.Patch).AddHeader("X-CSRF-TOKEN", csrf.Token).AddJsonBody(new { newDisplayName = DisplayName });

            RestResponse? dpResponse = await ExecuteAccountRestAsync(AccountManager.UsersClient, "https://users.roblox.com/", dpRequest, cancellationToken).ConfigureAwait(false);

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

        public async Task SetAvatarAsync(string AvatarJSONData, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(AvatarJSONData)) return;
            JsonDocument avatarDoc;
            try
            {
                avatarDoc = JsonDocument.Parse(AvatarJSONData);
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

                var csrf = await GetCSRFTokenAsync(cancellationToken).ConfigureAwait(false);
                if (!csrf.Success)
                {
                    Program.Logger.Warn($"[SetAvatarAsync] {Username}: Failed to acquire CSRF token: {csrf.Error}");
                    return;
                }

                var attributeTasks = new System.Collections.Generic.List<Task<RestResponse?>>(3);

                if (root.TryGetProperty("playerAvatarType", out var patElem) && patElem.ValueKind != JsonValueKind.Null && patElem.ValueKind != JsonValueKind.Undefined)
                {
                    string patVal = (patElem.ValueKind == JsonValueKind.String ? patElem.GetString() : patElem.ToString()) ?? string.Empty;
                    var patRequest = MakeRequest("v1/avatar/set-player-avatar-type", Method.Post)
                        .AddHeader("X-CSRF-TOKEN", csrf.Token)
                        .AddJsonBody(new { playerAvatarType = patVal });

                    attributeTasks.Add(ExecuteAccountRestAsync(AccountManager.AvatarClient, "https://avatar.roblox.com/", patRequest, cancellationToken));
                }

                if ((root.TryGetProperty("scales", out var scaleElem) || root.TryGetProperty("scale", out scaleElem)) &&
                    scaleElem.ValueKind == JsonValueKind.Object)
                {
                    var scaleRequest = MakeRequest("v1/avatar/set-scales", Method.Post)
                        .AddHeader("X-CSRF-TOKEN", csrf.Token)
                        .AddJsonBody(scaleElem.GetRawText());

                    attributeTasks.Add(ExecuteAccountRestAsync(AccountManager.AvatarClient, "https://avatar.roblox.com/", scaleRequest, cancellationToken));
                }

                if (root.TryGetProperty("bodyColors", out var bcElem) && bcElem.ValueKind == JsonValueKind.Object)
                {
                    var bcRequest = MakeRequest("v1/avatar/set-body-colors", Method.Post)
                        .AddHeader("X-CSRF-TOKEN", csrf.Token)
                        .AddJsonBody(bcElem.GetRawText());

                    attributeTasks.Add(ExecuteAccountRestAsync(AccountManager.AvatarClient, "https://avatar.roblox.com/", bcRequest, cancellationToken));
                }

                if (attributeTasks.Count > 0)
                {
                    await Task.WhenAll(attributeTasks).ConfigureAwait(false);
                }

                if (root.TryGetProperty("assets", out var assetsElem) && assetsElem.ValueKind == JsonValueKind.Array)
                {
                    var assetsRequest = MakeRequest("v2/avatar/set-wearing-assets", Method.Post)
                        .AddHeader("X-CSRF-TOKEN", csrf.Token)
                        .AddJsonBody($"{{\"assets\":{assetsElem.GetRawText()}}}");

                    RestResponse? Response = await ExecuteAccountRestAsync(AccountManager.AvatarClient, "https://avatar.roblox.com/", assetsRequest, cancellationToken).ConfigureAwait(false);

                    if (Response != null && Response.IsSuccessful && !string.IsNullOrWhiteSpace(Response.Content))
                    {
                        try
                        {
                            using var resDoc = JsonDocument.Parse(Response.Content);
                            if (resDoc.RootElement.TryGetProperty("invalidAssetIds", out var invalidArr) && invalidArr.ValueKind == JsonValueKind.Array)
                            {
                                long[] assetIds = invalidArr.EnumerateArray().Select(asset => asset.GetInt64()).ToArray();
                                AppServices.Dialogs.OpenMissingAssetsDialog(this, assetIds);
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

    public class RobloxPinInfo
    {
        [JsonPropertyName("isEnabled")]
        public bool isEnabled { get; set; }

        [JsonPropertyName("unlockedUntil")]
        public int? unlockedUntil { get; set; }
    }
}
