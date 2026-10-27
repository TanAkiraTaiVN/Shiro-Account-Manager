using RBX_Alt_Manager.Http;
using System;
using System.Net;

namespace RBX_Alt_Manager.Services
{
    /// <summary>
    /// Owns the named Roblox HTTP clients. Keeping construction here
    /// prevents forms and domain objects from creating independent clients.
    /// </summary>
    internal sealed class RobloxApiClient : IDisposable
    {
        public RestClient Main { get; }
        public RestClient Avatar { get; }
        public RestClient Friends { get; }
        public RestClient Users { get; }
        public RestClient Auth { get; }
        public RestClient Economy { get; }
        public RestClient Account { get; }
        public RestClient GameJoin { get; }
        public RestClient Thumbnails { get; }
        public RestClient Games { get; }
        public RestClient Develop { get; }
        public RestClient Presence { get; }
        public RestClient Api { get; }

        public RobloxApiClient()
        {
            Main = Create("https://www.roblox.com/");
            Avatar = Create("https://avatar.roblox.com/");
            Auth = Create("https://auth.roblox.com/");
            Economy = Create("https://economy.roblox.com/");
            Account = Create("https://accountsettings.roblox.com/");
            GameJoin = Create("https://gamejoin.roblox.com/", "Roblox/WinInet");
            Users = Create("https://users.roblox.com");
            Friends = Create("https://friends.roblox.com");
            Thumbnails = Create("https://thumbnails.roblox.com/");
            Games = Create("https://games.roblox.com/");
            Develop = Create("https://develop.roblox.com/");
            Presence = Create("https://presence.roblox.com/");
            Api = Create("https://apis.roblox.com/");
        }

        private static RestClient Create(string baseUrl, string userAgent = null)
        {
            return new RestClient(baseUrl, userAgent);
        }

        public void Dispose()
        {
            Main?.Dispose();
            Avatar?.Dispose();
            Friends?.Dispose();
            Users?.Dispose();
            Auth?.Dispose();
            Economy?.Dispose();
            Account?.Dispose();
            GameJoin?.Dispose();
            Thumbnails?.Dispose();
            Games?.Dispose();
            Develop?.Dispose();
            Presence?.Dispose();
            Api?.Dispose();
        }
    }
}
