#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Shiro.Classes;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public partial class AccountViewModel : ObservableObject
    {
        private Account _model;
        private bool _isSelected;
        private bool _isLaunching;
        private bool _isInGame;
        private bool _isLocalClientRunning;
        private bool _isCommandPaletteSelected;
        private string _liveMemoryText = string.Empty;

        internal string SortTitle = string.Empty;

        public Account Model => _model;

        public AccountViewModel(Account model)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
        }

        public void UpdateModel(Account model)
        {
            if (model == null) return;
            if (!ReferenceEquals(_model, model))
            {
                _model = model;
                NotifyAllPropertiesChanged();
            }
        }

        public void NotifyLanguageChanged()
        {
            OnPropertyChanged(nameof(IsBannedOrLocked));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(GroupDisplay));
            OnPropertyChanged(nameof(GroupTooltip));
            OnPropertyChanged(nameof(LaunchButtonText));
            OnPropertyChanged(nameof(FocusWindowButtonText));
            OnPropertyChanged(nameof(ViewBanButtonText));
        }

        public void NotifyStatusIssueChanged()
        {
            OnPropertyChanged(nameof(Valid));
            OnPropertyChanged(nameof(IsExpired));
            OnPropertyChanged(nameof(CanLaunch));
            OnPropertyChanged(nameof(ShowPlayButton));
            OnPropertyChanged(nameof(ShowRunningClientControls));
            OnPropertyChanged(nameof(IsExpiredAndNotBanned));
            OnPropertyChanged(nameof(IsBannedOrLocked));
            OnPropertyChanged(nameof(BanNoticeText));
            OnPropertyChanged(nameof(IsShortLivedSession));
            OnPropertyChanged(nameof(SessionHealth));
            OnPropertyChanged(nameof(StatusTooltip));
            OnPropertyChanged(nameof(StatusColorBrushName));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusBadgeBackground));
            OnPropertyChanged(nameof(StatusBadgeBorder));
            OnPropertyChanged(nameof(StatusBadgeForeground));
            OnPropertyChanged(nameof(CardBorderBrush));
        }

        public void NotifyDisplayNameChanged()
        {
            OnPropertyChanged(nameof(ShownUsername));
            OnPropertyChanged(nameof(PrimaryTitle));
            OnPropertyChanged(nameof(SecondaryTitle));
            OnPropertyChanged(nameof(DisplayTitle));
        }

        public string Username => _model.Username ?? "Unknown";

        public string ShownUsername
        {
            get
            {
                if (StreamerModeService.IsActive && (AppSettings.General?.Get<bool>(AppSettingsKeys.General.StreamerModeHideUsernames, true) ?? true))
                {
                    return StreamerModeService.MaskUsername(Username);
                }

                bool hideUsernames = AppSettings.General != null &&
                                     AppSettings.General.Exists("HideUsernames") &&
                                     AppSettings.General.Get<bool>("HideUsernames");
                return hideUsernames ? "Hidden Account" : Username;
            }
        }

        public long UserId => _model.UserID;

        public string Alias
        {
            get => _model.Alias;
            set
            {
                string val = value ?? string.Empty;
                if (_model.Alias != val)
                {
                    _model.Alias = val;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(PrimaryTitle));
                    OnPropertyChanged(nameof(SecondaryTitle));
                    OnPropertyChanged(nameof(DisplayTitle));
                }
            }
        }

        public string PrimaryTitle => !string.IsNullOrWhiteSpace(Alias) ? Alias : ShownUsername;

        public string SecondaryTitle
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Alias))
                    return UserId > 0 ? $"@{ShownUsername}  •  ID: {UserId}" : $"@{ShownUsername}";
                return UserId > 0 ? $"ID: {UserId}" : "Roblox Player";
            }
        }

        public string DisplayTitle => string.IsNullOrWhiteSpace(Alias) ? (UserId > 0 ? $"{ShownUsername} ({UserId})" : ShownUsername) : $"{Alias} ({ShownUsername})";

        public string Group
        {
            get => _model.Group ?? "Default";
            set
            {
                string val = value ?? "Default";
                if (_model.Group != val)
                {
                    _model.Group = val;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(GroupDisplay));
                    OnPropertyChanged(nameof(GroupTooltip));
                }
            }
        }

        public string GroupDisplay => string.IsNullOrEmpty(_model.Group) || string.Equals(_model.Group, "Default", StringComparison.OrdinalIgnoreCase)
            ? LocalizationService.Get("Auto_ValueConverters_033")
            : _model.Group;

        public string GroupTooltip => LocalizationService.Format("Auto_AccountViewModel_355", GroupDisplay);

        public string LaunchButtonText => LocalizationService.Get("Srv_Play");
        public string FocusWindowButtonText => LocalizationService.Get("Auto_AccountViewModel_356");
        public string ViewBanButtonText => LocalizationService.Get("Account.ViewBan");

        public string Description
        {
            get => _model.Description;
            set
            {
                string val = value ?? string.Empty;
                if (_model.Description != val)
                {
                    _model.Description = val;
                    OnPropertyChanged();
                }
            }
        }

        public bool Pinned
        {
            get => _model.Pinned;
            set
            {
                if (_model.Pinned != value)
                {
                    _model.Pinned = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(PinOpacity));
                    OnPropertyChanged(nameof(PinIconStroke));
                }
            }
        }

        public double PinOpacity => _model.Pinned ? 1.0 : 0.45;
        public IBrush PinIconStroke => _model.Pinned ? BrushLaunchingFg : BrushDefaultFg;

        public bool Valid
        {
            get => !string.IsNullOrWhiteSpace(_model.SecurityToken) && _model.Valid;
            set
            {
                if (_model.Valid != value)
                {
                    _model.Valid = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsExpired));
                    OnPropertyChanged(nameof(CanLaunch));
                    OnPropertyChanged(nameof(ShowPlayButton));
                    OnPropertyChanged(nameof(ShowRunningClientControls));
                    OnPropertyChanged(nameof(IsExpiredAndNotBanned));
                    OnPropertyChanged(nameof(IsBannedOrLocked));
                    OnPropertyChanged(nameof(BanNoticeText));
                    OnPropertyChanged(nameof(StatusColorBrushName));
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(StatusBadgeBackground));
                    OnPropertyChanged(nameof(StatusBadgeBorder));
                    OnPropertyChanged(nameof(StatusBadgeForeground));
                    OnPropertyChanged(nameof(CardBorderBrush));
                }
            }
        }

        public bool IsExpired => !Valid;
        public bool IsBannedOrLocked => _model.IsBannedOrLocked;
        public bool CanLaunch => Valid && !IsBannedOrLocked;
        public bool ShowPlayButton => CanLaunch && !IsLocalClientRunning;
        public bool ShowRunningClientControls => IsLocalClientRunning;
        public bool IsExpiredAndNotBanned => IsExpired && !IsBannedOrLocked;
        public string BanNoticeText => !string.IsNullOrEmpty(_model.AccountStatusIssue)
            ? _model.AccountStatusIssue
            : LocalizationService.Get("Auto_LaunchCoordinato_242");
        public bool IsShortLivedSession => _model.IsShortLivedSession;
        public SessionHealth SessionHealth => _model.SessionHealthStatus;

        public string Initials => string.IsNullOrWhiteSpace(Username) ? "?" : Username.Substring(0, 1).ToUpperInvariant();

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (SetProperty(ref _isSelected, value))
                {
                    OnPropertyChanged(nameof(CardBorderBrush));
                }
            }
        }

        public bool IsLaunching
        {
            get => _isLaunching;
            set
            {
                if (SetProperty(ref _isLaunching, value))
                {
                    OnPropertyChanged(nameof(StatusColorBrushName));
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(StatusBadgeBackground));
                    OnPropertyChanged(nameof(StatusBadgeBorder));
                    OnPropertyChanged(nameof(StatusBadgeForeground));
                }
            }
        }

        public bool IsInGame
        {
            get => _isInGame;
            set
            {
                if (SetProperty(ref _isInGame, value))
                {
                    OnPropertyChanged(nameof(StatusColorBrushName));
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(StatusBadgeBackground));
                    OnPropertyChanged(nameof(StatusBadgeBorder));
                    OnPropertyChanged(nameof(StatusBadgeForeground));
                    OnPropertyChanged(nameof(CardBorderBrush));
                }
            }
        }

        public bool IsLocalClientRunning
        {
            get => _isLocalClientRunning;
            set
            {
                if (SetProperty(ref _isLocalClientRunning, value))
                {
                    OnPropertyChanged(nameof(ShowPlayButton));
                    OnPropertyChanged(nameof(ShowRunningClientControls));
                    OnPropertyChanged(nameof(StatusColorBrushName));
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(StatusBadgeBackground));
                    OnPropertyChanged(nameof(StatusBadgeBorder));
                    OnPropertyChanged(nameof(StatusBadgeForeground));
                    OnPropertyChanged(nameof(CardBorderBrush));
                }
            }
        }

        public bool IsCommandPaletteSelected
        {
            get => _isCommandPaletteSelected;
            set
            {
                if (SetProperty(ref _isCommandPaletteSelected, value))
                {
                    OnPropertyChanged(nameof(CommandPaletteRowBackground));
                    OnPropertyChanged(nameof(CommandPaletteRowBorder));
                }
            }
        }

        public bool IsOnline => _model.Presence != null && _model.Presence.userPresenceType == UserPresenceType.Online;
        public bool IsInStudio => _model.Presence != null && _model.Presence.userPresenceType == UserPresenceType.InStudio;

        public string GameName => _model.Presence?.gameName ?? string.Empty;

        public string LiveMemoryText
        {
            get => _liveMemoryText;
            set
            {
                if (SetProperty(ref _liveMemoryText, value))
                {
                    OnPropertyChanged(nameof(StatusText));
                }
            }
        }

        public long RobuxBalance => _model?.RobuxBalance ?? -1;
        public bool HasRobux => RobuxBalance >= 0;
        public string RobuxText
        {
            get
            {
                if (StreamerModeService.IsActive && (AppSettings.General?.Get<bool>(AppSettingsKeys.General.StreamerModeHideRobux, true) ?? true))
                    return "***";
                return RobuxBalance >= 0 ? $"{RobuxBalance:N0}" : string.Empty;
            }
        }

        public string RobuxFormatted
        {
            get
            {
                if (StreamerModeService.IsActive && (AppSettings.General?.Get<bool>(AppSettingsKeys.General.StreamerModeHideRobux, true) ?? true))
                    return "R$ ***";
                return RobuxBalance >= 0 ? $"R$ {RobuxBalance:N0}" : string.Empty;
            }
        }
        public string RobuxTooltip => RobuxBalance >= 0 ? Shiro.Services.LocalizationService.Format("Auto_AccountViewModel_358", RobuxBalance) : Shiro.Services.LocalizationService.Get("Auto_AccountViewModel_357");

        public void NotifyThemeChanged()
        {
            OnPropertyChanged(nameof(CardBorderBrush));
        }

        public void NotifyValidityChanged()
        {
            OnPropertyChanged(nameof(Valid));
            OnPropertyChanged(nameof(IsExpired));
            OnPropertyChanged(nameof(CanLaunch));
            OnPropertyChanged(nameof(ShowPlayButton));
            OnPropertyChanged(nameof(ShowRunningClientControls));
            OnPropertyChanged(nameof(IsExpiredAndNotBanned));
            OnPropertyChanged(nameof(IsBannedOrLocked));
            OnPropertyChanged(nameof(BanNoticeText));
            OnPropertyChanged(nameof(IsShortLivedSession));
            OnPropertyChanged(nameof(SessionHealth));
            OnPropertyChanged(nameof(StatusTooltip));
            OnPropertyChanged(nameof(StatusColorBrushName));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusBadgeBackground));
            OnPropertyChanged(nameof(StatusBadgeBorder));
            OnPropertyChanged(nameof(StatusBadgeForeground));
            OnPropertyChanged(nameof(CardBorderBrush));
        }

        public void NotifyRobuxChanged()
        {
            OnPropertyChanged(nameof(RobuxBalance));
            OnPropertyChanged(nameof(HasRobux));
            OnPropertyChanged(nameof(RobuxText));
            OnPropertyChanged(nameof(RobuxFormatted));
            OnPropertyChanged(nameof(RobuxTooltip));
        }

        public void NotifyAllPropertiesChanged()
        {
            OnPropertyChanged(string.Empty);
        }
    }
}
