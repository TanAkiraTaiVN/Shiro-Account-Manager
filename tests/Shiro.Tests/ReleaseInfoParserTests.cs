#nullable enable
using System;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests;

public sealed class ReleaseInfoParserTests
{
    [Theory]
    [InlineData("""{"tag_name":"v4.3.0","assets":[{"name":"Shiro.zip","browser_download_url":"https://ex.com/s.zip","size":1024}]}""", true)]
    [InlineData("""{"tag_name":"4.2.0","assets":[{"name":"Shiro.zip","browser_download_url":"https://ex.com/s.zip","size":1024}]}""", false)]
    [InlineData("garbage", false)]
    [InlineData("""{"tag_name":"v4.3.0","assets":[{"name":"notes.txt","browser_download_url":"https://ex.com/n.txt","size":10}]}""", false)]
    public void TryGetNewer_EvaluatesReleaseJson(string json, bool expectedFound)
    {
        ReleaseInfo? info = ReleaseInfoParser.TryGetNewer(json, new Version(4, 2, 0));
        if (expectedFound)
        {
            Assert.NotNull(info);
            Assert.Equal("4.3.0", info.Tag);
            Assert.Equal(new Version(4, 3, 0), info.Version);
            Assert.Equal("https://ex.com/s.zip", info.ZipUrl);
            Assert.Equal(1024, info.Size);
        }
        else
        {
            Assert.Null(info);
        }
    }

    [Theory]
    [InlineData(0, 100, 30)]
    [InlineData(50, 100, 50)]
    [InlineData(100, 100, 70)]
    [InlineData(5, 0, 30)]
    public void ComputeDownloadPercent_ClampsBetween30And70(long read, long size, int expected)
    {
        Assert.Equal(expected, UpdateArchiveInstaller.ComputeDownloadPercent(read, size));
    }

    [Theory]
    [InlineData("../evil.exe", false)]
    [InlineData("sub/ok.dll", true)]
    [InlineData(@"C:\abs.dll", false)]
    public void IsSafeArchiveEntryPath_RejectsTraversalAndRootedPaths(string entry, bool expectedSafe)
    {
        bool ok = UpdateArchiveInstaller.IsSafeArchiveEntryPath(@"C:\Staging\Update", entry, out string resolved);
        Assert.Equal(expectedSafe, ok);
        if (expectedSafe)
        {
            Assert.StartsWith(@"C:\Staging\Update\", resolved, StringComparison.OrdinalIgnoreCase);
        }
    }
}
