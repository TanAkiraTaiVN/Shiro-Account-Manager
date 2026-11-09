#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Http;
using Shiro.Services;

namespace Shiro
{
    public partial class Account : IComparable<Account>
    {
        public Task<bool> CheckPinAsync(bool Internal = false, CancellationToken cancellationToken = default) =>
            RobloxSettingsApiService.Instance.CheckPinAsync(this, Internal, cancellationToken);

        public Task<bool> UnlockPinAsync(string pin, CancellationToken cancellationToken = default) =>
            RobloxSettingsApiService.Instance.UnlockPinAsync(this, pin, cancellationToken);

        public Task<bool> SetFollowPrivacyAsync(int privacy, CancellationToken cancellationToken = default) =>
            RobloxSettingsApiService.Instance.SetFollowPrivacyAsync(this, privacy, cancellationToken);

        public Task<bool> ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default) =>
            RobloxSettingsApiService.Instance.ChangePasswordAsync(this, currentPassword, newPassword, cancellationToken);

        public Task<bool> ChangeEmailAsync(string password, string newEmail, CancellationToken cancellationToken = default) =>
            RobloxSettingsApiService.Instance.ChangeEmailAsync(this, password, newEmail, cancellationToken);

        public Task<bool> LogOutOfOtherSessionsAsync(bool Internal = false, CancellationToken cancellationToken = default) =>
            RobloxSettingsApiService.Instance.LogOutOfOtherSessionsAsync(this, Internal, cancellationToken);

        public bool ParseAccessCode(RestResponse response, out string code) =>
            RobloxSettingsApiService.ParseAccessCode(response, out code);

        public Task<(bool Successful, string Message)> SetServerAsync(long placeId, string jobId, CancellationToken cancellationToken = default) =>
            RobloxSettingsApiService.Instance.SetServerAsync(this, placeId, jobId, cancellationToken);

        public Task SetDisplayNameAsync(string displayName, CancellationToken cancellationToken = default) =>
            RobloxSettingsApiService.Instance.SetDisplayNameAsync(this, displayName, cancellationToken);

        public Task SetAvatarAsync(string avatarJsonData, CancellationToken cancellationToken = default) =>
            RobloxSettingsApiService.Instance.SetAvatarAsync(this, avatarJsonData, cancellationToken);
    }
}
