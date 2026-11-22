#nullable enable
using System;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class StreamerModeServiceBehaviorTests : IDisposable
    {
        private readonly bool _origEnabled;
        private readonly bool _origAutoDetect;

        public StreamerModeServiceBehaviorTests()
        {
            _origEnabled = StreamerModeService.IsEnabled;
            _origAutoDetect = StreamerModeService.AutoDetectEnabled;
        }

        public void Dispose()
        {
            StreamerModeService.IsEnabled = _origEnabled;
            StreamerModeService.AutoDetectEnabled = _origAutoDetect;
        }

        [Fact]
        public void MaskUsername_MasksExpectedSubstrings()
        {
            Assert.Equal(string.Empty, StreamerModeService.MaskUsername(null));
            Assert.Equal(string.Empty, StreamerModeService.MaskUsername(""));
            Assert.Equal("**", StreamerModeService.MaskUsername("a"));
            Assert.Equal("**", StreamerModeService.MaskUsername("ab"));
            Assert.Equal("a*c", StreamerModeService.MaskUsername("abc"));
            Assert.Equal("R**********r", StreamerModeService.MaskUsername("RobloxMaster"));
        }

        [Fact]
        public void MaskRobux_And_MaskJobId_ReturnMaskedStrings()
        {
            string robux = StreamerModeService.MaskRobux(5000);
            Assert.Equal("*** R$", robux);

            string jobId = StreamerModeService.MaskJobId("job-guid-12345");
            Assert.Equal("***-***", jobId);
        }

        [Fact]
        public void StreamerMode_TogglesAndStateChanges()
        {
            bool? eventResult = null;
            Action<bool> handler = state => eventResult = state;
            StreamerModeService.StateChanged += handler;

            try
            {
                StreamerModeService.IsEnabled = true;
                Assert.True(StreamerModeService.IsEnabled);
                Assert.True(StreamerModeService.IsActive);

                StreamerModeService.IsEnabled = false;
                Assert.False(StreamerModeService.IsEnabled);

                StreamerModeService.AutoDetectEnabled = true;
                Assert.True(StreamerModeService.AutoDetectEnabled);

                StreamerModeService.AutoDetectEnabled = false;
                Assert.False(StreamerModeService.AutoDetectEnabled);

                // Initialize does not throw
                var ex = Record.Exception(() => StreamerModeService.Initialize());
                Assert.Null(ex);
            }
            finally
            {
                StreamerModeService.StateChanged -= handler;
            }
        }
    }
}
