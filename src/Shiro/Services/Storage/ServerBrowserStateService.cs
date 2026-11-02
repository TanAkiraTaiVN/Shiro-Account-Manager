#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public sealed class ServerBrowserStateService : IServerBrowserStateService
    {
        public static readonly ServerBrowserStateService Default = new();

        private string _currentPlaceId = string.Empty;
        private string _currentJobId = string.Empty;

        public Func<CancellationToken, Task>? LoadClientVersionDelegate { get; set; }

        public string CurrentPlaceId
        {
            get
            {
                if (!string.IsNullOrEmpty(_currentPlaceId))
                    return _currentPlaceId;

                var general = AppSettings.General;
                if (general != null && general.Exists("SavedPlaceId"))
                    return general.Get("SavedPlaceId") ?? "5315046213";

                return "5315046213";
            }
            set
            {
                _currentPlaceId = value ?? string.Empty;
                var general = AppSettings.General;
                if (general != null && !string.IsNullOrEmpty(_currentPlaceId))
                {
                    general.Set("SavedPlaceId", _currentPlaceId);
                    AppSettings.QueueDeferredSave();
                }
            }
        }

        public string CurrentJobId
        {
            get
            {
                if (!string.IsNullOrEmpty(_currentJobId))
                    return _currentJobId;

                var general = AppSettings.General;
                if (general != null && general.Exists("SavedJobId"))
                    return general.Get("SavedJobId") ?? string.Empty;

                return string.Empty;
            }
            set
            {
                _currentJobId = value ?? string.Empty;
                var general = AppSettings.General;
                if (general != null)
                {
                    general.Set("SavedJobId", _currentJobId);
                    AppSettings.QueueDeferredSave();
                }
            }
        }

        public bool ShuffleJobId { get; set; }
        public string? CurrentClientVersion { get; set; }
        public string? WebSocketPassword { get; set; }
        public List<ServerData> CachedServers { get; set; } = new();
        public List<Shiro.Game> RecentGames { get; set; } = new();

        public Task LoadClientVersionAsync(CancellationToken cancellationToken = default)
        {
            return LoadClientVersionDelegate != null
                ? LoadClientVersionDelegate(cancellationToken)
                : Task.CompletedTask;
        }
    }
}
