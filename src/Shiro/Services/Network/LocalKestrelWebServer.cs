#nullable enable
using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Shiro.Services
{
    /// <summary>
    /// Shared factory for Shiro's embedded Kestrel local web API host.
    /// Configures loopback binding, optional external listener settings,
    /// and graceful shutdown so existing API clients work cleanly.
    /// </summary>
    /// <remarks>
    /// <see cref="WebApplication.CreateEmptyBuilder"/> is used on purpose: it
    /// registers no logging, configuration providers or default middleware,
    /// which keeps startup cost low and stays Native AOT compatible.
    /// </remarks>
    internal static class LocalKestrelWebServer
    {
        public static readonly TimeSpan WebSocketKeepAlive = TimeSpan.FromSeconds(30);

        public static WebApplication Build(string applicationName, int port, bool allowExternal)
        {
            if (port < 1 || port > 65535)
                throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535.");

            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
            {
                ApplicationName = applicationName,
                EnvironmentName = "Production"
            });

            builder.WebHost.UseKestrel(options =>
            {
                if (allowExternal)
                    options.ListenAnyIP(port);
                else
                    options.ListenLocalhost(port);

                options.AddServerHeader = false;
            });

            builder.Services.AddRouting();
            return builder.Build();
        }

        public static void UseShiroWebSockets(this WebApplication app)
        {
            app.UseWebSockets(new WebSocketOptions
            {
                KeepAliveInterval = WebSocketKeepAlive
            });
        }
    }
}
