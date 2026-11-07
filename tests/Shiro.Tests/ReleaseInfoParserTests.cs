#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
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

    [Fact]
    public void TryGetNewer_ExtractsGitHubDigestAndSha256SumsUrl()
    {
        const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        string json = $$"""
        {
          "tag_name": "v5.1.0",
          "assets": [
            { "name": "SHA256SUMS.txt", "browser_download_url": "https://ex.com/SHA256SUMS.txt", "size": 96 },
            { "name": "Shiro-win-x64.zip", "browser_download_url": "https://ex.com/Shiro-win-x64.zip", "size": 2048, "digest": "sha256:{{hash}}" }
          ]
        }
        """;
        ReleaseInfo? info = ReleaseInfoParser.TryGetNewer(json, new Version(5, 0, 0));
        Assert.NotNull(info);
        Assert.Equal(hash, info!.ExpectedSha256);
        Assert.Equal("https://ex.com/SHA256SUMS.txt", info.ChecksumsUrl);
        Assert.Equal("Shiro-win-x64.zip", info.AssetName);
    }

    [Fact]
    public void TryGetNewer_HandlesTrailingSha256SumsAndRejectsMalformedDigest()
    {
        string json = """
        {
          "tag_name": "v5.2.0",
          "assets": [
            { "name": "Shiro-win-x64.zip", "browser_download_url": "https://ex.com/Shiro-win-x64.zip", "size": 4096, "digest": "sha256:not-a-valid-64-hex" },
            { "name": "SHA256SUMS.txt", "browser_download_url": "https://ex.com/sums.txt", "size": 128 }
          ]
        }
        """;
        ReleaseInfo? info = ReleaseInfoParser.TryGetNewer(json, new Version(5, 0, 0));
        Assert.NotNull(info);
        Assert.Null(info!.ExpectedSha256);
        Assert.Equal("https://ex.com/sums.txt", info.ChecksumsUrl);
        Assert.Equal("Shiro-win-x64.zip", info.AssetName);

        var legacyRecord = new ReleaseInfo("5.2.0", new Version(5, 2, 0), "https://ex.com/s.zip", 10);
        Assert.Null(legacyRecord.ExpectedSha256);
        Assert.Null(legacyRecord.ChecksumsUrl);
        Assert.Equal("Shiro-win-x64.zip", legacyRecord.AssetName);
    }

    [Fact]
    public void VerifyFileSha256_And_TryParseSha256Sums_ValidateCorrectly()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"shiro-sha-{Guid.NewGuid():N}.bin");
        try
        {
            byte[] data = "shiro-release-payload"u8.ToArray();
            File.WriteAllBytes(tmp, data);
            string hex = Convert.ToHexStringLower(SHA256.HashData(data));

            Assert.True(UpdateArchiveInstaller.VerifyFileSha256(tmp, hex));
            Assert.True(UpdateArchiveInstaller.VerifyFileSha256(tmp, hex.ToUpperInvariant()));
            Assert.False(UpdateArchiveInstaller.VerifyFileSha256(tmp, new string('0', 64)));
            Assert.False(UpdateArchiveInstaller.VerifyFileSha256(tmp, "not-hex"));
            Assert.False(UpdateArchiveInstaller.VerifyFileSha256(tmp, null));
            Assert.False(UpdateArchiveInstaller.VerifyFileSha256(tmp + ".missing", hex));

            string sums = $"{hex} *Shiro-win-x64.zip\n{new string('f', 64)} *Shiro.exe\n";
            Assert.Equal(hex, UpdateArchiveInstaller.TryParseSha256Sums(sums, "Shiro-win-x64.zip"));

            string bomSums = $"\uFEFF{hex.ToUpperInvariant()}  ./build/Release/Shiro-win-x64.zip\r\n";
            Assert.Equal(hex, UpdateArchiveInstaller.TryParseSha256Sums(bomSums, "Shiro-win-x64.zip"));
            Assert.Null(UpdateArchiveInstaller.TryParseSha256Sums(sums, "NonExistent.zip"));
            Assert.Null(UpdateArchiveInstaller.TryParseSha256Sums(null, "Shiro-win-x64.zip"));
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public void VerifyMicrosoftAuthenticode_ValidatesSignatureAndMicrosoftSubject()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"shiro-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            Assert.False(PrerequisiteService.VerifyMicrosoftAuthenticode(string.Empty));
            Assert.False(PrerequisiteService.VerifyMicrosoftAuthenticode(Path.Combine(tempDir, "missing.exe")));

            string garbagePath = Path.Combine(tempDir, "garbage.exe");
            File.WriteAllBytes(garbagePath, "not-a-valid-pe-binary"u8.ToArray());
            Assert.False(PrerequisiteService.VerifyMicrosoftAuthenticode(garbagePath));

            string unsignedPePath = typeof(PrerequisiteService).Assembly.Location;
            Assert.False(PrerequisiteService.VerifyMicrosoftAuthenticode(unsignedPePath));

            string nonMicrosoftSignedPePath = typeof(Assert).Assembly.Location;
            Assert.False(PrerequisiteService.VerifyMicrosoftAuthenticode(nonMicrosoftSignedPePath));

            string microsoftSignedPePath = typeof(Microsoft.Web.WebView2.Core.CoreWebView2).Assembly.Location;
            Assert.True(PrerequisiteService.VerifyMicrosoftAuthenticode(microsoftSignedPePath));

            string tamperedPath = Path.Combine(tempDir, "tampered.dll");
            byte[] signedBytes = File.ReadAllBytes(microsoftSignedPePath);
            signedBytes[signedBytes.Length / 2] ^= 0xFF;
            File.WriteAllBytes(tamperedPath, signedBytes);
            Assert.False(PrerequisiteService.VerifyMicrosoftAuthenticode(tamperedPath));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task InstallVCRedistCoreAsync_EnforcesAuthenticodeBeforeProcessExecution()
    {
        bool executedUnsigned = false;
        bool unsignedResult = await PrerequisiteService.InstallVCRedistCoreAsync(
            _ => Task.FromResult("unsigned-payload"u8.ToArray()),
            (_, _) =>
            {
                executedUnsigned = true;
                return Task.FromResult(0);
            });
        Assert.False(unsignedResult);
        Assert.False(executedUnsigned);

        string microsoftSignedPePath = typeof(Microsoft.Web.WebView2.Core.CoreWebView2).Assembly.Location;
        byte[] validSignedBytes = await File.ReadAllBytesAsync(microsoftSignedPePath);
        string? executedPath = null;
        bool signedResult = await PrerequisiteService.InstallVCRedistCoreAsync(
            _ => Task.FromResult(validSignedBytes),
            (path, _) =>
            {
                executedPath = path;
                Assert.True(File.Exists(path));
                return Task.FromResult(0);
            });
        Assert.True(signedResult);
        Assert.NotNull(executedPath);
        Assert.False(File.Exists(executedPath!));
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
