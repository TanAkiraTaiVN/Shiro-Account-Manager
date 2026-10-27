using System;
using RBX_Alt_Manager.Services;
using Xunit;

namespace RBX_Alt_Manager.Tests
{
    public class RobloxJobObjectServiceTests
    {
        [Fact]
        public void EnsureJobHandle_ReturnsValidHandle()
        {
            IntPtr hJob = RobloxJobObjectService.EnsureJobHandle();
            Assert.NotEqual(IntPtr.Zero, hJob);
            Assert.NotEqual((IntPtr)(-1), hJob);
        }

        [Fact]
        public void SetCpuRateLimit_AcceptsValidRange()
        {
            bool ok50 = RobloxJobObjectService.SetCpuRateLimit(50);
            Assert.True(ok50);

            bool ok100 = RobloxJobObjectService.SetCpuRateLimit(100);
            Assert.True(ok100);

            bool ok0 = RobloxJobObjectService.SetCpuRateLimit(0);
            Assert.True(ok0);
        }

        [Fact]
        public void AssignProcess_HandlesInvalidHandleSafely()
        {
            bool result = RobloxJobObjectService.AssignProcess(IntPtr.Zero);
            Assert.False(result);
        }
    }
}
