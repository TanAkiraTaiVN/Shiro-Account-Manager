#nullable enable
using Shiro.Services;

namespace Shiro
{
    public partial class Account
    {
        public static bool IsUnauthenticatedModerationResponse(string? content) =>
            RobloxModerationService.IsUnauthenticatedModerationResponse(content);

        public static (bool IsBanned, string? IssueBadge, string? Reason) ParseModerationText(string content, string? url = null) =>
            RobloxModerationService.ParseModerationText(content, url);
    }
}
