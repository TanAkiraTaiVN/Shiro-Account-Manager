#nullable enable
using Shiro.Http;
using System;
using System.Net;

namespace Shiro.Services
{
    public interface IRobloxApiClient : IDisposable
    {
        RestClient Main { get; }
        RestClient Avatar { get; }
        RestClient Friends { get; }
        RestClient Users { get; }
        RestClient Auth { get; }
        RestClient Economy { get; }
        RestClient Account { get; }
        RestClient GameJoin { get; }
        RestClient Thumbnails { get; }
        RestClient Games { get; }
        RestClient Develop { get; }
        RestClient Presence { get; }
        RestClient Api { get; }
    }

    /// <summary>
    /// Owns the named Roblox HTTP clients. Keeping construction here
    /// prevents forms and domain objects from creating independent clients.
    /// </summary>
    public sealed class RobloxApiClient : IRobloxApiClient
    {
        private static readonly Lazy<RobloxApiClient> _default = new(() => new RobloxApiClient());
        public static RobloxApiClient Default => _default.Value;

        public RestClient Main => AccountManager.MainClient ?? _main;
        public RestClient Avatar => AccountManager.AvatarClient ?? _avatar;
        public RestClient Friends => AccountManager.FriendsClient ?? _friends;
        public RestClient Users => AccountManager.UsersClient ?? _users;
        public RestClient Auth => AccountManager.AuthClient ?? _auth;
        public RestClient Economy => AccountManager.EconClient ?? _economy;
        public RestClient Account => AccountManager.AccountClient ?? _account;
        public RestClient GameJoin => AccountManager.GameJoinClient ?? _gameJoin;
        public RestClient Thumbnails => AccountManager.ThumbnailsClient ?? _thumbnails;
        public RestClient Games => AccountManager.GamesClient ?? _games;
        public RestClient Develop => AccountManager.DevelopClient ?? _develop;
        public RestClient Presence => AccountManager.PresenceClient ?? _presence;
        public RestClient Api => AccountManager.ApiClient ?? _api;

        private readonly RestClient _main;
        private readonly RestClient _avatar;
        private readonly RestClient _friends;
        private readonly RestClient _users;
        private readonly RestClient _auth;
        private readonly RestClient _economy;
        private readonly RestClient _account;
        private readonly RestClient _gameJoin;
        private readonly RestClient _thumbnails;
        private readonly RestClient _games;
        private readonly RestClient _develop;
        private readonly RestClient _presence;
        private readonly RestClient _api;

        public RobloxApiClient()
        {
            _main = Create("https://www.roblox.com/");
            _avatar = Create("https://avatar.roblox.com/");
            _auth = Create("https://auth.roblox.com/");
            _economy = Create("https://economy.roblox.com/");
            _account = Create("https://accountsettings.roblox.com/");
            _gameJoin = Create("https://gamejoin.roblox.com/", "Roblox/WinInet");
            _users = Create("https://users.roblox.com");
            _friends = Create("https://friends.roblox.com");
            _thumbnails = Create("https://thumbnails.roblox.com/");
            _games = Create("https://games.roblox.com/");
            _develop = Create("https://develop.roblox.com/");
            _presence = Create("https://presence.roblox.com/");
            _api = Create("https://apis.roblox.com/");
        }

        private static RestClient Create(string baseUrl, string? userAgent = null)
        {
            return new RestClient(baseUrl, userAgent);
        }

        public void Dispose()
        {
            _main?.Dispose();
            _avatar?.Dispose();
            _friends?.Dispose();
            _users?.Dispose();
            _auth?.Dispose();
            _economy?.Dispose();
            _account?.Dispose();
            _gameJoin?.Dispose();
            _thumbnails?.Dispose();
            _games?.Dispose();
            _develop?.Dispose();
            _presence?.Dispose();
            _api?.Dispose();
        }
    }
}
