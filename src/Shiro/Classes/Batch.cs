#nullable enable
using Shiro.Http;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Shiro.Classes
{
    public class ThumbnailBatchItem
    {
        [JsonPropertyName("targetId")]
        public long TargetId { get; set; }

        [JsonPropertyName("errorCode")]
        public int ErrorCode { get; set; }

        [JsonPropertyName("imageUrl")]
        public string? ImageUrl { get; set; }
    }

    public class ThumbnailBatchResponse
    {
        [JsonPropertyName("data")]
        public List<ThumbnailBatchItem>? Data { get; set; }
    }

    internal class Batch
    {
        /// <summary>
        /// Coalesces thumbnail and place-detail calls into Roblox batch
        /// endpoints. The queues are shared because the dashboard can create
        /// several cards concurrently while it is binding account data.
        /// </summary>

        // AccountManager owns the shared API clients. Resolve them lazily so
        // this helper remains safe during early static initialization while
        // avoiding a second handler pool during normal app startup.
        private static RestClient ThumbnailAPI => AccountManager.ThumbnailsClient
            ?? throw new InvalidOperationException("Roblox API clients have not been initialized.");
        private static RestClient GamesAPI => AccountManager.GamesClient
            ?? throw new InvalidOperationException("Roblox API clients have not been initialized.");

        private static readonly System.Threading.Lock PlaceTaskLock = new();
        private static readonly System.Threading.Lock BatchTaskLock = new();

        private sealed class BatchWork
        {
            public readonly TaskCompletionSource<List<ThumbnailBatchItem>> Completion =
                new TaskCompletionSource<List<ThumbnailBatchItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed class PlaceWork
        {
            public readonly TaskCompletionSource<bool> Completion =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed class BatchRequestPayload
        {
            [JsonPropertyName("requestId")]
            public string RequestId { get; init; } = string.Empty;

            [JsonPropertyName("type")]
            public string Type { get; init; } = string.Empty;

            [JsonPropertyName("targetId")]
            public long TargetId { get; init; }

            [JsonPropertyName("size")]
            public string Size { get; init; } = string.Empty;

            [JsonPropertyName("format")]
            public string? Format { get; init; }
        }

        private static BatchWork? CurrentBatchWork;
        private static PlaceWork? CurrentPlaceWork;

        private static readonly List<BatchRequestPayload> PendingBatch = new List<BatchRequestPayload>();
        private static readonly HashSet<string> PendingBatchIds = new HashSet<string>(StringComparer.Ordinal);
        private static readonly List<long> PendingPlace = new List<long>();
        private static readonly HashSet<long> PendingPlaceIds = new HashSet<long>();

        private static readonly ConcurrentDictionary<long, long> PlaceUniversePair =
            new ConcurrentDictionary<long, long>();
        public static readonly ConcurrentDictionary<long, GameDetails> PlaceDetails =
            new ConcurrentDictionary<long, GameDetails>();
        public static readonly ConcurrentDictionary<long, string> GameIcons =
            new ConcurrentDictionary<long, string>();

        public static async Task<string> GetImage(long assetId, string type, string size = "150x150", string? format = null)
        {
            if (AccountManager.General?.Get<bool>("DisableImages") == true)
                return string.Empty;

            string requestId = $"{assetId}:undefined:{type}:{size}:{format}:regular";

            BatchWork work;
            lock (BatchTaskLock)
            {
                // Queue the item before starting the worker and deduplicate identical requests within the same coalescing batch.
                if (PendingBatchIds.Add(requestId))
                {
                    PendingBatch.Add(new BatchRequestPayload
                    {
                        RequestId = requestId,
                        Type = type,
                        TargetId = assetId,
                        Size = size,
                        Format = format
                    });
                }

                if (CurrentBatchWork == null)
                {
                    work = CurrentBatchWork = new BatchWork();
                    _ = ProcessBatchAsync(work);
                }
                else
                    work = CurrentBatchWork;
            }

            List<ThumbnailBatchItem> result = await work.Completion.Task.ConfigureAwait(false);
            return result?
                .FirstOrDefault(item => item != null && item.ErrorCode == 0 && item.TargetId == assetId)?
                .ImageUrl ?? "UNK";
        }

        public static async Task<string> GetGameIcon(long placeId)
        {
            if (AccountManager.General?.Get<bool>("DisableImages") == true)
                return string.Empty;

            if (!PlaceUniversePair.TryGetValue(placeId, out long universeId))
            {
                PlaceWork work;
                lock (PlaceTaskLock)
                {
                    if (!PlaceDetails.ContainsKey(placeId) && PendingPlaceIds.Add(placeId))
                        PendingPlace.Add(placeId);

                    if (CurrentPlaceWork == null)
                    {
                        work = CurrentPlaceWork = new PlaceWork();
                        _ = ProcessPlaceAsync(work);
                    }
                    else
                        work = CurrentPlaceWork;
                }

                await work.Completion.Task.ConfigureAwait(false);
                PlaceUniversePair.TryGetValue(placeId, out universeId);
            }

            if (universeId == 0)
                return await GetAssetImage(placeId).ConfigureAwait(false);

            return await GetImage(universeId, "GameIcon", "512x512", "png").ConfigureAwait(false);
        }

        private static async Task ProcessBatchAsync(BatchWork work)
        {
            try
            {
                // Keep the original 50 ms coalescing window while ensuring
                // every queue operation is serialized.
                await Task.Delay(50).ConfigureAwait(false);
                List<ThumbnailBatchItem> returnList = new List<ThumbnailBatchItem>();

                while (true)
                {
                    BatchRequestPayload[] pending;
                    lock (BatchTaskLock)
                    {
                        // Clearing the work item under the same lock used by
                        // GetImage makes the handoff atomic. A request arriving
                        // after this point starts a new batch instead of being
                        // attached to a completed task.
                        if (PendingBatch.Count == 0)
                        {
                            PendingBatchIds.Clear();
                            if (ReferenceEquals(CurrentBatchWork, work))
                                CurrentBatchWork = null;

                            work.Completion.TrySetResult(returnList);
                            return;
                        }

                        int count = Math.Min(PendingBatch.Count, 100);
                        pending = new BatchRequestPayload[count];
                        PendingBatch.CopyTo(0, pending, 0, count);
                        for (int i = 0; i < count; i++)
                            PendingBatchIds.Remove(pending[i].RequestId);
                        PendingBatch.RemoveRange(0, count);
                    }

                    RestRequest request = new RestRequest("v1/batch", Method.Post);
                    request.AddJsonBody(pending);
                    RestResponse response = await ThumbnailAPI.ExecuteAsync(request).ConfigureAwait(false);

                    if (response == null || !response.IsSuccessful)
                        throw new HttpRequestException($"{response?.StatusCode} Batch request failed\nError: {response?.ErrorMessage}");

                    ThumbnailBatchResponse? data = Utilities.FastJson.TryDeserialize<ThumbnailBatchResponse>(response.Content ?? "{}", out var b) ? b : null;
                    if (data?.Data != null)
                        returnList.AddRange(data.Data);
                }
            }
            catch (Exception exception)
            {
                lock (BatchTaskLock)
                {
                    PendingBatchIds.Clear();
                    if (ReferenceEquals(CurrentBatchWork, work))
                        CurrentBatchWork = null;
                }

                // ProcessBatchAsync handles its own failures and delivers the
                // exception to each caller, so no fire-and-forget task remains
                // unobserved.
                work.Completion.TrySetException(exception);
            }
        }

        private static async Task ProcessPlaceAsync(PlaceWork work)
        {
            try
            {
                await Task.Delay(50).ConfigureAwait(false);

                // A game card can be rendered before the first account
                // finishes loading. Bound this wait instead of keeping an
                // orphaned task alive for the whole application lifetime.
                for (int attempt = 0; attempt < 100 && AccountManager.LastValidAccount == null; attempt++)
                    await Task.Delay(80).ConfigureAwait(false);

                if (AccountManager.LastValidAccount == null)
                {
                    lock (PlaceTaskLock)
                    {
                        PendingPlace.Clear();
                        PendingPlaceIds.Clear();
                        if (ReferenceEquals(CurrentPlaceWork, work))
                            CurrentPlaceWork = null;
                    }

                    work.Completion.TrySetResult(true);
                    return;
                }

                while (true)
                {
                    List<long> pending = new List<long>();
                    lock (PlaceTaskLock)
                    {
                        if (PendingPlace.Count == 0)
                        {
                            PendingPlaceIds.Clear();
                            if (ReferenceEquals(CurrentPlaceWork, work))
                                CurrentPlaceWork = null;

                            work.Completion.TrySetResult(true);
                            return;
                        }

                        int count = Math.Min(PendingPlace.Count, 50);
                        for (int i = 0; i < count; i++)
                        {
                            long id = PendingPlace[i];
                            PendingPlaceIds.Remove(id);
                            if (!PlaceDetails.ContainsKey(id))
                                pending.Add(id);
                        }
                        PendingPlace.RemoveRange(0, count);
                    }

                    if (pending.Count == 0)
                        continue;

                    RestRequest request = new RestRequest($"v1/games/multiget-place-details?placeIds={string.Join("&placeIds=", pending)}");
                    string? placeCookie = AccountManager.LastValidAccount?.SecurityToken;
                    if (!string.IsNullOrWhiteSpace(placeCookie))
                        request.AddCookie(".ROBLOSECURITY", placeCookie, "/", ".roblox.com");

                    RestResponse response = await GamesAPI.ExecuteAsync(request).ConfigureAwait(false);
                    if (response == null || !response.IsSuccessful)
                        continue;

                    GameDetails[]? games = Utilities.FastJson.TryDeserialize<GameDetails[]>(response.Content ?? "[]", out var parsedGames) ? parsedGames : null;
                    foreach (GameDetails game in games ?? Array.Empty<GameDetails>())
                    {
                        if (game == null)
                            continue;

                        PlaceDetails.TryAdd(game.placeId, game);
                        PlaceUniversePair.TryAdd(game.placeId, game.universeId);
                    }
                }
            }
            catch (Exception exception)
            {
                lock (PlaceTaskLock)
                    if (ReferenceEquals(CurrentPlaceWork, work))
                        CurrentPlaceWork = null;

                work.Completion.TrySetException(exception);
            }
        }

        private static async Task<string> GetAssetImage(long assetId)
        {
            if (AccountManager.General?.Get<bool>("DisableImages") == true)
                return string.Empty;

            RestRequest request = new RestRequest($"v1/assets?assetIds={assetId}&returnPolicy=PlaceHolder&size=150x150&format=Png&isCircular=false");
            string? assetCookie = AccountManager.LastValidAccount?.SecurityToken;
            if (!string.IsNullOrWhiteSpace(assetCookie))
                request.AddCookie(".ROBLOSECURITY", assetCookie, "/", ".roblox.com");

            RestResponse response = await ThumbnailAPI.ExecuteAsync(request).ConfigureAwait(false);
            if (response == null || !response.IsSuccessful)
            {
                Program.Logger.Warn($"{response?.StatusCode} Asset image request failed: {response?.ErrorMessage}");
                return "UNK";
            }

            ThumbnailBatchResponse? data = Utilities.FastJson.TryDeserialize<ThumbnailBatchResponse>(response.Content ?? "{}", out var parsed) ? parsed : null;
            return data?.Data?.FirstOrDefault()?.ImageUrl ?? "UNK";
        }
    }
}
