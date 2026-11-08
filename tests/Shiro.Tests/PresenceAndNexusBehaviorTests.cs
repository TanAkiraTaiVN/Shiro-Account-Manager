#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using NSubstitute;
using Shiro.Classes;
using Shiro.Http;
using Shiro.Nexus;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class PresenceAndNexusBehaviorTests : IDisposable
    {
        private readonly string? _originalShowPresence;

        public PresenceAndNexusBehaviorTests()
        {
            AppSettings.ActiveIniFile ??= new IniFile();
            _originalShowPresence = AppSettings.General.Get("ShowPresence");
            AppSettings.General.Set("ShowPresence", "true");
        }

        public void Dispose()
        {
            if (_originalShowPresence != null)
                AppSettings.General.Set("ShowPresence", _originalShowPresence);
            else
                AppSettings.General.Set("ShowPresence", "true");
        }

        [Fact]
        public void Presence_GetStatusText_HandlesAllPresenceStates()
        {
            // 1. Session expired (invalid account)
            var expiredAcc = new Account { Username = "ExpiredUser", Valid = false };
            string expiredText = Presence.GetStatusText(expiredAcc);
            Assert.Contains(expiredText, new[] { "Phiên đã hết hạn", "Session Expired", LocalizationService.Get("Presence.SessionExpiredBadge") });

            // 2. Launching state overrides presence
            var launchingAcc = new Account { Username = "LaunchingUser", Valid = true };
            Assert.Equal("● Launching…", Presence.GetStatusText(launchingAcc, isLaunching: true));

            // 3. ShowPresence disabled returns Ready
            AppSettings.General.Set("ShowPresence", "false");
            Assert.Equal("● Ready", Presence.GetStatusText(launchingAcc, isLaunching: false));
            AppSettings.General.Set("ShowPresence", "true");

            // 4. Checking presence (account.Presence is null)
            Assert.Equal("● Checking…", Presence.GetStatusText(launchingAcc, isLaunching: false));

            // 5. InGame with game name
            var inGameWithName = new Account
            {
                Username = "InGameUser1",
                Valid = true,
                Presence = new UserPresence
                {
                    userPresenceType = UserPresenceType.InGame,
                    gameName = "Blox Fruits"
                }
            };
            Assert.Equal("● Playing: Blox Fruits", Presence.GetStatusText(inGameWithName));

            // 6. InGame without game name
            var inGameNoName = new Account
            {
                Username = "InGameUser2",
                Valid = true,
                Presence = new UserPresence
                {
                    userPresenceType = UserPresenceType.InGame,
                    gameName = ""
                }
            };
            Assert.Equal("● Playing Roblox", Presence.GetStatusText(inGameNoName));

            // 7. InStudio
            var inStudio = new Account
            {
                Username = "StudioUser",
                Valid = true,
                Presence = new UserPresence { userPresenceType = UserPresenceType.InStudio }
            };
            Assert.Equal("● In Roblox Studio", Presence.GetStatusText(inStudio));

            // 8. Online
            var online = new Account
            {
                Username = "OnlineUser",
                Valid = true,
                Presence = new UserPresence { userPresenceType = UserPresenceType.Online }
            };
            Assert.Equal("● Online", Presence.GetStatusText(online));

            // 9. Offline
            var offline = new Account
            {
                Username = "OfflineUser",
                Valid = true,
                Presence = new UserPresence { userPresenceType = UserPresenceType.Offline }
            };
            Assert.Equal("● Offline", Presence.GetStatusText(offline));
        }

        [Fact]
        public void Presence_GetStatusColor_ReturnsMatchingColors()
        {
            var expiredAcc = new Account { Username = "Exp", Valid = false };
            Assert.Equal(Color.FromArgb(255, 255, 92, 92), Presence.GetStatusColor(expiredAcc));

            var launchAcc = new Account { Username = "Launch", Valid = true };
            Assert.Equal(Color.FromArgb(255, 170, 170, 170), Presence.GetStatusColor(launchAcc, isLaunching: true));

            var inGameAcc = new Account
            {
                Username = "InGame",
                Valid = true,
                Presence = new UserPresence { userPresenceType = UserPresenceType.InGame }
            };
            Assert.Equal(Presence.Colors[UserPresenceType.InGame], Presence.GetStatusColor(inGameAcc));

            var onlineAcc = new Account
            {
                Username = "Online",
                Valid = true,
                Presence = new UserPresence { userPresenceType = UserPresenceType.Online }
            };
            Assert.Equal(Presence.Colors[UserPresenceType.Online], Presence.GetStatusColor(onlineAcc));
        }

        [Fact]
        public void ControlledAccount_StatusTransitions_UpdateDisplayProperties()
        {
            var acc = new Account { Username = "NexusPlayer", UserID = 12345 };
            var controlled = new ControlledAccount(acc);

            Assert.Equal("NexusPlayer", controlled.Username);
            Assert.Equal(acc, controlled.LinkedAccount);
            Assert.Equal(AccountStatus.Offline, controlled.Status);
            Assert.Equal("Offline", controlled.StatusText);
            Assert.NotNull(controlled.StatusColorBrush);

            controlled.Status = AccountStatus.Online;
            Assert.Equal("Online", controlled.StatusText);

            controlled.Disconnect();
            Assert.Equal(AccountStatus.Offline, controlled.Status);
            Assert.Equal("Offline", controlled.StatusText);
        }

        [Fact]
        public void ControlledAccount_Properties_PersistValues()
        {
            var controlled = new ControlledAccount
            {
                Username = "TestControlled",
                Position = "X: 100, Y: 50, Z: -200",
                Fps = "60",
                Ping = "45ms",
                AutoExecute = "print('Hello Nexus')",
                PlaceId = 18973,
                JobId = "job-nexus-123",
                InGameJobId = "server-job-456",
                RelaunchDelay = 15.5,
                AutoRelaunch = true,
                IsChecked = true,
                ClientCanReceive = true
            };

            Assert.Equal("TestControlled", controlled.Username);
            Assert.Equal("X: 100, Y: 50, Z: -200", controlled.Position);
            Assert.Equal("60", controlled.Fps);
            Assert.Equal("45ms", controlled.Ping);
            Assert.Equal("print('Hello Nexus')", controlled.AutoExecute);
            Assert.Equal(18973, controlled.PlaceId);
            Assert.Equal("job-nexus-123", controlled.JobId);
            Assert.Equal("server-job-456", controlled.InGameJobId);
            Assert.Equal(15.5, controlled.RelaunchDelay);
            Assert.True(controlled.AutoRelaunch);
            Assert.True(controlled.IsChecked);
            Assert.True(controlled.ClientCanReceive);
        }

        [Fact]
        public async Task Presence_UpdateAndGetPresence_ExecutesNetworkAndCachesGameNames()
        {
            RobloxRateLimitCoordinator.Instance.ResetBackoff();
            var mockApi = Substitute.For<IRobloxApiClient>();

            string presenceJson = "{\"userPresences\":[{\"userId\":998877,\"userPresenceType\":2,\"placeId\":1818,\"universeId\":55555}]}";
            var presenceHandler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(presenceJson, System.Text.Encoding.UTF8, "application/json")
            });
            var presenceClient = new RestClient(new HttpClient(presenceHandler), "https://presence.roblox.com/");
            mockApi.Presence.Returns(presenceClient);

            string gamesJson = "{\"data\":[{\"id\":55555,\"name\":\"Test Game Experience\"}]}";
            var gamesHandler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(gamesJson, System.Text.Encoding.UTF8, "application/json")
            });
            var gamesClient = new RestClient(new HttpClient(gamesHandler), "https://games.roblox.com/");
            mockApi.Games.Returns(gamesClient);

            using var _ = AppServices.Override<IRobloxApiClient>(mockApi);

            var testAccount = new Account
            {
                Username = "PresenceTestUser",
                UserID = 998877,
                Valid = true
            };

            AccountRepository.Default.AddThreadSafe(testAccount);
            lock (AccountManager.AccountListLock)
            {
                AppServices.AccountRepository.AddThreadSafe(testAccount);
            }

            try
            {
                // 1. UpdatePresence
                await Presence.UpdatePresence(998877);
                Assert.NotNull(testAccount.Presence);
                Assert.Equal(UserPresenceType.InGame, testAccount.Presence!.userPresenceType);
                Assert.Equal("Test Game Experience", testAccount.Presence.gameName);

                // 2. GetPresence
                var dict = await Presence.GetPresence(998877);
                Assert.NotNull(dict);
                Assert.True(dict!.ContainsKey(998877));
                Assert.Equal(UserPresenceType.InGame, dict[998877].userPresenceType);

                // 3. GetPresenceSingular
                var singular = await Presence.GetPresenceSingular(998877);
                Assert.NotNull(singular);
                Assert.Equal(998877, singular!.userId);

                // 4. Invalid User IDs return empty dictionary or early return
                var empty = await Presence.GetPresence(0, -1);
                Assert.NotNull(empty);
                Assert.Empty(empty!);

                var ex = await Record.ExceptionAsync(() => Presence.UpdatePresence(0, -1));
                Assert.Null(ex);
            }
            finally
            {
                RobloxRateLimitCoordinator.Instance.ResetBackoff();
                AccountRepository.Default.Remove(testAccount);
                lock (AccountManager.AccountListLock)
                {
                    AccountRegistrationService.Default.RemoveAccount(testAccount);
                }
            }
        }
    }
}
