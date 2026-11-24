#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    internal sealed class SchedulerHostActionExecutorService : ISchedulerActionExecutor
    {
        private const int DefaultHomeLaunchStaggerMilliseconds = 2500;

        private readonly Func<ILaunchCoordinator?> _getCoordinator;
        private readonly Func<int> _getJoinDelaySeconds;
        private readonly Func<bool> _getAsyncJoin;
        private readonly Func<bool> _ensureMultiRoblox;
        private readonly Func<CancellationToken> _getLifetimeToken;
        private readonly IHostUiBridgeService? _hostUiBridge;
        private readonly ISettingsService? _settings;
        private readonly IAppLogger? _logger;

        public SchedulerHostActionExecutorService(
            Func<ILaunchCoordinator?> getCoordinator,
            Func<int> getJoinDelaySeconds,
            Func<bool> getAsyncJoin,
            Func<bool> ensureMultiRoblox,
            Func<CancellationToken> getLifetimeToken,
            IHostUiBridgeService? hostUiBridge = null,
            ISettingsService? settings = null,
            IAppLogger? logger = null)
        {
            _getCoordinator = getCoordinator ?? throw new ArgumentNullException(nameof(getCoordinator));
            _getJoinDelaySeconds = getJoinDelaySeconds ?? throw new ArgumentNullException(nameof(getJoinDelaySeconds));
            _getAsyncJoin = getAsyncJoin ?? throw new ArgumentNullException(nameof(getAsyncJoin));
            _ensureMultiRoblox = ensureMultiRoblox ?? throw new ArgumentNullException(nameof(ensureMultiRoblox));
            _getLifetimeToken = getLifetimeToken ?? throw new ArgumentNullException(nameof(getLifetimeToken));
            _hostUiBridge = hostUiBridge;
            _settings = settings;
            _logger = logger;
        }

        private IAppLogger Logger => _logger ?? AppServices.Logger;

        public IReadOnlySet<Account> GetLiveRunningAccounts(IReadOnlyList<Account> candidates)
        {
            if (candidates == null || candidates.Count == 0)
                return new HashSet<Account>(ReferenceEqualityComparer.Instance);

            Dictionary<Account, int> running = RobloxWindowArrangerService.ResolveRunningAccountPids(candidates);
            return new HashSet<Account>(running.Keys, ReferenceEqualityComparer.Instance);
        }

        public Task<int> CloseAccountsAsync(IReadOnlyList<Account> accounts, CancellationToken ct)
        {
            if (accounts == null || accounts.Count == 0)
                return Task.FromResult(0);

            ct.ThrowIfCancellationRequested();
            return Task.Run(() => RobloxProcessService.CloseAccountRobloxProcesses(accounts), ct);
        }

        public async Task<bool> WaitForLaunchQueueIdleAsync(TimeSpan timeout, CancellationToken ct)
        {
            ILaunchCoordinator? coordinator = _getCoordinator();
            if (coordinator == null)
            {
                ct.ThrowIfCancellationRequested();
                return true;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _getLifetimeToken());
            return await coordinator.WaitForIdleAsync(timeout, linked.Token).ConfigureAwait(false);
        }

        public bool TryEnsureMultiRoblox(int targetCount)
        {
            if (targetCount <= 1)
                return true;

            return _ensureMultiRoblox();
        }

        public Task<(int Succeeded, int Attempted, bool Cancelled, string Detail)> LaunchServerAsync(
            IReadOnlyList<Account> accounts,
            long placeId,
            string jobId,
            bool isVip,
            CancellationToken ct)
        {
            return RunBatchLaunchAsync(
                accounts,
                (coordinator, token) => AccountLaunchBatchService.LaunchAccountsForSchedulerAsync(
                    coordinator,
                    accounts,
                    placeId,
                    jobId,
                    isVip,
                    _getJoinDelaySeconds(),
                    _getAsyncJoin(),
                    statusCallback: null,
                    token),
                ct);
        }

        public Task<(int Succeeded, int Attempted, bool Cancelled, string Detail)> LaunchHomeAsync(
            IReadOnlyList<Account> accounts,
            CancellationToken ct)
        {
            return RunBatchLaunchAsync(
                accounts,
                (coordinator, token) => AccountLaunchBatchService.LaunchHomeAccountsForSchedulerAsync(
                    coordinator,
                    accounts,
                    DefaultHomeLaunchStaggerMilliseconds,
                    () =>
                    {
                        if (accounts.Count > 1)
                            _ensureMultiRoblox();
                    },
                    statusCallback: null,
                    token),
                ct);
        }

        public void ArrangeWindows(ScheduleArrangeMode mode)
        {
            if (mode == ScheduleArrangeMode.None)
                return;

            RobloxWindowLayoutMode layout = mode switch
            {
                ScheduleArrangeMode.Grid2x2 => RobloxWindowLayoutMode.Grid2x2,
                ScheduleArrangeMode.Grid3x2 => RobloxWindowLayoutMode.Grid3x2,
                ScheduleArrangeMode.Grid4x3 => RobloxWindowLayoutMode.Grid4x3,
                ScheduleArrangeMode.Cascade => RobloxWindowLayoutMode.CascadeCorner,
                _ => RobloxWindowLayoutMode.AutoGrid
            };

            RobloxWindowArrangerService.ArrangeWindows(layout);
        }

        public void NotifyWebhook(LaunchSchedule schedule, ScheduleRunEntry entry)
        {
            if (schedule == null || entry == null || !schedule.SendWebhookNotification)
                return;

            string targetLabel = BuildSafeWebhookTargetLabel(schedule);
            string destination = schedule.LaunchMode == ScheduleLaunchMode.OpenHome
                ? LocalizationService.Get("Sched_Webhook_DestinationHome")
                : LocalizationService.Format("Sched_Webhook_DestinationPlace", schedule.PlaceId);

            string description = $"{schedule.Name} · {entry.Phase} ({entry.Outcome}) · {destination} · {entry.Summary}";
            WebhookNotificationService.EnqueueAlert(new WebhookAlert
            {
                EventType = WebhookEventType.Info,
                AccountUsername = targetLabel,
                Description = description,
                Timestamp = entry.TimestampUtc
            });
        }

        public void NotifyLiveProcessStatusChanged()
        {
            (_hostUiBridge ?? AppServices.HostUiBridge).NotifyLiveProcessStatusChanged();
        }

        private async Task<(int Succeeded, int Attempted, bool Cancelled, string Detail)> RunBatchLaunchAsync(
            IReadOnlyList<Account> accounts,
            Func<ILaunchCoordinator, CancellationToken, Task<LaunchBatchResult>> launchAction,
            CancellationToken ct)
        {
            if (accounts == null || accounts.Count == 0)
                return (0, 0, false, string.Empty);

            ILaunchCoordinator? coordinator = _getCoordinator();
            if (coordinator == null)
                return (0, accounts.Count, false, LocalizationService.Get("Sched_Run_NoCoordinator"));

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _getLifetimeToken());
            try
            {
                LaunchBatchResult batch = await launchAction(coordinator, linked.Token).ConfigureAwait(false);
                return SummarizeBatch(batch, accounts.Count);
            }
            catch (InvalidOperationException ex)
            {
                Logger.Warn($"[SchedulerHostActionExecutor] Launch batch rejected: {ex.Message}");
                return (0, accounts.Count, false, ex.Message);
            }
        }

        private (int Succeeded, int Attempted, bool Cancelled, string Detail) SummarizeBatch(LaunchBatchResult batch, int requestedCount)
        {
            int attempted = batch.Results.Count > 0 ? batch.Results.Count : requestedCount;
            int succeeded = batch.Results.Count(r => r.Status == LaunchOutcome.Success);

            if (batch.Cancelled)
                return (succeeded, attempted, true, LocalizationService.Format("Sched_Run_Cancelled", succeeded, attempted));

            List<string> failedNames = batch.Results
                .Where(r => r.Status != LaunchOutcome.Success && r.Status != LaunchOutcome.Pending)
                .Select(r => StreamerModeService.GetDisplayUsername(r.Account?.Username, _settings))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            string detail = failedNames.Count > 0
                ? LocalizationService.Format("Sched_Run_DetailFailed", string.Join(", ", failedNames))
                : string.Empty;

            return (succeeded, attempted, false, detail);
        }

        private string BuildSafeWebhookTargetLabel(LaunchSchedule schedule)
        {
            if (schedule.TargetMode == ScheduleTargetMode.Group)
                return LocalizationService.Format("Sched_Webhook_GroupTarget", schedule.TargetGroup);

            int count = schedule.TargetAccounts?.Count ?? 0;
            if (count == 1)
                return StreamerModeService.GetDisplayUsername(schedule.TargetAccounts![0].Username, _settings);

            return LocalizationService.Format("Sched_Webhook_AccountCount", count);
        }
    }
}
