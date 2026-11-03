#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;
using Shiro.Http;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public partial class ServerListViewModel
    {
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
                    var vipInfo = Utilities.FastJson.TryDeserialize<ServersInfo>(vipResp.Content, out var vInfo) ? vInfo : null;
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
                Program.Logger?.Debug($"[LoadVIPServers] {ex.Message}");
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
                    _mainViewModel.StatusMessage = Shiro.Services.LocalizationService.Format("Auto_ServerListViewMo_542", targetId);
                }

                ClipboardHelper.SetText(targetId);
                LoadingStatus = Shiro.Services.LocalizationService.Format("Auto_ServerListViewMo_543", targetId);
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
                LoadingStatus = Shiro.Services.LocalizationService.Format("Auto_ServerListViewMo_544", targetId);
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
                LoadingStatus = Shiro.Services.LocalizationService.Get("Auto_ServerListViewMo_545");
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
                string warnTitle = Shiro.Services.LocalizationService.Get("Auto_MainViewModelLau_494");
                string warnMsg = Shiro.Services.LocalizationService.Get("Auto_ServerListViewMo_546");
                LoadingStatus = warnMsg;
                if (_mainViewModel != null)
                    _mainViewModel.StatusMessage = warnMsg;
                ToastService.ShowWarning(warnTitle, warnMsg);
                return;
            }

            string serverDesc = string.IsNullOrEmpty(targetServer) ? Shiro.Services.LocalizationService.Get("Auto_ServerListViewMo_547") : targetServer;
            LoadingStatus = Shiro.Services.LocalizationService.Format("Auto_ServerListViewMo_548", serverDesc, selectedAccounts.Count);

            {
            await AppSettings.JoinServerFromHostAsync(selectedAccounts, placeId, targetServer).ConfigureAwait(true);
            }
        }

        private Account? ResolveAuthAccount()
        {
            var selectedFromVm = _mainViewModel?.Accounts?.FirstOrDefault(a => a.IsSelected && a.Valid && a.Model != null)?.Model;
            if (selectedFromVm != null)
                return selectedFromVm;

            if (AppSettings.SelectedAccount != null && AppSettings.SelectedAccount.Valid)
                return AppSettings.SelectedAccount;

            if (AppSettings.LastValidAccount != null && AppSettings.LastValidAccount.Valid)
                return AppSettings.LastValidAccount;

            return AppServices.AccountRepository.GetAll().FirstOrDefault(a => a != null && a.Valid)
                   ?? AppSettings.SelectedAccount
                   ?? AppSettings.LastValidAccount;
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
            server.IsBusyPinging = true;

            try
            {
                var acc = ResolveAuthAccount();
                if (acc == null)
                {
                    server.Region = Shiro.Services.LocalizationService.Get("Auto_RobloxServerQuer_270");
                    return;
                }

                string rawPid = !string.IsNullOrWhiteSpace(PlaceIdInput) ? PlaceIdInput : (_mainViewModel?.PlaceId ?? string.Empty);
                long placeId = AccountLauncherService.ParsePlaceId(rawPid);
                if (placeId <= 0) return;

                var result = await RobloxServerQueryService.Default.QueryServerRegionAndPingAsync(acc, placeId, server.RawData.id, cancellationToken: ct).ConfigureAwait(true);
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
                    server.Region = !string.IsNullOrEmpty(result.Region) ? result.Region : Shiro.Services.LocalizationService.Get("Auto_RobloxServerQuer_271");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                server.Region = Shiro.Services.LocalizationService.Get("Auto_MissingAssetsDia_138");
                Program.Logger?.Warn($"[LoadRegionAsync] {ex.Message}");
            }
            finally
            {
                server.IsBusyPinging = false;
            }
        }

        private async Task LoadAllRegionsAsync()
        {
            if (_servers.Count == 0) return;

            var cts = ResetOperationCts(createNew: true);
            var ct = cts!.Token;

            var pendingServers = _servers
                .Take(25)
                .Where(server => string.IsNullOrEmpty(server.Region) || server.Region == Shiro.Services.LocalizationService.Get("Auto_ServerItemViewMo_522") || server.Region == "Not loaded" || string.IsNullOrEmpty(server.Ip))
                .ToList();

            if (pendingServers.Count == 0)
            {
                LoadingStatus = LocalizationService.Get("Auto_ServerListViewMo_549");
                return;
            }

            LoadingStatus = LocalizationService.Format("Auto_ServerListViewMo_550", pendingServers.Count);

            try
            {
                using var gate = new SemaphoreSlim(4, 4);
                var tasks = pendingServers.Select(async server =>
                {
                    await gate.WaitAsync(ct);
                    try
                    {
                        if (ct.IsCancellationRequested) return;
                        await LoadRegionCoreAsync(server, ct);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }).ToArray();

                await Task.WhenAll(tasks);
                if (SortMode is ServerSortMode.PingAsc or ServerSortMode.PingDesc)
                {
                    ApplyServerSortAndFilter();
                }
                LoadingStatus = LocalizationService.Get("Auto_ServerListViewMo_549");
            }
            catch (OperationCanceledException)
            {
                LoadingStatus = LocalizationService.Get("Auto_ServerListViewMo_551");
            }
        }
    }
}
