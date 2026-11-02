#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Http;
using Shiro.Services;

namespace Shiro
{
    public partial class Account
    {
        public async Task<(bool Success, bool Unblocked)> TogglePlayerBlockedAsync(string username, CancellationToken cancellationToken = default)
        {
            if (!await CheckPinAsync(true, cancellationToken).ConfigureAwait(false)) throw new Exception("Pin is Locked!");
            var userRes = await AccountManager.GetUserIDAsync(username, cancellationToken).ConfigureAwait(false);
            if (!userRes.Success) throw new Exception($"Failed to obtain UserId of {username}!");

            RestResponse? blockedResponse = await GetBlockedListAsync(cancellationToken).ConfigureAwait(false);
            if (blockedResponse == null || !blockedResponse.IsSuccessful) throw new Exception("Failed to obtain blocked users list!");

            string blockedUsers = blockedResponse.Content ?? string.Empty;
            long blockeeId = userRes.UserId;
            bool isBlocked = IsUserIdInBlockedListJson(blockedUsers, blockeeId);

            if (!isBlocked)
            {
                var blockRes = await BlockUserIdAsync($"{blockeeId}", false, false, cancellationToken).ConfigureAwait(false);
                return (blockRes?.IsSuccessful == true, false);
            }

            var unblockRes = await BlockUserIdAsync($"{blockeeId}", false, true, cancellationToken).ConfigureAwait(false);
            return (unblockRes?.IsSuccessful == true, true);
        }

        internal static bool IsUserIdInBlockedListJson(string json, long targetUserId) =>
            RobloxSocialApiService.IsUserIdInBlockedListJson(json, targetUserId);

        internal static List<long> ExtractBlockedUserIdsFromJson(string json) =>
            RobloxSocialApiService.ExtractBlockedUserIdsFromJson(json);

        public Task<RestResponse?> BlockUserIdAsync(string userId, bool skipPinCheck = false, bool unblock = false, CancellationToken cancellationToken = default) =>
            RobloxSocialApiService.Instance.BlockUserIdAsync(this, userId, skipPinCheck, unblock, cancellationToken);

        public Task<RestResponse?> UnblockUserIdAsync(string userId, bool skipPinCheck = false, CancellationToken cancellationToken = default) =>
            RobloxSocialApiService.Instance.UnblockUserIdAsync(this, userId, skipPinCheck, cancellationToken);

        public Task<RestResponse?> GetBlockedListAsync(CancellationToken cancellationToken = default) =>
            RobloxSocialApiService.Instance.GetBlockedListAsync(this, cancellationToken);

        public Task<(bool Success, string Message)> UnblockEveryoneAsync(CancellationToken cancellationToken = default) =>
            RobloxSocialApiService.Instance.UnblockEveryoneAsync(this, cancellationToken);

        public Task<bool> SendFriendRequestAsync(string username, CancellationToken cancellationToken = default) =>
            RobloxSocialApiService.Instance.SendFriendRequestAsync(this, username, cancellationToken);

        public Task<bool> QuickLogIn(string code) => QuickLogInAsync(code);

        public Task<bool> QuickLogInAsync(string code, CancellationToken cancellationToken = default) =>
            RobloxSocialApiService.Instance.QuickLogInAsync(this, code, cancellationToken);
    }
}
