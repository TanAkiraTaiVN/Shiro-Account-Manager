#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Http;
using Shiro.Services;

namespace Shiro
{
    public partial class Account
    {
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

        internal static List<long> ExtractBlockedUserIdsFromJson(string json)
        {
            var result = new List<long>();
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

        public async Task<(bool Success, string Message)> UnblockEveryoneAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!await CheckPinAsync(true, cancellationToken).ConfigureAwait(false))
                return (false, "Pin is Locked");

            RestResponse? response = await GetBlockedListAsync(cancellationToken).ConfigureAwait(false);
            if (response == null || !response.IsSuccessful || response.StatusCode != System.Net.HttpStatusCode.OK)
                return (false, "Failed to unblock everyone");

            var blockedIds = ExtractBlockedUserIdsFromJson(response.Content ?? string.Empty);
            if (blockedIds.Count == 0)
                return (true, "No blocked users");

            foreach (long blockedId in blockedIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string userId = blockedId.ToString(CultureInfo.InvariantCulture);

                RestResponse? unblockResponse = await UnblockUserIdAsync(userId, true, cancellationToken).ConfigureAwait(false);
                if (unblockResponse?.IsSuccessful == true)
                    continue;

                await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                var retryResponse = await UnblockUserIdAsync(userId, true, cancellationToken).ConfigureAwait(false);
                if ((retryResponse == null || !retryResponse.IsSuccessful) && !await CheckPinAsync(true, cancellationToken).ConfigureAwait(false))
                    break;
            }

            return (true, "Unblocked everyone");
        }

        public async Task<bool> SendFriendRequestAsync(string Username, CancellationToken cancellationToken = default)
        {
            var userRes = await AccountManager.GetUserIDAsync(Username, cancellationToken).ConfigureAwait(false);
            if (!userRes.Success) return false;
            var csrf = await GetCSRFTokenAsync(cancellationToken).ConfigureAwait(false);
            if (!csrf.Success) return false;

            RestRequest friendRequest = MakeRequest($"/v1/users/{userRes.UserId}/request-friendship", Method.Post).AddHeader("X-CSRF-TOKEN", csrf.Token);

            RestResponse? response = await ExecuteAccountRestAsync(AccountManager.FriendsClient, "https://friends.roblox.com/", friendRequest, cancellationToken).ConfigureAwait(false);

            return response != null && response.IsSuccessful && response.StatusCode == System.Net.HttpStatusCode.OK;
        }

        public Task<bool> QuickLogIn(string Code) => QuickLogInAsync(Code);

        public async Task<bool> QuickLogInAsync(string Code, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(Code) || Code.Length != 6) return false;
            var csrf = await GetCSRFTokenAsync(cancellationToken).ConfigureAwait(false);
            if (!csrf.Success || string.IsNullOrEmpty(csrf.Token)) return false;
            string Token = csrf.Token;

            RestRequest enterRequest = MakeRequest("auth-token-service/v1/login/enterCode", Method.Post)
                .AddHeader("X-CSRF-TOKEN", Token)
                .AddJsonBody(new { code = Code });

            var Response = await ExecuteAccountRestAsync(AccountManager.ApiClient, "https://apis.roblox.com/", enterRequest, cancellationToken).ConfigureAwait(false);

            if (Response != null && Response.IsSuccessful && !string.IsNullOrWhiteSpace(Response.Content))
            {
                string deviceInfo = "Unknown";
                string location = "Unknown";
                try
                {
                    using var infoDoc = JsonDocument.Parse(Response.Content);
                    if (infoDoc.RootElement.TryGetProperty("deviceInfo", out var di)) deviceInfo = di.GetString() ?? "Unknown";
                    if (infoDoc.RootElement.TryGetProperty("location", out var loc)) location = loc.GetString() ?? "Unknown";
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"Parse QuickLogIn deviceInfo failed: {ex.Message}");
                }

                if (Utilities.YesNoPrompt("Quick Log In", "Please confirm you are logging in with this device", $"Device: {deviceInfo}\nLocation: {location}"))
                {
                    RestRequest validateRequest = MakeRequest("auth-token-service/v1/login/validateCode", Method.Post)
                        .AddHeader("X-CSRF-TOKEN", Token)
                        .AddJsonBody(new { code = Code });

                    var validateResponse = await ExecuteAccountRestAsync(AccountManager.ApiClient, "https://apis.roblox.com/", validateRequest, cancellationToken).ConfigureAwait(false);
                    return validateResponse?.IsSuccessful == true;
                }
            }

            return false;
        }
    }
}
