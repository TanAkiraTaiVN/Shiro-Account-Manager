using RBX_Alt_Manager.Services;
using Xunit;

namespace RBX_Alt_Manager.Tests
{
    public class ProxyManagerServiceTests
    {
        [Fact]
        public void ParseProxy_ReturnsNullForEmptyOrComments()
        {
            Assert.Null(ProxyManagerService.ParseProxy(null));
            Assert.Null(ProxyManagerService.ParseProxy("   "));
            Assert.Null(ProxyManagerService.ParseProxy("# This is a comment"));
        }

        [Fact]
        public void ParseProxy_ParsesHostAndPort()
        {
            var item = ProxyManagerService.ParseProxy("127.0.0.1:8080");
            Assert.NotNull(item);
            Assert.Equal("127.0.0.1", item.Host);
            Assert.Equal(8080, item.Port);
            Assert.Equal("http", item.Protocol);
            Assert.Empty(item.Username);
            Assert.Empty(item.Password);
            Assert.Equal("127.0.0.1:8080", item.AddressDisplay);
            Assert.Equal("Không có (No Auth)", item.AuthDisplay);
        }

        [Fact]
        public void ParseProxy_ParsesSchemeAndHostPort()
        {
            var item = ProxyManagerService.ParseProxy("socks5://proxy.example.com:1080");
            Assert.NotNull(item);
            Assert.Equal("proxy.example.com", item.Host);
            Assert.Equal(1080, item.Port);
            Assert.Equal("socks5", item.Protocol);
            Assert.Equal("SOCKS5", item.ProtocolUpper);
        }

        [Fact]
        public void ParseProxy_ParsesFourPartColonFormat()
        {
            var item = ProxyManagerService.ParseProxy("1.2.3.4:9000:myuser:mypass");
            Assert.NotNull(item);
            Assert.Equal("1.2.3.4", item.Host);
            Assert.Equal(9000, item.Port);
            Assert.Equal("myuser", item.Username);
            Assert.Equal("mypass", item.Password);
            Assert.Equal("myuser:***", item.AuthDisplay);
        }

        [Fact]
        public void ParseProxy_ParsesUserPassAtHostPort()
        {
            var item = ProxyManagerService.ParseProxy("admin:secret@192.168.1.100:3128");
            Assert.NotNull(item);
            Assert.Equal("192.168.1.100", item.Host);
            Assert.Equal(3128, item.Port);
            Assert.Equal("admin", item.Username);
            Assert.Equal("secret", item.Password);
        }

        [Fact]
        public void ParseProxy_StatusColorAndTextReflectLatency()
        {
            var item = new ProxyItem();

            item.LatencyMs = -1;
            Assert.Equal("Chưa test", item.StatusText);
            Assert.False(item.IsTested);
            Assert.False(item.IsOnline);

            item.LatencyMs = 85;
            Assert.Equal("⚡ 85 ms", item.StatusText);
            Assert.True(item.IsOnline);
            Assert.Equal("#22C55E", item.StatusColor);

            item.LatencyMs = -2;
            Assert.Equal("❌ Timeout / Lỗi", item.StatusText);
            Assert.False(item.IsOnline);
            Assert.Equal("#EF4444", item.StatusColor);
        }
    }
}
