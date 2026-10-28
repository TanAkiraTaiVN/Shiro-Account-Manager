#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;

namespace Shiro.Services
{
    public sealed class LaunchRequest
    {
        public Account Account { get; }
        public long PlaceId { get; }
        public string JobId { get; }
        public bool FollowUser { get; }
        public bool VipServer { get; }
        public bool OpenHome { get; }
        public bool PreferChromeHome { get; }

        public LaunchRequest(
            Account account,
            long placeId,
            string jobId,
            bool followUser,
            bool vipServer,
            bool openHome = false,
            bool preferChromeHome = false)
        {
            Account = account;
            PlaceId = placeId;
            JobId = jobId ?? string.Empty;
            FollowUser = followUser;
            VipServer = vipServer;
            OpenHome = openHome;
            PreferChromeHome = preferChromeHome;
        }
    }

    public sealed class LaunchProgress
    {
        public int Index { get; set; }
        public int Total { get; set; }
        public Account? Account { get; set; }
        public LaunchOutcome Status { get; set; }
        public string? Result { get; set; }
        public string? Message { get; set; }
        public bool Completed { get; set; }
    }

    public enum LaunchOutcome
    {
        Pending,
        Success,
        RateLimited,
        TicketError,
        Timeout,
        Failed,
        Cancelled
    }

    internal sealed class LaunchBatchResult
    {
        public IReadOnlyList<LaunchProgress> Results { get; }
        public bool Cancelled { get; }

        public LaunchBatchResult(IReadOnlyList<LaunchProgress> results, bool cancelled)
        {
            Results = results ?? Array.Empty<LaunchProgress>();
            Cancelled = cancelled;
        }
    }

    internal interface ILaunchCoordinator : IDisposable
    {
        bool IsRunning { get; }

        Task<LaunchBatchResult> EnqueueAsync(
            IReadOnlyList<LaunchRequest> requests,
            int delaySeconds,
            bool asyncJoin,
            Action<LaunchProgress>? progress,
            CancellationToken cancellationToken = default(CancellationToken));

        void SignalNext();
        void Cancel();
        Task<bool> WaitForIdleAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken = default(CancellationToken));
    }
}
