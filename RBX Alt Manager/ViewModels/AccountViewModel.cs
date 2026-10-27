using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Services;

namespace RBX_Alt_Manager.ViewModels
{
    public class AccountViewModel : ObservableObject
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
            OnPropertyChanged(nameof(IsBannedOrLocked));
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
                if (_model.Alias != value)
                {
                    _model.Alias = value;
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
                if (_model.Group != value)
                {
                    _model.Group = value;
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
                if (_model.Description != value)
                {
                    _model.Description = value;
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
        public bool IsShortLivedSession => _model.IsShortLivedSession;
        public SessionHealth SessionHealth => _model.SessionHealthStatus;

        public string StatusTooltip
        {
            get
            {
                if (IsBannedOrLocked)
                    return $"Tài khoản gặp sự cố: {_model.AccountStatusIssue}";
                if (IsExpired)
                    return "Phiên đăng nhập (.ROBLOSECURITY) đã hết hạn hoặc bị thu hồi. Hãy nhấn Đăng nhập lại (Re-login).";
                int payloadLen = Account.GetSessionTokenPayloadLength(_model.SecurityToken);
                string lastUseStr = _model.LastUse != DateTime.MinValue ? _model.LastUse.ToString("dd/MM/yyyy HH:mm") : "Chưa rõ";
                if (IsShortLivedSession)
                    return $"Phiên ngắn hạn (Payload: {payloadLen} ký tự, cấp lúc {lastUseStr}). Có thể hết hạn sau ~30 phút — khuyến nghị Re-login để nhận phiên dài hạn 939 ký tự.";
                return payloadLen > 0
                    ? $"Phiên dài hạn khỏe mạnh (Payload: {payloadLen} ký tự, cập nhật: {lastUseStr})"
                    : $"Phiên hợp lệ (Cập nhật: {lastUseStr})";
            }
        }

        public string Initials => string.IsNullOrWhiteSpace(Username) ? "?" : Username.Substring(0, 1).ToUpperInvariant();

        private const int MaxAvatarBitmapCacheEntries = 512;
        private static readonly ConcurrentDictionary<long, byte> _requestedAvatarDownloads = new ConcurrentDictionary<long, byte>();
        private static readonly Services.LruCache<long, Bitmap> _avatarBitmapCache = new Services.LruCache<long, Bitmap>(MaxAvatarBitmapCacheEntries);
        private string _avatarUrl;

        public static void ClearAvatarMemoryCache()
        {
            _avatarBitmapCache.Clear();
            _requestedAvatarDownloads.Clear();
        }

        public Bitmap AvatarImage
        {
            get
            {
                if (UserId <= 0) return null;
                if (_avatarBitmapCache.TryGetValue(UserId, out var cached))
                    return cached;

                string diskPath = AvatarUrl;
                if (!string.IsNullOrEmpty(diskPath) && System.IO.File.Exists(diskPath))
                {
                    try
                    {
                        using var stream = System.IO.File.OpenRead(diskPath);
                        var bmp = Bitmap.DecodeToWidth(stream, 72);
                        _avatarBitmapCache[UserId] = bmp;
                        return bmp;
                    }
                    catch (Exception ex)
                    {
                        Program.Logger?.Debug($"[AvatarImage] Error decoding avatar for {UserId}: {ex.Message}");
                    }
                }
                return null;
            }
        }

        public string AvatarUrl
        {
            get
            {
                if (_avatarUrl != null) return _avatarUrl;
                if (UserId <= 0) return string.Empty;
                try
                {
                    string diskPath = System.IO.Path.Combine(Services.StoragePaths.CacheDirectory, "Avatars", $"{UserId}.png");
                    if (System.IO.File.Exists(diskPath))
                    {
                        _avatarUrl = diskPath;
                        return _avatarUrl;
                    }
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"[AvatarUrl] Could not read cached avatar for {UserId}: {ex.Message}");
                }

                // Automatically trigger download in background if missing from disk
                if (_requestedAvatarDownloads.TryAdd(UserId, 0))
                {
                    _ = FetchAndCacheAvatarAsync();
                }

                return string.Empty;
            }
        }

        public async Task<string> FetchAndCacheAvatarAsync()
        {
            try
            {
                string path = await Services.AvatarService.EnsureAvatarAsync(UserId).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(path))
                {
                    _avatarUrl = path;
                    _avatarBitmapCache.TryRemove(UserId, out _);
                    AppDispatcher.Post(() =>
                    {
                        OnPropertyChanged(nameof(AvatarUrl));
                        OnPropertyChanged(nameof(AvatarImage));
                    });
                    return path;
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[FetchAndCacheAvatarAsync] Could not fetch avatar for {UserId}: {ex.Message}");
            }
            finally
            {
                // Remove from tracking if avatar was not retrieved so retry is possible
                if (string.IsNullOrEmpty(_avatarUrl))
                {
                    _requestedAvatarDownloads.TryRemove(UserId, out _);
                }
            }
            return string.Empty;
        }

        public void InvalidateAvatar()
        {
            _avatarUrl = null;
            _avatarBitmapCache.TryRemove(UserId, out _);
            _requestedAvatarDownloads.TryRemove(UserId, out _);
            OnPropertyChanged(nameof(AvatarUrl));
            OnPropertyChanged(nameof(AvatarImage));
        }

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

        public string StatusColorBrushName
        {
            get
            {
                if (IsExpired) return "RedBrush";
                if (IsLaunching) return "AmberBrush";
                if (IsInGame) return "GreenBrush";
                if (IsInStudio) return "AccentBrush";
                if (IsOnline) return "BlueBrush";
                return "MutedTextBrush";
            }
        }

        public string StatusText
        {
            get
            {
                if (IsBannedOrLocked) return _model.AccountStatusIssue;
                if (IsExpired) return Loc.Get("Status_Expired");
                if (IsLaunching) return Loc.Get("Status_Launching");
                if (IsInGame)
                {
                    if (!string.IsNullOrWhiteSpace(GameName))
                        return Loc.Get("Status_InGameNamed", GameName);
                    return string.IsNullOrEmpty(LiveMemoryText) ? Loc.Get("Status_InGame") : $"{Loc.Get("Status_InGame")} ({LiveMemoryText})";
                }
                if (IsInStudio) return "Roblox Studio";
                if (IsOnline) return Loc.Get("Status_Online");
                if (IsShortLivedSession) return LocalizationService.IsEnglish ? "⚠️ Short Session (~30m)" : "⚠️ Phiên ngắn (~30p)";
                return Loc.Get("Status_Ready");
            }
        }

        #region Pre-allocated Frozen Brushes (Zero Allocation & GPU Cached)
        private static readonly SolidColorBrush BrushBannedFg = CreateFrozenBrush("#FDA4AF");
        private static readonly SolidColorBrush BrushExpiredFg = CreateFrozenBrush("#F87171");
        private static readonly SolidColorBrush BrushLaunchingFg = CreateFrozenBrush("#FBBF24");
        private static readonly SolidColorBrush BrushInGameFg = CreateFrozenBrush("#4ADE80");
        private static readonly SolidColorBrush BrushInStudioFg = CreateFrozenBrush("#D8B4FE");
        private static readonly SolidColorBrush BrushOnlineFg = CreateFrozenBrush("#38BDF8");
        private static readonly SolidColorBrush BrushDefaultFg = CreateFrozenBrush("#A1A1AA");

        private static readonly SolidColorBrush BrushBannedBg = CreateFrozenBrush("#2D0F18");
        private static readonly SolidColorBrush BrushExpiredBg = CreateFrozenBrush("#221315");
        private static readonly SolidColorBrush BrushLaunchingBg = CreateFrozenBrush("#221C11");
        private static readonly SolidColorBrush BrushInGameBg = CreateFrozenBrush("#122018");
        private static readonly SolidColorBrush BrushInStudioBg = CreateFrozenBrush("#1A1725");
        private static readonly SolidColorBrush BrushOnlineBg = CreateFrozenBrush("#111E29");
        private static readonly SolidColorBrush BrushDefaultBg = CreateFrozenBrush("#16161C");

        private static readonly SolidColorBrush BrushBannedBorder = CreateFrozenBrush("#9F1239");
        private static readonly SolidColorBrush BrushExpiredBorder = CreateFrozenBrush("#4D2125");
        private static readonly SolidColorBrush BrushLaunchingBorder = CreateFrozenBrush("#4D381B");
        private static readonly SolidColorBrush BrushInGameBorder = CreateFrozenBrush("#1D3B29");
        private static readonly SolidColorBrush BrushInStudioBorder = CreateFrozenBrush("#332B4A");
        private static readonly SolidColorBrush BrushOnlineBorder = CreateFrozenBrush("#1A354A");
        private static readonly SolidColorBrush BrushDefaultBorder = CreateFrozenBrush("#272733");

        private static SolidColorBrush _brushCardSelected = CreateFrozenBrush("#8B5CF6");
        public static SolidColorBrush BrushCardSelected => _brushCardSelected;

        private static readonly SolidColorBrush BrushCardBanned = CreateFrozenBrush("#9F1239");
        private static readonly SolidColorBrush BrushCardExpired = CreateFrozenBrush("#4D2125");
        private static readonly SolidColorBrush BrushCardInGame = CreateFrozenBrush("#1E3B2A");
        private static readonly SolidColorBrush BrushCardDefault = CreateFrozenBrush("#24242C");

        static AccountViewModel()
        {
            try
            {
                UpdateThemeBrush(AccentThemeService.CurrentTheme);
                AccentThemeService.ThemeChanged += UpdateThemeBrush;
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"AccountViewModel static ctor theme brush init failed: {ex.Message}");
            }
        }

        private static void UpdateThemeBrush(AccentTheme theme)
        {
            try
            {
                var (primary, _, _, _, _, _) = AccentThemeService.GetThemeColors(theme);
                _brushCardSelected = new SolidColorBrush(primary);
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"UpdateThemeBrush failed: {ex.Message}");
            }
        }

        private static SolidColorBrush CreateFrozenBrush(string hex)
        {
            return new SolidColorBrush(Color.Parse(hex));
        }
        #endregion

        public IBrush StatusBadgeBackground
        {
            get
            {
                if (IsBannedOrLocked) return BrushBannedBg;
                if (IsExpired) return BrushExpiredBg;
                if (IsLaunching) return BrushLaunchingBg;
                if (IsInGame) return BrushInGameBg;
                if (IsInStudio) return BrushInStudioBg;
                if (IsOnline) return BrushOnlineBg;
                return BrushDefaultBg;
            }
        }

        public IBrush StatusBadgeBorder
        {
            get
            {
                if (IsBannedOrLocked) return BrushBannedBorder;
                if (IsExpired) return BrushExpiredBorder;
                if (IsLaunching) return BrushLaunchingBorder;
                if (IsInGame) return BrushInGameBorder;
                if (IsInStudio) return BrushInStudioBorder;
                if (IsOnline) return BrushOnlineBorder;
                return BrushDefaultBorder;
            }
        }

        public IBrush StatusBadgeForeground
        {
            get
            {
                if (IsBannedOrLocked) return BrushBannedFg;
                if (IsExpired) return BrushExpiredFg;
                if (IsLaunching) return BrushLaunchingFg;
                if (IsInGame) return BrushInGameFg;
                if (IsInStudio) return BrushInStudioFg;
                if (IsOnline) return BrushOnlineFg;
                return BrushDefaultFg;
            }
        }

        public IBrush CardBorderBrush
        {
            get
            {
                if (IsSelected) return BrushCardSelected;
                if (IsBannedOrLocked) return BrushCardBanned;
                if (IsExpired) return BrushCardExpired;
                if (IsInGame) return BrushCardInGame;
                return BrushCardDefault;
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
            OnPropertyChanged(nameof(IsBannedOrLocked));
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
