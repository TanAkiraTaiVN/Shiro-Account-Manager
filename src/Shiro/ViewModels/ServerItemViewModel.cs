#nullable enable
using System;
using Shiro.Classes;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public class ServerItemViewModel : ObservableObject
    {
        private string _jobId = string.Empty;
        private string _type = "Public";
        private int _maxPlayers = 20;
        private int _playing;
        private int _ping = -1;
        private string _region = string.Empty;
        private string _countryCode = string.Empty;
        private string _flagEmoji = string.Empty;
        private string _ip = string.Empty;
        private bool _isBusyPinging;

        public string JobId
        {
            get => _jobId;
            set => SetProperty(ref _jobId, value ?? string.Empty);
        }

        public string Type
        {
            get => _type;
            set => SetProperty(ref _type, value ?? string.Empty);
        }

        public int MaxPlayers
        {
            get => _maxPlayers;
            set
            {
                if (SetProperty(ref _maxPlayers, value))
                {
                    OnPropertyChanged(nameof(PlayerDisplay));
                    OnPropertyChanged(nameof(PlayerPercentage));
                }
            }
        }

        public int Playing
        {
            get => _playing;
            set
            {
                if (SetProperty(ref _playing, value))
                {
                    OnPropertyChanged(nameof(PlayerDisplay));
                    OnPropertyChanged(nameof(PlayerPercentage));
                }
            }
        }

        public string PlayerDisplay
        {
            get => $"{Playing} / {MaxPlayers}";
            set { }
        }

        public double PlayerPercentage
        {
            get => MaxPlayers > 0 ? Math.Min(100.0, ((double)Playing / MaxPlayers) * 100.0) : 0;
            set { }
        }

        public int Ping
        {
            get => _ping;
            set
            {
                if (SetProperty(ref _ping, value))
                {
                    OnPropertyChanged(nameof(PingDisplay));
                    OnPropertyChanged(nameof(PingColor));
                }
            }
        }

        public string PingDisplay
        {
            get => Ping >= 0 ? $"{Ping} ms" : "-- ms";
            set { }
        }

        public string PingColor
        {
            get
            {
                if (Ping < 0) return "#71717A"; // Muted
                if (Ping < 90) return "#22C55E"; // Green
                if (Ping < 160) return "#F59E0B"; // Amber
                return "#EF4444"; // Red
            }
            set { }
        }

        public string CountryCode
        {
            get => _countryCode;
            set => SetProperty(ref _countryCode, value ?? string.Empty);
        }

        public string FlagEmoji
        {
            get => _flagEmoji;
            set
            {
                if (SetProperty(ref _flagEmoji, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(RegionDisplay));
                }
            }
        }

        public string Region
        {
            get => _region;
            set
            {
                if (SetProperty(ref _region, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(RegionDisplay));
                }
            }
        }

        public string RegionDisplay
        {
            get
            {
                if (string.IsNullOrEmpty(_region) || _region == "Chưa nạp" || _region == "Not loaded")
                {
                    return LocalizationService.IsEnglish ? "Not loaded" : "Chưa nạp";
                }
                if (!string.IsNullOrEmpty(_flagEmoji))
                {
                    return $"{_flagEmoji} {_region}";
                }
                return _region;
            }
        }

        public void NotifyLanguageChanged()
        {
            OnPropertyChanged(nameof(RegionDisplay));
        }

        public string Ip
        {
            get => _ip;
            set => SetProperty(ref _ip, value ?? string.Empty);
        }

        public bool IsBusyPinging
        {
            get => _isBusyPinging;
            set => SetProperty(ref _isBusyPinging, value);
        }

        public ServerData? RawData { get; set; }
    }
}
