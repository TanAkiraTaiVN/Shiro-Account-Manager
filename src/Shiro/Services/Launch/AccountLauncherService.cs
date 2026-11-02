#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Shiro.Classes;

namespace Shiro.Services
{
    /// <summary>
    /// Decoupled launcher service handling Roblox App Home, Game Server Join, and Multi-Launch flows.
    /// Eliminates direct tight-coupling between Avalonia Views and Roblox controllers.
    /// </summary>
    public static partial class AccountLauncherService
    {
        [GeneratedRegex(@"\/games\/(\d+)[\/|\?]?")]
        private static partial Regex GameUrlRegex();

        [GeneratedRegex("[^0-9]")]
        private static partial Regex NonDigitRegex();
        /// <summary>
        /// Launches the Roblox client directly to the Home screen for a single account.
        /// </summary>
        public static async Task LaunchAppHomeAsync(Account account)
        {
            if (account == null) return;

            try
            {
                await AppSettings.LaunchAppHomeFromHostAsync(account).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Program.Logger?.Error($"[AccountLauncherService.LaunchAppHomeAsync] {ex}");
            }
        }

        /// <summary>
        /// Joins a specific game server (Place ID and optional Job ID / VIP server) for one or more accounts.
        /// </summary>
        public static async Task JoinServerAsync(IEnumerable<Account>? accounts, long placeId, string? jobId)
        {
            var targetList = accounts?.Where(a => a != null).ToList();
            if (targetList == null || targetList.Count == 0)
            {
                targetList = AppSettings.GetCurrentSelectedAccountsFromHost().ToList();
            }

            if (targetList == null || targetList.Count == 0)
            {
                Program.Logger?.Warn("[AccountLauncherService] JoinServer requested with no target accounts.");
                return;
            }

            try
            {
                await AppSettings.JoinServerFromHostAsync(targetList, placeId, jobId ?? string.Empty).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Program.Logger?.Error($"[AccountLauncherService.JoinServerAsync] {ex}");
            }
        }

        /// <summary>
        /// Triggers multi-launch to open Roblox Home for selected accounts.
        /// </summary>
        public static void MultiLaunchHome()
        {
            try
            {
                AppSettings.MultiLaunchHomeFromHost();
            }
            catch (Exception ex)
            {
                Program.Logger?.Error($"[AccountLauncherService.MultiLaunchHome] {ex}");
            }
        }

        /// <summary>
        /// Parses a raw place ID input string (supporting game URLs and numeric strings).
        /// </summary>
        public static long ParsePlaceId(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return 0;
            var match = GameUrlRegex().Match(input);
            string clean = match.Success ? match.Groups[1].Value : NonDigitRegex().Replace(input, "");
            return long.TryParse(clean, out long pid) ? pid : 0;
        }
    }
}
