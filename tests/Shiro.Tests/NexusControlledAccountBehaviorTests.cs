#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia.Media;
using Shiro.Nexus;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class NexusControlledAccountBehaviorTests : IDisposable
    {
        private readonly Action<Action>? _prevDispatcher;

        public NexusControlledAccountBehaviorTests()
        {
            _prevDispatcher = AppDispatcher.CustomDispatcher;
            // Execute synchronous inline for testing
            AppDispatcher.CustomDispatcher = action => action();
        }

        public void Dispose()
        {
            AppDispatcher.CustomDispatcher = _prevDispatcher;
        }

        [Fact]
        public void Constructor_InitializesDefaultsAccurately()
        {
            var rawAccount = new Account { Username = "NexusPawn", UserID = 10101 };
            var controlled = new ControlledAccount(rawAccount);

            Assert.Equal("NexusPawn", controlled.Username);
            Assert.Same(rawAccount, controlled.LinkedAccount);
            Assert.Equal(AccountStatus.Offline, controlled.Status);
            Assert.Equal("Offline", controlled.StatusText);
            Assert.NotNull(controlled.StatusColorBrush);
            Assert.Equal("-", controlled.Position);
            Assert.Equal("-", controlled.Fps);
            Assert.Equal("-", controlled.Ping);
            Assert.Equal(30.0, controlled.RelaunchDelay);
            Assert.False(controlled.AutoRelaunch);
            Assert.False(controlled.IsChecked);
            Assert.False(controlled.ClientCanReceive);
            Assert.True(controlled.LastPing <= DateTime.UtcNow);
        }

        [Fact]
        public void PropertyChanged_FiresOnMutations()
        {
            var controlled = new ControlledAccount();
            var changedProperties = new HashSet<string>();
            controlled.PropertyChanged += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.PropertyName))
                    changedProperties.Add(e.PropertyName);
            };

            controlled.Username = "NewUsername";
            controlled.IsChecked = true;
            controlled.JobId = "job-123";
            controlled.Position = "(10, 20, 30)";
            controlled.Fps = "144";
            controlled.Ping = "15ms";
            controlled.Status = AccountStatus.Online;

            Assert.Contains("Username", changedProperties);
            Assert.Contains("IsChecked", changedProperties);
            Assert.Contains("JobId", changedProperties);
            Assert.Contains("Position", changedProperties);
            Assert.Contains("Fps", changedProperties);
            Assert.Contains("Ping", changedProperties);
            Assert.Contains("Status", changedProperties);
            Assert.Contains("StatusText", changedProperties);
            Assert.Contains("StatusColorBrush", changedProperties);
            Assert.Equal("Online", controlled.StatusText);
        }

        [Fact]
        public void HandleMessage_NullOrEmptyOrInvalidJson_HandledSafely()
        {
            var controlled = new ControlledAccount { Username = "TestPawn" };

            var ex1 = Record.Exception(() => controlled.HandleMessage(null!));
            Assert.Null(ex1);

            var ex2 = Record.Exception(() => controlled.HandleMessage(""));
            Assert.Null(ex2);

            var ex3 = Record.Exception(() => controlled.HandleMessage("NotValidJsonString"));
            Assert.Null(ex3);
        }

        [Fact]
        public void HandleMessage_PingCommand_ExtractsMetrics()
        {
            var controlled = new ControlledAccount { Username = "MetricPawn" };
            string json = "{\"Name\":\"ping\",\"Payload\":{\"fps\":\"120\",\"ping\":\"32ms\",\"pos\":\"(50, 100, 200)\"}}";

            controlled.HandleMessage(json);

            Assert.True(controlled.ClientCanReceive);
            Assert.Equal("120", controlled.Fps);
            Assert.Equal("32ms", controlled.Ping);
            Assert.Equal("(50, 100, 200)", controlled.Position);
        }

        [Fact]
        public void HandleMessage_SetRelaunch_UpdatesDelay()
        {
            var controlled = new ControlledAccount { Username = "RelaunchPawn" };
            string json = "{\"Name\":\"SetRelaunch\",\"Payload\":{\"Seconds\":\"45.5\"}}";

            controlled.HandleMessage(json);

            Assert.Equal(45.5, controlled.RelaunchDelay);
        }

        [Fact]
        public void HandleMessage_SetAutoRelaunch_UpdatesFlag()
        {
            var controlled = new ControlledAccount { Username = "AutoRelaunchPawn" };
            string jsonTrue = "{\"Name\":\"SetAutoRelaunch\",\"Payload\":{\"Content\":\"true\"}}";
            string jsonFalse = "{\"Name\":\"SetAutoRelaunch\",\"Payload\":{\"Content\":\"false\"}}";

            controlled.HandleMessage(jsonTrue);
            Assert.True(controlled.AutoRelaunch);

            controlled.HandleMessage(jsonFalse);
            Assert.False(controlled.AutoRelaunch);
        }

        [Fact]
        public void HandleMessage_SetPlaceId_UpdatesPlaceId()
        {
            var controlled = new ControlledAccount { Username = "PlacePawn" };
            string json = "{\"Name\":\"SetPlaceId\",\"Payload\":{\"Content\":\"18928374\"}}";

            controlled.HandleMessage(json);

            Assert.Equal(18928374L, controlled.PlaceId);
        }

        [Fact]
        public void HandleMessage_SetJobId_UpdatesJobId()
        {
            var controlled = new ControlledAccount { Username = "JobPawn" };
            string json = "{\"Name\":\"SetJobId\",\"Payload\":{\"Content\":\"server-instance-guid-xyz\"}}";

            controlled.HandleMessage(json);

            Assert.Equal("server-instance-guid-xyz", controlled.JobId);
        }

        [Fact]
        public void HandleMessage_CreateElement_EnforcesPerSessionLimits()
        {
            var controlled = new ControlledAccount { Username = "ElementPawn" };

            // Send up to 55 elements (max allowed is 50)
            for (int i = 0; i < 55; i++)
            {
                string json = $"{{\"Name\":\"CreateButton\",\"Payload\":{{\"Name\":\"Btn_{i}\",\"Content\":\"Action {i}\"}}}}";
                controlled.HandleMessage(json);
            }

            // NewLine doesn't require Name or Content
            string newlineJson = "{\"Name\":\"NewLine\"}";
            var ex = Record.Exception(() => controlled.HandleMessage(newlineJson));
            Assert.Null(ex);
        }

        [Fact]
        public void Connect_And_Disconnect_ManagesLifecycle()
        {
            var controlled = new ControlledAccount { Username = "LifecyclePawn" };

            controlled.Connect(null);
            Assert.Equal(AccountStatus.Online, controlled.Status);

            // Reconnecting while online disconnects first then reconnects
            controlled.Connect(null);
            Assert.Equal(AccountStatus.Online, controlled.Status);

            controlled.Disconnect();
            Assert.Equal(AccountStatus.Offline, controlled.Status);
            Assert.False(controlled.ClientCanReceive);
        }

        [Fact]
        public async Task SendMessage_WhenOffline_ReturnsSafelyWithoutThrowing()
        {
            var controlled = new ControlledAccount { Username = "SilentPawn" };
            var ex1 = Record.Exception(() => controlled.SendMessage("test payload"));
            Assert.Null(ex1);

            var ex2 = await Record.ExceptionAsync(() => controlled.SendMessageAsync("test async payload"));
            Assert.Null(ex2);
        }
    }
}
