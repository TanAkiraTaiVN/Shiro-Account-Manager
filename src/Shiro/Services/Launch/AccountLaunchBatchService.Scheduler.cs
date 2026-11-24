#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    internal static partial class AccountLaunchBatchService
    {
        public static Task<LaunchBatchResult> LaunchAccountsForSchedulerAsync(
            ILaunchCoordinator launchCoordinator,
            IReadOnlyList<Account>? accounts,
            long placeId,
            string? jobId,
            bool vipServer,
            int accountJoinDelaySeconds,
            bool asyncJoin,
            Action<string>? statusCallback,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(launchCoordinator);

            List<Account> launchList = FilterDistinctAccounts(accounts);
            if (launchList.Count == 0)
                return Task.FromResult(new LaunchBatchResult(Array.Empty<LaunchProgress>(), false));

            string targetJobId = jobId ?? string.Empty;
            var requests = new List<LaunchRequest>(launchList.Count);
            foreach (Account account in launchList)
            {
                // Scheduler server targets never fall back to per-account SavedPlaceId/SavedJobId.
                requests.Add(new LaunchRequest(
                    account,
                    placeId,
                    targetJobId,
                    followUser: false,
                    vipServer: vipServer,
                    openHome: false));
            }

            return EnqueueSchedulerRequestsAsync(
                launchCoordinator,
                requests,
                accountJoinDelaySeconds,
                asyncJoin,
                statusCallback,
                cancellationToken);
        }

        public static Task<LaunchBatchResult> LaunchHomeAccountsForSchedulerAsync(
            ILaunchCoordinator launchCoordinator,
            IReadOnlyList<Account>? accounts,
            int homeLaunchStaggerMilliseconds,
            Action? ensureMultiRoblox,
            Action<string>? statusCallback,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(launchCoordinator);

            List<Account> launchList = FilterDistinctAccounts(accounts);
            if (launchList.Count == 0)
                return Task.FromResult(new LaunchBatchResult(Array.Empty<LaunchProgress>(), false));

            ensureMultiRoblox?.Invoke();

            var requests = new List<LaunchRequest>(launchList.Count);
            foreach (Account account in launchList)
            {
                requests.Add(new LaunchRequest(
                    account,
                    placeId: 0,
                    jobId: string.Empty,
                    followUser: false,
                    vipServer: false,
                    openHome: true));
            }

            int delaySeconds = Math.Max(1, homeLaunchStaggerMilliseconds / 1000);
            return EnqueueSchedulerRequestsAsync(
                launchCoordinator,
                requests,
                delaySeconds,
                asyncJoin: false,
                statusCallback,
                cancellationToken);
        }

        private static List<Account> FilterDistinctAccounts(IReadOnlyList<Account>? accounts)
        {
            var result = new List<Account>(accounts?.Count ?? 0);
            if (accounts == null || accounts.Count == 0)
                return result;

            var seen = new HashSet<Account>(ReferenceEqualityComparer.Instance);
            foreach (Account account in accounts)
            {
                if (account != null && seen.Add(account))
                    result.Add(account);
            }

            return result;
        }

        private static async Task<LaunchBatchResult> EnqueueSchedulerRequestsAsync(
            ILaunchCoordinator launchCoordinator,
            IReadOnlyList<LaunchRequest> requests,
            int delaySeconds,
            bool asyncJoin,
            Action<string>? statusCallback,
            CancellationToken cancellationToken)
        {
            try
            {
                return await launchCoordinator.EnqueueAsync(
                    requests,
                    delaySeconds,
                    asyncJoin,
                    progress =>
                    {
                        if (!string.IsNullOrWhiteSpace(progress?.Message))
                            statusCallback?.Invoke(progress.Message);
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                AppServices.Logger.Debug($"[AccountLaunchBatchService.Scheduler] Launch batch cancelled: {ex.Message}");
                return new LaunchBatchResult(Array.Empty<LaunchProgress>(), true);
            }
        }
    }
}
