using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace RBX_Alt_Manager.Services
{
    public static partial class LocalizationService
    {
        private static bool _coreKeysLoaded;
        private static FrozenDictionary<string, string> _frozenEnStrings = null!;
        private static FrozenDictionary<string, string> _frozenViStrings = null!;
        private static ResourceDictionary _enResourceDict = null!;
        private static ResourceDictionary _viResourceDict = null!;
        private static ResourceDictionary? _activeResourceDict;

        static LocalizationService()
        {
            EnsureCoreKeys();
        }

        public static void EnsureCoreKeys()
        {
            if (_coreKeysLoaded) return;
            _coreKeysLoaded = true;

            // === ENGLISH STRINGS ===
            EnStrings["Nav_Workspace"] = "WORKSPACE";
            EnStrings["Nav_Accounts"] = "Accounts";
            EnStrings["Nav_ServerList"] = "Server List";
            EnStrings["Nav_RecentGames"] = "Recent Games";
            EnStrings["Nav_Tools"] = "Tools";
            EnStrings["Nav_Settings"] = "Settings";
            EnStrings["Nav_About"] = "About";
            EnStrings["Nav_CloseRoblox"] = "Close Roblox";
            EnStrings["Nav_CloseRobloxSelected"] = "Close Roblox ({0} selected)";
            EnStrings["Nav_SystemOnline"] = "System Online";
            EnStrings["Nav_CommandPalettePlaceholder"] = "Search / Command...";
            EnStrings["Nav_Ready"] = "Shiro • Ready";

            // Dashboard
            EnStrings["Dash_Title"] = "Accounts";
            EnStrings["Dash_Playing"] = "PLAYING";
            EnStrings["Dash_Ready"] = "READY";
            EnStrings["Dash_Expired"] = "EXPIRED";
            EnStrings["Dash_TotalRobux"] = "R$ TOTAL ROBUX";
            EnStrings["Dash_SelectAll"] = "Select All";
            EnStrings["Dash_AddAccount"] = "Add account";
            EnStrings["Dash_ScanExpired"] = "Scan Expired";
            EnStrings["Dash_ViewCards"] = "Cards";
            EnStrings["Dash_ViewCompact"] = "Table";
            EnStrings["Dash_LaunchHome"] = "Launch Home";
            EnStrings["Dash_JoinServer"] = "Join Server";
            EnStrings["Dash_SearchPlaceholder"] = "Search accounts, 1-10...";
            EnStrings["Dash_RecentGamesBtn"] = "Recent";
            EnStrings["Dash_ColAccount"] = "ACCOUNT / ALIAS";
            EnStrings["Dash_ColUserId"] = "USER ID";
            EnStrings["Dash_ColGroup"] = "GROUP";
            EnStrings["Dash_ColStatus"] = "STATUS";
            EnStrings["Dash_ColRobux"] = "ROBUX";
            EnStrings["Dash_ColActions"] = "ACTIONS";

            // Settings
            EnStrings["Set_Title"] = "System Settings";
            EnStrings["Set_Subtitle"] = "Configure Roblox launching, performance optimization, database security and networking.";
            EnStrings["Set_TabGeneral"] = "General & UI";
            EnStrings["Set_TabPerformance"] = "Performance & FastFlags";
            EnStrings["Set_TabLaunch"] = "Launch & Network";
            EnStrings["Set_TabSecurity"] = "Security & Data";
            EnStrings["Set_TabPresets"] = "1-Click Presets";
            EnStrings["Set_SectionUI"] = "UI & Experience";
            EnStrings["Set_Language"] = "Display Language";
            EnStrings["Set_LanguageTip"] = "Choose UI language for Shiro RAM (supports Vietnamese and English).";
            EnStrings["Set_Theme"] = "Accent Theme Color";
            EnStrings["Set_ThemeTip"] = "Customize the primary accent color across all buttons and states.";
            EnStrings["Set_StreamerMode"] = "Hide Account Names (Streamer Mode)";
            EnStrings["Set_StreamerModeTip"] = "Mask all account usernames with 'Hidden Account' during screen recording.";
            EnStrings["Set_AutoUpdate"] = "Automatically check for new updates";
            EnStrings["Set_AutoUpdateTip"] = "Check for new Shiro RAM releases from GitHub on startup.";
            EnStrings["Set_SaveSettings"] = "Save Settings";

            // Tools Menu
            EnStrings["Tool_AntiAfk"] = "Auto Anti-AFK (20 Mins)";
            EnStrings["Tool_Potato"] = "FastFlags Potato Mode (Ultra Low)";
            EnStrings["Tool_ZeroGpu"] = "Hide All Roblox (Zero GPU Mode)";
            EnStrings["Tool_OptimizeRam"] = "Optimize RAM Memory Now";
            EnStrings["Tool_MuteAll"] = "Mute/Unmute All Roblox";
            EnStrings["Tool_SmartMute"] = "Smart Auto-Mute Background Clients";
            EnStrings["Tool_Nexus"] = "Nexus Control Panel";
            EnStrings["Tool_Proxy"] = "Proxy Manager";
            EnStrings["Tool_CleanTray"] = "Clean Windows System Tray";

            // Server List
            EnStrings["Srv_PlaceIdPlaceholder"] = "Place ID (e.g. 920587237)...";
            EnStrings["Srv_SearchServers"] = "Search Servers";
            EnStrings["Srv_SearchGames"] = "Search Games";
            EnStrings["Srv_Cancel"] = "Cancel";
            EnStrings["Srv_LoadAllRegions"] = "Load All Regions";
            EnStrings["Srv_SearchPlayerPlaceholder"] = "Player username...";
            EnStrings["Srv_Find"] = "Find";
            EnStrings["Srv_TabServers"] = "Server List";
            EnStrings["Srv_TabExplorer"] = "Game Explorer";
            EnStrings["Srv_TabFavorites"] = "Favorite Games";
            EnStrings["Srv_TabWatcher"] = "Roblox Watcher";
            EnStrings["Srv_ColJobId"] = "JOB ID (SERVER GUID)";
            EnStrings["Srv_ColPlayers"] = "PLAYER COUNT";
            EnStrings["Srv_ColPing"] = "PING";
            EnStrings["Srv_ColRegion"] = "REGION / LOCATION";
            EnStrings["Srv_ColActions"] = "ACTIONS";
            EnStrings["Srv_JoinServer"] = "Join Server";
            EnStrings["Srv_Select"] = "Select";
            EnStrings["Srv_Play"] = "Play";
            EnStrings["Srv_AddFavorite"] = "Add current game to Favorites";
            EnStrings["Srv_SearchGamePlaceholder"] = "Search Roblox game name (e.g. Blox Fruits, Pet Simulator)...";
            EnStrings["Srv_SearchingGamesHint"] = "Searching Roblox games...";
            EnStrings["Srv_NoGamesFound"] = "No games found. Try another search term.";
            EnStrings["Srv_EmptyHint"] = "Enter Place ID and click 'Search Servers' to browse live Roblox servers.";
            EnStrings["Srv_WatcherActivate"] = "Enable Roblox Watcher";
            EnStrings["Srv_WatcherActivateDesc"] = "Automatically monitor, restart on crashes, and manage Roblox background clients.";
            EnStrings["Srv_WatcherExitOnBeta"] = "Close process when Roblox launches into Desktop Beta App";
            EnStrings["Srv_WatcherExitIfNoConn"] = "Close process if connection is lost or game stops responding";
            EnStrings["Srv_WatcherSaveWindowPositions"] = "Automatically save and restore Roblox window positions";
            EnStrings["Srv_WatcherVerifyDataModel"] = "Verify DataModel to detect frozen / stalled clients";
            EnStrings["Srv_WatcherIgnoreExisting"] = "Ignore Roblox processes started before Shiro launched";
            EnStrings["Srv_WatcherCloseRbxMemory"] = "Close game if RAM memory consumption exceeds threshold";
            EnStrings["Srv_WatcherMemoryThreshold"] = "Maximum RAM Memory Threshold (MB)";
            EnStrings["Srv_WatcherMemoryThresholdDesc"] = "If client exceeds this memory limit, Watcher will automatically terminate and restart it.";
            EnStrings["Srv_TipCopyJobId"] = "Copy Job ID";
            EnStrings["Srv_TipLoadRegion"] = "Load server IP & location";
            EnStrings["Srv_TipSelectPlace"] = "Select this Place ID";
            EnStrings["Srv_TipPlayGame"] = "Launch Roblox with this game";
            EnStrings["Srv_TipRemoveFav"] = "Remove from Favorites";

            // Settings - Performance
            EnStrings["Set_SectionPerf"] = "Performance & Optimization";
            EnStrings["Set_UnlockFps"] = "Unlock FPS Limit";
            EnStrings["Set_UnlockFpsTip"] = "Bypass Roblox's default 60 FPS cap.";
            EnStrings["Set_TargetFps"] = "Target FPS";
            EnStrings["Set_TargetFpsTip"] = "Maximum frame rate per Roblox window (e.g. 60, 144, 240, 360).";
            EnStrings["Set_PotatoMode"] = "Ultra Low Graphics (Potato Mode FastFlags)";
            EnStrings["Set_PotatoModeTip"] = "Loads ClientAppSettings FastFlags: disables shadows, anti-aliasing, locks 30 FPS for low-end PCs.";
            EnStrings["Set_MultiRbx"] = "Enable Multi-Roblox Instances";
            EnStrings["Set_MultiRbxTip"] = "Bypasses ROBLOX_singletonMutex to run multiple accounts on a single PC.";
            EnStrings["Set_AntiAfk"] = "Anti-AFK Global Protection (20 Mins)";
            EnStrings["Set_AntiAfkTip"] = "Sends periodic heartbeat to prevent 20-minute idle disconnects.";
            EnStrings["Set_AutoMute"] = "Auto-Mute Background Clients (WASAPI)";
            EnStrings["Set_AutoMuteTip"] = "Silences all inactive background Roblox instances using Windows Core Audio API, keeping only the focused client audible.";
            EnStrings["Set_SmartFastLaunch"] = "Smart Fast Launch (Zero-Delay Queue)";
            EnStrings["Set_SmartFastLaunchTip"] = "Advances immediately to the next account once the previous instance unlocks its singleton barrier, cutting launch time by 3x.";
            EnStrings["Set_AggressiveRamTrim"] = "Aggressive RAM Trimming";
            EnStrings["Set_AggressiveRamTrimTip"] = "Periodically flushes Working Set memory for all running Roblox clients, saving up to 1GB RAM per client.";

            // Settings - Launching & Network
            EnStrings["Set_SectionLaunch"] = "Launching & Networking";
            EnStrings["Set_JoinDelay"] = "Account Join Delay (Seconds)";
            EnStrings["Set_JoinDelayTip"] = "Stagger interval (seconds) when launching accounts. Recommended 4s - 6s to prevent Roblox HTTP 429 rate-limiting.";
            EnStrings["Set_UseProxies"] = "Route Traffic via Proxies (Proxy Manager)";
            EnStrings["Set_UseProxiesTip"] = "Use HTTP/SOCKS5 proxies to distribute traffic across accounts.";
            EnStrings["Set_EnableWebServer"] = "Enable Embedded WebServer (Kestrel API)";
            EnStrings["Set_EnableWebServerTip"] = "Allows external tools or bots to command launches via REST API.";
            EnStrings["Set_WebServerPort"] = "WebServer Port";
            EnStrings["Set_WebServerPortTip"] = "Local TCP port for internal API (default: 7963).";
            EnStrings["Set_NexusPort"] = "Nexus Controller Port";
            EnStrings["Set_NexusPortTip"] = "Port connecting in-game sync scripts (default: 5242).";

            // Settings - Security & Data
            EnStrings["Set_SectionSecurity"] = "Security & Database";
            EnStrings["Set_AutoCookie"] = "Auto Cookie Rotation & Extension";
            EnStrings["Set_AutoCookieTip"] = "Periodically refreshes .ROBLOSECURITY tokens to prevent session expiration.";
            EnStrings["Set_SavePasswords"] = "Save Account Passwords in Database";
            EnStrings["Set_SavePasswordsTip"] = "Encrypted password storage for automatic re-login on cookie expiration.";
            EnStrings["Set_MasterPassword"] = "Master Password (Database Encryption)";
            EnStrings["Set_MasterPasswordTip"] = "Encrypts the account vault using Authenticated AES-256-GCM + PBKDF2.";
            EnStrings["Set_ChangeMasterPassBtn"] = "Set / Change Master Password";
            EnStrings["Set_Maintenance"] = "System Maintenance & Cleanup";
            EnStrings["Set_MaintenanceTip"] = "Clears avatar caches, temporary logs, and ghost tray icons.";
            EnStrings["Set_CleanTrayBtn"] = "Clean Windows Tray";
            EnStrings["Set_ClearAvatarCacheBtn"] = "Clear Avatar Cache";

            // Settings - 1-Click Presets
            EnStrings["Set_SectionPresets"] = "1-Click Presets";
            EnStrings["Set_PresetsSubtitle"] = "Instantly apply pre-tuned configuration combinations for your use case.";
            EnStrings["Set_PresetSafeTitle"] = "Safe Launch Profile";
            EnStrings["Set_PresetSafeDesc"] = "8s Join Delay • 60 FPS • Multi-Roblox Enabled • Cookie Rotation • Anti-Rate-Limit.";
            EnStrings["Set_PresetAfkTitle"] = "AFK Farming Profile";
            EnStrings["Set_PresetAfkDesc"] = "5s Join Delay • Anti-AFK Enabled • Auto-Cookie Refresh • Continuous RAM optimization.";
            EnStrings["Set_PresetPotatoTitle"] = "Ultra Potato Profile (30 FPS)";
            EnStrings["Set_PresetPotatoDesc"] = "3s Join Delay • Potato FastFlags Enabled • 30 FPS • Minimum CPU/GPU load for low-end PCs.";
            EnStrings["Set_ApplyPresetBtn"] = "Apply Now";

            // === VIETNAMESE STRINGS ===
            ViStrings["Nav_Workspace"] = "KHÔNG GIAN LÀM VIỆC";
            ViStrings["Nav_Accounts"] = "Tài khoản";
            ViStrings["Nav_ServerList"] = "Danh sách máy chủ";
            ViStrings["Nav_RecentGames"] = "Game gần đây";
            ViStrings["Nav_Tools"] = "Công cụ";
            ViStrings["Nav_Settings"] = "Cài đặt";
            ViStrings["Nav_About"] = "Thông tin";
            ViStrings["Nav_CloseRoblox"] = "Đóng Roblox";
            ViStrings["Nav_CloseRobloxSelected"] = "Đóng Roblox ({0} đã chọn)";
            ViStrings["Nav_SystemOnline"] = "System Online";
            ViStrings["Nav_CommandPalettePlaceholder"] = "Tìm kiếm / Lệnh...";
            ViStrings["Nav_Ready"] = "Shiro • Ready";

            // Dashboard
            ViStrings["Dash_Title"] = "Tài khoản";
            ViStrings["Dash_Playing"] = "ĐANG CHƠI";
            ViStrings["Dash_Ready"] = "SẴN SÀNG";
            ViStrings["Dash_Expired"] = "HẾT HẠN";
            ViStrings["Dash_TotalRobux"] = "R$ TỔNG ROBUX";
            ViStrings["Dash_SelectAll"] = "Chọn tất cả";
            ViStrings["Dash_AddAccount"] = "Thêm tài khoản";
            ViStrings["Dash_ScanExpired"] = "Quét kiểm tra hết hạn";
            ViStrings["Dash_ViewCards"] = "Lưới thẻ";
            ViStrings["Dash_ViewCompact"] = "Bảng gọn";
            ViStrings["Dash_LaunchHome"] = "Mở Roblox Home";
            ViStrings["Dash_JoinServer"] = "Vào Server";
            ViStrings["Dash_SearchPlaceholder"] = "Tìm kiếm tài khoản, 1-10...";
            ViStrings["Dash_RecentGamesBtn"] = "Gần đây";
            ViStrings["Dash_ColAccount"] = "TÀI KHOẢN / ALIAS";
            ViStrings["Dash_ColUserId"] = "USER ID";
            ViStrings["Dash_ColGroup"] = "NHÓM";
            ViStrings["Dash_ColStatus"] = "TRẠNG THÁI";
            ViStrings["Dash_ColRobux"] = "ROBUX";
            ViStrings["Dash_ColActions"] = "THAO TÁC";

            // Settings
            ViStrings["Set_Title"] = "Cài đặt hệ thống";
            ViStrings["Set_Subtitle"] = "Cấu hình khởi chạy Roblox, tối ưu hiệu năng, bảo mật cơ sở dữ liệu và mạng.";
            ViStrings["Set_TabGeneral"] = "Chung & Giao diện";
            ViStrings["Set_TabPerformance"] = "Hiệu năng & FastFlags";
            ViStrings["Set_TabLaunch"] = "Khởi chạy & Mạng";
            ViStrings["Set_TabSecurity"] = "Bảo mật & Dữ liệu";
            ViStrings["Set_TabPresets"] = "1-Click Presets";
            ViStrings["Set_SectionUI"] = "Giao diện & Trải nghiệm";
            ViStrings["Set_Language"] = "Ngôn ngữ hiển thị (Language)";
            ViStrings["Set_LanguageTip"] = "Chọn ngôn ngữ giao diện của Shiro RAM (hỗ trợ Tiếng Việt và English).";
            ViStrings["Set_Theme"] = "Màu chủ đề (Accent Theme)";
            ViStrings["Set_ThemeTip"] = "Tùy chỉnh màu sắc chủ đạo cho toàn bộ các nút bấm và trạng thái.";
            ViStrings["Set_StreamerMode"] = "Ẩn tên tài khoản (Streamer Mode)";
            ViStrings["Set_StreamerModeTip"] = "Che toàn bộ tên đăng nhập tài khoản bằng 'Hidden Account' khi quay màn hình.";
            ViStrings["Set_AutoUpdate"] = "Tự động kiểm tra bản cập nhật mới";
            ViStrings["Set_AutoUpdateTip"] = "Kiểm tra các phiên bản mới của Shiro RAM từ GitHub khi khởi động.";
            ViStrings["Set_SaveSettings"] = "Lưu cài đặt (Save Settings)";

            // Tools Menu
            ViStrings["Tool_AntiAfk"] = "Tự động Anti-AFK (20 Phút)";
            ViStrings["Tool_Potato"] = "FastFlags Potato Mode (Siêu nhẹ)";
            ViStrings["Tool_ZeroGpu"] = "Ẩn toàn bộ Roblox (Zero GPU Mode)";
            ViStrings["Tool_OptimizeRam"] = "Tối ưu bộ nhớ RAM ngay";
            ViStrings["Tool_MuteAll"] = "Bật/Tắt tiếng toàn bộ Roblox";
            ViStrings["Tool_SmartMute"] = "Tự động tắt tiếng các acc chạy ngầm";
            ViStrings["Tool_Nexus"] = "Bảng điều khiển Nexus";
            ViStrings["Tool_Proxy"] = "Trình quản lý Proxy";
            ViStrings["Tool_CleanTray"] = "Làm sạch khay hệ thống Windows";

            // Server List
            ViStrings["Srv_PlaceIdPlaceholder"] = "Place ID (Ví dụ: 920587237)...";
            ViStrings["Srv_SearchServers"] = "Tìm Server";
            ViStrings["Srv_SearchGames"] = "Tìm Game";
            ViStrings["Srv_Cancel"] = "Hủy";
            ViStrings["Srv_LoadAllRegions"] = "Nạp tất cả khu vực";
            ViStrings["Srv_SearchPlayerPlaceholder"] = "Tên người chơi...";
            ViStrings["Srv_Find"] = "Tìm";
            ViStrings["Srv_TabServers"] = "Danh sách Server";
            ViStrings["Srv_TabExplorer"] = "Khám phá Game";
            ViStrings["Srv_TabFavorites"] = "Game Yêu thích";
            ViStrings["Srv_TabWatcher"] = "Giám sát Watcher";
            ViStrings["Srv_ColJobId"] = "JOB ID (SERVER GUID)";
            ViStrings["Srv_ColPlayers"] = "SỐ LƯỢNG NGƯỜI CHƠI";
            ViStrings["Srv_ColPing"] = "PING";
            ViStrings["Srv_ColRegion"] = "KHU VỰC / VỊ TRÍ";
            ViStrings["Srv_ColActions"] = "THAO TÁC";
            ViStrings["Srv_JoinServer"] = "Vào Server";
            ViStrings["Srv_Select"] = "Chọn";
            ViStrings["Srv_Play"] = "Chơi";
            ViStrings["Srv_AddFavorite"] = "Thêm game hiện tại vào Yêu thích";
            ViStrings["Srv_SearchGamePlaceholder"] = "Tìm kiếm tên game Roblox (ví dụ: Blox Fruits, Pet Simulator)...";
            ViStrings["Srv_SearchingGamesHint"] = "Đang tải danh sách game Roblox...";
            ViStrings["Srv_NoGamesFound"] = "Không tìm thấy game nào. Hãy thử từ khóa khác.";
            ViStrings["Srv_EmptyHint"] = "Nhập Place ID và bấm 'Tìm Server' để duyệt toàn bộ danh sách server Roblox trực tiếp.";
            ViStrings["Srv_WatcherActivate"] = "Kích hoạt Roblox Watcher";
            ViStrings["Srv_WatcherActivateDesc"] = "Tự động giám sát, khởi động lại khi crash, và quản lý các client Roblox chạy ngầm.";
            ViStrings["Srv_WatcherExitOnBeta"] = "Đóng tiến trình khi Roblox khởi động vào màn hình Beta App";
            ViStrings["Srv_WatcherExitIfNoConn"] = "Đóng tiến trình nếu mất kết nối hoặc game không phản hồi";
            ViStrings["Srv_WatcherSaveWindowPositions"] = "Tự động lưu và phục hồi vị trí các cửa sổ Roblox";
            ViStrings["Srv_WatcherVerifyDataModel"] = "Xác thực DataModel để phát hiện client bị freeze / đứng hình";
            ViStrings["Srv_WatcherIgnoreExisting"] = "Bỏ qua các tiến trình Roblox đã chạy trước khi mở Shiro";
            ViStrings["Srv_WatcherCloseRbxMemory"] = "Đóng game nếu bộ nhớ RAM tiêu thụ vượt ngưỡng cấu hình";
            ViStrings["Srv_WatcherMemoryThreshold"] = "Ngưỡng bộ nhớ RAM tối đa (MB)";
            ViStrings["Srv_WatcherMemoryThresholdDesc"] = "Nếu client vượt quá mức RAM này, Watcher sẽ tự động đóng và khởi động lại.";
            ViStrings["Srv_TipCopyJobId"] = "Sao chép Job ID";
            ViStrings["Srv_TipLoadRegion"] = "Nạp vị trí IP server";
            ViStrings["Srv_TipSelectPlace"] = "Chọn Place ID này";
            ViStrings["Srv_TipPlayGame"] = "Mở Roblox chơi game này";
            ViStrings["Srv_TipRemoveFav"] = "Xóa khỏi Yêu thích";

            // Settings - Performance
            ViStrings["Set_SectionPerf"] = "Hiệu năng & Tối ưu hóa Roblox";
            ViStrings["Set_UnlockFps"] = "Mở khóa giới hạn FPS (Unlock FPS)";
            ViStrings["Set_UnlockFpsTip"] = "Vượt qua giới hạn mặc định 60 FPS của Roblox Client.";
            ViStrings["Set_TargetFps"] = "Mục tiêu FPS (Target FPS)";
            ViStrings["Set_TargetFpsTip"] = "Khung hình tối đa cho mỗi cửa sổ Roblox (ví dụ: 60, 144, 240, 360).";
            ViStrings["Set_PotatoMode"] = "Chế độ đồ họa siêu nhẹ (Potato Mode FastFlags)";
            ViStrings["Set_PotatoModeTip"] = "Tự động nạp ClientAppSettings FastFlags: tắt bóng, tắt hiệu ứng khử răng cưa, khóa 30 FPS để tối ưu máy yếu.";
            ViStrings["Set_MultiRbx"] = "Cho phép mở nhiều tài khoản cùng lúc (Multi-Roblox)";
            ViStrings["Set_MultiRbxTip"] = "Bỏ qua mutex đơn thể ROBLOX_singletonMutex để treo nhiều acc trên một máy tính.";
            ViStrings["Set_AntiAfk"] = "Tự động chống kick 20 phút (Anti-AFK Global Protection)";
            ViStrings["Set_AntiAfkTip"] = "Gửi tín hiệu an toàn đến tất cả client đang chạy để không bị văng sau 20 phút không thao tác.";
            ViStrings["Set_AutoMute"] = "Tự động tắt tiếng các acc chạy ngầm (Auto-Mute WASAPI)";
            ViStrings["Set_AutoMuteTip"] = "Tự động ngắt âm thanh của các client Roblox chạy ngầm bằng Windows Core Audio API, chỉ giữ âm thanh của cửa sổ đang chọn.";
            ViStrings["Set_SmartFastLaunch"] = "Khởi chạy thông minh không độ trễ (Smart Fast Launch)";
            ViStrings["Set_SmartFastLaunchTip"] = "Tự động chuyển sang tài khoản tiếp theo ngay khi client trước dựng cửa sổ và mở khóa singleton, giúp mở 10-20 acc nhanh gấp 3 lần.";
            ViStrings["Set_AggressiveRamTrim"] = "Siêu tối ưu RAM cho Multi-Roblox (Aggressive RAM Trim)";
            ViStrings["Set_AggressiveRamTrimTip"] = "Tự động dọn sạch Working Set định kỳ cho tất cả client Roblox đang chạy, giảm RAM xuống ~350MB/acc.";

            // Settings - Launching & Network
            ViStrings["Set_SectionLaunch"] = "Khởi chạy & Mạng";
            ViStrings["Set_JoinDelay"] = "Thời gian chờ giữa các lần mở tài khoản (Account Join Delay)";
            ViStrings["Set_JoinDelayTip"] = "Khoảng thời gian giãn cách (giây) giữa các acc khi mở hàng loạt. Khuyến nghị 4s - 6s để tránh bị Roblox chặn IP (Error 429).";
            ViStrings["Set_UseProxies"] = "Kích hoạt định tuyến qua Proxy (Proxy Manager)";
            ViStrings["Set_UseProxiesTip"] = "Sử dụng danh sách Proxy HTTP/SOCKS5 để phân tải lưu lượng cho các tài khoản.";
            ViStrings["Set_EnableWebServer"] = "Bật WebServer tích hợp (Kestrel ASP.NET Core API)";
            ViStrings["Set_EnableWebServerTip"] = "Cho phép các ứng dụng ngoài hoặc Discord bot điều khiển mở game qua REST API.";
            ViStrings["Set_WebServerPort"] = "Cổng kết nối WebServer (WebServer Port)";
            ViStrings["Set_WebServerPortTip"] = "Cổng TCP lắng nghe API nội bộ (mặc định: 7963).";
            ViStrings["Set_NexusPort"] = "Cổng điều khiển Nexus Controller (Nexus Port)";
            ViStrings["Set_NexusPortTip"] = "Cổng UDP/TCP kết nối script đồng bộ trong Roblox (mặc định: 5242).";

            // Settings - Security & Data
            ViStrings["Set_SectionSecurity"] = "Bảo mật & Dữ liệu cơ sở";
            ViStrings["Set_AutoCookie"] = "Tự động gia hạn và xoay Cookie (Auto Cookie Rotation)";
            ViStrings["Set_AutoCookieTip"] = "Tự động tạo mới token .ROBLOSECURITY định kỳ để ngăn chặn tài khoản bị hết hạn phiên.";
            ViStrings["Set_SavePasswords"] = "Lưu mật khẩu tài khoản trong Database";
            ViStrings["Set_SavePasswordsTip"] = "Mã hóa và lưu trữ mật khẩu để hỗ trợ tính năng Re-login tự động khi cookie hết hạn.";
            ViStrings["Set_MasterPassword"] = "Mật khẩu Master Password (Mã hóa toàn bộ DB)";
            ViStrings["Set_MasterPasswordTip"] = "Mã hóa file lưu trữ tài khoản bằng chuẩn Authenticated AES-256-GCM + PBKDF2.";
            ViStrings["Set_ChangeMasterPassBtn"] = "Đặt / Đổi Master Password";
            ViStrings["Set_Maintenance"] = "Bảo trì & Dọn dẹp hệ thống";
            ViStrings["Set_MaintenanceTip"] = "Làm sạch bộ nhớ đệm Avatars, log tạm thời và icon ma trên khay hệ thống Windows.";
            ViStrings["Set_CleanTrayBtn"] = "Làm sạch Khay Windows";
            ViStrings["Set_ClearAvatarCacheBtn"] = "Xóa Cache Avatar";

            // Settings - 1-Click Presets
            ViStrings["Set_SectionPresets"] = "Cấu hình nhanh 1 chạm (1-Click Presets)";
            ViStrings["Set_PresetsSubtitle"] = "Áp dụng ngay các tổ hợp cấu hình được tinh chỉnh sẵn cho từng mục đích sử dụng.";
            ViStrings["Set_PresetSafeTitle"] = "Cấu hình An toàn (Safe Launch)";
            ViStrings["Set_PresetSafeDesc"] = "Join Delay 8s • 60 FPS • Multi-Roblox Bật • Tự động xoay Cookie • Chống Rate Limit tối đa.";
            ViStrings["Set_PresetAfkTitle"] = "Cấu hình AFK Treo Game (AFK Farm)";
            ViStrings["Set_PresetAfkDesc"] = "Join Delay 5s • Anti-AFK Bật • Auto Cookie Refresh Bật • Bộ nhớ RAM được tối ưu liên tục.";
            ViStrings["Set_PresetPotatoTitle"] = "Cấu hình Siêu nhẹ (Ultra Potato 30 FPS)";
            ViStrings["Set_PresetPotatoDesc"] = "Join Delay 3s • FastFlags Potato Mode Bật • 30 FPS • Tiêu thụ CPU và GPU tối thiểu cho máy yếu.";
            ViStrings["Set_ApplyPresetBtn"] = "Áp dụng ngay";

            _frozenEnStrings = EnStrings.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
            _frozenViStrings = ViStrings.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

            var enRes = new ResourceDictionary();
            foreach (var kvp in _frozenEnStrings)
            {
                enRes[kvp.Key] = kvp.Value;
                enRes["Loc_" + kvp.Key] = kvp.Value;
            }
            _enResourceDict = enRes;

            var viRes = new ResourceDictionary();
            foreach (var kvp in _frozenViStrings)
            {
                viRes[kvp.Key] = kvp.Value;
                viRes["Loc_" + kvp.Key] = kvp.Value;
            }
            _viResourceDict = viRes;
        }
    }
}
