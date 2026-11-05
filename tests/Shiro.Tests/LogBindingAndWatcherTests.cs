using Shiro.Classes;
using Xunit;

namespace Shiro.Tests
{
    public class LogBindingAndWatcherTests
    {
        [Theory]
        [InlineData("2026-11-04T10:00:00.000Z,0.1,1234,6 [FLog::Output] Command line: RobloxPlayerBeta.exe --app -t abc -b 112233445566", "112233445566", LogHeaderMatchResult.Matched)]
        [InlineData("2026-11-04T10:00:00.000Z,0.1,1234,6 [FLog::Output] browserTrackerId=112233445566", "112233445566", LogHeaderMatchResult.Matched)]
        [InlineData("2026-11-04T10:00:00.000Z,0.1,1234,6 [FLog::Output] Command line: RobloxPlayerBeta.exe --app -t abc -b 998877665544", "112233445566", LogHeaderMatchResult.Mismatched)]
        [InlineData("2026-11-04T10:00:00.000Z,0.1,1234,6 [FLog::Output] Starting Roblox client...", "112233445566", LogHeaderMatchResult.Inconclusive)]
        [InlineData("", "112233445566", LogHeaderMatchResult.Inconclusive)]
        [InlineData("any header", "", LogHeaderMatchResult.Inconclusive)]
        public void EvaluateLogHeaderForTracker_ClassifiesTrackerOwnershipDeterministically(
            string headerSample,
            string expectedTrackerId,
            LogHeaderMatchResult expected)
        {
            var actual = RobloxProcess.EvaluateLogHeaderForTracker(headerSample, expectedTrackerId);
            Assert.Equal(expected, actual);
        }
    }
}
