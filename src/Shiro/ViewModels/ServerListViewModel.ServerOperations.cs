#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Http;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public partial class ServerListViewModel
    {
        internal static Task<List<ServerItemViewModel>?> TestFetchVipServersAsync(
            RestClient gamesClient,
            Account vipAuthAccount,
            long placeId,
            CancellationToken ct) => FetchVipServersAsync(gamesClient, vipAuthAccount, placeId, ct);

        private static async Task<List<ServerItemViewModel>?> FetchVipServersAsync(
            RestClient gamesClient,
            Account vipAuthAccount,
            long placeId,
            CancellationToken ct)
        {
            try
            {
                var vipReq = vipAuthAccount.MakeRequest($"v1/games/{placeId}/servers/VIP?sortOrder=Asc&limit=25", Method.Get);
                var vipResp = await vipAuthAccount.ExecuteAccountRestAsync(gamesClient, "https://games.roblox.com/", vipReq, ct).ConfigureAwait(false);
                if (vipResp != null && vipResp.IsSuccessful && !string.IsNullOrEmpty(vipResp.Content) && !ct.IsCancellationRequested)
                {
                    var vipInfo = FastJson.TryDeserialize<ServersInfo>(vipResp.Content, out var vInfo) ? vInfo : null;
                    if (vipInfo?.data != null && vipInfo.data.Count > 0)
                    {
                        var vipList = new List<ServerItemViewModel>(vipInfo.data.Count);
                        foreach (var v in vipInfo.data)
                        {
                            string jobId = !string.IsNullOrEmpty(v.accessCode)
                                ? $"VIP:{v.accessCode}"
                                : (!string.IsNullOrEmpty(v.id)
                                    ? v.id
                                    : (!string.IsNullOrEmpty(v.name) ? v.name : "VIP Server"));

                            vipList.Add(new ServerItemViewModel
                            {
                                JobId = jobId,
                                Type = "VIP",
                                MaxPlayers = v.maxPlayers,
                                Playing = v.playing,
                                Ping = v.ping ?? -1,
                                Region = !string.IsNullOrEmpty(v.region) ? v.region : "VIP Private",
                                Ip = v.ip ?? string.Empty,
                                RawData = v
                            });
                        }
                        return vipList;
                    }
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug($"[LoadVIPServers] {ex.Message}");
            }

            return null;
        }

        private void SelectServerJobId(object? param)
        {
            if (param is ServerItemViewModel server)
            {
                string targetId = server.Type == "VIP" && server.RawData != null && !string.IsNullOrEmpty(server.RawData.accessCode)
                    ? $"VIP:{server.RawData.accessCode}"
                    : server.JobId;

                if (_mainViewModel != null)
                {
                    if (!string.IsNullOrWhiteSpace(PlaceIdInput))
                        _mainViewModel.PlaceId = PlaceIdInput;
                    _mainViewModel.JobId = targetId;
                    _mainViewModel.StatusMessage = Shiro.Services.LocalizationService.Format("ServerList.ServerSelected", targetId);
                }

                ClipboardHelper.SetText(targetId);
                LoadingStatus = Shiro.Services.LocalizationService.Format("ServerList.JobIdSelectedAndCopied", targetId);
            }
        }

        private void CopyJobId(object? param)
        {
            if (param is ServerItemViewModel server)
            {
                string targetId = server.Type == "VIP" && server.RawData != null && !string.IsNullOrEmpty(server.RawData.accessCode)
                    ? $"VIP:{server.RawData.accessCode}"
                    : server.JobId;
                ClipboardHelper.SetText(targetId);
                LoadingStatus = Shiro.Services.LocalizationService.Format("ServerList.JobIdCopied", targetId);
            }
        }

        private async Task JoinServerAsync(object? param)
        {
            if (param is not ServerItemViewModel server) return;

            string targetServer = server.Type == "VIP" && server.RawData != null && !string.IsNullOrEmpty(server.RawData.accessCode)
                ? $"VIP:{server.RawData.accessCode}"
                : server.JobId;

            string pidText = !string.IsNullOrWhiteSpace(PlaceIdInput) ? PlaceIdInput : (_mainViewModel?.PlaceId ?? string.Empty);
            long placeId = AccountLauncherService.ParsePlaceId(pidText);
            if (placeId <= 0)
            {
                LoadingStatus = Shiro.Services.LocalizationService.Get("ServerList.InvalidPlaceId");
                return;
            }

            if (_mainViewModel != null)
            {
                _mainViewModel.PlaceId = placeId.ToString();
                _mainViewModel.JobId = targetServer;
            }

            var selectedAccounts = _mainViewModel?.GetSelectedAccounts();
            if (selectedAccounts == null || selectedAccounts.Count == 0)
            {
                string warnTitle = Shiro.Services.LocalizationService.Get("Main.NoAccountSelectedTitle");
                string warnMsg = Shiro.Services.LocalizationService.Get("ServerList.SelectAccountBeforeJoinServer");
                LoadingStatus = warnMsg;
                if (_mainViewModel != null)
                    _mainViewModel.StatusMessage = warnMsg;
                ToastService.ShowWarning(warnTitle, warnMsg);
                return;
            }

            string serverDesc = string.IsNullOrEmpty(targetServer) ? Shiro.Services.LocalizationService.Get("ServerList.RandomServerKeyword") : targetServer;
            LoadingStatus = Shiro.Services.LocalizationService.Format("ServerList.JoiningServerWithAccounts", serverDesc, selectedAccounts.Count);

            await AppServices.HostUiBridge.JoinServerFromHostAsync(selectedAccounts, placeId, targetServer).ConfigureAwait(true);
        }

        private Account? ResolveAuthAccount()
        {
            var selectedFromVm = _mainViewModel?.Accounts?.FirstOrDefault(a => a.IsSelected && a.Valid && a.Model != null)?.Model;
            if (selectedFromVm != null)
                return selectedFromVm;

            if (AppServices.SelectionState.SelectedAccount != null && AppServices.SelectionState.SelectedAccount.Valid)
                return AppServices.SelectionState.SelectedAccount;

            if (AppServices.SelectionState.LastValidAccount != null && AppServices.SelectionState.LastValidAccount.Valid)
                return AppServices.SelectionState.LastValidAccount;

            return AppServices.AccountRepository.GetAll().FirstOrDefault(a => a != null && a.Valid)
                   ?? AppServices.SelectionState.SelectedAccount
                   ?? AppServices.SelectionState.LastValidAccount;
        }

        private async Task LoadRegionAsync(object? param)
        {
            await LoadRegionCoreAsync(param as ServerItemViewModel, CancellationToken.None);
            if (SortMode is ServerSortMode.PingAsc or ServerSortMode.PingDesc)
            {
                ApplyServerSortAndFilter();
            }
        }

        private async Task LoadRegionCoreAsync(ServerItemViewModel? server, CancellationToken ct)
        {
            if (server == null || server.RawData == null) return;
            AppDispatcher.Post(() => server.IsBusyPinging = true);

            try
            {
                var acc = ResolveAuthAccount();
                if (acc == null)
                {
                    AppDispatcher.Post(() => server.Region = Shiro.Services.LocalizationService.Get("ServerQuery.FetchingRegion"));
                    return;
                }

                string rawPid = !string.IsNullOrWhiteSpace(PlaceIdInput) ? PlaceIdInput : (_mainViewModel?.PlaceId ?? string.Empty);
                long placeId = AccountLauncherService.ParsePlaceId(rawPid);
                if (placeId <= 0) return;

                var result = await AppServices.RobloxServerQueryService.QueryServerRegionAndPingAsync(acc, placeId, server.RawData.id, cancellationToken: ct).ConfigureAwait(false);
                AppDispatcher.Post(() =>
                {
                    if (result.Success)
                    {
                        server.Ip = result.Ip ?? string.Empty;
                        if (result.PingMs.HasValue) server.Ping = result.PingMs.Value;
                        server.CountryCode = result.CountryCode ?? string.Empty;
                        server.FlagEmoji = result.FlagEmoji ?? string.Empty;
                        server.Region = result.Region ?? string.Empty;
                    }
                    else
                    {
                        server.Region = !string.IsNullOrEmpty(result.Region) ? result.Region : Shiro.Services.LocalizationService.Get("ServerQuery.UnknownRegion");
                    }
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                AppDispatcher.Post(() => server.Region = Shiro.Services.LocalizationService.Get("MissingAssets.PriceLoadError"));
                _logger.Warn($"[LoadRegionAsync] {ex.Message}");
            }
            finally
            {
                AppDispatcher.Post(() => server.IsBusyPinging = false);
            }
        }

        private async Task LoadAllRegionsAsync()
        {
            if (_servers.Count == 0) return;

            var cts = ResetOperationCts(createNew: true);
            var ct = cts!.Token;

            string notLoadedText = LocalizationService.Get("ServerList.NotLoaded");
            var pendingServers = new List<ServerItemViewModel>(25);
            for (int i = 0; i < _servers.Count && pendingServers.Count < 25; i++)
            {
                ServerItemViewModel server = _servers[i];
                if (string.IsNullOrEmpty(server.Region) ||
                    string.Equals(server.Region, notLoadedText, StringComparison.Ordinal) ||
                    string.Equals(server.Region, "Not loaded", StringComparison.Ordinal) ||
                    string.IsNullOrEmpty(server.Ip))
                {
                    pendingServers.Add(server);
                }
            }

            if (pendingServers.Count == 0)
            {
                LoadingStatus = LocalizationService.Get("ServerList.RegionsLoadedComplete");
                return;
            }

            LoadingStatus = LocalizationService.Format("ServerList.LoadingRegionsAndPinging", pendingServers.Count);

            try
            {
                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = 4,
                    CancellationToken = ct
                };

                await Parallel.ForEachAsync(pendingServers, parallelOptions, async (server, token) =>
                {
                    if (token.IsCancellationRequested) return;
                    await LoadRegionCoreAsync(server, token);
                });

                if (SortMode is ServerSortMode.PingAsc or ServerSortMode.PingDesc)
                {
                    ApplyServerSortAndFilter();
                }
                LoadingStatus = LocalizationService.Get("ServerList.RegionsLoadedComplete");
            }
            catch (OperationCanceledException)
            {
                LoadingStatus = LocalizationService.Get("ServerList.LoadingRegionsCancelled");
            }
        }
    }
}
