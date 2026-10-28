#nullable enable
using System;
using Avalonia.Media;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Services;

namespace RBX_Alt_Manager.ViewModels
{
    public partial class AccountViewModel
    {
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
    }
}
