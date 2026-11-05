using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("StaticSeams")]
    public class JobObjectCompletionPortTests
    {
        [Fact]
        public void DispatchCompletionMessage_RaisesProcessExitedFromJob_OnNormalAndAbnormalExit()
        {
            int observedPid = 0;
            bool observedAbnormal = false;
            int callCount = 0;

            void Handler(int pid, bool abnormal)
            {
                observedPid = pid;
                observedAbnormal = abnormal;
                callCount++;
            }

            RobloxJobObjectService.ProcessExitedFromJob += Handler;
            try
            {
                // JOB_OBJECT_MSG_EXIT_PROCESS = 4
                bool handledExit = RobloxJobObjectService.DispatchCompletionMessage(4, 12345);
                Assert.True(handledExit);
                Assert.Equal(1, callCount);
                Assert.Equal(12345, observedPid);
                Assert.False(observedAbnormal);

                // JOB_OBJECT_MSG_ABNORMAL_EXIT_PROCESS = 8
                bool handledAbnormal = RobloxJobObjectService.DispatchCompletionMessage(8, 67890);
                Assert.True(handledAbnormal);
                Assert.Equal(2, callCount);
                Assert.Equal(67890, observedPid);
                Assert.True(observedAbnormal);

                // Unrelated message = 99
                Assert.False(RobloxJobObjectService.DispatchCompletionMessage(99, 111));
                Assert.Equal(2, callCount);
            }
            finally
            {
                RobloxJobObjectService.ProcessExitedFromJob -= Handler;
            }
        }
    }
}
