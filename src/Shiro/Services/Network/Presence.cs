#nullable enable
using Shiro.Http;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Shiro.Services;

namespace Shiro.Services
{
    public static class Presence
    {
        public static Dictionary<UserPresenceType, Color> Colors = new Dictionary<UserPresenceType, Color> {
            { UserPresenceType.Online, Color.FromArgb(255, 210, 210, 210) },
            { UserPresenceType.InGame, Color.FromArgb(255, 245, 245, 245) },
            { UserPresenceType.InStudio, Color.FromArgb(255, 170, 170, 170) }
        };

        public static RestClient PresenceClient => AppServices.RobloxApi.Presence
            ?? throw new InvalidOperationException("Roblox API clients have not been initialized.");
        private static RestClient GamesClient => AppServices.RobloxApi.Games
            ?? throw new InvalidOperationException("Roblox API clients have not been initialized.");
        private static readonly Dictionary<long, string> GameNameCache = new Dictionary<long, string>();
        private static readonly System.Threading.Lock GameNameCacheLock = new();
        private static readonly SemaphoreSlim UpdateLock = new SemaphoreSlim(1, 1);

        private static readonly AsyncLocal<Func<string?, HttpClient?>?> _testProxyHttpClientFactory = new();
        internal static Func<string?, HttpClient?>? TestProxyHttpClientFactory { get => _testProxyHttpClientFactory.Value; set => _testProxyHttpClientFactory.Value = value; }

        private sealed record ProxyGroup<TItem>(
            string ProxyKey,
            string? ProxyScope,
            Account? Representative,
            List<TItem> Items);

        public static Task UpdatePresence(params long[] UserIds)
        {
            return UpdatePresence(CancellationToken.None, UserIds);
        }

        private static Dictionary<long, Account> BuildAccountsById(IReadOnlyList<Account> accountsSnapshot)
        {
            var accountsById = new Dictionary<long, Account>(accountsSnapshot.Count);
            foreach (Account account in accountsSnapshot)
            {
                if (account != null && account.UserID > 0)
                    accountsById[account.UserID] = account;
            }
            return accountsById;
        }

        private static (string ProxyKey, string? ProxyScope, Account? Representative) ResolveProxyIdentity(
            long userId,
            IReadOnlyDictionary<long, Account> accountsById)
        {
            if (!accountsById.TryGetValue(userId, out Account? account) || account == null)
                return (string.Empty, null, null);

            string? effectiveProxy = ProxyBindingService.GetEffectiveProxyForAccount(account);
            if (string.IsNullOrWhiteSpace(effectiveProxy))
                return (string.Empty, null, account);

            string normalizedEndpoint = NetworkSignatureHelper.NormalizeAccountProxyEndpoint(effectiveProxy) ?? effectiveProxy.Trim();
            ProxyItem? parsed = ProxyManagerService.ParseProxy(effectiveProxy);
            string groupKey = parsed != null && !string.IsNullOrEmpty(parsed.Username)
                ? $"{parsed.Username}@{normalizedEndpoint}"
                : normalizedEndpoint;
            return (groupKey, normalizedEndpoint, account);
        }

        private static List<ProxyGroup<TItem>> GroupByEffectiveProxy<TItem>(
            IEnumerable<TItem> items,
            Func<TItem, long> userIdSelector,
            IReadOnlyDictionary<long, Account> accountsById)
        {
            var groups = new List<ProxyGroup<TItem>>();
            var indexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (TItem item in items)
            {
                long userId = userIdSelector(item);
                var (proxyKey, proxyScope, rep) = ResolveProxyIdentity(userId, accountsById);
                if (!indexByKey.TryGetValue(proxyKey, out int idx))
                {
                    idx = groups.Count;
                    indexByKey[proxyKey] = idx;
                    groups.Add(new ProxyGroup<TItem>(proxyKey, proxyScope, rep, new List<TItem>()));
                }
                groups[idx].Items.Add(item);
            }

            return groups;
        }

        private static HttpClient? ResolveBatchHttpClient<TItem>(ProxyGroup<TItem> group)
        {
            if (string.IsNullOrEmpty(group.ProxyKey))
                return null;

            if (TestProxyHttpClientFactory != null)
                return TestProxyHttpClientFactory(group.ProxyScope);

            return group.Representative != null
                ? ProxyBindingService.GetClient(group.Representative)
                : ProxyBindingService.GetClient(group.ProxyKey);
        }

        private static async Task<bool> ExecutePresenceBatchesAsync(
            long[] uniqueUserIds,
            IReadOnlyDictionary<long, Account> accountsById,
            bool attachSessionAuth,
            Func<List<UserPresence>, Task> onPresencesReceived,
            CancellationToken cancellationToken)
        {
            bool receivedAny = false;
            var groups = GroupByEffectiveProxy(uniqueUserIds, static id => id, accountsById);

            foreach (var group in groups)
            {
                HttpClient? clientOverride;
                try
                {
                    clientOverride = ResolveBatchHttpClient(group);
                }
                catch (Exception ex)
                {
                    AppServices.Logger.Warn($"Presence proxy resolution failed: {LogRedactor.Redact(ex.Message)}");
                    continue;
                }

                // Roblox accepts up to 50 user IDs per Presence request.
                for (int offset = 0; offset < group.Items.Count; offset += 50)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await RobloxRateLimitCoordinator.Instance.WaitForBackoffAsync(
                        RateLimitCategory.Presence,
                        group.ProxyScope,
                        cancellationToken).ConfigureAwait(false);

                    int count = Math.Min(50, group.Items.Count - offset);
                    long[] batch = group.Items.GetRange(offset, count).ToArray();
                    (bool Success, RobloxPresenceEnvelope? Data, System.Net.HttpStatusCode StatusCode, string? ErrorMessage) result;
                    try
                    {
                        var req = new RestRequest("v1/presence/users", Method.Post)
                            .AddJsonBody(new RobloxPresenceUsersRequest(batch));
                        if (attachSessionAuth && group.Representative != null && !string.IsNullOrWhiteSpace(group.Representative.SecurityToken))
                        {
                            string cookie = group.Representative.BuildCookieHeaderValue();
                            if (!string.IsNullOrEmpty(cookie))
                                req.AddHeader("Cookie", cookie);
                            AppServices.AccountApiService.ApplySessionFingerprintAndHbaToRestRequest(
                                group.Representative,
                                req,
                                PresenceClient.BaseUrl ?? "https://presence.roblox.com/");
                        }

                        result = await PresenceClient.ExecuteJsonAsync(
                            req,
                            ShiroAppJson.Default.RobloxPresenceEnvelope,
                            clientOverride,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        AppServices.Logger.Warn($"Presence request failed: {LogRedactor.Redact(exception.Message)}");
                        continue;
                    }

                    if (!result.Success)
                    {
                        AppServices.Logger.Warn($"Presence request returned HTTP {(int)result.StatusCode} {result.StatusCode}: {result.ErrorMessage}");
                        if ((int)result.StatusCode == 429)
                        {
                            RobloxRateLimitCoordinator.Instance.RecordRateLimitHit(
                                RateLimitCategory.Presence,
                                defaultBackoffSeconds: 6,
                                proxyScope: group.ProxyScope);
                            AppServices.Logger.Warn("Roblox presence API rate limit (429) hit, aborting remaining presence batches for proxy group.");
                            break;
                        }
                        continue;
                    }

                    RobloxRateLimitCoordinator.Instance.RecordSuccess(RateLimitCategory.Presence, group.ProxyScope);

                    List<UserPresence>? presences = result.Data?.userPresences;
                    if (presences == null)
                    {
                        AppServices.Logger.Warn("Presence response did not contain userPresences.");
                        continue;
                    }

                    receivedAny = true;
                    await onPresencesReceived(presences).ConfigureAwait(false);

                    if (offset + 50 < group.Items.Count)
                        await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                }
            }

            return receivedAny;
        }

        /// <summary>
        /// Refresh presence without leaving a non-cancellable RestSharp call
        /// behind during application shutdown or when a newer refresh
        /// supersedes an older one.
        /// </summary>
        public static async Task UpdatePresence(CancellationToken cancellationToken, params long[] UserIds)
        {
            if (cancellationToken.IsCancellationRequested || UserIds == null) return;

            long[] uniqueUserIds = UserIds.Where(id => id > 0).Distinct().ToArray();
            if (uniqueUserIds.Length == 0) return;

            await UpdateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var updated = new HashSet<Account>();
                var accountsSnapshot = AppServices.AccountRepository.GetAll();
                var accountsById = BuildAccountsById(accountsSnapshot);

                await ExecutePresenceBatchesAsync(
                    uniqueUserIds,
                    accountsById,
                    attachSessionAuth: true,
                    async presences =>
                    {
                        await PopulateGameNames(presences, accountsById, cancellationToken).ConfigureAwait(false);
                        foreach (UserPresence presence in presences)
                        {
                            if (presence == null) continue;
                            foreach (Account account in accountsSnapshot)
                            {
                                if (account != null && account.UserID == presence.userId)
                                {
                                    account.Presence = presence;
                                    updated.Add(account);
                                }
                            }
                        }
                    },
                    cancellationToken).ConfigureAwait(false);

                if (updated.Count > 0)
                {
                    AppServices.HostUiBridge.RefreshAccountView();
                }
            }
            finally
            {
                UpdateLock.Release();
            }
        }

        private static async Task PopulateGameNames(
            IEnumerable<UserPresence> presences,
            IReadOnlyDictionary<long, Account> accountsById,
            CancellationToken cancellationToken)
        {
            List<UserPresence> inGame = presences?
                .Where(presence => presence != null && presence.userPresenceType == UserPresenceType.InGame && presence.universeId.HasValue && presence.universeId.Value > 0)
                .ToList() ?? new List<UserPresence>();

            if (inGame.Count == 0) return;

            var groups = GroupByEffectiveProxy(inGame, static p => p.userId, accountsById);
            foreach (var group in groups)
            {
                long[] missingUniverseIds;
                lock (GameNameCacheLock)
                {
                    missingUniverseIds = group.Items
                        .Select(presence => presence.universeId!.Value)
                        .Distinct()
                        .Where(universeId => !GameNameCache.ContainsKey(universeId))
                        .ToArray();
                }

                if (missingUniverseIds.Length == 0)
                    continue;

                HttpClient? clientOverride;
                try
                {
                    clientOverride = ResolveBatchHttpClient(group);
                }
                catch (Exception ex)
                {
                    AppServices.Logger.Debug($"[Presence] Game-name proxy resolution failed: {LogRedactor.Redact(ex.Message)}");
                    continue;
                }

                // The games endpoint accepts up to 50 universe IDs per request.
                for (int offset = 0; offset < missingUniverseIds.Length; offset += 50)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await RobloxRateLimitCoordinator.Instance.WaitForBackoffAsync(
                        RateLimitCategory.General,
                        group.ProxyScope,
                        cancellationToken).ConfigureAwait(false);

                    int batchEnd = Math.Min(offset + 50, missingUniverseIds.Length);
                    long[] batch = missingUniverseIds[offset..batchEnd];
                    try
                    {
                        var gamesResult = await GamesClient.ExecuteJsonAsync(
                            new RestRequest($"v1/games?universeIds={string.Join(",", batch)}", Method.Get),
                            ShiroAppJson.Default.RobloxGamesEnvelope,
                            clientOverride,
                            cancellationToken).ConfigureAwait(false);
                        if (!gamesResult.Success)
                        {
                            if ((int)gamesResult.StatusCode == 429)
                            {
                                RobloxRateLimitCoordinator.Instance.RecordRateLimitHit(
                                    RateLimitCategory.General,
                                    defaultBackoffSeconds: 5,
                                    proxyScope: group.ProxyScope);
                                AppServices.Logger.Warn("Roblox games API rate limit (429) hit, aborting remaining game-name batches for proxy group.");
                                break;
                            }
                            continue;
                        }

                        RobloxRateLimitCoordinator.Instance.RecordSuccess(RateLimitCategory.General, group.ProxyScope);
                        if (gamesResult.Data?.data != null)
                        {
                            lock (GameNameCacheLock)
                            {
                                if (GameNameCache.Count > 1000)
                                {
                                    foreach (long staleKey in GameNameCache.Keys.Take(500).ToList())
                                        GameNameCache.Remove(staleKey);
                                }

                                foreach (var game in gamesResult.Data.data)
                                {
                                    if (game.id <= 0 || string.IsNullOrWhiteSpace(game.name)) continue;
                                    string cleanName = GameInfoService.SanitizeDisplayTitle(game.name);
                                    if (!string.IsNullOrWhiteSpace(cleanName))
                                        GameNameCache[game.id] = cleanName;
                                }
                            }
                        }

                        if (offset + 50 < missingUniverseIds.Length)
                            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Presence should continue to work if the optional game-name request fails.
                        AppServices.Logger.Debug("[Presence] Optional game-name request failed", ex);
                    }
                }
            }

            lock (GameNameCacheLock)
            {
                foreach (UserPresence presence in inGame)
                {
                    if (presence.universeId.HasValue && GameNameCache.TryGetValue(presence.universeId.Value, out string? gameName))
                        presence.gameName = gameName;
                }
            }
        }

        public static string GetStatusText(Account? account, bool isLaunching = false)
        {
            if (account != null && !account.Valid) return Shiro.Services.LocalizationService.Get("Presence.SessionExpiredBadge");
            if (isLaunching) return "● Launching…";
            if (!AppServices.GeneralSettings.Get<bool>("ShowPresence", true)) return "● Ready";
            if (account?.Presence == null) return "● Checking…";

            switch (account.Presence.userPresenceType)
            {
                case UserPresenceType.InGame:
                    return string.IsNullOrWhiteSpace(account.Presence.gameName)
                        ? "● Playing Roblox"
                        : $"● Playing: {account.Presence.gameName}";
                case UserPresenceType.InStudio:
                    return "● In Roblox Studio";
                case UserPresenceType.Online:
                    return "● Online";
                default:
                    return "● Offline";
            }
        }

        public static Color GetStatusColor(Account? account, bool isLaunching = false)
        {
            if (account != null && !account.Valid) return Color.FromArgb(255, 255, 92, 92);
            if (isLaunching) return Color.FromArgb(255, 170, 170, 170);
            if (account?.Presence == null || !AppServices.GeneralSettings.Get<bool>("ShowPresence", true))
                return Color.FromArgb(255, 160, 160, 160);

            return Colors.TryGetValue(account.Presence.userPresenceType, out Color color)
                ? color
                : Color.FromArgb(255, 160, 160, 160);
        }

        public static Task<Dictionary<long, UserPresence>?> GetPresence(params long[] UserIds)
        {
            return GetPresence(CancellationToken.None, UserIds);
        }

        public static async Task<Dictionary<long, UserPresence>?> GetPresence(CancellationToken cancellationToken, params long[] UserIds)
        {
            if (cancellationToken.IsCancellationRequested || UserIds == null || UserIds.Length == 0)
                return new Dictionary<long, UserPresence>();

            long[] uniqueIds = UserIds.Where(id => id > 0).Distinct().ToArray();
            if (uniqueIds.Length == 0)
                return new Dictionary<long, UserPresence>();

            var result = new Dictionary<long, UserPresence>();
            var accountsById = BuildAccountsById(AppServices.AccountRepository.GetAll());
            bool receivedSuccessfulResponse = await ExecutePresenceBatchesAsync(
                uniqueIds,
                accountsById,
                attachSessionAuth: false,
                presences =>
                {
                    foreach (UserPresence presence in presences)
                    {
                        if (presence != null)
                            result[presence.userId] = presence;
                    }
                    return Task.CompletedTask;
                },
                cancellationToken).ConfigureAwait(false);

            return receivedSuccessfulResponse ? result : null;
        }
    }
}
