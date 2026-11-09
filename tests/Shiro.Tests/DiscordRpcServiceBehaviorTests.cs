#nullable enable
using System;
using System.Collections.Generic;
using NSubstitute;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("StaticSeams")]
    public class DiscordRpcServiceBehaviorTests
    {
        [Fact]
        public void DefaultClientId_IsConstant()
        {
            Assert.Equal("1301012345678901234", DiscordRpcService.DefaultClientId);
        }

        [Fact]
        public void DiscordActivityItem_PropertiesAndButtons_HoldValues()
        {
            var activity = new DiscordActivityItem
            {
                Details = "Playing Brookhaven",
                State = "In Game",
                StartTimestamp = 1700000000,
                LargeImageKey = "shiro_custom",
                LargeImageText = "Shiro AM",
                SmallImageKey = "game_icon",
                SmallImageText = "Brookhaven",
                Buttons = new List<DiscordButtonItem>
                {
                    new DiscordButtonItem { Label = "Join Server", Url = "https://roblox.com" }
                }
            };

            Assert.Equal("Playing Brookhaven", activity.Details);
            Assert.Equal("In Game", activity.State);
            Assert.Equal(1700000000, activity.StartTimestamp);
            Assert.Equal("shiro_custom", activity.LargeImageKey);
            Assert.Equal("Shiro AM", activity.LargeImageText);
            Assert.Equal("game_icon", activity.SmallImageKey);
            Assert.Equal("Brookhaven", activity.SmallImageText);
            Assert.Single(activity.Buttons);
            Assert.Equal("Join Server", activity.Buttons[0].Label);
            Assert.Equal("https://roblox.com", activity.Buttons[0].Url);
        }

        [Fact]
        public void IsEnabled_ReadsAndWritesFromSettings()
        {
            var logger = Substitute.For<IAppLogger>();
            var settings = Substitute.For<ISettingsService>();

            settings.Get<bool>(AppSettingsKeys.General.DiscordRpcEnabled, true).Returns(true);

            using var service = new DiscordRpcService(logger, settings);

            Assert.True(service.IsEnabled);
            Assert.False(service.IsConnected);

            service.IsEnabled = false;
            settings.Received().Set(AppSettingsKeys.General.DiscordRpcEnabled, "false");
        }

        [Fact]
        public void UpdatePresence_And_ClearPresence_ManageCachedActivity()
        {
            var logger = Substitute.For<IAppLogger>();
            var settings = Substitute.For<ISettingsService>();
            settings.Get<bool>(AppSettingsKeys.General.DiscordRpcEnabled, true).Returns(false);

            using var service = new DiscordRpcService(logger, settings);

            // When disconnected and disabled, UpdatePresence builds activity without throwing
            var ex1 = Record.Exception(() => service.UpdatePresence("Blox Fruits", 2753915549, "job-123", 5, 2));
            Assert.Null(ex1);

            // ClearPresence removes cached activity
            var ex2 = Record.Exception(() => service.ClearPresence());
            Assert.Null(ex2);

            // Calling Stop when not running is safe
            var ex3 = Record.Exception(() => service.Stop());
            Assert.Null(ex3);
        }

        [Fact]
        public void UpdatePresence_WhenEnabled_FormatsPayloadVariants()
        {
            var logger = Substitute.For<IAppLogger>();
            var settings = Substitute.For<ISettingsService>();
            settings.Get<bool>(AppSettingsKeys.General.DiscordRpcEnabled, true).Returns(true);
            settings.Get<bool>(AppSettingsKeys.General.DiscordRpcJoinCurrentServer, true).Returns(true);
            settings.Get<bool>(AppSettingsKeys.General.DiscordRpcShowGame, true).Returns(true);

            using var service = new DiscordRpcService(logger, settings);

            // 1. In game with placeId and jobId
            var ex1 = Record.Exception(() => service.UpdatePresence("Blox Fruits", 2753915549, "job-guid-123", 5, 2));
            Assert.Null(ex1);

            // 2. In game with placeId but empty jobId
            var ex2 = Record.Exception(() => service.UpdatePresence("Blox Fruits", 2753915549, null, 5, 2));
            Assert.Null(ex2);

            // 3. In game with placeId <= 0
            var ex3 = Record.Exception(() => service.UpdatePresence("Blox Fruits", 0, null, 5, 2));
            Assert.Null(ex3);

            // 4. In game with 0 active in game accounts (ready to launch)
            var ex4 = Record.Exception(() => service.UpdatePresence(null, 0, null, 10, 0));
            Assert.Null(ex4);

            // 5. With join disabled
            settings.Get<bool>(AppSettingsKeys.General.DiscordRpcJoinCurrentServer, true).Returns(false);
            var ex5 = Record.Exception(() => service.UpdatePresence("Blox Fruits", 2753915549, "job-1", 3, 1));
            Assert.Null(ex5);

            // 6. With show game disabled
            settings.Get<bool>(AppSettingsKeys.General.DiscordRpcShowGame, true).Returns(false);
            var ex6 = Record.Exception(() => service.UpdatePresence("Blox Fruits", 2753915549, "job-1", 3, 1));
            Assert.Null(ex6);
        }

        [Fact]
        public void Service_DisposesCleanly()
        {
            var logger = Substitute.For<IAppLogger>();
            var settings = Substitute.For<ISettingsService>();
            var service = new DiscordRpcService(logger, settings);

            var ex = Record.Exception(() =>
            {
                service.Dispose();
                // Double dispose should be safe
                service.Dispose();
            });

            Assert.Null(ex);
        }
    }
}
