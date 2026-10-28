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
        private string _liveMemoryText = string.Empty;

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
            OnPropertyChanged(nameof(LaunchButtonText));
        }

        public void NotifyStatusIssueChanged()
        {
            OnPropertyChanged(nameof(Valid));
            OnPropertyChanged(nameof(IsExpired));
            OnPropertyChanged(nameof(CanLaunch));
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
                bool hideUsernames = AccountManager.General != null &&
                                     AccountManager.General.Exists("HideUsernames") &&
                                     AccountManager.General.Get<bool>("HideUsernames");
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
                }
            }
        }

        public string GroupDisplay => LocalizationService.IsEnglish
            ? $"Group: {(string.IsNullOrEmpty(_model.Group) ? "Default" : _model.Group)}"
            : $"Nhóm: {(string.IsNullOrEmpty(_model.Group) || _model.Group == "Default" ? "Mặc định" : _model.Group)}";

        public string LaunchButtonText => LocalizationService.IsEnglish ? "Launch" : "Chơi";

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
                }
            }
        }

        public double PinOpacity => _model.Pinned ? 1.0 : 0.3;

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
        public bool IsExpiredAndNotBanned => IsExpired && !IsBannedOrLocked;
        public string BanNoticeText => !string.IsNullOrEmpty(_model.AccountStatusIssue)
            ? _model.AccountStatusIssue
            : (LocalizationService.IsEnglish ? "⛔ Account Moderated / Locked" : "⛔ Tài khoản bị khóa / Ban");
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
        public string RobuxText => RobuxBalance >= 0 ? $"{RobuxBalance:N0}" : string.Empty;
        public string RobuxFormatted => RobuxBalance >= 0 ? $"R$ {RobuxBalance:N0}" : string.Empty;
        public string RobuxTooltip => RobuxBalance >= 0 ? $"Số dư: {RobuxBalance:N0} Robux" : "Chưa cập nhật số dư Robux";

        public void NotifyThemeChanged()
        {
            OnPropertyChanged(nameof(CardBorderBrush));
        }

        public void NotifyValidityChanged()
        {
            OnPropertyChanged(nameof(Valid));
            OnPropertyChanged(nameof(IsExpired));
            OnPropertyChanged(nameof(CanLaunch));
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
