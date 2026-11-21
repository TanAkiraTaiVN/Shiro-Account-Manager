#nullable enable
using System;
using System.IO;
using System.Linq;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class ProxyManagerServiceBehaviorTests
    {
        [Fact]
        public void ProxyItem_PropertiesAndDisplays_ReflectValues()
        {
            var item = new ProxyItem
            {
                Host = "192.168.1.10",
                Port = 8080,
                Protocol = "http",
                Username = "alice",
                Password = "pwd"
            };

            Assert.Equal("192.168.1.10:8080", item.AddressDisplay);
            Assert.Equal("alice:***", item.AuthDisplay);
            Assert.Equal("HTTP", item.ProtocolUpper);

            item.Username = "";
            Assert.Contains(item.AuthDisplay, new[] { "Không có (No Auth)", "No Authentication" });
        }

        [Fact]
        public void ProxyItem_LatencyStateTransitions_UpdateStatus()
        {
            var item = new ProxyItem { Host = "127.0.0.1", Port = 8080 };

            // Default untested
            Assert.Equal(-1, item.LatencyMs);
            Assert.False(item.IsTested);
            Assert.False(item.IsOnline);

            // Fast online
            item.LatencyMs = 45;
            Assert.True(item.IsTested);
            Assert.True(item.IsOnline);
            Assert.Contains("45 ms", item.StatusText);
            Assert.NotNull(item.StatusBrush);

            // Medium online
            item.LatencyMs = 250;
            Assert.True(item.IsTested);
            Assert.True(item.IsOnline);

            // Slow online
            item.LatencyMs = 800;
            Assert.True(item.IsTested);
            Assert.True(item.IsOnline);

            // Failed / Offline (LatencyMs = -2)
            item.LatencyMs = -2;
            Assert.True(item.IsTested);
            Assert.False(item.IsOnline);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("# This is a comment")]
        [InlineData("#127.0.0.1:8080")]
        public void ParseProxy_NullEmptyOrComment_ReturnsNull(string? raw)
        {
            Assert.Null(ProxyManagerService.ParseProxy(raw));
        }

        [Fact]
        public void ParseProxy_HostPortOnly_ReturnsHttpProxy()
        {
            var p = ProxyManagerService.ParseProxy("1.2.3.4:8080");
            Assert.NotNull(p);
            Assert.Equal("1.2.3.4", p!.Host);
            Assert.Equal(8080, p.Port);
            Assert.Equal("http", p.Protocol);
            Assert.Empty(p.Username);
            Assert.Empty(p.Password);
        }

        [Fact]
        public void ParseProxy_Socks5Scheme_ParsesProtocol()
        {
            var p = ProxyManagerService.ParseProxy("socks5://10.0.0.1:1080");
            Assert.NotNull(p);
            Assert.Equal("10.0.0.1", p!.Host);
            Assert.Equal(1080, p.Port);
            Assert.Equal("socks5", p.Protocol);
        }

        [Fact]
        public void ParseProxy_ColonDelimitedWithCredentials_ParsesCorrectly()
        {
            var p = ProxyManagerService.ParseProxy("10.0.0.5:3128:user1:pass1");
            Assert.NotNull(p);
            Assert.Equal("10.0.0.5", p!.Host);
            Assert.Equal(3128, p.Port);
            Assert.Equal("user1", p.Username);
            Assert.Equal("pass1", p.Password);
        }

        [Fact]
        public void ParseProxy_ColonDelimitedPasswordWithColons_PreservesAllColons()
        {
            var p = ProxyManagerService.ParseProxy("10.0.0.6:3128:myUser:pass:with:many:colons");
            Assert.NotNull(p);
            Assert.Equal("10.0.0.6", p!.Host);
            Assert.Equal(3128, p.Port);
            Assert.Equal("myUser", p.Username);
            Assert.Equal("pass:with:many:colons", p.Password);
        }

        [Fact]
        public void ParseProxy_UserPassAtHostPort_ParsesCorrectly()
        {
            var p = ProxyManagerService.ParseProxy("admin:secret@10.0.0.7:8080");
            Assert.NotNull(p);
            Assert.Equal("10.0.0.7", p!.Host);
            Assert.Equal(8080, p.Port);
            Assert.Equal("admin", p.Username);
            Assert.Equal("secret", p.Password);
        }

        [Theory]
        [InlineData("10.0.0.1:0")]
        [InlineData("10.0.0.1:99999")]
        [InlineData("10.0.0.1:not_a_number")]
        [InlineData("just_a_string")]
        public void ParseProxy_InvalidPortOrFormat_ReturnsNull(string invalid)
        {
            Assert.Null(ProxyManagerService.ParseProxy(invalid));
        }

        [Fact]
        public void LoadAndSaveProxies_RoundTripInIsolatedDirectory()
        {
            string isolated = Path.Combine(Path.GetTempPath(), "Shiro-ProxyTest-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (StoragePaths.OverrideDataDirectoryForTesting(isolated))
                {
                    ProxyManagerService.InvalidateCache();

                    var proxies = new[]
                    {
                        new ProxyItem { RawEntry = "10.0.0.1:8080", Host = "10.0.0.1", Port = 8080 },
                        new ProxyItem { RawEntry = "10.0.0.2:9090:u:p", Host = "10.0.0.2", Port = 9090, Username = "u", Password = "p" }
                    };

                    ProxyManagerService.SaveProxies(proxies);
                    Assert.True(File.Exists(ProxyManagerService.ProxiesFilePath));

                    ProxyManagerService.InvalidateCache();
                    var loaded = ProxyManagerService.LoadProxies();

                    Assert.Equal(2, loaded.Count);
                    Assert.Equal("10.0.0.1", loaded[0].Host);
                    Assert.Equal(8080, loaded[0].Port);
                    Assert.Equal("10.0.0.2", loaded[1].Host);
                    Assert.Equal("u", loaded[1].Username);

                    var cachedStrings = ProxyManagerService.GetCachedProxyStrings();
                    Assert.Equal(2, cachedStrings.Count);
                }
            }
            finally
            {
                if (Directory.Exists(isolated))
                {
                    try { Directory.Delete(isolated, true); }
                    catch (IOException) { }
                }
            }
        }
    }
}
