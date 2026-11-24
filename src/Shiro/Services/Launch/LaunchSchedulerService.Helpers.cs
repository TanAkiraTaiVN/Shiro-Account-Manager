#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public sealed partial class LaunchSchedulerService
    {
        private ScheduleRunEntry CreateOpenEntry(
            LaunchSchedulerExecutionHelper.ResolvedOpenPlan plan,
            ScheduleRunOutcome outcome,
            int succeededCount,
            int skippedCount,
            string summary) => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            ScheduleId = plan.Schedule.Id,
            ScheduleName = plan.Schedule.Name,
            TimestampUtc = _timeProvider.GetUtcNow(),
            Phase = plan.Phase,
            Outcome = outcome,
            TargetCount = plan.TotalMatchedCount,
            SucceededCount = succeededCount,
            SkippedCount = skippedCount,
            Summary = summary
        };

        private void RecordRunEntryAndNotify(
            ISchedulerActionExecutor executor,
            LaunchSchedule schedule,
            ScheduleRunEntry entry,
            bool isErrorToast)
        {
            lock (_stateLock)
            {
                if (_disposed)
                    return;

                _history.Insert(0, CloneRunEntry(entry));
                if (_history.Count > LaunchScheduleStore.MaxHistoryEntries)
                    _history.RemoveRange(LaunchScheduleStore.MaxHistoryEntries, _history.Count - LaunchScheduleStore.MaxHistoryEntries);

                SaveDocumentLocked();
            }

            string toastTitle = entry.Outcome switch
            {
                ScheduleRunOutcome.Failed => LocalizationService.Format("Sched_Toast_FailedTitle", schedule.Name),
                ScheduleRunOutcome.Partial => LocalizationService.Format("Sched_Toast_PartialTitle", schedule.Name),
                ScheduleRunOutcome.Skipped => LocalizationService.Format("Sched_Toast_SkippedTitle", schedule.Name),
                _ => LocalizationService.Format("Sched_Toast_SuccessTitle", schedule.Name)
            };

            if (isErrorToast)
                _toastNotifier.ShowError(toastTitle, entry.Summary);
            else
                _toastNotifier.ShowSuccess(toastTitle, entry.Summary);

            if (schedule.SendWebhookNotification)
                executor.NotifyWebhook(schedule, entry);

            executor.NotifyLiveProcessStatusChanged();
            HistoryChanged?.Invoke();
        }

        private bool IsDisposedOrPausedOrLocked(out ISchedulerActionExecutor? executor)
        {
            lock (_stateLock)
            {
                executor = _executor;
                if (_disposed || !_isGlobalEnabled || executor == null)
                    return true;
            }

            return _isVaultLocked();
        }

        private async Task<bool> TryAcquireExecutionGateAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _activeGateUsers);
            bool acquired = false;
            try
            {
                if (IsDisposed)
                    return false;

                await _executionGate.WaitAsync(ct).ConfigureAwait(false);
                acquired = true;
                if (IsDisposed)
                {
                    ReleaseExecutionGate(acquired);
                    return false;
                }

                return true;
            }
            catch (ObjectDisposedException ex)
            {
                Logger.Debug($"[LaunchSchedulerService] Gate disposed while waiting: {ex.Message}");
                ReleaseExecutionGate(acquired);
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ReleaseExecutionGate(acquired);
                throw;
            }
        }

        private void ReleaseExecutionGate(bool acquired)
        {
            if (acquired)
            {
                try { _executionGate.Release(); }
                catch (ObjectDisposedException ex) { Logger.Debug($"[LaunchSchedulerService] Gate release during dispose: {ex.Message}"); }
            }

            if (Interlocked.Decrement(ref _activeGateUsers) == 0 && IsDisposed)
                TryDisposeGate();
        }

        private bool IsDisposed
        {
            get
            {
                lock (_stateLock)
                    return _disposed;
            }
        }

        private void TryDisposeGate()
        {
            if (Interlocked.Exchange(ref _gateDisposed, 1) == 0)
            {
                try { _executionGate.Dispose(); }
                catch (ObjectDisposedException ex) { Logger.Debug($"[LaunchSchedulerService] Gate already disposed: {ex.Message}"); }
            }
        }
    }
}
