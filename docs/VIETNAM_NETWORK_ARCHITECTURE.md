# Kiến Trúc Mạng & Cơ Chế Giữ Phiên Roblox Tại Việt Nam (Vietnam ISP & HBA Architecture)

> [!IMPORTANT]
> **Ghi chú bất biến (Architectural Rule & Invariant)**:
> - **Các nhà mạng Việt Nam (Viettel, VNPT, FPT...) chặn web và dịch vụ Roblox ở tầng sâu** (DPI SNI drop, packet dropping, DNS poisoning, connection timeout).
> - **GoodbyeDPI và Cloudflare WARP đã được thử nghiệm nhiều lần và ĐỀU THẤT BẠI** trước hệ thống lọc DPI của các ISP tại Việt Nam. Không bao giờ khuyến nghị hoặc giả định người dùng có thể sử dụng GoodbyeDPI hoặc WARP để vượt chặn.
> - **Quy trình hoạt động chuẩn của người dùng**:
>   1. Bật VPN (ví dụ: ProtonVPN sang Nhật/Singapore/Mỹ) để thực hiện đăng nhập lại (Re-login / In-App Login / Quick Sign In).
>   2. Sau khi tài khoản đã đăng nhập thành công vào Shiro, người dùng **tắt VPN để trở về IP mạng chính tại Việt Nam** nhằm đảm bảo tốc độ và ping thấp khi vào game.
>   3. Nhấn khởi chạy (Vào Server / Mở Roblox Home) trên IP mạng Việt Nam.

---

## 1. Kỹ Thuật Lập Trình Giữ Phiên & Chuyển IP (HBA Session Persistence)

Để tài khoản không bị Roblox đánh dấu "Hết phiên" (HTTP 401 Unauthorized / Token Revocation) khi chuyển từ IP VPN sang IP Việt Nam, Shiro sử dụng **kỹ thuật định danh phần cứng HBA (Hardware-Bound Authentication)**:

### 1.1. Cơ Chế Hoạt Động Của HBA
1. **Trích xuất khóa phần cứng (HBA ECDSA Key Capture)**:
   - Khi đăng nhập qua WebView2 (`FastLoginDialog` & `FastLoginCoordinator`), hook JavaScript can thiệp vào `SubtleCrypto.prototype.generateKey` và đồng bộ khóa từ `IndexedDB` (`hbaDB` -> `hbaObjectStore` -> `hba_keys`).
   - Khóa ECDSA P-256 (PKCS#8 Base64) cùng với `BrowserTrackerId` (từ cookie `RBXEventTrackerV2`) được lưu trữ bảo mật trong Shiro vault cùng tài khoản (`HbaPrivateKey`, `HbaPublicKey`, `HbaTemplate`).

2. **Gắn Chữ Ký BAT (`x-bound-auth-token`)**:
   - Khi gửi các yêu cầu nhạy cảm (`users/authenticated`, `authentication-ticket`, `session/refresh`), Shiro tạo chữ ký điện tử kép IEEE P1363 bằng khóa private key thật của thiết bị.
   - Header `x-bound-auth-token` chứng minh với máy chủ Roblox rằng yêu cầu này đến từ **chính thiết bị vật lý đã đăng nhập**, chứ không phải cookie bị đánh cắp mang sang máy khác.

3. **Chủ Động Re-bind Session Khi Chuyển Mạng (`TryRebindSessionToCurrentNetworkAsync`)**:
   - `Account.GetAuthTicketCoreAsync` và `AccountLaunchService` (`LaunchAccount` / `LaunchAccountHome`) so sánh `LastBoundIpSignature` với `RobloxHbaService.GetNetworkSignatureForAccount(account)` (kết hợp `RobloxHbaService.GetLocalNetworkSignature()` và endpoint proxy riêng của tài khoản nếu có).
   - Nếu phát hiện người dùng vừa tắt VPN, đổi mạng, hoặc đổi proxy trên tài khoản (`LastBoundIpSignature != currentNetSig`), hệ thống chủ động gọi `TryRebindSessionToCurrentNetworkAsync(force: true)`.
   - Gửi request `v1/users/authenticated` có kèm chữ ký HBA BAT. Roblox xác nhận khóa thiết bị hợp lệ và xoay cookie `.ROBLOSECURITY` sang gắn với mạng/IP mới thông qua `Set-Cookie`.
   - Lấy auth ticket qua `v1/authentication-ticket` với chữ ký BAT và khởi chạy client thông qua `RobloxLaunchExecutorHelper.ExecuteWithGateAndDirectPreferenceAsync` (mặc định dùng URI protocol handler `roblox-player` và fallback sang `RobloxPlayerBeta.exe`; nếu bật `PreferDirectLaunch`, `RobloxDirectLauncher` khởi chạy trực tiếp `RobloxPlayerBeta.exe` trước và fallback về URI protocol nếu không khả dụng).

---

## 2. Các Quy Tắc Code Bắt Buộc (Code Invariants)

1. **`HasHbaBinding`**:
   ```csharp
   [JsonIgnore]
   public bool HasHbaBinding => !string.IsNullOrWhiteSpace(HbaPrivateKey);
   ```
   - **Tuyệt đối không** thêm điều kiện `!IsShortLivedCookie(_SecurityToken)` vào `HasHbaBinding`.
   - Các cookie thu được từ Quick Sign In (QR/mã số) hoặc Web login ban đầu thường có độ dài payload ~790 ký tự. Chúng **vẫn sở hữu khóa HBA hợp lệ** trong `hbaDB`. Nếu tắt HBA đối với các cookie này, Roblox sẽ coi việc đổi IP là đánh cắp phiên và lập tức thu hồi cookie.

2. **Bảo Tồn Khóa HBA (`CopyHbaAndFingerprintTo`)**:
   - Khi cập nhật phiên tài khoản, **tuyệt đối không** xóa trắng `HbaPrivateKey` / `HbaPublicKey` nếu tài khoản đã có sẵn khóa.
   - Khi đối tượng mới có khóa, sao chép khóa sang đối tượng đích. Nếu đối tượng mới không có khóa, giữ nguyên khóa cũ của đối tượng đích.

3. **Không Xóa Khóa Khi Gặp 401 Tạm Thời**:
   - Khi một request lấy ticket bị 401 và cần thử lại không có HBA, **tuyệt đối không** xóa trắng `HbaPrivateKey` / `HbaPublicKey` khỏi tài khoản hoặc ghi đè xóa vĩnh viễn khóa trên ổ đĩa. Khóa phải được giữ nguyên để phục vụ cho các lần đồng bộ mạng tiếp theo.
