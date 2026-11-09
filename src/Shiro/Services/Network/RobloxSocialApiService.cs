#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Http;

namespace Shiro.Services
{
    public sealed class RobloxSocialApiService
    {
        private static readonly Lazy<RobloxSocialApiService> _defaultInstance = new(() => new RobloxSocialApiService(AppServices.Logger));
        public static RobloxSocialApiService Default => _defaultInstance.Value;
        public static RobloxSocialApiService Instance => Default;

        private static IAppLogger _staticLogger = AppServices.Logger;
        public static IAppLogger Logger
        {
            get => _staticLogger;
            set => _staticLogger = value ?? throw new ArgumentNullException(nameof(value));
        }

        private readonly IAppLogger _logger;

        public RobloxSocialApiService() : this(AppServices.Logger) { }

        public RobloxSocialApiService(IAppLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public static bool IsUserIdInBlockedListJson(string json, long targetUserId)
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
                _staticLogger.Debug($"Parse blocked users json failed: {ex.Message}");
            }

            return false;
        }

        public static List<long> ExtractBlockedUserIdsFromJson(string json)
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
                _staticLogger.Debug($"Extract blocked user ids json failed: {ex.Message}");
            }

            return result;
        }

        public async Task<RestResponse?> BlockUserIdAsync(Account account, string userId, bool skipPinCheck = false, bool unblock = false, CancellationToken cancellationToken = default)
        {
            if (!skipPinCheck && !await account.CheckPinAsync(true, cancellationToken).ConfigureAwait(false)) throw new Exception("Pin Locked");
            var csrf = await account.GetCSRFTokenAsync(cancellationToken).ConfigureAwait(false);
            if (!csrf.Success) throw new Exception("Invalid X-CSRF-Token");
            string token = csrf.Token;

            string action = unblock ? "unblock-user" : "block-user";
            RestRequest blockReq = account.MakeRequest($"user-blocking-api/v1/users/{userId}/{action}", Method.Post)
                .AddHeader("X-CSRF-TOKEN", token)
                .AddJsonBody(new EmptyJsonObjectPayload());

            RestResponse? blockRes = await account.ExecuteAccountRestAsync(AppServices.RobloxApi.Api, "https://apis.roblox.com/", blockReq, cancellationToken).ConfigureAwait(false);

            _logger.Info($"Block Response for {userId} | Unblocking: {unblock}: [{blockRes?.StatusCode}] {blockRes?.Content}");

            return blockRes;
        }

        public Task<RestResponse?> UnblockUserIdAsync(Account account, string userId, bool skipPinCheck = false, CancellationToken cancellationToken = default)
            => BlockUserIdAsync(account, userId, skipPinCheck, unblock: true, cancellationToken);

        public async Task<RestResponse?> GetBlockedListAsync(Account account, CancellationToken cancellationToken = default)
        {
            if (!await account.CheckPinAsync(true, cancellationToken).ConfigureAwait(false)) throw new Exception("Pin is Locked");

            RestRequest request = account.MakeRequest("user-blocking-api/v1/users/get-blocked-users", Method.Get);
            RestResponse? response = await account.ExecuteAccountRestAsync(AppServices.RobloxApi.Api, "https://apis.roblox.com/", request, cancellationToken).ConfigureAwait(false);

            return response;
        }

        public async Task<(bool Success, string Message)> UnblockEveryoneAsync(Account account, CancellationToken cancellationToken = default)
        {
            if (!await account.CheckPinAsync(true, cancellationToken).ConfigureAwait(false))
                return (false, "Pin is Locked");

            RestResponse? response = await GetBlockedListAsync(account, cancellationToken).ConfigureAwait(false);
            if (response == null || !response.IsSuccessful || response.StatusCode != System.Net.HttpStatusCode.OK)
                return (false, "Failed to unblock everyone");

            var blockedIds = ExtractBlockedUserIdsFromJson(response.Content ?? string.Empty);
            if (blockedIds.Count == 0)
                return (true, "No blocked users");

            foreach (long blockedId in blockedIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string userId = blockedId.ToString(CultureInfo.InvariantCulture);

                RestResponse? unblockResponse = await UnblockUserIdAsync(account, userId, true, cancellationToken).ConfigureAwait(false);
                if (unblockResponse?.IsSuccessful == true)
                    continue;

                await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                var retryResponse = await UnblockUserIdAsync(account, userId, true, cancellationToken).ConfigureAwait(false);
                if ((retryResponse == null || !retryResponse.IsSuccessful) && !await account.CheckPinAsync(true, cancellationToken).ConfigureAwait(false))
                    break;
            }

            return (true, "Unblocked everyone");
        }

        public async Task<bool> SendFriendRequestAsync(Account account, string username, CancellationToken cancellationToken = default)
        {
            var userRes = await AppServices.AccountRepository.LookupUserIdAsync(username, cancellationToken).ConfigureAwait(false);
            if (!userRes.Success) return false;
            var csrf = await account.GetCSRFTokenAsync(cancellationToken).ConfigureAwait(false);
            if (!csrf.Success) return false;

            RestRequest friendRequest = account.MakeRequest($"/v1/users/{userRes.UserId}/request-friendship", Method.Post).AddHeader("X-CSRF-TOKEN", csrf.Token);
            RestResponse? response = await account.ExecuteAccountRestAsync(AppServices.RobloxApi.Friends, "https://friends.roblox.com/", friendRequest, cancellationToken).ConfigureAwait(false);

            return response != null && response.IsSuccessful && response.StatusCode == System.Net.HttpStatusCode.OK;
        }

        public async Task<bool> QuickLogInAsync(Account account, string code, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(code) || code.Length != 6) return false;
            var csrf = await account.GetCSRFTokenAsync(cancellationToken).ConfigureAwait(false);
            if (!csrf.Success || string.IsNullOrEmpty(csrf.Token)) return false;
            string token = csrf.Token;

            RestRequest enterRequest = account.MakeRequest("auth-token-service/v1/login/enterCode", Method.Post)
                .AddHeader("X-CSRF-TOKEN", token)
                .AddJsonBody(new QuickLoginCodePayload(code));

            var response = await account.ExecuteAccountRestAsync(AppServices.RobloxApi.Api, "https://apis.roblox.com/", enterRequest, cancellationToken).ConfigureAwait(false);

            if (response != null && response.IsSuccessful && !string.IsNullOrWhiteSpace(response.Content))
            {
                string deviceInfo = "Unknown";
                string location = "Unknown";
                try
                {
                    using var infoDoc = JsonDocument.Parse(response.Content);
                    if (infoDoc.RootElement.TryGetProperty("deviceInfo", out var di)) deviceInfo = di.GetString() ?? "Unknown";
                    if (infoDoc.RootElement.TryGetProperty("location", out var loc)) location = loc.GetString() ?? "Unknown";
                }
                catch (Exception ex)
                {
                    _logger.Debug($"Parse QuickLogIn deviceInfo failed: {ex.Message}");
                }

                if (Utilities.YesNoPrompt("Quick Log In", "Please confirm you are logging in with this device", $"Device: {deviceInfo}\nLocation: {location}"))
                {
                    RestRequest validateRequest = account.MakeRequest("auth-token-service/v1/login/validateCode", Method.Post)
                        .AddHeader("X-CSRF-TOKEN", token)
                        .AddJsonBody(new QuickLoginCodePayload(code));

                    var validateResponse = await account.ExecuteAccountRestAsync(AppServices.RobloxApi.Api, "https://apis.roblox.com/", validateRequest, cancellationToken).ConfigureAwait(false);
                    return validateResponse?.IsSuccessful == true;
                }
            }

            return false;
        }
    }
}
