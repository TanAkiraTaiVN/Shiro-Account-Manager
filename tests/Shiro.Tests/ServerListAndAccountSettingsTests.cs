using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Shiro;
using Shiro.Classes;
using Shiro.Http;
using Shiro.Services;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class ServerListAndAccountSettingsTests
    {
        [Fact]
        public void ServerListViewModel_SortingFilteringAndWatcher_WorkCorrectly()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "Shiro-ServerListTest-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (StoragePaths.OverrideDataDirectoryForTesting(tempDir))
                {
                    AppSettings.ActiveIniFile ??= new IniFile();

                    var mainVm = new MainViewModel();
                    var vm = mainVm.ServerListViewModel;

                    // 1. SyncPlaceId & PlaceIdInput
                    vm.PlaceIdInput = "1818";
                    Assert.Equal("1818", mainVm.PlaceId);
                    vm.SyncPlaceId("2929");
                    Assert.Equal("2929", vm.PlaceIdInput);

                    // 2. SetRawServers, AppendRawServers, PrependRawServers, ClearRawServers
                    var s1 = new ServerItemViewModel { JobId = "job-1", Type = "Public", Playing = 8, MaxPlayers = 10, Ping = 120, Region = "US-East" };
                    var s2 = new ServerItemViewModel { JobId = "job-2", Type = "Public", Playing = 10, MaxPlayers = 10, Ping = 45, Region = "SG" };
                    var sVip = new ServerItemViewModel { JobId = "VIP:code123", Type = "VIP", Playing = 2, MaxPlayers = 10, Ping = 60, Region = "VIP Private", RawData = new ServerData { accessCode = "code123", id = "vip-id" } };

                    vm.SetRawServers(new[] { s1, s2 });
                    Assert.Equal(2, vm.RawServersCount);
                    Assert.Equal(2, vm.TotalServersCount);
                    Assert.True(vm.HasServers);
                    Assert.False(vm.IsServersEmpty);

                    vm.PrependRawServers(new[] { sVip });
                    Assert.Equal(3, vm.RawServersCount);
                    Assert.Equal("VIP:code123", vm.Servers[0].JobId);

                    // 3. Sorting modes via commands
                    Assert.Equal(ServerSortMode.Default, vm.SortMode);
                    vm.ToggleSortPlayersCommand.Execute(null);
                    Assert.Equal(ServerSortMode.PlayersAsc, vm.SortMode);
                    Assert.True(vm.HasPlayersSortIndicator);
                    Assert.Equal(2, vm.Servers[0].Playing);

                    vm.ToggleSortPlayersCommand.Execute(null);
                    Assert.Equal(ServerSortMode.PlayersDesc, vm.SortMode);
                    Assert.Equal(10, vm.Servers[0].Playing);

                    vm.ToggleSortPlayersCommand.Execute(null);
                    Assert.Equal(ServerSortMode.Default, vm.SortMode);

                    vm.ToggleSortPingCommand.Execute(null);
                    Assert.Equal(ServerSortMode.PingAsc, vm.SortMode);
                    Assert.True(vm.HasPingSortIndicator);
                    Assert.Equal(45, vm.Servers[0].Ping);

                    vm.ToggleSortPingCommand.Execute(null);
                    Assert.Equal(ServerSortMode.PingDesc, vm.SortMode);
                    Assert.Equal(120, vm.Servers[0].Ping);

                    vm.ToggleSortPingCommand.Execute(null);
                    Assert.Equal(ServerSortMode.Default, vm.SortMode);

                    // 4. Filtering: HideFullServers & HideVipServers
                    vm.HideFullServers = true;
                    Assert.DoesNotContain(vm.Servers, s => s.JobId == "job-2");

                    vm.HideVipServers = true;
                    Assert.Single(vm.Servers);
                    Assert.Equal("job-1", vm.Servers[0].JobId);

                    vm.HideFullServers = false;
                    vm.HideVipServers = false;
                    Assert.Equal(3, vm.Servers.Count);

                    // 5. SelectServerJobIdCommand & CopyJobIdCommand
                    vm.SelectServerJobIdCommand.Execute(sVip);
                    Assert.Equal("VIP:code123", mainVm.JobId);
                    vm.CopyJobIdCommand.Execute(s1);
                    Assert.Contains("job-1", vm.LoadingStatus);

                    // 6. Favorites: Add, Duplicate check, Select, Launch, Remove
                    var searchedGame = new GameSearchItemViewModel
                    {
                        PlaceId = 555666,
                        Name = "Blox Fruits Test",
                        PlayerCount = 1000,
                        ThumbnailUrl = "https://tr.rbxcdn.com/icon.png"
                    };

                    vm.AddFavoriteCommand.Execute(searchedGame);
                    Assert.Single(vm.Favorites);
                    Assert.False(vm.IsFavoritesEmpty);
                    Assert.Equal(555666, vm.Favorites[0].PlaceId);

                    // Duplicate add should not add second entry
                    vm.AddFavoriteCommand.Execute(searchedGame);
                    Assert.Single(vm.Favorites);

                    // Add Favorite from PlaceIdInput when parameter is null
                    vm.PlaceIdInput = "777888";
                    vm.AddFavoriteCommand.Execute(null);
                    Assert.Equal(2, vm.Favorites.Count);

                    var favItem = vm.Favorites[0];
                    favItem.PrivateServer = "VIP:favcode";
                    vm.SelectFavoriteCommand.Execute(favItem);
                    Assert.Equal("555666", mainVm.PlaceId);
                    Assert.Equal("VIP:favcode", mainVm.JobId);

                    vm.LaunchFavoriteCommand.Execute(favItem);

                    // SelectGame & LaunchGame
                    vm.SelectGameCommand.Execute(searchedGame);
                    Assert.Equal("555666", mainVm.PlaceId);
                    vm.LaunchGameCommand.Execute(searchedGame);

                    // RecentGame commands in library
                    var recentItem = new RecentGameItemViewModel { PlaceId = 333444, Name = "Recent Test Game" };
                    vm.SelectRecentGameInLibraryCommand.Execute(recentItem);
                    Assert.Equal("333444", mainVm.PlaceId);
                    vm.LaunchRecentGameInLibraryCommand.Execute(recentItem);

                    vm.RemoveFavoriteCommand.Execute(favItem);
                    Assert.Single(vm.Favorites);

                    // 7. Watcher properties
                    vm.ExitOnBeta = true;
                    Assert.True(vm.ExitOnBeta);
                    vm.ExitIfNoConnection = true;
                    Assert.True(vm.ExitIfNoConnection);
                    vm.SaveWindowPositions = true;
                    Assert.True(vm.SaveWindowPositions);
                    vm.VerifyDataModel = false;
                    Assert.False(vm.VerifyDataModel);
                    vm.IgnoreExistingProcesses = false;
                    Assert.False(vm.IgnoreExistingProcesses);
                    vm.CloseRbxMemory = true;
                    Assert.True(vm.CloseRbxMemory);
                    vm.MemoryThresholdMb = 350;
                    Assert.Equal(350, vm.MemoryThresholdMb);
                    vm.ConnectionTimeoutSec = 90;
                    Assert.Equal(90, vm.ConnectionTimeoutSec);
                    vm.ExpectedWindowTitle = "CustomTitle";
                    Assert.Equal("CustomTitle", vm.ExpectedWindowTitle);

                    // 8. CancelOperation & ClearRawServers
                    vm.CancelOperationCommand.Execute(null);
                    Assert.False(vm.IsLoadingServers);
                    vm.ClearRawServers();
                    Assert.Equal(0, vm.RawServersCount);
                    Assert.True(vm.IsServersEmpty);
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); }
                    catch (IOException) { }
                }
            }
        }

        [Fact]
        public async Task ServerListViewModel_PlayerSearchValidation_AndJoinValidation_WorkCorrectly()
        {
            var mainVm = new MainViewModel();
            var vm = mainVm.ServerListViewModel;

            // 1. SearchPlayerAsync with empty username
            vm.SearchPlayerUsername = "";
            vm.SearchPlayerCommand.Execute(null);
            await Task.Delay(30);
            Assert.NotEmpty(vm.PlayerSearchStatus);

            // 2. SearchPlayerAsync with invalid PlaceId
            vm.SearchPlayerUsername = "SomePlayer";
            vm.PlaceIdInput = "invalid";
            vm.SearchPlayerCommand.Execute(null);
            await Task.Delay(30);
            Assert.Contains("Place ID", vm.PlayerSearchStatus);

            // 3. RefreshServersAsync with invalid PlaceId
            vm.PlaceIdInput = "0";
            await vm.RefreshServersAsync();
            Assert.Contains("Place ID", vm.LoadingStatus);

            // 4. JoinServerCommand with invalid PlaceId & with no selected accounts
            var server = new ServerItemViewModel { JobId = "job-abc", Type = "Public" };
            vm.PlaceIdInput = "-5";
            vm.JoinServerCommand.Execute(server);
            await Task.Delay(30);

            vm.PlaceIdInput = "1818";
            foreach (var a in mainVm.Accounts) a.IsSelected = false;
            vm.JoinServerCommand.Execute(server);
            await Task.Delay(30);
        }

        [Fact]
        public async Task AccountSettingsApi_ParseAccessCode_PinAndTicketHelpers_WorkCorrectly()
        {
            var acc = new Account
            {
                Username = "SettingsTester",
                UserID = 12345,
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--fake-token",
                PinUnlocked = DateTime.Now.AddMinutes(10)
            };

            // 1. CheckPinAsync returns true immediately when PinUnlocked is in the future
            Assert.True(await acc.CheckPinAsync());

            // 2. UnlockPinAsync rejects non-4-char pin, and returns true if already unlocked
            Assert.False(await acc.UnlockPinAsync("12"));
            Assert.True(await acc.UnlockPinAsync("1234"));

            // 3. When PinUnlocked is expired and SecurityToken is empty, CheckPinAsync returns false
            acc.PinUnlocked = DateTime.MinValue;
            acc.SecurityToken = "";
            Assert.False(await acc.CheckPinAsync(Internal: true));

            // 4. ParseAccessCode supports both legacy JoinPrivateGame and modern JSON accessCode/gameInstanceId
            var legacyResp = new RestResponse
            {
                StatusCode = HttpStatusCode.OK,
                Content = "Roblox.GameLauncher.joinPrivateGame(1818, '11111111-2222-3333-4444-555555555555', false)"
            };
            Assert.True(acc.ParseAccessCode(legacyResp, out string code1));
            Assert.Equal("11111111-2222-3333-4444-555555555555", code1);

            var modernResp = new RestResponse
            {
                StatusCode = HttpStatusCode.OK,
                Content = "{\"accessCode\": \"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\"}"
            };
            Assert.True(acc.ParseAccessCode(modernResp, out string code2));
            Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", code2);

            var emptyResp = new RestResponse { StatusCode = HttpStatusCode.OK, Content = "{}" };
            Assert.False(acc.ParseAccessCode(emptyResp, out _));

            // 5. SetAvatarAsync handles empty or invalid JSON gracefully without throwing
            await acc.SetAvatarAsync("");
            await acc.SetAvatarAsync("{ invalid json");

            // 6. AccountLaunchService.CreateTicketRequest builds signed request with expected headers
            acc.SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--valid-looking-token";
            using var ticketReq = AccountLaunchService.CreateTicketRequest(acc, "csrf-123");
            Assert.Equal(System.Net.Http.HttpMethod.Post, ticketReq.Method);
            Assert.Equal("https://auth.roblox.com/v1/authentication-ticket", ticketReq.RequestUri?.ToString());
            Assert.True(ticketReq.Headers.Contains("X-CSRF-TOKEN"));
            Assert.True(ticketReq.Headers.Contains("rbxauthenticationnegotiation"));
        }
    }
}
