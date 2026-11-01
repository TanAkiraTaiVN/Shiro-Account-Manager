#nullable enable
using System;
using Shiro.Services;

namespace Shiro
{
    public partial class Account : IComparable<Account>
    {
        public static void InvalidateExecutableCache() =>
            AccountLaunchService.InvalidateExecutableCache();

        public static string? FindRobloxPlayerExecutablePublic() =>
            AccountLaunchService.FindRobloxPlayerExecutable();

        public static bool HasRobloxPlayerProtocolHandlerPublic() =>
            AccountLaunchService.HasRobloxPlayerProtocolHandler();
    }
}
