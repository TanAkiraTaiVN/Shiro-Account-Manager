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

        private readonly IAppLogger _logger;
        private readonly ConcurrentDictionary<RateLimitCategory, SemaphoreSlim> _categorySemaphores;
        private readonly System.Collections.Generic.Dictionary<RateLimitCategory, DateTime> _nextAllowedSlotUtc;
        private readonly System.Threading.Lock _backoffLock = new();
        private readonly System.Threading.Lock _pacingLock = new();

        private DateTime _backoffUntilUtc = DateTime.MinValue;
        private int _consecutive429Count = 0;

        public const int MaxBackoffSeconds = 30;
        public const int MaxRetryAfterSeconds = 120;

        public int ConsecutiveRateLimitHits
        {
            get
            {
                lock (_backoffLock)
                {
                    return _consecutive429Count;
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
        }

        public bool IsBackingOff
        {
            get
            {
                lock (_backoffLock)
                {
                    return DateTime.UtcNow < _backoffUntilUtc;
                }
            }
        }

        public TimeSpan RemainingBackoff
        {
            get
            {
                lock (_backoffLock)
                {
                    var rem = _backoffUntilUtc - DateTime.UtcNow;
                    return rem > TimeSpan.Zero ? rem : TimeSpan.Zero;
                }
            }
        }

        public void RecordRateLimitHit(RateLimitCategory category, HttpResponseMessage? response = null, int defaultBackoffSeconds = 4)
        {
            lock (_backoffLock)
            {
                _consecutive429Count++;

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
                    double exponential = Math.Min(MaxBackoffSeconds, 3 * Math.Pow(2, Math.Min(_consecutive429Count - 1, 4)));
                    backoffSeconds = Math.Max(defaultBackoffSeconds, exponential);
                }

                backoffSeconds = Math.Clamp(backoffSeconds, 1, MaxRetryAfterSeconds);
                int jitterMs = Random.Shared.Next(200, 1000);

                DateTime proposedExpiry = DateTime.UtcNow.AddSeconds(backoffSeconds).AddMilliseconds(jitterMs);
                if (proposedExpiry > _backoffUntilUtc)
                {
                    _backoffUntilUtc = proposedExpiry;
                    _logger.Warn($"[RateLimitCoordinator] HTTP 429 Rate-Limit hit #{_consecutive429Count} for {category}. Backing off for {backoffSeconds:0.#}s (+{jitterMs}ms jitter) across application.");

                    WebhookNotificationService.EnqueueAlert(new WebhookAlert
                    {
                        EventType = WebhookEventType.RateLimited,
                        Description = $"HTTP 429 Rate-Limit hit #{_consecutive429Count} for {category}. Backing off for {backoffSeconds:0.#}s.",
                        Timestamp = DateTimeOffset.UtcNow
                    });
                }
            }
        }

        public void RecordSuccess(RateLimitCategory category)
        {
            lock (_backoffLock)
            {
                if (_consecutive429Count > 0)
                {
                    _consecutive429Count = 0;
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
                _consecutive429Count = 0;
                _backoffUntilUtc = DateTime.MinValue;
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
            TimeSpan delay;
            lock (_backoffLock)
            {
                delay = _backoffUntilUtc - DateTime.UtcNow;
            }

            if (delay > TimeSpan.Zero)
            {
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

            // Wait for any active backoff & staggered slot BEFORE claiming a concurrency permit.
            await WaitForBackoffAsync(cancellationToken).ConfigureAwait(false);
            TimeSpan slotDelay = ReserveNextSlotDelay(category, minIntervalMs, DateTime.UtcNow);
            if (slotDelay > TimeSpan.Zero)
            {
                await Task.Delay(slotDelay, cancellationToken).ConfigureAwait(false);
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
