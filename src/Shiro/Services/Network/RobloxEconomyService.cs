#nullable enable
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;

namespace Shiro.Services
{
    public sealed class RobloxEconomyService
    {
        public static RobloxEconomyService Instance { get; } = new();

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
                            if (Utilities.FastJson.TryDeserialize<RobloxCurrencyResponse>(json, out var res) && res != null)
                            {
                                account.RobuxBalance = res.Robux;
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
                Program.Logger.Debug($"[FetchRobuxBalanceAsync] {account.Username}: {ex.Message}");
                return account.RobuxBalance;
            }
        }
    }
}
