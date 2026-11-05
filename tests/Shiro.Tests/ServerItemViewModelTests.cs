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
            Assert.Equal("Tokyo", srvItem.RegionText);
            Assert.True(srvItem.HasFlagImage);
            Assert.NotNull(srvItem.FlagImage);

            srvItem.NotifyLanguageChanged();
            Assert.Equal("JP Tokyo", srvItem.RegionDisplay);
        }

        [Theory]
        [InlineData("US", "Newark, US", "us")]
        [InlineData("VN", "Hanoi, VN", "vn")]
        [InlineData("SG", "Singapore, SG", "sg")]
        [InlineData("EU", "Frankfurt, DE", "eu")]
        [InlineData("", "Ashburn, US", "us")]
        [InlineData("", "US-East", "us")]
        public void FlagIconService_LoadsFlagDrawingImage_ForCommonCountries(string countryCode, string region, string expectedCode)
        {
            string? inferred = Services.FlagIconService.InferCountryCode(countryCode, region);
            Assert.Equal(expectedCode, inferred);

            var img = Services.FlagIconService.GetFlagImage(inferred);
            Assert.NotNull(img);
        }

        [Fact]
        public void ServerListViewModel_HideFullAndHideEmpty_FiltersCorrectly()
        {
            var srvVm = new ServerListViewModel();
            srvVm.SetRawServers(new[]
            {
                new ServerItemViewModel { JobId = "empty-0", Type = "Public", Playing = 0, MaxPlayers = 10 },
                new ServerItemViewModel { JobId = "sparse-1", Type = "Public", Playing = 1, MaxPlayers = 10 },
                new ServerItemViewModel { JobId = "active-5", Type = "Public", Playing = 5, MaxPlayers = 10 },
                new ServerItemViewModel { JobId = "full-10", Type = "Public", Playing = 10, MaxPlayers = 10 }
            });

            Assert.Equal(4, srvVm.Servers.Count);

            // HideFullServers removes 10/10
            srvVm.HideFullServers = true;
            Assert.Equal(3, srvVm.Servers.Count);
            Assert.DoesNotContain(srvVm.Servers, s => s.JobId == "full-10");

            // HideVipServers ("Ẩn server trống") removes 0/10 and 1/10 when active (>=2) servers exist
            srvVm.HideVipServers = true;
            Assert.Single(srvVm.Servers);
            Assert.Equal("active-5", srvVm.Servers[0].JobId);

            // Turning both off restores all 4
            srvVm.HideFullServers = false;
            srvVm.HideVipServers = false;
            Assert.Equal(4, srvVm.Servers.Count);
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
