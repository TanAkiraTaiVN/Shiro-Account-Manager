#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Http;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public sealed partial class ServerBrowserTabViewModel
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
            set
            {
                if (SetProperty(ref _isSearchingPlayer, value))
                    _notifyCancelState();
            }
        }

        public string PlayerSearchStatus
        {
            get => _playerSearchStatus;
            set => SetProperty(ref _playerSearchStatus, value);
        }

        private void PostPlayerSearchStatus(CancellationTokenSource cts, CancellationToken ct, string status) => AppDispatcher.Post(() =>
        {
            if (!ct.IsCancellationRequested && ReferenceEquals(Volatile.Read(ref _playerSearchCts), cts))
            {
                PlayerSearchStatus = status;
                _setLoadingStatus(status);
            }
        });

        private async Task SearchPlayerAsync()
        {
            if (string.IsNullOrWhiteSpace(SearchPlayerUsername))
            {
                PlayerSearchStatus = LocalizationService.Get("ServerList.EnterPlayerUsername");
                _setLoadingStatus(PlayerSearchStatus);
                return;
            }

            long placeId = AccountLauncherService.ParsePlaceId(_getPlaceIdInput());
            if (placeId <= 0)
            {
                PlayerSearchStatus = LocalizationService.Get("ServerList.EnterPlaceIdFirst");
                _setLoadingStatus(PlayerSearchStatus);
                return;
            }

            var cts = ServerListOperationHelper.ReplaceCts(ref _playerSearchCts, createNew: true, "ServerListViewModel.PlayerSearchCts")!;
            var ct = cts.Token;

            IsSearchingPlayer = true;
            PlayerSearchStatus = LocalizationService.Format("ServerList.SearchingPlayer", SearchPlayerUsername);
            _setLoadingStatus(PlayerSearchStatus);

            try
            {
                var (userFound, userId, _) = await AppServices.AccountRepository.LookupUserIdAsync(SearchPlayerUsername.Trim(), ct);
                ct.ThrowIfCancellationRequested();

                if (!userFound)
                {
                    PostPlayerSearchStatus(cts, ct, LocalizationService.Format("ServerList.PlayerUserIdNotFound", SearchPlayerUsername));
                    return;
                }

                var thumbClient = AppServices.RobloxApi.Thumbnails;
                var avReq = new RestRequest($"v1/users/avatar-headshot?size=48x48&format=png&userIds={userId}", Method.Get);
                var avResp = await thumbClient.ExecuteAsync(avReq, ct);
                ct.ThrowIfCancellationRequested();

                if (!avResp.IsSuccessful || string.IsNullOrEmpty(avResp.Content))
                {
                    PostPlayerSearchStatus(cts, ct, LocalizationService.Get("ServerList.CannotGetPlayerAvatar"));
                    return;
                }

                var avatarData = FastJson.TryDeserialize<AvatarRoot>(avResp.Content, out var ad) ? ad : null;
                string? targetAvatarUrl = avatarData?.data?.FirstOrDefault()?.imageUrl;
                if (string.IsNullOrEmpty(targetAvatarUrl))
                {
                    PostPlayerSearchStatus(cts, ct, LocalizationService.Get("ServerList.PlayerAvatarNotExist"));
                    return;
                }

                bool populateInitialServers = _servers.Count == 0;
                var gamesClient = AppServices.RobloxApi.Games;
                ServerData? matchedServer = null;
                int scannedServers = 0;

                using var searchLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var searchToken = searchLinkedCts.Token;
                var channel = System.Threading.Channels.Channel.CreateBounded<(List<TokenRequest> Requests, Dictionary<string, ServerData> RequestMap, ServerData? FallbackServer)>(
                    new System.Threading.Channels.BoundedChannelOptions(6)
                    {
                        SingleWriter = true,
                        FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait
                    });

                async Task ProduceServerBatchesAsync()
                {
                    try
                    {
                        string cursor = "_";
                        while (!string.IsNullOrEmpty(cursor) && Volatile.Read(ref matchedServer) == null && !searchToken.IsCancellationRequested)
                        {
                            string cursorParam = cursor == "_" ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}";
                            var req = new RestRequest($"v1/games/{placeId}/servers/public?limit=100{cursorParam}", Method.Get);
                            var resp = await gamesClient.ExecuteAsync(req, searchToken);
                            if (!resp.IsSuccessful || string.IsNullOrEmpty(resp.Content)) break;

                            var sInfo = FastJson.TryDeserialize<ServersInfo>(resp.Content, out var si) ? si : null;
                            if (sInfo?.data == null || sInfo.data.Count == 0) break;

                            if (populateInitialServers)
                            {
                                populateInitialServers = false;
                                var initialBatch = sInfo.data.Select(ServerListOperationHelper.CreatePublicServerItem).ToList();
                                AppDispatcher.Post(() =>
                                {
                                    if (!ct.IsCancellationRequested && _allServers.Count == 0)
                                        AppendRawServers(initialBatch);
                                });
                            }

                            var currentRequests = new List<TokenRequest>(100);
                            var currentMap = new Dictionary<string, ServerData>(StringComparer.Ordinal);
                            ServerData? lastServerInBatch = null;

                            foreach (var s in sInfo.data)
                            {
                                if (s?.playerTokens == null || s.playerTokens.Count == 0) continue;
                                int totalScanned = Interlocked.Increment(ref scannedServers);

                                foreach (var token in s.playerTokens)
                                {
                                    if (string.IsNullOrEmpty(token)) continue;
                                    if (currentRequests.Count >= 100)
                                    {
                                        await channel.Writer.WriteAsync((currentRequests, currentMap, lastServerInBatch), searchToken);
                                        currentRequests = new List<TokenRequest>(100);
                                        currentMap = new Dictionary<string, ServerData>(StringComparer.Ordinal);
                                    }

                                    var tokenReq = new TokenRequest(token);
                                    currentRequests.Add(tokenReq);
                                    currentMap[tokenReq.requestId] = s;
                                    lastServerInBatch = s;
                                }

                                PostPlayerSearchStatus(cts, ct, LocalizationService.Format("ServerList.ScanningServersForPlayer", totalScanned, SearchPlayerUsername));
                            }

                            if (currentRequests.Count > 0 && Volatile.Read(ref matchedServer) == null)
                                await channel.Writer.WriteAsync((currentRequests, currentMap, lastServerInBatch), searchToken);

                            cursor = sInfo.nextPageCursor ?? string.Empty;
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Stopped early because a consumer matched targetAvatarUrl
                    }
                    finally { channel.Writer.TryComplete(); }
                }

                async Task ConsumeBatchesAsync()
                {
                    try
                    {
                        await foreach (var batch in channel.Reader.ReadAllAsync(searchToken))
                        {
                            if (Volatile.Read(ref matchedServer) != null || searchToken.IsCancellationRequested) break;

                            var batchReq = new RestRequest("v1/batch", Method.Post);
                            batchReq.AddJsonBody(batch.Requests);
                            var batchResp = await thumbClient.ExecuteAsync(batchReq, searchToken);
                            if (!batchResp.IsSuccessful || string.IsNullOrEmpty(batchResp.Content)) continue;

                            var avatars = FastJson.TryDeserialize<TokenAvatarRoot>(batchResp.Content, out var ta) ? ta : null;
                            TokenAvatar? hit = avatars?.data?.FirstOrDefault(a => a != null && a.imageUrl == targetAvatarUrl);
                            if (hit != null)
                            {
                                ServerData? serverHit = null;
                                if (hit.requestId == null || !batch.RequestMap.TryGetValue(hit.requestId, out serverHit))
                                    serverHit = batch.FallbackServer;

                                if (serverHit != null && Interlocked.CompareExchange(ref matchedServer, serverHit, null) == null)
                                {
                                    try { searchLinkedCts.Cancel(); }
                                    catch (ObjectDisposedException) { /* already disposed */ }
                                    break;
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Cancelled early on match
                    }
                }

                await Task.WhenAll(ProduceServerBatchesAsync(), ConsumeBatchesAsync(), ConsumeBatchesAsync(), ConsumeBatchesAsync());

                AppDispatcher.Post(() =>
                {
                    if (ct.IsCancellationRequested || !ReferenceEquals(Volatile.Read(ref _playerSearchCts), cts)) return;
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

                        if (!_allServers.Contains(matchVm)) PrependRawServers(new[] { matchVm });
                        else if (!_servers.Contains(matchVm)) _servers.Insert(0, matchVm);

                        SelectedServer = matchVm;
                        SelectServerJobId(matchVm);
                        PlayerSearchStatus = LocalizationService.Format(
                            "ServerList.PlayerFoundInServer",
                            SearchPlayerUsername,
                            StreamerModeService.IsActive ? StreamerModeService.MaskJobId(matchedServer.id) : matchedServer.id);
                    }
                    else
                    {
                        PlayerSearchStatus = LocalizationService.Format("ServerList.PlayerNotFound", SearchPlayerUsername);
                    }
                    _setLoadingStatus(PlayerSearchStatus);
                });
            }
            catch (OperationCanceledException)
            {
                AppDispatcher.Post(() =>
                {
                    if (!ReferenceEquals(Volatile.Read(ref _playerSearchCts), cts)) return;
                    PlayerSearchStatus = LocalizationService.Get("ServerList.PlayerSearchCancelled");
                    _setLoadingStatus(PlayerSearchStatus);
                });
            }
            catch (Exception ex)
            {
                AppDispatcher.Post(() =>
                {
                    if (!ReferenceEquals(Volatile.Read(ref _playerSearchCts), cts)) return;
                    PlayerSearchStatus = LocalizationService.Format("ServerList.PlayerSearchError", ex.Message);
                    _setLoadingStatus(PlayerSearchStatus);
                });
            }
            finally
            {
                AppDispatcher.Post(() =>
                {
                    if (!ReferenceEquals(Volatile.Read(ref _playerSearchCts), cts)) return;
                    IsSearchingPlayer = false;
                    ServerListOperationHelper.ReleaseFinishedCts(ref _playerSearchCts, cts, "ServerListViewModel.PlayerSearchCts");
                });
            }
        }
    }
}
