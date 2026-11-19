#nullable enable
using System;
using System.Net;
using System.Net.Http;

namespace Shiro.Services
{
    /// <summary>
    /// Shared <see cref="SocketsHttpHandler"/> and default HTTP header configuration factory
    /// used by both <see cref="SharedHttpClient"/> and <see cref="ProxyBindingService"/>.
    /// </summary>
    internal static class RobloxHttpHandlerHelper
    {
        internal const string ChromeUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

        internal static SocketsHttpHandler CreateDefaultHandler(
            bool allowAutoRedirect = true,
            IWebProxy? proxy = null,
            bool useProxy = true,
            TimeSpan? connectTimeout = null,
            bool useConnectionPoolSettings = true)
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                AllowAutoRedirect = allowAutoRedirect,
                UseCookies = false,
                Proxy = proxy,
                UseProxy = proxy != null || useProxy,
                ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(10)
            };

            if (useConnectionPoolSettings)
            {
                handler.PooledConnectionLifetime = TimeSpan.FromMinutes(10);
                handler.PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2);
                handler.KeepAlivePingDelay = TimeSpan.FromSeconds(30);
                handler.KeepAlivePingTimeout = TimeSpan.FromSeconds(10);
                handler.EnableMultipleHttp2Connections = true;
            }

            return handler;
        }
    }
}
