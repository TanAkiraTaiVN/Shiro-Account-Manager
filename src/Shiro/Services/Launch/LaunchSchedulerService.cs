#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public sealed partial class LaunchSchedulerService : ILaunchSchedulerService
    {
        private readonly LaunchScheduleStore _store;
        private readonly IAccountRepository _accountRepository;
        private readonly ISettingsService _settings;
        private readonly IToastNotifier _toastNotifier;
        private readonly IHostUiBridgeService _hostUiBridge;
        private readonly Func<bool> _isVaultLocked;
        private readonly TimeProvider _timeProvider;
        private readonly IAppLogger? _logger;

        private readonly Lock _stateLock = new();
        private readonly SemaphoreSlim _executionGate = new(1, 1);
        private readonly List<LaunchSchedule> _schedules = new();
        private readonly List<ScheduleRunEntry> _history = new();

        private ISchedulerActionExecutor? _executor;
        private CancellationTokenSource? _loopCts;
        private bool _isGlobalEnabled;
        private bool _isRunning;
        private bool _disposed;
        private bool _startupCatchUpPending = true;
        private int _activeGateUsers;
        private int _gateDisposed;

        public LaunchSchedulerService(
            LaunchScheduleStore store,
            IAccountRepository accountRepository,
            ISettingsService settings,
            IToastNotifier toastNotifier,
            IHostUiBridgeService hostUiBridge,
            Func<bool>? isVaultLocked = null,
            TimeProvider? timeProvider = null,
            IAppLogger? logger = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _accountRepository = accountRepository ?? throw new ArgumentNullException(nameof(accountRepository));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _toastNotifier = toastNotifier ?? throw new ArgumentNullException(nameof(toastNotifier));
            _hostUiBridge = hostUiBridge ?? throw new ArgumentNullException(nameof(hostUiBridge));
            _isVaultLocked = isVaultLocked ?? (() => AppServices.AccountPersistence.IsVaultLockedPendingMasterPassword);
            _timeProvider = timeProvider ?? TimeProvider.System;
            _logger = logger;

            _isGlobalEnabled = _settings.GetBool(AppSettingsKeys.General.SchedulerEnabled, true);

            LaunchScheduleDocument doc = _store.Load();
            lock (_stateLock)
            {
                if (doc.Schedules != null)
                {
                    foreach (LaunchSchedule s in doc.Schedules)
                    {
                        if (s != null)
                            _schedules.Add(NormalizeScheduleClone(s));
                    }
                }

                if (doc.History != null)
                {
                    foreach (ScheduleRunEntry entry in doc.History.Where(e => e != null).Take(LaunchScheduleStore.MaxHistoryEntries))
                        _history.Add(CloneRunEntry(entry));
                }
            }
        }

        private IAppLogger Logger => _logger ?? AppServices.Logger;

        public event Action? SchedulesChanged;
        public event Action? HistoryChanged;
        public event Action? StateChanged;

        public bool IsGlobalEnabled
        {
            get
            {
                lock (_stateLock)
                    return _isGlobalEnabled;
            }
            set
            {
                bool changed;
                lock (_stateLock)
                {
                    changed = _isGlobalEnabled != value;
                    _isGlobalEnabled = value;
                    _settings.Set(AppSettingsKeys.General.SchedulerEnabled, value ? "true" : "false");
                }

                _hostUiBridge.QueueDeferredSaveSettings();
                if (changed)
                    StateChanged?.Invoke();
            }
        }

        public bool IsRunning
        {
            get
            {
                lock (_stateLock)
                    return _isRunning;
            }
        }

        public IReadOnlyList<LaunchSchedule> GetSchedules()
        {
            lock (_stateLock)
                return _schedules.Select(s => s.Clone()).ToList().AsReadOnly();
        }

        public IReadOnlyList<ScheduleRunEntry> GetHistory()
        {
            lock (_stateLock)
                return _history.Select(CloneRunEntry).ToList().AsReadOnly();
        }

        public void AttachExecutor(ISchedulerActionExecutor executor)
        {
            ArgumentNullException.ThrowIfNull(executor);
            lock (_stateLock)
                _executor = executor;
        }

        public void Start()
        {
            CancellationTokenSource cts;
            lock (_stateLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_isRunning)
                    return;

                _isRunning = true;
                _startupCatchUpPending = true;
                cts = new CancellationTokenSource();
                _loopCts = cts;
                _accountRepository.AccountsChanged -= OnRepositoryAccountsChanged;
                _accountRepository.AccountsChanged += OnRepositoryAccountsChanged;
            }

            _ = Task.Run(() => RunTickLoopAsync(cts));
            StateChanged?.Invoke();
        }

        public void StopForShutdown()
        {
            CancellationTokenSource? cts;
            bool changed;
            lock (_stateLock)
            {
                changed = _isRunning;
                _isRunning = false;
                cts = _loopCts;
                _loopCts = null;
                _accountRepository.AccountsChanged -= OnRepositoryAccountsChanged;
            }

            SafeExecuteHelper.Try(() => cts?.Cancel(), "LaunchSchedulerService.StopForShutdown.Cancel");
            if (changed)
                StateChanged?.Invoke();
        }

        public ScheduleValidationResult UpsertSchedule(LaunchSchedule schedule)
        {
            ArgumentNullException.ThrowIfNull(schedule);
            ScheduleValidationResult validation = LaunchScheduleEvaluatorHelper.Validate(schedule);
            if (!validation.IsValid)
                return validation;

            LaunchSchedule copy = NormalizeScheduleClone(schedule);
            DateTimeOffset nowUtc = _timeProvider.GetUtcNow();

            lock (_stateLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                int index = _schedules.FindIndex(s => string.Equals(s.Id, copy.Id, StringComparison.Ordinal));
                if (index >= 0)
                {
                    LaunchSchedule existing = _schedules[index];
                    copy.CreatedAtUtc = existing.CreatedAtUtc != default ? existing.CreatedAtUtc : nowUtc;
                    copy.UpdatedAtUtc = nowUtc;
                    PreserveOrResetTriggerKeys(existing, copy);
                    _schedules[index] = copy;
                }
                else
                {
                    copy.CreatedAtUtc = copy.CreatedAtUtc != default ? copy.CreatedAtUtc : nowUtc;
                    copy.UpdatedAtUtc = nowUtc;
                    _schedules.Add(copy);
                }

                SaveDocumentLocked();
            }

            SchedulesChanged?.Invoke();
            return validation;
        }

        public bool DeleteSchedule(string scheduleId, out LaunchSchedule? removed)
        {
            removed = null;
            if (string.IsNullOrWhiteSpace(scheduleId))
                return false;

            lock (_stateLock)
            {
                int index = _schedules.FindIndex(s => string.Equals(s.Id, scheduleId, StringComparison.Ordinal));
                if (index < 0)
                    return false;

                removed = _schedules[index].Clone();
                _schedules.RemoveAt(index);
                SaveDocumentLocked();
            }

            SchedulesChanged?.Invoke();
            return true;
        }

        public bool RestoreSchedule(LaunchSchedule schedule)
        {
            ArgumentNullException.ThrowIfNull(schedule);
            if (!LaunchScheduleEvaluatorHelper.Validate(schedule).IsValid)
                return false;

            LaunchSchedule copy = NormalizeScheduleClone(schedule);
            lock (_stateLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                int index = _schedules.FindIndex(s => string.Equals(s.Id, copy.Id, StringComparison.Ordinal));
                if (index >= 0)
                    _schedules[index] = copy;
                else
                    _schedules.Add(copy);

                SaveDocumentLocked();
            }

            SchedulesChanged?.Invoke();
            return true;
        }

        public bool SetScheduleEnabled(string scheduleId, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(scheduleId))
                return false;

            bool changed = false;
            lock (_stateLock)
            {
                LaunchSchedule? existing = _schedules.FirstOrDefault(s => string.Equals(s.Id, scheduleId, StringComparison.Ordinal));
                if (existing == null)
                    return false;

                if (existing.IsEnabled != enabled)
                {
                    existing.IsEnabled = enabled;
                    existing.UpdatedAtUtc = _timeProvider.GetUtcNow();
                    SaveDocumentLocked();
                    changed = true;
                }
            }

            if (changed)
                SchedulesChanged?.Invoke();

            return true;
        }

        public void ClearHistory()
        {
            lock (_stateLock)
            {
                if (_history.Count == 0)
                    return;

                _history.Clear();
                SaveDocumentLocked();
            }

            HistoryChanged?.Invoke();
        }

        public void Dispose()
        {
            CancellationTokenSource? cts;
            bool wasRunning;
            lock (_stateLock)
            {
                if (_disposed)
                    return;

                _disposed = true;
                wasRunning = _isRunning;
                _isRunning = false;
                cts = _loopCts;
                _loopCts = null;
                _accountRepository.AccountsChanged -= OnRepositoryAccountsChanged;
            }

            SafeExecuteHelper.Try(() => cts?.Cancel(), "LaunchSchedulerService.Dispose.Cancel");
            if (wasRunning)
                StateChanged?.Invoke();

            if (Volatile.Read(ref _activeGateUsers) == 0)
                TryDisposeGate();
        }

        private void OnRepositoryAccountsChanged(object? sender, AccountsChangedEventArgs e)
        {
            CancellationToken token;
            lock (_stateLock)
            {
                if (_disposed || !_isRunning || !_isGlobalEnabled || _loopCts == null)
                    return;

                token = _loopCts.Token;
            }

            if (_isVaultLocked())
                return;

            _ = Task.Run(() => SafeEvaluateTickAsync(token, "AccountsChanged"));
        }

        private async Task RunTickLoopAsync(CancellationTokenSource cts)
        {
            CancellationToken ct = cts.Token;
            try
            {
                await SafeEvaluateTickAsync(ct, "Startup").ConfigureAwait(false);
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _timeProvider);
                while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    await SafeEvaluateTickAsync(ct, "PeriodicTimer").ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Expected on shutdown
            }
            catch (Exception ex)
            {
                Logger.Error($"[LaunchSchedulerService] Background loop failed: {ex.Message}");
            }
            finally
            {
                SafeExecuteHelper.Try(cts.Dispose, "LaunchSchedulerService.DisposeLoopCts");
            }
        }

        private async Task SafeEvaluateTickAsync(CancellationToken ct, string source)
        {
            try
            {
                await EvaluateTickOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Expected during shutdown
            }
            catch (Exception ex)
            {
                Logger.Warn($"[LaunchSchedulerService] {source} tick failed: {ex.Message}");
            }
        }

        private void SaveDocumentLocked()
        {
            var doc = new LaunchScheduleDocument
            {
                SchemaVersion = 1,
                Schedules = _schedules.Select(s => s.Clone()).ToList(),
                History = _history.Select(CloneRunEntry).ToList()
            };
            _store.Save(doc);
        }

        private static LaunchSchedule NormalizeScheduleClone(LaunchSchedule schedule)
        {
            LaunchSchedule copy = schedule.Clone();
            if (string.IsNullOrWhiteSpace(copy.Id))
                copy.Id = Guid.NewGuid().ToString("N");
            copy.Name = (copy.Name ?? string.Empty).Trim();
            copy.ColorHex = string.IsNullOrWhiteSpace(copy.ColorHex) ? "#38BDF8" : copy.ColorHex.Trim();
            copy.OneTimeDate = copy.Recurrence == ScheduleRecurrence.OneTime
                ? (copy.OneTimeDate ?? string.Empty).Trim()
                : null;
            copy.StartTime = LaunchScheduleEvaluatorHelper.NormalizeTimeText(copy.StartTime);
            copy.EndTime = copy.HasEndTime ? LaunchScheduleEvaluatorHelper.NormalizeTimeText(copy.EndTime) : null;
            copy.TargetGroup = (copy.TargetGroup ?? string.Empty).Trim();
            copy.JobId = (copy.JobId ?? string.Empty).Trim();
            copy.HandoffDelaySeconds = Math.Clamp(copy.HandoffDelaySeconds, 0, 15);
            copy.TargetAccounts ??= new List<ScheduleAccountRef>();
            return copy;
        }

        private static void PreserveOrResetTriggerKeys(LaunchSchedule existing, LaunchSchedule updated)
        {
            bool startChanged =
                existing.Recurrence != updated.Recurrence ||
                !string.Equals(existing.StartTime, updated.StartTime, StringComparison.Ordinal) ||
                (updated.Recurrence == ScheduleRecurrence.Weekly &&
                 (existing.DaysOfWeek & ScheduleDaysOfWeek.All) != (updated.DaysOfWeek & ScheduleDaysOfWeek.All)) ||
                (updated.Recurrence == ScheduleRecurrence.OneTime &&
                 !string.Equals(existing.OneTimeDate, updated.OneTimeDate, StringComparison.Ordinal));

            bool endChanged =
                startChanged ||
                existing.HasEndTime != updated.HasEndTime ||
                (updated.HasEndTime && !string.Equals(existing.EndTime, updated.EndTime, StringComparison.Ordinal));

            updated.LastTriggeredStartKey = startChanged ? null : updated.LastTriggeredStartKey ?? existing.LastTriggeredStartKey;
            updated.LastTriggeredEndKey = endChanged ? null : updated.LastTriggeredEndKey ?? existing.LastTriggeredEndKey;
        }

        private static ScheduleRunEntry CloneRunEntry(ScheduleRunEntry entry) => new()
        {
            Id = entry.Id,
            ScheduleId = entry.ScheduleId,
            ScheduleName = entry.ScheduleName,
            TimestampUtc = entry.TimestampUtc,
            Phase = entry.Phase,
            Outcome = entry.Outcome,
            TargetCount = entry.TargetCount,
            SucceededCount = entry.SucceededCount,
            SkippedCount = entry.SkippedCount,
            Summary = entry.Summary
        };
    }
}
