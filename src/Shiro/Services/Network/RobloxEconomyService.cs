#nullable enable
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public sealed class RobloxEconomyService
    {
        private static readonly Lazy<RobloxEconomyService> _defaultInstance = new(() => new RobloxEconomyService(AppServices.Logger));
        public static RobloxEconomyService Default => _defaultInstance.Value;
        public static RobloxEconomyService Instance => Default;

        private readonly IAppLogger _logger;

        public RobloxEconomyService() : this(AppServices.Logger) { }

        public RobloxEconomyService(IAppLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        internal static bool TryParseCurrencyBalance(string? json, out long robux)
        {
            robux = 0;
            if (string.IsNullOrWhiteSpace(json) || json.AsSpan().TrimStart().StartsWith("<".AsSpan(), StringComparison.Ordinal))
                return false;

            if (FastJson.TryDeserialize<RobloxCurrencyResponse>(json, out var res) && res != null)
            {
                robux = res.Robux;
                return true;
            }

            return false;
        }

        public async Task<long> FetchRobuxBalanceAsync(Account account, CancellationToken cancellationToken = default)
        {
            if (account == null || account.UserID <= 0 || string.IsNullOrWhiteSpace(account.SecurityToken) || !account.Valid)
                return account?.RobuxBalance ?? 0;

            try
            {
                long balance = await RobloxRateLimitCoordinator.Instance.ExecuteWithPacingAsync(
                    RateLimitCategory.General,
                    async () =>
                    {
                        HttpClient httpClient = account.GetSessionHttpClient();
                        using var request = account.CreateAuthenticatedGetRequest(
                            $"https://economy.roblox.com/v1/users/{account.UserID}/currency",
                            "application/json");

                        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
                        account.TryUpdateRotatedCookie(response);
                        if (response.IsSuccessStatusCode)
                        {
                            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                            if (TryParseCurrencyBalance(json, out long robux))
                            {
                                account.RobuxBalance = robux;
                                account.LastEconomyCheck = DateTime.UtcNow;
                                RobloxRateLimitCoordinator.Instance.RecordSuccess(RateLimitCategory.General);
                                return account.RobuxBalance;
                            }
                        }
                        else if ((int)response.StatusCode == 429)
                        {
                            RobloxRateLimitCoordinator.Instance.RecordRateLimitHit(RateLimitCategory.General, response);
                        }
                        return account.RobuxBalance;
                    },
                    minIntervalMs: 250,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                return balance;
            }
            catch (Exception ex)
            {
                _logger.Debug($"[FetchRobuxBalanceAsync] {account.Username}: {ex.Message}");
                return account.RobuxBalance;
            }
        }
    }
}
