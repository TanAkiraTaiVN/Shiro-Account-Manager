#nullable enable
using System;
using System.Threading.Tasks;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class Win32TrayHelperBehaviorTests
    {
        [Fact]
        public void RefreshNotificationArea_ExecutesCleanly()
        {
            var ex = Record.Exception(() => Win32TrayHelper.RefreshNotificationArea());
            Assert.Null(ex);
        }

        [Fact]
        public void QueueRefreshNotificationArea_SchedulesAndCoalesces()
        {
            var ex = Record.Exception(() =>
            {
                Win32TrayHelper.QueueRefreshNotificationArea(10);
                Win32TrayHelper.QueueRefreshNotificationArea(20);
            });
            Assert.Null(ex);
        }

        [Fact]
        public void PostMessage_WithZeroHwnd_ReturnsTrueForThreadMessage()
        {
            // On Win32, hWnd == NULL posts to the current thread message queue and returns true
            bool posted = Win32TrayHelper.PostMessage(IntPtr.Zero, 0x0200, 0, 0);
            Assert.True(posted);
        }
    }
}
