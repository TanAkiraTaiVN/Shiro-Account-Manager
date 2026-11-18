#nullable enable
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    /// <summary>
    /// Tracks active Roblox game sessions and automatically recovers/reconnects accounts
    /// when the Roblox client crashes, disconnects (Errors 277/268), or terminates unexpectedly.
    /// Provides exponential backoff, maximum retry limits, and suppression on user-initiated closures.
    /// </summary>
    public sealed partial class AutoReconnectService : IDisposable
    {
        public static AutoReconnectService Instance { get; } = new AutoReconnectService();

        private readonly ConcurrentDictionary<string, ReconnectSession> _sessions =
            new ConcurrentDictionary<string, ReconnectSession>(StringComparer.OrdinalIgnoreCase);

        private readonly SemaphoreSlim _reconnectGate = new SemaphoreSlim(1, 1);
        private readonly Timer _monitorTimer;
        private readonly System.Threading.Lock _lock = new();
        private bool _disposed;
        private int _totalReconnections;

        private static string GetSessionKey(Account? account)
        {
            if (account == null) return string.Empty;
            if (!string.IsNullOrWhiteSpace(account.Username)) return account.Username;
            return account.UserID > 0 ? $"uid:{account.UserID}" : string.Empty;
        }

        private const string LegacyAutoReconnectKey = "EnableAutoReconnect";

        private static bool ReadAutoReconnectSetting()
        {
            var settings = AppServices.GeneralSettings;
            if (settings.Exists(AppSettingsKeys.General.AutoReconnect))
                return settings.Get<bool>(AppSettingsKeys.General.AutoReconnect, false);

            return settings.Exists(LegacyAutoReconnectKey)
                && settings.Get<bool>(LegacyAutoReconnectKey, false);
        }

        public bool IsEnabled
        {
            get => _isEnabled && ReadAutoReconnectSetting();
            set
            {
                _isEnabled = value;
                AppServices.GeneralSettings.Set(AppSettingsKeys.General.AutoReconnect, value ? "true" : "false");
                if (!value)
                {
                    foreach (var s in _sessions.Values) s?.CancelPendingReconnect();
                }
            }
        }
        private bool _isEnabled = true;
        public int MaxRetries { get; set; } = 5;
        public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(10);
        internal int? TestReconnectDelayMsOverride { get; set; }
        internal Func<ReconnectSession, Task<string>>? TestJoinServerOverride { get; set; }
        public TimeSpan LaunchGracePeriod { get; set; } = TimeSpan.FromSeconds(15);
        public TimeSpan DisconnectTimeout { get; set; } = TimeSpan.FromSeconds(20);
        public TimeSpan StableResetWindow { get; set; } = TimeSpan.FromMinutes(10);
        public int ActiveSessionsCount
        {
            get
            {
                int count = 0;
                foreach (var kvp in _sessions)
                {
                    var s = kvp.Value;
                    if (s != null && s.IsActive && !s.IsSuppressed)
                        count++;
                }
                return count;
            }
        }
        public int TotalReconnections => _totalReconnections;

        public event Action<ReconnectSession, int>? ReconnectAttempted;
        public event Action<ReconnectSession>? MaxRetriesExceeded;

        public AutoReconnectService() : this(null) { }

        internal AutoReconnectService(TimeSpan? monitorInterval)
        {
            RobloxProcess.Disconnected += NotifyDisconnected;
            RobloxProcess.Connected += NotifyConnected;
            RobloxJobObjectService.ProcessExitedFromJob += OnJobProcessExited;
            MaxRetriesExceeded += OnMaxRetriesExceeded;

            TimeSpan interval = monitorInterval ?? TimeSpan.FromSeconds(5);
            _monitorTimer = new Timer(CheckSessionsCallback, null, interval, interval);
        }

        private static void OnMaxRetriesExceeded(ReconnectSession session)
        {
            if (session?.Account?.Username is { } username)
            {
                WebhookNotificationService.EnqueueAlert(new WebhookAlert
                {
                    AccountUsername = username,
                    EventType = WebhookEventType.MaxRetriesExceeded,
                    Description = $"Exceeded max reconnect attempts ({session.RetryCount}).",
                    Timestamp = DateTimeOffset.UtcNow
                });
            }
        }

        public void NotifyDisconnected(int processId, int reason = 0)
        {
            if (processId <= 0 || _disposed) return;

            foreach (var kvp in _sessions)
            {
                var session = kvp.Value;
                if (session.ProcessId == processId && session.IsActive && !session.IsSuppressed)
                {
                    bool isFirstDisconnect = !session.LastDisconnectUtc.HasValue;
                    if (isFirstDisconnect)
                    {
                        session.LastDisconnectUtc = DateTime.UtcNow;
                    }
                    session.LastDisconnectReason = reason;
                    AppServices.Logger?.Warn($"[AutoReconnect] Detected in-game disconnect (reason {reason}) for '{session.Account?.Username}' (PID: {processId}).");

                    if (isFirstDisconnect && session.Account?.Username is { } username)
                    {
                        WebhookNotificationService.EnqueueAlert(new WebhookAlert
                        {
                            AccountUsername = username,
                            EventType = WebhookEventType.Disconnected,
                            Description = reason > 0
                                ? $"Roblox client disconnected with Error {reason}."
                                : "Roblox client disconnected.",
                            Timestamp = DateTimeOffset.UtcNow
                        });
                    }
                }
            }
        }

        public void NotifyConnected(int processId)
        {
            if (processId <= 0 || _disposed) return;

            foreach (var kvp in _sessions)
            {
                var session = kvp.Value;
                if (session.ProcessId == processId && session.LastDisconnectUtc.HasValue)
                {
                    session.LastDisconnectUtc = null;
                    session.LastDisconnectReason = 0;
                }
            }
        }

        /// <summary>
        /// Registers or updates an active launched game session for monitoring.
        /// Preserves RetryCount if re-registered within StableResetWindow, resetting it only after stable play.
        /// </summary>
        public void RegisterSession(Account account, long placeId, string jobId = "", bool followUser = false, bool joinVip = false, int processId = 0, DateTime? nowUtcOverride = null)
        {
            string sessionKey = GetSessionKey(account);
            if (account == null || string.IsNullOrWhiteSpace(sessionKey))
                return;

            DateTime now = nowUtcOverride ?? DateTime.UtcNow;
            ReconnectSession session;
            int preservedRetries = 0;
            lock (_lock)
            {
                DateTime? preservedLastReconnect = null;
                if (_sessions.TryGetValue(sessionKey, out var existing) && existing != null)
                {
                    existing.IsSuppressed = true;
                    existing.IsActive = false;
                    existing.CancelPendingReconnect();
                    DateTime baseline = existing.LastReconnectUtc ?? existing.LaunchTimeUtc;
                    if (existing.RetryCount > 0 && (now - baseline) < StableResetWindow)
                    {
                        preservedRetries = existing.RetryCount;
                        preservedLastReconnect = existing.LastReconnectUtc;
                    }
                }

                session = new ReconnectSession
                {
                    Account = account,
                    PlaceId = placeId,
                    JobId = jobId ?? string.Empty,
                    FollowUser = followUser,
                    JoinVip = joinVip,
                    ProcessId = processId,
                    RetryCount = preservedRetries,
                    LaunchTimeUtc = now,
                    LastCheckUtc = now,
                    LastReconnectUtc = preservedLastReconnect,
                    LastDisconnectUtc = null,
                    LastDisconnectReason = 0,
                    IsActive = true,
                    IsSuppressed = false
                };

                _sessions[sessionKey] = session;
            }
            AppServices.Logger?.Info($"[AutoReconnect] Registered session for '{sessionKey}' (PID: {processId}, PlaceId: {placeId}, RetryCount: {preservedRetries}).");

            if (processId > 0)
            {
                _ = ObserveProcessExitImmediateAsync(session);
            }
        }

        internal void HandleProcessExit(ReconnectSession session)
        {
            if (_disposed || !IsEnabled)
                return;

            lock (_lock)
            {
                if (!session.IsActive || session.IsSuppressed)
                    return;

                session.IsActive = false;
                session.NeedsRetry = false;
                session.ProcessId = 0;
                session.ObservedProcessExited = false;
                session.ObservedExitCode = null;
            }

            string sessionKey = GetSessionKey(session.Account);
            if (session.RetryCount >= MaxRetries)
            {
                AppServices.Logger?.Warn($"[AutoReconnect] '{session.Account.Username}' exceeded max reconnect attempts ({MaxRetries}). Aborting.");
                MaxRetriesExceeded?.Invoke(session);
                _sessions.TryRemove(sessionKey, out _);
                return;
            }

            session.RetryCount++;
            session.LastReconnectUtc = DateTime.UtcNow;
            session.LastDisconnectUtc = null;
            int delaySeconds = (int)(InitialBackoff.TotalSeconds * Math.Pow(1.5, session.RetryCount - 1));
            delaySeconds = Math.Clamp(delaySeconds, 5, 60);
            CancellationToken reconnectToken = session.ResetReconnectToken(CancellationToken.None);

            AppServices.Logger?.Warn($"[AutoReconnect] Roblox client for '{session.Account.Username}' terminated/disconnected. Scheduling reconnect attempt #{session.RetryCount}/{MaxRetries} in {delaySeconds}s...");
            ReconnectAttempted?.Invoke(session, session.RetryCount);

            Task.Run(async () =>
            {
                try
                {
                    int waitMs = TestReconnectDelayMsOverride ?? (delaySeconds * 1000);
                    await Task.Delay(waitMs, reconnectToken).ConfigureAwait(false);

                    if (_disposed || !IsEnabled || session.IsSuppressed ||
                        !_sessions.TryGetValue(sessionKey, out var activeSession) || !ReferenceEquals(activeSession, session))
                        return;

                    if (!TryEnterReconnectGate())
                        return;

                    bool gateAcquired = false;
                    try
                    {
                        await _reconnectGate.WaitAsync(reconnectToken).ConfigureAwait(false);
                        gateAcquired = true;

                        if (_disposed || !IsEnabled || session.IsSuppressed ||
                            !_sessions.TryGetValue(sessionKey, out activeSession) || !ReferenceEquals(activeSession, session))
                            return;

                        await RobloxRateLimitCoordinator.Instance.WaitForBackoffAsync(reconnectToken).ConfigureAwait(false);

                        AppServices.Logger?.Info($"[AutoReconnect] Reconnecting '{session.Account.Username}' to PlaceId: {session.PlaceId}...");
                        string result = TestJoinServerOverride != null
                            ? await TestJoinServerOverride(session).ConfigureAwait(false)
                            : await session.Account.JoinServer(session.PlaceId, session.JobId, session.FollowUser, session.JoinVip).ConfigureAwait(false);

                        if (result == "Success")
                        {
                            Interlocked.Increment(ref _totalReconnections);
                            lock (_lock)
                            {
                                session.LaunchTimeUtc = DateTime.UtcNow;
                                session.ObservedProcessExited = false;
                                session.ObservedExitCode = null;
                                session.NeedsRetry = false;
                                session.IsActive = true;
                            }
                            AppServices.Logger?.Info($"[AutoReconnect] Successfully reconnected '{session.Account.Username}'.");
                        }
                        else
                        {
                            AppServices.Logger?.Warn($"[AutoReconnect] Reconnect for '{session.Account.Username}' failed: {result}");
                            lock (_lock)
                            {
                                session.NeedsRetry = true;
                                session.IsActive = true;
                            }
                        }
                    }
                    catch (ObjectDisposedException) when (_disposed)
                    {
                        /* disposed concurrently during shutdown */
                    }
                    catch (OperationCanceledException) when (reconnectToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        AppServices.Logger?.Error($"[AutoReconnect] Reconnect task error for '{session.Account.Username}': {ex.Message}");
                        lock (_lock)
                        {
                            session.NeedsRetry = true;
                            session.IsActive = true;
                        }
                    }
                    finally
                    {
                        ExitReconnectGate(gateAcquired);
                    }
                }
                    catch (OperationCanceledException) { /* canceled or disabled during backoff */ }
                catch (Exception ex)
                {
                    AppServices.Logger?.Error($"[AutoReconnect] Unexpected backoff loop error for '{session.Account.Username}': {ex.Message}");
                }
            }, reconnectToken);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            RobloxProcess.Disconnected -= NotifyDisconnected;
            RobloxProcess.Connected -= NotifyConnected;
            RobloxJobObjectService.ProcessExitedFromJob -= OnJobProcessExited;
            MaxRetriesExceeded -= OnMaxRetriesExceeded;
            _monitorTimer?.Dispose();
            foreach (var session in _sessions.Values)
                session?.CancelPendingReconnect();
            _sessions.Clear();
            if (Volatile.Read(ref _activeGateUsers) == 0)
                TryDisposeReconnectGate();
        }
    }
}
