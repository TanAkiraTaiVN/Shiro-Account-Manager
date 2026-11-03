#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;
using Shiro.Http;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public partial class ServerListViewModel
    {
        private string _searchPlayerUsername = string.Empty;
        private bool _isSearchingPlayer;
        private string _playerSearchStatus = string.Empty;

        public string SearchPlayerUsername
        {
            get => _searchPlayerUsername;
            set => SetProperty(ref _searchPlayerUsername, value);
        }

        public bool IsSearchingPlayer
        {
            get => _isSearchingPlayer;
            set => SetProperty(ref _isSearchingPlayer, value);
        }

        public string PlayerSearchStatus
        {
            get => _playerSearchStatus;
            set => SetProperty(ref _playerSearchStatus, value);
        }

        private async Task SearchPlayerAsync()
        {
            if (string.IsNullOrWhiteSpace(SearchPlayerUsername))
            {
                PlayerSearchStatus = LocalizationService.IsEnglish ? "Please enter player username!" : Shiro.Services.LocalizationService.Get("Auto_ServerListViewMo_531");
                LoadingStatus = PlayerSearchStatus;
                return;
            }

            if (!long.TryParse(PlaceIdInput, out long placeId) || placeId <= 0)
            {
                PlayerSearchStatus = LocalizationService.IsEnglish ? "Please enter Place ID above first!" : Shiro.Services.LocalizationService.Get("Auto_ServerListViewMo_532");
                LoadingStatus = PlayerSearchStatus;
                return;
            }

            var cts = ResetOperationCts(createNew: true)!;
            var ct = cts.Token;

            IsSearchingPlayer = true;
            PlayerSearchStatus = LocalizationService.IsEnglish ? $"Searching for player '{SearchPlayerUsername}'..." : Shiro.Services.LocalizationService.Format("Auto_ServerListViewMo_533", SearchPlayerUsername);
            LoadingStatus = PlayerSearchStatus;

            try
            {
                var (userFound, userId, _) = await AppServices.AccountRepository.LookupUserIdAsync(SearchPlayerUsername.Trim(), ct);
                ct.ThrowIfCancellationRequested();

                if (!userFound)
                {
                    PlayerSearchStatus = LocalizationService.IsEnglish ? $"User ID not found for {SearchPlayerUsername}!" : Shiro.Services.LocalizationService.Format("Auto_ServerListViewMo_534", SearchPlayerUsername);
                    LoadingStatus = PlayerSearchStatus;
                    return;
                }

                var thumbClient = AppServices.RobloxApi.Thumbnails;
                var avReq = new RestRequest($"v1/users/avatar-headshot?size=48x48&format=png&userIds={userId}", Method.Get);
                var avResp = await thumbClient.ExecuteAsync(avReq, ct);
                ct.ThrowIfCancellationRequested();

                if (!avResp.IsSuccessful || string.IsNullOrEmpty(avResp.Content))
                {
                    PlayerSearchStatus = LocalizationService.IsEnglish ? "Could not fetch player avatar!" : Shiro.Services.LocalizationService.Get("Auto_ServerListViewMo_535");
                    LoadingStatus = PlayerSearchStatus;
                    return;
                }

                var avatarData = Utilities.FastJson.TryDeserialize<AvatarRoot>(avResp.Content, out var ad) ? ad : null;
                string? targetAvatarUrl = avatarData?.data?.FirstOrDefault()?.imageUrl;

                if (string.IsNullOrEmpty(targetAvatarUrl))
                {
                    PlayerSearchStatus = LocalizationService.IsEnglish ? "Player avatar does not exist!" : Shiro.Services.LocalizationService.Get("Auto_ServerListViewMo_536");
                    LoadingStatus = PlayerSearchStatus;
                    return;
                }

                bool populateInitialServers = _servers.Count == 0;
                var gamesClient = AppServices.RobloxApi.Games;
                string cursor = "_";
                ServerData? matchedServer = null;
                int scannedServers = 0;

                while (!string.IsNullOrEmpty(cursor) && matchedServer == null)
                {
                    ct.ThrowIfCancellationRequested();

                    string cursorParam = cursor == "_" ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}";
                    var req = new RestRequest($"v1/games/{placeId}/servers/public?limit=100{cursorParam}", Method.Get);
                    var resp = await gamesClient.ExecuteAsync(req, ct);
                    if (!resp.IsSuccessful || string.IsNullOrEmpty(resp.Content)) break;

                    var sInfo = Utilities.FastJson.TryDeserialize<ServersInfo>(resp.Content, out var si) ? si : null;
                    if (sInfo?.data == null || sInfo.data.Count == 0) break;

                    if (populateInitialServers)
                    {
                        populateInitialServers = false;
                        var initialBatch = sInfo.data.Select(s => new ServerItemViewModel
                        {
                            JobId = s.id,
                            Type = "Public",
                            MaxPlayers = s.maxPlayers,
                            Playing = s.playing,
                            Ping = s.ping ?? -1,
                            Region = string.IsNullOrEmpty(s.region) ? string.Empty : s.region,
                            Ip = s.ip ?? string.Empty,
                            RawData = s
                        }).ToList();

                        AppDispatcher.Post(() =>
                        {
                            if (!ct.IsCancellationRequested && _allServers.Count == 0)
                            {
                                AppendRawServers(initialBatch);
                            }
                        });
                    }

                    // Pack playerTokens across multiple servers into chunks of up to 100 tokens (Roblox v1/batch limit)
                    var batches = new List<(List<TokenRequest> Requests, Dictionary<string, ServerData> RequestMap, ServerData? FallbackServer)>();
                    var currentRequests = new List<TokenRequest>(100);
                    var currentMap = new Dictionary<string, ServerData>(StringComparer.Ordinal);
                    ServerData? lastServerInBatch = null;

                    foreach (var s in sInfo.data)
                    {
                        if (s?.playerTokens == null || s.playerTokens.Count == 0) continue;
                        scannedServers++;

                        foreach (var token in s.playerTokens)
                        {
                            if (string.IsNullOrEmpty(token)) continue;
                            if (currentRequests.Count >= 100)
                            {
                                batches.Add((currentRequests, currentMap, lastServerInBatch));
                                currentRequests = new List<TokenRequest>(100);
                                currentMap = new Dictionary<string, ServerData>(StringComparer.Ordinal);
                            }

                            var tokenReq = new TokenRequest(token);
                            currentRequests.Add(tokenReq);
                            currentMap[tokenReq.requestId] = s;
                            lastServerInBatch = s;
                        }
                    }

                    if (currentRequests.Count > 0)
                    {
                        batches.Add((currentRequests, currentMap, lastServerInBatch));
                    }

                    PlayerSearchStatus = LocalizationService.IsEnglish
                        ? $"Scanning {scannedServers} servers for '{SearchPlayerUsername}'..."
                        : Shiro.Services.LocalizationService.Format("Auto_ServerListViewMo_537", scannedServers, SearchPlayerUsername);
                    LoadingStatus = PlayerSearchStatus;

                    // Process packed batches with bounded concurrency (up to 3 requests in flight)
                    using var batchGate = new SemaphoreSlim(3, 3);
                    var batchTasks = batches.Select(async batch =>
                    {
                        await batchGate.WaitAsync(ct);
                        try
                        {
                            if (Volatile.Read(ref matchedServer) != null || ct.IsCancellationRequested)
                                return;

                            var batchReq = new RestRequest("v1/batch", Method.Post);
                            batchReq.AddJsonBody(batch.Requests);
                            var batchResp = await thumbClient.ExecuteAsync(batchReq, ct);
                            if (!batchResp.IsSuccessful || string.IsNullOrEmpty(batchResp.Content))
                                return;

                            var avatars = Utilities.FastJson.TryDeserialize<TokenAvatarRoot>(batchResp.Content, out var ta) ? ta : null;
                            TokenAvatar? hit = null;
                            if (avatars?.data != null)
                            {
                                foreach (var a in avatars.data)
                                {
                                    if (a != null && a.imageUrl == targetAvatarUrl)
                                    {
                                        hit = a;
                                        break;
                                    }
                                }
                            }
                            if (hit != null)
                            {
                                ServerData? serverHit = null;
                                if (hit.requestId == null || !batch.RequestMap.TryGetValue(hit.requestId, out serverHit))
                                {
                                    serverHit = batch.FallbackServer;
                                }
                                if (serverHit != null)
                                {
                                    Interlocked.CompareExchange(ref matchedServer, serverHit, null);
                                }
                            }
                        }
                        finally
                        {
                            batchGate.Release();
                        }
                    }).ToArray();

                    await Task.WhenAll(batchTasks);
                    cursor = sInfo.nextPageCursor ?? string.Empty;
                }

                if (matchedServer != null)
                {
                    var matchVm = _allServers.FirstOrDefault(x => x.JobId == matchedServer.id) ?? new ServerItemViewModel
                    {
                        JobId = matchedServer.id,
                        Type = "Public",
                        MaxPlayers = matchedServer.maxPlayers,
                        Playing = matchedServer.playing,
                        RawData = matchedServer
                    };

                    if (!_allServers.Contains(matchVm))
                        PrependRawServers(new[] { matchVm });
                    else if (!_servers.Contains(matchVm))
                        _servers.Insert(0, matchVm);

                    SelectedServer = matchVm;
                    SelectServerJobId(matchVm);
                    PlayerSearchStatus = LocalizationService.IsEnglish
                        ? $"FOUND! {SearchPlayerUsername} is in server: {matchedServer.id}"
                        : Shiro.Services.LocalizationService.Format("Auto_ServerListViewMo_538", SearchPlayerUsername, matchedServer.id);
                    LoadingStatus = PlayerSearchStatus;
                }
                else
                {
                    PlayerSearchStatus = LocalizationService.IsEnglish ? $"Player '{SearchPlayerUsername}' was not found in current public servers." : Shiro.Services.LocalizationService.Format("Auto_ServerListViewMo_539", SearchPlayerUsername);
                    LoadingStatus = PlayerSearchStatus;
                }
            }
            catch (OperationCanceledException)
            {
                PlayerSearchStatus = LocalizationService.IsEnglish ? "Player search cancelled." : Shiro.Services.LocalizationService.Get("Auto_ServerListViewMo_540");
                LoadingStatus = PlayerSearchStatus;
            }
            catch (Exception ex)
            {
                PlayerSearchStatus = LocalizationService.IsEnglish ? $"Search error: {ex.Message}" : Shiro.Services.LocalizationService.Format("Auto_ServerListViewMo_541", ex.Message);
                LoadingStatus = PlayerSearchStatus;
            }
            finally
            {
                if (ReferenceEquals(_operationCts, cts))
                    IsSearchingPlayer = false;
            }
        }
    }
}
