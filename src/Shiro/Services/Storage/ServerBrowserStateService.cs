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

        private readonly ISettingsService _settings;
        private string _currentPlaceId = string.Empty;
        private bool _placeIdExplicitlyCleared;
        private string _currentJobId = string.Empty;

        public ServerBrowserStateService() : this(AppServices.GeneralSettings) { }

        public ServerBrowserStateService(ISettingsService settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public Func<CancellationToken, Task>? LoadClientVersionDelegate { get; set; }
        public bool IsPlaceIdExplicitlyCleared => _placeIdExplicitlyCleared;

        public string CurrentPlaceId
        {
            get
            {
                if (!string.IsNullOrEmpty(_currentPlaceId))
                    return _currentPlaceId;

                var general = _settings;
                if (general != null && general.Exists("SavedPlaceId"))
                    return general.Get("SavedPlaceId") ?? "5315046213";

                return "5315046213";
            }
            set
            {
                _currentPlaceId = value ?? string.Empty;
                _placeIdExplicitlyCleared = string.IsNullOrEmpty(_currentPlaceId);
                var general = _settings;
                if (general != null && !string.IsNullOrEmpty(_currentPlaceId))
                {
                    general.Set("SavedPlaceId", _currentPlaceId);
                    AppServices.HostUiBridge.QueueDeferredSaveSettings();
                }
            }
        }

        public string CurrentJobId
        {
            get => _currentJobId;
            set
            {
                _currentJobId = value ?? string.Empty;
                var general = _settings;
                if (general != null && general.Exists("SavedJobId"))
                {
                    // Clear legacy persisted server GUIDs so expired jobs cannot hijack future launches.
                    general.RemoveProperty("SavedJobId");
                    AppServices.HostUiBridge.QueueDeferredSaveSettings();
                }
            }
        }

        public bool ShuffleJobId { get; set; }
        public string? CurrentClientVersion { get; set; }
        public string? WebSocketPassword { get; set; }
        public long CachedServersPlaceId { get; set; }
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
