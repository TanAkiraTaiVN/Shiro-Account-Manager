using System;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Services;

namespace RBX_Alt_Manager.ViewModels
{
    public class ServerItemViewModel : ObservableObject
    {
        private string _jobId;
        private string _type = "Public";
        private int _maxPlayers = 20;
        private int _playing;
        private int _ping = -1;
        private string _region = string.Empty;
        private string _ip;
        private bool _isBusyPinging;

        public string JobId
        {
            get => _jobId;
            set => SetProperty(ref _jobId, value);
        }

        public string Type
        {
            get => _type;
            set => SetProperty(ref _type, value);
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

        public string Region
        {
            get => _region;
            set
            {
                if (SetProperty(ref _region, value))
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
            set => SetProperty(ref _ip, value);
        }

        public bool IsBusyPinging
        {
            get => _isBusyPinging;
            set => SetProperty(ref _isBusyPinging, value);
        }

        public ServerData RawData { get; set; }
    }
}
