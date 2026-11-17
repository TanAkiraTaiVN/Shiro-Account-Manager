using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
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
                // JOB_OBJECT_MSG_EXIT_PROCESS = 7
                bool handledExit = RobloxJobObjectService.DispatchCompletionMessage(7, 12345);
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

                // JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO = 4 must not raise ProcessExitedFromJob
                Assert.False(RobloxJobObjectService.DispatchCompletionMessage(4, 54321));
                Assert.Equal(2, callCount);

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
