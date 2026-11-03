#nullable enable
using Shiro.Classes;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests
{
    public class ServerItemViewModelTests
    {
        [Fact]
        public void ServerItemViewModel_DisplayProperties_MatchExactExpectedValues()
        {
            var srvItem = new ServerItemViewModel
            {
                JobId = "job-123",
                Type = "Public",
                Playing = 5,
                MaxPlayers = 10,
                Ping = 45,
                CountryCode = "JP",
                FlagEmoji = "JP",
                Region = "Tokyo",
                Ip = "128.116.1.1",
                IsBusyPinging = true,
                RawData = new ServerData { id = "job-123", playing = 5, maxPlayers = 10, ping = 45 }
            };

            Assert.Equal("5 / 10", srvItem.PlayerDisplay);
            Assert.Equal(50.0, srvItem.PlayerPercentage);
            Assert.Equal("45 ms", srvItem.PingDisplay);
            Assert.Equal("#22C55E", srvItem.PingColor);

            srvItem.Ping = 120;
            Assert.Equal("#F59E0B", srvItem.PingColor);

            srvItem.Ping = 200;
            Assert.Equal("#EF4444", srvItem.PingColor);

            srvItem.Ping = -1;
            Assert.Equal("#71717A", srvItem.PingColor);

            Assert.Equal("JP Tokyo", srvItem.RegionDisplay);
            srvItem.NotifyLanguageChanged();
            Assert.Equal("JP Tokyo", srvItem.RegionDisplay);
        }

        [Fact]
        public void ServerListViewModel_SearchAndSort_UpdatesStatusAndIndicators()
        {
            var mainVm = new MainViewModel();
            var srvVm = new ServerListViewModel(mainVm);

            srvVm.SearchPlayerUsername = "";
            srvVm.SearchPlayerCommand.Execute(null);
            Assert.Equal(Services.LocalizationService.Get("ServerList.EnterPlayerUsername"), srvVm.PlayerSearchStatus);

            srvVm.SearchPlayerUsername = "builderman";
            srvVm.PlaceIdInput = "invalid";
            srvVm.SearchPlayerCommand.Execute(null);
            Assert.Equal(Services.LocalizationService.Get("ServerList.EnterPlaceIdFirst"), srvVm.PlayerSearchStatus);

            srvVm.ToggleSortPlayersCommand.Execute(null);
            Assert.Equal("▲", srvVm.PlayersSortHeaderIndicator);
            srvVm.ToggleSortPlayersCommand.Execute(null);
            Assert.Equal("▼", srvVm.PlayersSortHeaderIndicator);
            srvVm.ToggleSortPlayersCommand.Execute(null);
            Assert.Equal(string.Empty, srvVm.PlayersSortHeaderIndicator);

            srvVm.ToggleSortPingCommand.Execute(null);
            Assert.Equal("▲", srvVm.PingSortHeaderIndicator);
            srvVm.ToggleSortPingCommand.Execute(null);
            Assert.Equal("▼", srvVm.PingSortHeaderIndicator);
            srvVm.ToggleSortPingCommand.Execute(null);
            Assert.Equal(string.Empty, srvVm.PingSortHeaderIndicator);
        }
    }
}
