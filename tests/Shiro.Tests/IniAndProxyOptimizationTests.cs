#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class IniAndProxyOptimizationTests
    {
        [Fact]
        public void IniFile_ToUtf8JsonBytesAndSave_WritesDirectUtf8AndRoundTripsAllTypes()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroIniTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string filePath = Path.Combine(tempDir, "RAMSettings.json");
                var ini = new IniFile();
                ini.Section("General").Set("UnlockFPS", "true");
                ini.Section("General").Set("MaxFPSValue", "240");
                ini.Section("General").Set("WindowScale", "1.25");
                ini.Section("General").Set("AccentTheme", "Monochrome");
                ini.Section("WebServer").Set("WebServerPort", "7963");

                byte[] utf8Bytes = ini.ToUtf8JsonBytes();
                Assert.True(utf8Bytes.Length > 20);
                string json = Encoding.UTF8.GetString(utf8Bytes);
                Assert.Contains("\"General\"", json, StringComparison.Ordinal);
                Assert.Contains("\"MaxFPSValue\": \"240\"", json, StringComparison.Ordinal);

                ini.Save(filePath);
                Assert.True(File.Exists(filePath));

                var reloaded = new IniFile(filePath);
                Assert.True(reloaded.Section("General").GetBool("UnlockFPS"));
                Assert.Equal(240, reloaded.Section("General").GetInt("MaxFPSValue"));
                Assert.Equal(1.25m, reloaded.Section("General").GetDecimal("WindowScale"));
                Assert.Equal("Monochrome", reloaded.Section("General").Get("AccentTheme"));
                Assert.Equal(7963, reloaded.Section("WebServer").GetInt("WebServerPort"));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [Theory]
        [InlineData("127.0.0.1:8080", "http", "127.0.0.1", 8080, "", "")]
        [InlineData("  socks5://10.0.0.5:1080  ", "socks5", "10.0.0.5", 1080, "", "")]
        [InlineData("192.168.1.10:3128:myuser:myp@ss:with:colons", "http", "192.168.1.10", 3128, "myuser", "myp@ss:with:colons")]
        [InlineData("http://alice:secret123@proxy.example.com:8888", "http", "proxy.example.com", 8888, "alice", "secret123")]
        [InlineData("socks5://proxy.example.com:1080@bob:pass456", "socks5", "proxy.example.com", 1080, "bob", "pass456")]
        public void ProxyManagerService_ParseProxy_SpanParserExtractsAllFormatsAccurately(
            string raw,
            string expectedProtocol,
            string expectedHost,
            int expectedPort,
            string expectedUser,
            string expectedPass)
        {
            ProxyItem? parsed = ProxyManagerService.ParseProxy(raw);
            Assert.NotNull(parsed);
            Assert.Equal(expectedProtocol, parsed!.Protocol);
            Assert.Equal(expectedHost, parsed.Host);
            Assert.Equal(expectedPort, parsed.Port);
            Assert.Equal(expectedUser, parsed.Username);
            Assert.Equal(expectedPass, parsed.Password);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("# comment line")]
        [InlineData("invalid_no_port")]
        [InlineData("127.0.0.1:99999")]
        [InlineData(":8080")]
        public void ProxyManagerService_ParseProxy_RejectsInvalidAndCommentLines(string? raw)
        {
            ProxyItem? parsed = ProxyManagerService.ParseProxy(raw);
            Assert.Null(parsed);
        }

        [Fact]
        public async Task GameThumbnailService_GetThumbnailBitmapAsync_HandlesInvalidPlaceIdCleanly()
        {
            GameThumbnailService.ClearCache();
            var bmp = await GameThumbnailService.GetThumbnailBitmapAsync(0);
            Assert.Null(bmp);
            Assert.False(GameThumbnailService.HasDiskCache(-1));
        }

        [Fact]
        public async Task Presence_GetPresence_ReturnsEmptyDictionaryForEmptyInput()
        {
            var dict = await Presence.GetPresence(Array.Empty<long>());
            Assert.NotNull(dict);
            Assert.Empty(dict!);
        }

        [Fact]
        public async Task ProxyManagerService_TestAllProxiesAsync_ParallelForEachCompletesCleanlyOnEmptyInput()
        {
            var reported = new List<ProxyItem>();
            var progress = new Progress<ProxyItem>(p => reported.Add(p));
            await ProxyManagerService.TestAllProxiesAsync(Array.Empty<ProxyItem>(), maxConcurrency: 4, timeoutMs: 100, progress: progress);
            Assert.Empty(reported);
        }
    }
}

