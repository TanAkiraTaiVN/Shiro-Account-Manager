#nullable enable
using System;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public partial class AccountViewModel
    {
        public string StatusTooltip
        {
            get
            {
                if (IsBannedOrLocked)
                    return Shiro.Services.LocalizationService.Format("Account.StatusIssue", _model.AccountStatusIssue);
                if (IsExpired)
                    return Shiro.Services.LocalizationService.Get("Account.SessionExpiredTip");
                int payloadLen = _model.SecurityTokenPayloadLength;
                string lastUseStr = _model.LastUse != DateTime.MinValue ? _model.LastUse.ToString("dd/MM/yyyy HH:mm") : Shiro.Services.LocalizationService.Get("Account.SessionUnknown");
                if (IsShortLivedSession)
                    return Shiro.Services.LocalizationService.Format("Account.ShortSessionTip", payloadLen, lastUseStr);
                return payloadLen > 0
                    ? Shiro.Services.LocalizationService.Format("Account.HealthySessionTip", payloadLen, lastUseStr)
                    : Shiro.Services.LocalizationService.Format("Account.ValidSessionTip", lastUseStr);
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
                        return Loc.Format("Status_InGameNamed", GameName);
                    return Loc.Get("Status_InGame");
                }
                if (IsLocalClientRunning)
                    return LocalizationService.Get("Account.OpeningRoblox");
                if (IsInStudio) return "Roblox Studio";
                if (IsOnline) return Loc.Get("Status_Online");
                if (IsShortLivedSession) return LocalizationService.Get("Account.ShortSessionBadge");
                return Loc.Get("Status_Ready");
            }
        }

        #region Pre-allocated Frozen Brushes (Zero Allocation & GPU Cached)
        private static readonly ImmutableSolidColorBrush BrushBannedFg = CreateFrozenBrush("#FDA4AF");
        private static readonly ImmutableSolidColorBrush BrushExpiredFg = CreateFrozenBrush("#F87171");
        private static readonly ImmutableSolidColorBrush BrushLaunchingFg = CreateFrozenBrush("#FBBF24");
        private static readonly ImmutableSolidColorBrush BrushInGameFg = CreateFrozenBrush("#4ADE80");
        private static readonly ImmutableSolidColorBrush BrushInStudioFg = CreateFrozenBrush("#D8B4FE");
        private static readonly ImmutableSolidColorBrush BrushOnlineFg = CreateFrozenBrush("#38BDF8");
        private static readonly ImmutableSolidColorBrush BrushDefaultFg = CreateFrozenBrush("#A1A1B2");

        private static readonly ImmutableSolidColorBrush BrushBannedBg = CreateFrozenBrush("#2A1119");
        private static readonly ImmutableSolidColorBrush BrushExpiredBg = CreateFrozenBrush("#271217");
        private static readonly ImmutableSolidColorBrush BrushLaunchingBg = CreateFrozenBrush("#261D0E");
        private static readonly ImmutableSolidColorBrush BrushInGameBg = CreateFrozenBrush("#11261B");
        private static readonly ImmutableSolidColorBrush BrushInStudioBg = CreateFrozenBrush("#1B1629");
        private static readonly ImmutableSolidColorBrush BrushOnlineBg = CreateFrozenBrush("#102131");
        private static readonly ImmutableSolidColorBrush BrushDefaultBg = CreateFrozenBrush("#1A1A26");

        private static readonly ImmutableSolidColorBrush BrushBannedBorder = CreateFrozenBrush("#7A1D35");
        private static readonly ImmutableSolidColorBrush BrushExpiredBorder = CreateFrozenBrush("#541D28");
        private static readonly ImmutableSolidColorBrush BrushLaunchingBorder = CreateFrozenBrush("#593C14");
        private static readonly ImmutableSolidColorBrush BrushInGameBorder = CreateFrozenBrush("#1E4931");
        private static readonly ImmutableSolidColorBrush BrushInStudioBorder = CreateFrozenBrush("#3A2C56");
        private static readonly ImmutableSolidColorBrush BrushOnlineBorder = CreateFrozenBrush("#1E3E5C");
        private static readonly ImmutableSolidColorBrush BrushDefaultBorder = CreateFrozenBrush("#282838");

        private static volatile ImmutableSolidColorBrush _brushCardSelected = CreateFrozenBrush("#8B5CF6");
        public static ISolidColorBrush BrushCardSelected => _brushCardSelected;

        private static readonly ImmutableSolidColorBrush BrushCardBanned = CreateFrozenBrush("#6E1B30");
        private static readonly ImmutableSolidColorBrush BrushCardExpired = CreateFrozenBrush("#541D28");
        private static readonly ImmutableSolidColorBrush BrushCardInGame = CreateFrozenBrush("#22593B");
        private static readonly ImmutableSolidColorBrush BrushCardDefault = CreateFrozenBrush("#222230");

        private static readonly ImmutableSolidColorBrush BrushPaletteSelectedBg = CreateFrozenBrush("#1F1F30");
        private static readonly ImmutableSolidColorBrush BrushPaletteDefaultBg = CreateFrozenBrush("#14141D");
        private static readonly ImmutableSolidColorBrush BrushPaletteSelectedBorder = CreateFrozenBrush("#6366F1");
        private static readonly ImmutableSolidColorBrush BrushPaletteDefaultBorder = CreateFrozenBrush("#222230");

        static AccountViewModel()
        {
            try
            {
                UpdateThemeBrush(AccentThemeService.CurrentTheme);
                AccentThemeService.ThemeChanged += UpdateThemeBrush;
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug($"AccountViewModel static ctor theme brush init failed: {ex.Message}");
            }
        }

        private static void UpdateThemeBrush(AccentTheme theme)
        {
            try
            {
                var (primary, _, _, _, _, _) = AccentThemeService.GetThemeColors(theme);
                _brushCardSelected = new ImmutableSolidColorBrush(primary);
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug($"UpdateThemeBrush failed: {ex.Message}");
            }
        }

        private static ImmutableSolidColorBrush CreateFrozenBrush(string hex)
        {
            return new ImmutableSolidColorBrush(Color.Parse(hex));
        }
        #endregion

        public IBrush StatusBadgeBackground
        {
            get
            {
                if (IsBannedOrLocked) return BrushBannedBg;
                if (IsExpired) return BrushExpiredBg;
                if (IsLaunching) return BrushLaunchingBg;
                if (IsInGame || IsLocalClientRunning) return BrushInGameBg;
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
                if (IsInGame || IsLocalClientRunning) return BrushInGameBorder;
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
                if (IsInGame || IsLocalClientRunning) return BrushInGameFg;
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
                if (IsInGame || IsLocalClientRunning) return BrushCardInGame;
                return BrushCardDefault;
            }
        }

        public IBrush CommandPaletteRowBackground => IsCommandPaletteSelected ? BrushPaletteSelectedBg : BrushPaletteDefaultBg;
        public IBrush CommandPaletteRowBorder => IsCommandPaletteSelected ? BrushPaletteSelectedBorder : BrushPaletteDefaultBorder;
        public IBrush MemoryPillBackground => BrushOnlineBg;
        public IBrush MemoryPillBorder => BrushOnlineBorder;
        public IBrush MemoryPillForeground => BrushOnlineFg;
    }
}
