#nullable enable
using System;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests.Launch
{
    public sealed class PrerequisiteServiceTests
    {
        [Fact]
        public void VcRedistUrl_PointsToEvergreenVs18X64Channel()
        {
            Assert.Equal("https://aka.ms/vs/18/release/vc_redist.x64.exe", PrerequisiteService.VcRedistUrl);
        }

        [Theory]
        [InlineData(1, 14, true)]
        [InlineData(1, 15, true)]
        [InlineData(1, 50, true)]
        [InlineData(1, null, true)]
        [InlineData(1, 13, false)]
        [InlineData(0, 14, false)]
        [InlineData(null, 14, false)]
        [InlineData(null, null, false)]
        [InlineData(-1, 14, false)]
        public void IsVcRuntimeSatisfied_EvaluatesInstalledAndMajorValues(object? installed, object? major, bool expected)
        {
            bool actual = PrerequisiteService.IsVcRuntimeSatisfied(installed, major);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void IsVcRuntimeSatisfied_SupportsAlternativeNumericAndStringTypes()
        {
            Assert.True(PrerequisiteService.IsVcRuntimeSatisfied("1", "14"));
            Assert.True(PrerequisiteService.IsVcRuntimeSatisfied(1L, 14L));
            Assert.False(PrerequisiteService.IsVcRuntimeSatisfied("invalid", "invalid"));
            Assert.False(PrerequisiteService.IsVcRuntimeSatisfied(1, "not-a-number"));
        }

        [Fact]
        public void NeedsVCRedist_ExecutesSafelyWithoutThrowing()
        {
            bool first = PrerequisiteService.NeedsVCRedist();
            bool second = PrerequisiteService.NeedsVCRedist();
            Assert.Equal(first, second);
        }
    }
}
