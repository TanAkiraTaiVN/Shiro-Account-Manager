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
    /// <summary>
    /// Stateless CTS lifecycle, VIP server lookup, and server-item mapping helpers for <see cref="ServerListViewModel"/>.
    /// </summary>
    internal static class ServerListOperationHelper
    {
        public static CancellationTokenSource? ReplaceCts(ref CancellationTokenSource? slot, bool createNew, string context)
        {
            var nextCts = createNew ? new CancellationTokenSource() : null;
            var prevCts = Interlocked.Exchange(ref slot, nextCts);
            if (prevCts != null)
            {
                SafeExecuteHelper.Try(() => prevCts.Cancel(), $"{context}.Cancel");
                SafeExecuteHelper.Try(() => prevCts.Dispose(), $"{context}.Dispose");
            }
            return nextCts;
        }

        public static void ReleaseFinishedCts(ref CancellationTokenSource? slot, CancellationTokenSource cts, string context)
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref slot, null, cts), cts))
                SafeExecuteHelper.Try(() => cts.Dispose(), $"{context}.Dispose");
        }

        public static string ResolveTargetServerId(ServerItemViewModel server) =>
            server.Type == "VIP" && server.RawData != null && !string.IsNullOrEmpty(server.RawData.accessCode)
                ? $"VIP:{server.RawData.accessCode}"
                : server.JobId;

        public static ServerItemViewModel CreatePublicServerItem(ServerData data) => new()
        {
            JobId = data.id,
            Type = "Public",
            MaxPlayers = data.maxPlayers,
            Playing = data.playing,
            Ping = data.ping ?? -1,
            Region = string.IsNullOrEmpty(data.region) ? string.Empty : data.region,
            Ip = data.ip ?? string.Empty,
            RawData = data
        };

        public static List<ServerItemViewModel> CollectPendingRegionServers(IReadOnlyList<ServerItemViewModel> servers, int maxCount = 25)
        {
            string notLoadedText = LocalizationService.Get("ServerList.NotLoaded");
            var pending = new List<ServerItemViewModel>(maxCount);
            for (int i = 0; i < servers.Count && pending.Count < maxCount; i++)
            {
                ServerItemViewModel server = servers[i];
                if (string.IsNullOrEmpty(server.Region) ||
                    string.Equals(server.Region, notLoadedText, StringComparison.Ordinal) ||
                    string.Equals(server.Region, "Not loaded", StringComparison.Ordinal) ||
                    string.IsNullOrEmpty(server.Ip))
                {
                    pending.Add(server);
                }
            }
            return pending;
        }

        public static Account? ResolveAuthAccount(MainViewModel? mainViewModel)
        {
            var selectedFromVm = mainViewModel?.Accounts?.FirstOrDefault(a => a.IsSelected && a.Valid && a.Model != null)?.Model;
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

        public static async Task<List<ServerItemViewModel>?> FetchVipServersAsync(
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
    }
}
