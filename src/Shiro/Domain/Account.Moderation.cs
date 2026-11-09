#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;

namespace Shiro
{
    public partial class Account
    {
        private Task<(bool IsBanned, string? IssueBadge, string? Reason)> CheckModerationStatusAsync(string token, CancellationToken cancellationToken) =>
            RobloxModerationService.Instance.CheckModerationStatusAsync(this, token, cancellationToken);

        public static bool IsUnauthenticatedModerationResponse(string? content) =>
            RobloxModerationService.IsUnauthenticatedModerationResponse(content);

        public static (bool IsBanned, string? IssueBadge, string? Reason) ParseModerationText(string content, string? url = null) =>
            RobloxModerationService.ParseModerationText(content, url);
    }
}
