# Performance & Quality Baseline (Giai đoạn 0 — Cập nhật `v5.0.0`)

Tài liệu này ghi nhận trạng thái baseline trước khi thực hiện các nâng cấp Bảo mật (Giai đoạn 1), Độ ổn định/Tốc độ (Giai đoạn 2) và Tài liệu (Giai đoạn 3), cùng trạng thái hoàn tất trong phiên bản **`v5.0.0`**.

## 1. Trạng thái Build & Test Suite
- **Nền tảng**: .NET 10 (Windows x64, Release build, `-warnaserror`)
- **Tổng số bài test (`Shiro.Tests`)**: Baseline ban đầu `1,559` tests -> Hiện tại (`v5.0.0`): **`2,416` test cases** (`1,819` test methods)
- **Kết quả**: `100% Passed`, `0 Failed`, `0 Skipped`
- **Coverage Gates (`v5.0.0`)**: Overall Line `~83.7%` (Gate `>= 75.0%`), Branch `~73.7%` (Gate `>= 60.0%`), Core Security/Persistence `94.09%` (Gate `>= 92.0%`)

## 2. Hiệu năng dẫn xuất khóa (KDF - PBKDF2-SHA256 & HKDF-SHA256 trên .NET 10 Windows CNG)
Đo đạc thực tế thông qua `Rfc2898DeriveBytes.Pbkdf2` và `HKDF.DeriveKey` (SHA-256, 32-byte key) trên .NET 10:
- **210.000 vòng (Chuẩn V1/V2 cũ)**: `~25 ms` / lần dẫn xuất
- **300.000 vòng (`VaultExportService` V1)**: `~36 ms` / lần dẫn xuất (`72 ms` cho chu trình round-trip Export + Import); `VaultExportService` V2 dùng **600.000 vòng** (`SHIRO_VAULT_V2`)
- **600.000 vòng (Chuẩn OWASP 2023+ / Vault V3 cho mật khẩu người dùng)**: `~72 ms` / lần dẫn xuất
- **HKDF-SHA256 (`SHIRO_SECURE_VAULT_V4` cho khóa máy 256-bit `VaultKey` bảo vệ bởi DPAPI)**: `< 1 ms` / lần dẫn xuất

> **Đánh giá**: Kiến trúc Dual-KDF (`V3` PBKDF2 600.000 vòng cho mật khẩu người dùng + `V4` HKDF-SHA256 cho khóa máy 256-bit) giúp loại bỏ hoàn toàn độ trễ KDF khi dùng khóa máy mặc định đồng thời tăng gấp ~2.85 lần chi phí tấn công brute-force ngoại tuyến đối với mật khẩu người dùng.

## 3. Trạng thái khắc phục các điểm nghẽn / rủi ro trong `v5.0.0` (Remediated)
1. **[ĐÃ KHẮC PHỤC - `v5.0.0`] KDF & Định dạng Vault (`Services/Storage/Cryptography.cs`, `Services/Storage/VaultExportService.cs`)**:
   - Đã triển khai **Dual-KDF**: Header V3 (`SHIRO_SECURE_VAULT_V3`, 600.000 vòng PBKDF2-SHA256) cho mật khẩu người dùng, Header V4 (`SHIRO_SECURE_VAULT_V4`, HKDF-SHA256) cho khóa máy 256-bit (`VaultKey`), và `SHIRO_VAULT_V2` (600.000 vòng PBKDF2-SHA256) cho xuất/nhập Vault độc lập thiết bị.
   - Giữ tương thích ngược đọc V1/V2 và tự động đánh dấu `NeedsMigration` trong `AccountFileDecoder` để nâng cấp ở lần lưu kế tiếp.
2. **[ĐÃ KHẮC PHỤC - `v5.0.0`] Xác thực HTTP & WebSocket Gateway (`Services/Auth/WebAuthPolicy.cs`, `Services/Network/KestrelWebServer.cs`, `Services/Network/AccountApiDispatcher.cs`)**:
   - Đã nâng yêu cầu mật khẩu tối thiểu khi lắng nghe/truy cập ngoài loopback (`!IsLoopbackRemote`) lên **12 ký tự** (`MinExternalPasswordLength = 12`, giữ mức 6 ký tự cho loopback nội bộ) cùng cơ chế lockout sau 5 lần sai.
   - Đã chặn truy cập các trường nội bộ `Hba*` qua Kestrel `SetField`/`GetField` và yêu cầu xác thực handshake (`/ws?token=` hoặc header) + kiểm tra `Origin` trên các endpoint WebSocket (`/ws`, `/events`, `/v2/events`).
3. **[ĐÃ KHẮC PHỤC - `v5.0.0`] Độ trễ cố định (`Task.Delay`), Giám sát tiến trình & Quản lý vòng đời tài nguyên**:
   - Đã thay thế các khoảng chờ cố định bằng cơ chế chờ sự kiện (`WaitForExitAsync`, polling thích ứng có `CancellationToken`).
   - `MemoryOptimizer.cs` chỉ kích hoạt tối ưu hóa khi có tiến trình Roblox thực sự cần xử lý và tắt mặc định `EmptyWorkingSet` định kỳ để tránh giật khung hình.
   - Đã triển khai `IDisposable` trên `MainViewModel` và `ServerListViewModel` cùng cơ chế dọn dẹp bộ đệm nhạy cảm (`CryptographicOperations.ZeroMemory`) trong `AccountPersistenceService.Recovery.cs`.
