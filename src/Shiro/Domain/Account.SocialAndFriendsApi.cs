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
        internal static bool IsUserIdInBlockedListJson(string json, long targetUserId) =>
            RobloxSocialApiService.IsUserIdInBlockedListJson(json, targetUserId);

        internal static List<long> ExtractBlockedUserIdsFromJson(string json) =>
            RobloxSocialApiService.ExtractBlockedUserIdsFromJson(json);

        public Task<RestResponse?> BlockUserIdAsync(string userId, bool skipPinCheck = false, bool unblock = false, CancellationToken cancellationToken = default) =>
            RobloxSocialApiService.Instance.BlockUserIdAsync(this, userId, skipPinCheck, unblock, cancellationToken);

        public Task<RestResponse?> UnblockUserIdAsync(string userId, bool skipPinCheck = false, bool unblock = false, CancellationToken cancellationToken = default) =>
            RobloxSocialApiService.Instance.UnblockUserIdAsync(this, userId, skipPinCheck, cancellationToken);

        public Task<RestResponse?> GetBlockedListAsync(CancellationToken cancellationToken = default) =>
            RobloxSocialApiService.Instance.GetBlockedListAsync(this, cancellationToken);

        public Task<(bool Success, string Message)> UnblockEveryoneAsync(CancellationToken cancellationToken = default) =>
            RobloxSocialApiService.Instance.UnblockEveryoneAsync(this, cancellationToken);

        public Task<bool> SendFriendRequestAsync(string username, CancellationToken cancellationToken = default) =>
            RobloxSocialApiService.Instance.SendFriendRequestAsync(this, username, cancellationToken);

        public Task<bool> QuickLogInAsync(string code, CancellationToken cancellationToken = default) =>
            RobloxSocialApiService.Instance.QuickLogInAsync(this, code, cancellationToken);
    }
}
