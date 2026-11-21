#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Http;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public sealed partial class ServerBrowserTabViewModel
    {
        internal static Task<List<ServerItemViewModel>?> TestFetchVipServersAsync(
            RestClient gamesClient,
            Account vipAuthAccount,
            long placeId,
            CancellationToken ct) => ServerListOperationHelper.FetchVipServersAsync(gamesClient, vipAuthAccount, placeId, ct);

        private static string FormatDisplayServerId(string serverId) =>
            StreamerModeService.IsActive ? StreamerModeService.MaskJobId(serverId) : serverId;

        private void SelectServerJobId(object? param)
        {
            if (param is not ServerItemViewModel server) return;
            string targetId = ServerListOperationHelper.ResolveTargetServerId(server);
            string displayId = FormatDisplayServerId(targetId);
            string currentInput = _getPlaceIdInput();
            if (_mainViewModel != null)
            {
                if (!string.IsNullOrWhiteSpace(currentInput))
                    _mainViewModel.PlaceId = currentInput;
                _mainViewModel.JobId = targetId;
                _mainViewModel.StatusMessage = LocalizationService.Format("ServerList.ServerSelected", displayId);
            }
            ClipboardHelper.SetText(targetId);
            _setLoadingStatus(LocalizationService.Format("ServerList.JobIdSelectedAndCopied", displayId));
        }

        private void CopyJobId(object? param)
        {
            if (param is not ServerItemViewModel server) return;
            string targetId = ServerListOperationHelper.ResolveTargetServerId(server);
            ClipboardHelper.SetText(targetId);
            _setLoadingStatus(LocalizationService.Format("ServerList.JobIdCopied", FormatDisplayServerId(targetId)));
        }

        private async Task JoinServerAsync(object? param)
        {
            if (param is not ServerItemViewModel server) return;

            string targetServer = ServerListOperationHelper.ResolveTargetServerId(server);
            string currentInput = _getPlaceIdInput();
            string pidText = !string.IsNullOrWhiteSpace(currentInput) ? currentInput : (_mainViewModel?.PlaceId ?? string.Empty);
            long placeId = AccountLauncherService.ParsePlaceId(pidText);
            if (placeId <= 0)
            {
                _setLoadingStatus(LocalizationService.Get("ServerList.InvalidPlaceId"));
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
                string warnTitle = LocalizationService.Get("Main.NoAccountSelectedTitle");
                string warnMsg = LocalizationService.Get("ServerList.SelectAccountBeforeJoinServer");
                _setLoadingStatus(warnMsg);
                if (_mainViewModel != null)
                    _mainViewModel.StatusMessage = warnMsg;
                ToastService.ShowWarning(warnTitle, warnMsg);
                return;
            }

            string serverDesc = string.IsNullOrEmpty(targetServer) ? LocalizationService.Get("ServerList.RandomServerKeyword") : FormatDisplayServerId(targetServer);
            _setLoadingStatus(LocalizationService.Format("ServerList.JoiningServerWithAccounts", serverDesc, selectedAccounts.Count));

            await AppServices.HostUiBridge.JoinServerFromHostAsync(selectedAccounts, placeId, targetServer).ConfigureAwait(true);
        }

        private Account? ResolveAuthAccount() => ServerListOperationHelper.ResolveAuthAccount(_mainViewModel);

        private async Task LoadRegionAsync(object? param)
        {
            await LoadRegionCoreAsync(param as ServerItemViewModel, CancellationToken.None);
            if (SortMode is ServerSortMode.PingAsc or ServerSortMode.PingDesc)
                ApplyServerSortAndFilter();
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
                    AppDispatcher.Post(() => server.Region = LocalizationService.Get("ServerQuery.LoginRequired"));
                    return;
                }

                string currentInput = _getPlaceIdInput();
                string rawPid = !string.IsNullOrWhiteSpace(currentInput) ? currentInput : (_mainViewModel?.PlaceId ?? string.Empty);
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
                        server.Region = !string.IsNullOrEmpty(result.Region) ? result.Region : LocalizationService.Get("ServerQuery.UnknownRegion");
                    }
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Operation cancelled
            }
            catch (Exception ex)
            {
                AppDispatcher.Post(() => server.Region = LocalizationService.Get("ServerQuery.RegionLoadError"));
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

            var pendingServers = ServerListOperationHelper.CollectPendingRegionServers(_servers);
            if (pendingServers.Count == 0)
            {
                _setLoadingStatus(LocalizationService.Get("ServerList.RegionsLoadedComplete"));
                return;
            }

            var cts = ServerListOperationHelper.ReplaceCts(ref _regionsCts, createNew: true, "ServerListViewModel.RegionsCts")!;
            var ct = cts.Token;

            IsLoadingRegions = true;
            _setLoadingStatus(LocalizationService.Format("ServerList.LoadingRegionsAndPinging", pendingServers.Count));

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

                AppDispatcher.Post(() =>
                {
                    if (!ReferenceEquals(Volatile.Read(ref _regionsCts), cts)) return;
                    if (SortMode is ServerSortMode.PingAsc or ServerSortMode.PingDesc)
                        ApplyServerSortAndFilter();
                    _setLoadingStatus(LocalizationService.Get("ServerList.RegionsLoadedComplete"));
                });
            }
            catch (OperationCanceledException)
            {
                AppDispatcher.Post(() =>
                {
                    if (!ReferenceEquals(Volatile.Read(ref _regionsCts), cts)) return;
                    _setLoadingStatus(LocalizationService.Get("ServerList.LoadingRegionsCancelled"));
                });
            }
            finally
            {
                AppDispatcher.Post(() =>
                {
                    if (!ReferenceEquals(Volatile.Read(ref _regionsCts), cts)) return;
                    IsLoadingRegions = false;
                    ServerListOperationHelper.ReleaseFinishedCts(ref _regionsCts, cts, "ServerListViewModel.RegionsCts");
                });
            }
        }
    }
}
