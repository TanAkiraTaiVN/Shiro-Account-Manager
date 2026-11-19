#nullable enable
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public enum RateLimitCategory
    {
        SessionCheck,
        Authentication,
        Presence,
        General
    }

    /// <summary>
    /// Coordinates all Roblox API calls across the application to prevent HTTP 429 (Too Many Requests),
    /// manage concurrency per endpoint category, and handle adaptive backoff automatically.
    /// </summary>
    public sealed class RobloxRateLimitCoordinator
    {
        private static readonly Lazy<RobloxRateLimitCoordinator> _instance =
            new Lazy<RobloxRateLimitCoordinator>(() => new RobloxRateLimitCoordinator(AppServices.Logger), LazyThreadSafetyMode.ExecutionAndPublication);

        public static RobloxRateLimitCoordinator Default => _instance.Value;
        public static RobloxRateLimitCoordinator Instance => _instance.Value;

        private sealed class CategoryBackoffState
        {
            public DateTime BackoffUntilUtc = DateTime.MinValue;
            public int Consecutive429Count;
        }

        private readonly IAppLogger _logger;
        private readonly ConcurrentDictionary<RateLimitCategory, SemaphoreSlim> _categorySemaphores;
        private readonly System.Collections.Generic.Dictionary<RateLimitCategory, DateTime> _nextAllowedSlotUtc;
        private readonly System.Collections.Generic.Dictionary<RateLimitCategory, CategoryBackoffState> _categoryBackoffs;
        private readonly System.Threading.Lock _backoffLock = new();
        private readonly System.Threading.Lock _pacingLock = new();

        public const int MaxBackoffSeconds = 30;
        public const int MaxRetryAfterSeconds = 120;

        public int ConsecutiveRateLimitHits
        {
            get
            {
                lock (_backoffLock)
                {
                    int total = 0;
                    foreach (var state in _categoryBackoffs.Values)
                        total += state.Consecutive429Count;
                    return total;
                }
            }
        }

        public RobloxRateLimitCoordinator() : this(AppServices.Logger) { }

        public RobloxRateLimitCoordinator(IAppLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _categorySemaphores = new ConcurrentDictionary<RateLimitCategory, SemaphoreSlim>
            {
                [RateLimitCategory.SessionCheck] = new SemaphoreSlim(12, 12),
                [RateLimitCategory.Authentication] = new SemaphoreSlim(2, 2),
                [RateLimitCategory.Presence] = new SemaphoreSlim(2, 2),
                [RateLimitCategory.General] = new SemaphoreSlim(6, 6)
            };

            _nextAllowedSlotUtc = new System.Collections.Generic.Dictionary<RateLimitCategory, DateTime>();
            _categoryBackoffs = new System.Collections.Generic.Dictionary<RateLimitCategory, CategoryBackoffState>();
        }

        private CategoryBackoffState GetOrCreateBackoffStateUnderLock(RateLimitCategory category)
        {
            if (!_categoryBackoffs.TryGetValue(category, out var state))
            {
                state = new CategoryBackoffState();
                _categoryBackoffs[category] = state;
            }
            return state;
        }

        public bool IsBackingOff
        {
            get
            {
                DateTime now = DateTime.UtcNow;
                lock (_backoffLock)
                {
                    foreach (var state in _categoryBackoffs.Values)
                    {
                        if (now < state.BackoffUntilUtc)
                            return true;
                    }
                    return false;
                }
            }
        }

        public bool IsBackingOffCategory(RateLimitCategory category)
        {
            DateTime now = DateTime.UtcNow;
            lock (_backoffLock)
            {
                return _categoryBackoffs.TryGetValue(category, out var state) && now < state.BackoffUntilUtc;
            }
        }

        public TimeSpan RemainingBackoff
        {
            get
            {
                DateTime now = DateTime.UtcNow;
                TimeSpan max = TimeSpan.Zero;
                lock (_backoffLock)
                {
                    foreach (var state in _categoryBackoffs.Values)
                    {
                        var rem = state.BackoffUntilUtc - now;
                        if (rem > max)
                            max = rem;
                    }
                }
                return max;
            }
        }

        public TimeSpan GetRemainingBackoff(RateLimitCategory category)
        {
            DateTime now = DateTime.UtcNow;
            lock (_backoffLock)
            {
                if (!_categoryBackoffs.TryGetValue(category, out var state))
                    return TimeSpan.Zero;
                var rem = state.BackoffUntilUtc - now;
                return rem > TimeSpan.Zero ? rem : TimeSpan.Zero;
            }
        }

        public void RecordRateLimitHit(RateLimitCategory category, HttpResponseMessage? response = null, int defaultBackoffSeconds = 4)
        {
            lock (_backoffLock)
            {
                var state = GetOrCreateBackoffStateUnderLock(category);
                state.Consecutive429Count++;

                double backoffSeconds = -1;

                // Try parse Retry-After header if present
                if (response?.Headers?.RetryAfter != null)
                {
                    if (response.Headers.RetryAfter.Delta.HasValue)
                    {
                        backoffSeconds = response.Headers.RetryAfter.Delta.Value.TotalSeconds;
                    }
                    else if (response.Headers.RetryAfter.Date.HasValue)
                    {
                        var diff = response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
                        if (diff.TotalSeconds > 0)
                            backoffSeconds = diff.TotalSeconds;
                    }
                }

                if (backoffSeconds <= 0)
                {
                    // Exponential backoff: 3s, 6s, 12s, 24s, 30s (capped at MaxBackoffSeconds)
                    double exponential = Math.Min(MaxBackoffSeconds, 3 * Math.Pow(2, Math.Min(state.Consecutive429Count - 1, 4)));
                    backoffSeconds = Math.Max(defaultBackoffSeconds, exponential);
                }

                backoffSeconds = Math.Clamp(backoffSeconds, 1, MaxRetryAfterSeconds);
                int jitterMs = Random.Shared.Next(200, 1000);

                DateTime proposedExpiry = DateTime.UtcNow.AddSeconds(backoffSeconds).AddMilliseconds(jitterMs);
                if (proposedExpiry > state.BackoffUntilUtc)
                {
                    state.BackoffUntilUtc = proposedExpiry;
                    _logger.Warn($"[RateLimitCoordinator] HTTP 429 Rate-Limit hit #{state.Consecutive429Count} for {category}. Backing off {category} for {backoffSeconds:0.#}s (+{jitterMs}ms jitter).");

                    WebhookNotificationService.EnqueueAlert(new WebhookAlert
                    {
                        EventType = WebhookEventType.RateLimited,
                        Description = $"HTTP 429 Rate-Limit hit #{state.Consecutive429Count} for {category}. Backing off for {backoffSeconds:0.#}s.",
                        Timestamp = DateTimeOffset.UtcNow
                    });
                }
            }
        }

        public void RecordSuccess(RateLimitCategory category)
        {
            lock (_backoffLock)
            {
                if (_categoryBackoffs.TryGetValue(category, out var state) && state.Consecutive429Count > 0)
                {
                    state.Consecutive429Count = 0;
                }
            }
        }

        /// <summary>
        /// Explicitly resets any active rate limit backoff and consecutive error count.
        /// Useful when network conditions change or when testing.
        /// </summary>
        public void ResetBackoff()
        {
            lock (_backoffLock)
            {
                _categoryBackoffs.Clear();
            }

            lock (_pacingLock)
            {
                _nextAllowedSlotUtc.Clear();
            }
        }

        /// <summary>
        /// Atomically reserves the next available execution time slot for <paramref name="category"/>,
        /// ensuring concurrent callers stagger smoothly by <paramref name="minIntervalMs"/> instead of bursting together.
        /// </summary>
        internal TimeSpan ReserveNextSlotDelay(RateLimitCategory category, int minIntervalMs, DateTime utcNow)
        {
            if (minIntervalMs <= 0)
                return TimeSpan.Zero;

            lock (_pacingLock)
            {
                DateTime nextSlot = _nextAllowedSlotUtc.TryGetValue(category, out DateTime existing) && existing > utcNow
                    ? existing
                    : utcNow;

                _nextAllowedSlotUtc[category] = nextSlot.AddMilliseconds(minIntervalMs);
                return nextSlot > utcNow ? nextSlot - utcNow : TimeSpan.Zero;
            }
        }

        /// <summary>
        /// Waits for any active rate-limit backoff to clear before allowing requests to proceed.
        /// </summary>
        public async Task WaitForBackoffAsync(CancellationToken cancellationToken = default)
        {
            while (true)
            {
                TimeSpan delay = RemainingBackoff;
                if (delay <= TimeSpan.Zero)
                    return;

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Waits for active rate-limit backoff on a specific category before allowing requests to proceed.
        /// </summary>
        public async Task WaitForBackoffAsync(RateLimitCategory category, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                TimeSpan delay = GetRemainingBackoff(category);
                if (delay <= TimeSpan.Zero)
                    return;

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        private static readonly AsyncLocal<int> ActiveReentrancyDepth = new();

        /// <summary>
        /// Executes an API action with category-specific concurrency control, delay pacing, and automatic rate-limit backoff.
        /// </summary>
        public async Task<T> ExecuteWithPacingAsync<T>(
            RateLimitCategory category,
            Func<Task<T>> action,
            int minIntervalMs = 150,
            CancellationToken cancellationToken = default)
        {
            // Prevent nested/re-entrant ExecuteWithPacingAsync calls on the same async flow from starving permits.
            if (ActiveReentrancyDepth.Value > 0)
            {
                return await action().ConfigureAwait(false);
            }

            // Wait for category backoff & staggered slot BEFORE claiming a concurrency permit.
            await WaitForBackoffAsync(category, cancellationToken).ConfigureAwait(false);
            TimeSpan slotDelay = ReserveNextSlotDelay(category, minIntervalMs, DateTime.UtcNow);
            if (slotDelay > TimeSpan.Zero)
            {
                await Task.Delay(slotDelay, cancellationToken).ConfigureAwait(false);
                // Re-check backoff in case another request triggered 429 during the slot wait.
                await WaitForBackoffAsync(category, cancellationToken).ConfigureAwait(false);
            }

            var sem = _categorySemaphores.GetOrAdd(category, _ => new SemaphoreSlim(2, 2));
            await sem.WaitAsync(cancellationToken).ConfigureAwait(false);
            int prevDepth = ActiveReentrancyDepth.Value;
            ActiveReentrancyDepth.Value = prevDepth + 1;
            try
            {
                return await action().ConfigureAwait(false);
            }
            finally
            {
                ActiveReentrancyDepth.Value = prevDepth;
                sem.Release();
            }
        }
    }

    public sealed class RobloxAuthenticatedUser
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public long Id { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = string.Empty;
    }

    public sealed class RobloxFriendsCountResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("count")]
        public int Count { get; set; }
    }

    public sealed class RobloxCurrencyResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("robux")]
        public long Robux { get; set; }
    }
}
