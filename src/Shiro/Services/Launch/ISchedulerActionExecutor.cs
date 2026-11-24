#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public interface ISchedulerActionExecutor
    {
        IReadOnlySet<Account> GetLiveRunningAccounts(IReadOnlyList<Account> candidates);
        Task<int> CloseAccountsAsync(IReadOnlyList<Account> accounts, CancellationToken ct);
        Task<bool> WaitForLaunchQueueIdleAsync(TimeSpan timeout, CancellationToken ct);
        bool TryEnsureMultiRoblox(int targetCount);
        Task<(int Succeeded, int Attempted, bool Cancelled, string Detail)> LaunchServerAsync(
            IReadOnlyList<Account> accounts,
            long placeId,
            string jobId,
            bool isVip,
            CancellationToken ct);
        Task<(int Succeeded, int Attempted, bool Cancelled, string Detail)> LaunchHomeAsync(
            IReadOnlyList<Account> accounts,
            CancellationToken ct);
        void ArrangeWindows(ScheduleArrangeMode mode);
        void NotifyWebhook(LaunchSchedule schedule, ScheduleRunEntry entry);
        void NotifyLiveProcessStatusChanged();
    }
}
