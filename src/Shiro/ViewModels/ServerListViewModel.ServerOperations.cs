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
                    _mainViewModel.StatusMessage = LocalizationService.IsEnglish ? $"🎯 Selected Server: {targetId}" : $"🎯 Đã chọn Server: {targetId}";
                }

                ClipboardHelper.SetText(targetId);
                LoadingStatus = LocalizationService.IsEnglish ? $"📋 Selected and copied Job ID: {targetId}" : $"📋 Đã chọn và sao chép Job ID: {targetId}";
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
                LoadingStatus = LocalizationService.IsEnglish ? $"📋 Copied Job ID: {targetId}" : $"📋 Đã sao chép Job ID: {targetId}";
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
                LoadingStatus = LocalizationService.IsEnglish ? "⚠️ Invalid Place ID!" : "⚠️ Place ID không hợp lệ!";
                return;
            }

            if (_mainViewModel != null)
            {
                _mainViewModel.PlaceId = placeId.ToString();
                _mainViewModel.JobId = targetServer;
            }

            var selectedAccounts = _mainViewModel?.Accounts?.Where(a => a.IsSelected && a.Model != null).Select(a => a.Model).ToList();
            if (selectedAccounts == null || selectedAccounts.Count == 0)
            {
                if (AccountManager.SelectedAccount != null)
                    selectedAccounts = new List<Account> { AccountManager.SelectedAccount };
                else if (AccountManager.LastValidAccount != null)
                    selectedAccounts = new List<Account> { AccountManager.LastValidAccount };
                else
                {
                    var firstAcc = _mainViewModel?.Accounts?.FirstOrDefault(a => a.Model != null)?.Model
                                   ?? AccountManager.GetAccountsSnapshot()?.FirstOrDefault();
                    if (firstAcc != null)
                        selectedAccounts = new List<Account> { firstAcc };
                }
            }

            if (selectedAccounts == null || selectedAccounts.Count == 0)
            {
                LoadingStatus = LocalizationService.IsEnglish
                    ? "⚠️ Please add accounts in the Accounts tab before joining!"
                    : "⚠️ Vui lòng thêm tài khoản trong tab Tài khoản trước khi vào server!";
                return;
            }

            string serverDesc = string.IsNullOrEmpty(targetServer) ? (LocalizationService.IsEnglish ? "random" : "ngẫu nhiên") : targetServer;
            LoadingStatus = LocalizationService.IsEnglish
                ? $"🚀 Joining Server {serverDesc} with {selectedAccounts.Count} account(s)..."
                : $"🚀 Đang vào Server {serverDesc} với {selectedAccounts.Count} tài khoản...";
            if (AccountManager.Instance != null)
            {
                await AccountManager.Instance.ModernJoinServerDirect(selectedAccounts, placeId, targetServer).ConfigureAwait(true);
            }
        }

        private Account? ResolveAuthAccount()
        {
            var selectedFromVm = _mainViewModel?.Accounts?.FirstOrDefault(a => a.IsSelected && a.Valid && a.Model != null)?.Model;
            if (selectedFromVm != null)
                return selectedFromVm;

            if (AccountManager.SelectedAccount != null && AccountManager.SelectedAccount.Valid)
                return AccountManager.SelectedAccount;

            if (AccountManager.LastValidAccount != null && AccountManager.LastValidAccount.Valid)
                return AccountManager.LastValidAccount;

            return AccountManager.GetAccountsSnapshot().FirstOrDefault(a => a != null && a.Valid)
                   ?? AccountManager.SelectedAccount
                   ?? AccountManager.LastValidAccount;
        }

        private Task LoadRegionAsync(object? param) => LoadRegionCoreAsync(param as ServerItemViewModel, CancellationToken.None);

        private async Task LoadRegionCoreAsync(ServerItemViewModel? server, CancellationToken ct)
        {
            if (server == null || server.RawData == null) return;
            server.IsBusyPinging = true;

            try
            {
                var acc = ResolveAuthAccount();
                if (acc == null)
                {
                    server.Region = LocalizationService.IsEnglish ? "Login required" : "Cần đăng nhập";
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
                    server.Region = result.Region ?? string.Empty;
                }
                else
                {
                    server.Region = !string.IsNullOrEmpty(result.Region) ? result.Region : (LocalizationService.IsEnglish ? "Unknown" : "Không rõ");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                server.Region = LocalizationService.IsEnglish ? "Load error" : "Lỗi nạp";
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
                .Where(server => string.IsNullOrEmpty(server.Region) || server.Region == "Chưa nạp" || server.Region == "Not loaded" || string.IsNullOrEmpty(server.Ip))
                .ToList();

            if (pendingServers.Count == 0)
            {
                LoadingStatus = LocalizationService.IsEnglish
                    ? "✅ Server regions loaded."
                    : "✅ Đã nạp vị trí máy chủ hoàn tất.";
                return;
            }

            LoadingStatus = LocalizationService.IsEnglish
                ? $"🌍 Loading server regions and measuring ping ({pendingServers.Count} servers)..."
                : $"🌍 Đang nạp vị trí vùng và đo ping ({pendingServers.Count} máy chủ)...";

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
                LoadingStatus = LocalizationService.IsEnglish
                    ? "✅ Server regions loaded."
                    : "✅ Đã nạp vị trí máy chủ hoàn tất.";
            }
            catch (OperationCanceledException)
            {
                LoadingStatus = LocalizationService.IsEnglish
                    ? "⏹️ Region loading cancelled."
                    : "⏹️ Đã hủy nạp vị trí máy chủ.";
            }
        }
    }
}
