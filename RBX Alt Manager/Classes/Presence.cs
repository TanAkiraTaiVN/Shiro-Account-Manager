
#nullable enable
using RBX_Alt_Manager.Http;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using RBX_Alt_Manager.Services;

namespace RBX_Alt_Manager.Classes
{
    public static class Presence
    {
        public static Dictionary<UserPresenceType, Color> Colors = new Dictionary<UserPresenceType, Color> {
            { UserPresenceType.Online, Color.FromArgb(255, 210, 210, 210) },
            { UserPresenceType.InGame, Color.FromArgb(255, 245, 245, 245) },
            { UserPresenceType.InStudio, Color.FromArgb(255, 170, 170, 170) }
        };

        public static RestClient PresenceClient => AccountManager.PresenceClient
            ?? throw new InvalidOperationException("Roblox API clients have not been initialized.");
        private static RestClient GamesClient => AccountManager.GamesClient
            ?? throw new InvalidOperationException("Roblox API clients have not been initialized.");
        private static readonly Dictionary<long, string> GameNameCache = new Dictionary<long, string>();
        private static readonly System.Threading.Lock GameNameCacheLock = new();
        private static readonly SemaphoreSlim UpdateLock = new SemaphoreSlim(1, 1);

        public static Task UpdatePresence(params long[] UserIds)
        {
            return UpdatePresence(CancellationToken.None, UserIds);
        }

        /// <summary>
        /// Refresh presence without leaving a non-cancellable RestSharp call
        /// behind during application shutdown or when a newer refresh
        /// supersedes an older one.
        /// </summary>
        public static async Task UpdatePresence(CancellationToken cancellationToken, params long[] UserIds)
        {
            if (cancellationToken.IsCancellationRequested || UserIds == null) return;

            long[] UniqueUserIds = UserIds.Where(id => id > 0).Distinct().ToArray();
            if (UniqueUserIds.Length == 0) return;

            // Wait for any active rate-limit backoff before acquiring the update lock
            await RobloxRateLimitCoordinator.Instance.WaitForBackoffAsync(cancellationToken).ConfigureAwait(false);

            await UpdateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                HashSet<Account> Updated = new HashSet<Account>();
                var accountsSnapshot = AccountManager.GetAccountsSnapshot();
                var AccountsById = new Dictionary<long, Account>(accountsSnapshot.Count);
                foreach (Account account in accountsSnapshot)
                {
                    if (account != null && account.UserID > 0)
                        AccountsById.TryAdd(account.UserID, account);
                }

                // Roblox accepts up to 50 user IDs per Presence request.
                for (int offset = 0; offset < UniqueUserIds.Length; offset += 50)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    await RobloxRateLimitCoordinator.Instance.WaitForBackoffAsync(cancellationToken).ConfigureAwait(false);

                    int batchEnd = Math.Min(offset + 50, UniqueUserIds.Length);
                    long[] batch = UniqueUserIds[offset..batchEnd];
                    RestResponse? Response;
                    try
                    {
                        Response = await PresenceClient.ExecuteAsync(
                            new RestRequest("v1/presence/users", Method.Post).AddJsonBody(new { userIds = batch }),
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        Program.Logger?.Warn($"Presence request failed: {exception.Message}");
                        continue;
                    }

                    if (Response == null || !Response.IsSuccessful)
                    {
                        Program.Logger?.Warn($"Presence request returned HTTP {(Response == null ? 0 : (int)Response.StatusCode)} {Response?.StatusCode}: {Response?.Content}");
                        if (Response != null && (int)Response.StatusCode == 429)
                        {
                            RobloxRateLimitCoordinator.Instance.RecordRateLimitHit(RateLimitCategory.Presence, defaultBackoffSeconds: 6);
                            Program.Logger?.Warn("Roblox presence API rate limit (429) hit, aborting remaining presence batches to protect IP.");
                            break;
                        }
                        continue;
                    }

                    RobloxRateLimitCoordinator.Instance.RecordSuccess(RateLimitCategory.Presence);

                    // High-performance System.Text.Json zero-intermediate parsing
                    List<UserPresence>? Presences = null;
                    if (Response.Content != null && Utilities.FastJson.TryDeserialize<RobloxPresenceEnvelope>(Response.Content, out var envelope) && envelope?.userPresences != null)
                    {
                        Presences = envelope.userPresences;
                    }

                    if (Presences == null)
                    {
                        Program.Logger?.Warn("Presence response did not contain userPresences.");
                        continue;
                    }

                    await PopulateGameNames(Presences, cancellationToken).ConfigureAwait(false);

                    foreach (UserPresence presence in Presences)
                        if (AccountsById.TryGetValue(presence.userId, out Account? account) && account != null)
                        {
                            account.Presence = presence;
                            Updated.Add(account);
                        }

                    if (offset + 50 < UniqueUserIds.Length)
                    {
                        await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (Updated.Count > 0)
                {
                    RBX_Alt_Manager.AvaloniaUI.Views.MainWindow.Instance?.RefreshLiveProcessStatus();
                }
            }
            finally
            {
                UpdateLock.Release();
            }
        }

        private static async Task PopulateGameNames(IEnumerable<UserPresence> presences, CancellationToken cancellationToken)
        {
            List<UserPresence> InGame = presences?
                .Where(presence => presence != null && presence.userPresenceType == UserPresenceType.InGame && presence.universeId.HasValue && presence.universeId.Value > 0)
                .ToList() ?? new List<UserPresence>();

            if (InGame.Count == 0) return;

            long[] MissingUniverseIds;
            lock (GameNameCacheLock)
            {
                MissingUniverseIds = InGame
                    .Select(presence => presence.universeId!.Value)
                    .Distinct()
                    .Where(universeId => !GameNameCache.ContainsKey(universeId))
                    .ToArray();
            }

            // The games endpoint accepts up to 50 universe IDs per request.
            for (int offset = 0; offset < MissingUniverseIds.Length; offset += 50)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int batchEnd = Math.Min(offset + 50, MissingUniverseIds.Length);
                long[] batch = MissingUniverseIds[offset..batchEnd];
                try
                {
                    RestResponse Response = await GamesClient.ExecuteAsync(
                        new RestRequest($"v1/games?universeIds={string.Join(",", batch)}", Method.Get),
                        cancellationToken).ConfigureAwait(false);
                    if (Response == null || !Response.IsSuccessful)
                    {
                        if (Response != null && (int)Response.StatusCode == 429)
                        {
                            RobloxRateLimitCoordinator.Instance.RecordRateLimitHit(RateLimitCategory.General, defaultBackoffSeconds: 5);
                            Program.Logger?.Warn("Roblox games API rate limit (429) hit, aborting remaining game-name batches.");
                            break;
                        }
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(Response.Content)) continue;

                    if (Utilities.FastJson.TryDeserialize<RobloxGamesEnvelope>(Response.Content, out var gamesEnv) && gamesEnv?.data != null)
                    {
                        lock (GameNameCacheLock)
                        {
                            if (GameNameCache.Count > 1000)
                            {
                                foreach (long staleKey in GameNameCache.Keys.Take(500).ToList())
                                {
                                    GameNameCache.Remove(staleKey);
                                }
                            }

                            foreach (var game in gamesEnv.data)
                            {
                                if (game.id <= 0 || string.IsNullOrWhiteSpace(game.name)) continue;
                                GameNameCache[game.id] = game.name.Trim();
                            }
                        }
                    }

                    if (offset + 50 < MissingUniverseIds.Length)
                    {
                        await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // Presence should continue to work if the optional game-name request fails.
                }
            }

            lock (GameNameCacheLock)
            {
                foreach (UserPresence presence in InGame)
                {
                    if (presence.universeId.HasValue && GameNameCache.TryGetValue(presence.universeId.Value, out string? gameName))
                        presence.gameName = gameName;
                }
            }
        }

        public static string GetStatusText(Account? account, bool isLaunching = false)
        {
            if (account != null && !account.Valid) return "● Hết hạn phiên (Re-login)";
            if (isLaunching) return "● Launching…";
            if (AccountManager.General == null || !AccountManager.General.Get<bool>("ShowPresence")) return "● Ready";
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
            if (account?.Presence == null || AccountManager.General == null || !AccountManager.General.Get<bool>("ShowPresence"))
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

            Dictionary<long, UserPresence> result = new Dictionary<long, UserPresence>();
            bool receivedSuccessfulResponse = false;

            for (int offset = 0; offset < uniqueIds.Length; offset += 50)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await RobloxRateLimitCoordinator.Instance.WaitForBackoffAsync(cancellationToken).ConfigureAwait(false);

                int batchEnd = Math.Min(offset + 50, uniqueIds.Length);
                long[] batch = uniqueIds[offset..batchEnd];
                RestResponse? response;
                try
                {
                    response = await PresenceClient.ExecuteAsync(
                        new RestRequest("v1/presence/users", Method.Post).AddJsonBody(new { userIds = batch }),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    Program.Logger?.Warn($"Presence request failed: {exception.Message}");
                    continue;
                }

                if (response == null || !response.IsSuccessful)
                {
                    Program.Logger?.Warn($"Presence request returned HTTP {(response == null ? 0 : (int)response.StatusCode)} {response?.StatusCode}: {response?.Content}");
                    if (response != null && (int)response.StatusCode == 429)
                    {
                        RobloxRateLimitCoordinator.Instance.RecordRateLimitHit(RateLimitCategory.Presence, defaultBackoffSeconds: 6);
                        Program.Logger?.Warn("Roblox presence API rate limit (429) hit, aborting remaining presence batches to protect IP.");
                        break;
                    }
                    continue;
                }

                RobloxRateLimitCoordinator.Instance.RecordSuccess(RateLimitCategory.Presence);

                List<UserPresence>? presences = null;
                if (response.Content != null && Utilities.FastJson.TryDeserialize<RobloxPresenceEnvelope>(response.Content, out var env) && env?.userPresences != null)
                {
                    presences = env.userPresences;
                }

                if (presences == null)
                {
                    Program.Logger?.Warn("Presence response did not contain userPresences.");
                    continue;
                }

                receivedSuccessfulResponse = true;
                foreach (UserPresence presence in presences)
                    if (presence != null)
                        result[presence.userId] = presence;

                if (offset + 50 < uniqueIds.Length)
                {
                    await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                }
            }

            return receivedSuccessfulResponse ? result : null;
        }

        public static async Task<UserPresence?> GetPresenceSingular(long UserId) => ((await GetPresence(UserId)) is Dictionary<long, UserPresence> Dict && Dict.ContainsKey(UserId)) ? Dict[UserId] : null;
    }

    public sealed class RobloxPresenceEnvelope
    {
        [JsonPropertyName("userPresences")]
        public List<UserPresence> userPresences { get; set; } = new List<UserPresence>();
    }

    public sealed class RobloxGamesEnvelope
    {
        [JsonPropertyName("data")]
        public List<RobloxGameDataEntry> data { get; set; } = new List<RobloxGameDataEntry>();
    }

    public sealed class RobloxGameDataEntry
    {
        [JsonPropertyName("id")]
        public long id { get; set; }

        [JsonPropertyName("name")]
        public string name { get; set; } = string.Empty;
    }

    public class UserPresence
    {
        [JsonPropertyName("userPresenceType")]
        public UserPresenceType userPresenceType { get; set; }

        [JsonPropertyName("lastLocation")]
        public string? lastLocation { get; set; }

        [JsonPropertyName("placeId")]
        public long? placeId { get; set; }

        [JsonPropertyName("rootPlaceId")]
        public long? rootPlaceId { get; set; }

        [JsonPropertyName("gameId")]
        public string? gameId { get; set; }

        [JsonPropertyName("universeId")]
        public long? universeId { get; set; }

        [JsonPropertyName("userId")]
        public long userId { get; set; }

        [JsonPropertyName("lastOnline")]
        public DateTime lastOnline { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public string? gameName { get; set; }
    }

    public enum UserPresenceType
    {
        Offline,
        Online,
        InGame,
        InStudio
    }
}

