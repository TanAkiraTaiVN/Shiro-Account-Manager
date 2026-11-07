#nullable enable
using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Shiro.ViewModels.Dialogs;
using Xunit;

namespace Shiro.Tests.Dialogs;

public sealed class UpdaterViewModelTests
{
    [Fact]
    public async Task Start_NetworkFailure_ShowsErrorPanel_DoesNotClose()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"updater-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var handler = new FakeHttpHandler(_ => throw new HttpRequestException("DNS failure"));
            using var http = new HttpClient(handler);
            var vm = new UpdaterViewModel(http, () => new Version(4, 2, 0), tempDir, _ => { });

            bool closed = false;
            vm.CloseRequested += (_, _) => closed = true;

            await vm.StartCommand.ExecuteAsync(null);

            Assert.True(vm.HasError);
            Assert.False(closed);
            Assert.Contains("DNS failure", vm.SubStatusText, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Start_NoNewer_Closes()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"updater-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var handler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.OK, """{"tag_name":"v4.2.0","assets":[]}""");
            using var http = new HttpClient(handler);
            var vm = new UpdaterViewModel(http, () => new Version(4, 2, 0), tempDir, _ => { });

            bool closed = false;
            vm.CloseRequested += (_, _) => closed = true;

            await vm.StartCommand.ExecuteAsync(null);

            Assert.False(vm.HasError);
            Assert.True(closed);
            Assert.Equal("100%", vm.PercentText);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Retry_AfterError_ClearsError()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"updater-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            int attempt = 0;
            var handler = new FakeHttpHandler(_ =>
            {
                attempt++;
                if (attempt == 1)
                    throw new HttpRequestException("first fail");
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });
            using var http = new HttpClient(handler);
            var vm = new UpdaterViewModel(http, () => new Version(4, 2, 0), tempDir, _ => { });

            await vm.StartCommand.ExecuteAsync(null);
            Assert.True(vm.HasError);

            await vm.RetryCommand.ExecuteAsync(null);
            Assert.False(vm.HasError);
            Assert.Equal(2, attempt);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Start_DeletesPreExistingArchive_AndAbortsOnMissingOrMismatchedSha256()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"updater-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string staleZip = Path.Combine(tempDir, "Update.zip");
            File.WriteAllBytes(staleZip, new byte[] { 1, 2, 3 });
            bool restarted = false;

            var notFoundHandler = new FakeHttpHandler().Enqueue(HttpStatusCode.NotFound, "");
            using (var http1 = new HttpClient(notFoundHandler))
            {
                var vm1 = new UpdaterViewModel(http1, () => new Version(4, 2, 0), tempDir, _ => restarted = true);
                await vm1.StartCommand.ExecuteAsync(null);
                Assert.False(File.Exists(staleZip));
                Assert.False(restarted);
            }

            string releaseJson = """{"tag_name":"v9.0.0","assets":[{"name":"Shiro-win-x64.zip","browser_download_url":"https://ex.com/s.zip","size":4,"digest":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]}""";
            int step = 0;
            var mismatchHandler = new FakeHttpHandler(_ =>
                ++step == 1
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releaseJson) }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 0x50, 0x4B, 0x05, 0x06 }) });
            using var http2 = new HttpClient(mismatchHandler);
            var vm2 = new UpdaterViewModel(http2, () => new Version(4, 2, 0), tempDir, _ => restarted = true);
            await vm2.StartCommand.ExecuteAsync(null);

            Assert.True(vm2.HasError);
            Assert.False(File.Exists(staleZip));
            Assert.False(restarted);

            string missingDigestJson = """{"tag_name":"v9.0.0","assets":[{"name":"Shiro-win-x64.zip","browser_download_url":"https://ex.com/s.zip","size":4}]}""";
            int stepMissing = 0;
            var missingDigestHandler = new FakeHttpHandler(_ =>
                ++stepMissing == 1
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(missingDigestJson) }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 0x50, 0x4B, 0x05, 0x06 }) });
            using var http3 = new HttpClient(missingDigestHandler);
            var vm3 = new UpdaterViewModel(http3, () => new Version(4, 2, 0), tempDir, _ => restarted = true);
            await vm3.StartCommand.ExecuteAsync(null);

            Assert.True(vm3.HasError);
            Assert.False(File.Exists(staleZip));
            Assert.False(restarted);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Start_ValidSha256Digest_AppliesArchiveAndRestarts()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"updater-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Shiro.exe"), "stub");
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = zip.CreateEntry("payload.txt");
                await using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync("verified");
            }
            byte[] zipBytes = ms.ToArray();
            string hex = Convert.ToHexStringLower(SHA256.HashData(zipBytes));
            string releaseJson = $$"""{"tag_name":"v9.0.0","assets":[{"name":"Shiro-win-x64.zip","browser_download_url":"https://ex.com/s.zip","size":{{zipBytes.Length}},"digest":"sha256:{{hex}}"}]}""";

            int step = 0;
            var handler = new FakeHttpHandler(_ =>
                ++step == 1
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releaseJson) }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes) });

            string? restartedExe = null;
            using var http = new HttpClient(handler);
            var vm = new UpdaterViewModel(http, () => new Version(4, 2, 0), tempDir, exe => restartedExe = exe);
            await vm.StartCommand.ExecuteAsync(null);

            Assert.False(vm.HasError);
            Assert.Equal("verified", File.ReadAllText(Path.Combine(tempDir, "payload.txt")));
            Assert.NotNull(restartedExe);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Start_Sha256SumsUrlFallback_VerifiesAndAppliesArchive()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"updater-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Shiro.exe"), "stub");
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = zip.CreateEntry("fallback.txt");
                await using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync("verified-via-sums");
            }
            byte[] zipBytes = ms.ToArray();
            string hex = Convert.ToHexStringLower(SHA256.HashData(zipBytes));
            string sumsContent = $"\uFEFF{new string('a', 64)} *Shiro.exe\r\n{hex.ToUpperInvariant()} *Shiro-win-x64.zip\r\n";
            string releaseJson = $$"""{"tag_name":"v9.1.0","assets":[{"name":"Shiro-win-x64.zip","browser_download_url":"https://ex.com/s.zip","size":{{zipBytes.Length}}},{"name":"SHA256SUMS.txt","browser_download_url":"https://ex.com/SHA256SUMS.txt","size":160}]}""";

            var handler = new FakeHttpHandler(req =>
            {
                string url = req.RequestUri?.ToString() ?? string.Empty;
                if (url.EndsWith("/s.zip", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes) };
                if (url.EndsWith("/SHA256SUMS.txt", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sumsContent) };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releaseJson) };
            });

            string? restartedExe = null;
            using var http = new HttpClient(handler);
            var vm = new UpdaterViewModel(http, () => new Version(4, 2, 0), tempDir, exe => restartedExe = exe);
            await vm.StartCommand.ExecuteAsync(null);

            Assert.False(vm.HasError);
            Assert.Equal("verified-via-sums", File.ReadAllText(Path.Combine(tempDir, "fallback.txt")));
            Assert.NotNull(restartedExe);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }
}
