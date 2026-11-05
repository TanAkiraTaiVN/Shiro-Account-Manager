# Performance & Quality Baseline (Giai đoạn 0)

Tài liệu này ghi nhận trạng thái baseline trước khi thực hiện các nâng cấp Bảo mật (Giai đoạn 1), Độ ổn định/Tốc độ (Giai đoạn 2) và Tài liệu (Giai đoạn 3).

## 1. Trạng thái Build & Test Suite
- **Nền tảng**: .NET 10.0.2 (Windows x64, Release build)
- **Tổng số bài test (`Shiro.Tests`)**: `1.559` tests
- **Kết quả**: `1.559 Passed`, `0 Failed`, `0 Skipped`
- **Tổng thời gian chạy test suite**: `~44 giây`

## 2. Hiệu năng dẫn xuất khóa (KDF - PBKDF2-SHA256 trên .NET 10 Windows CNG)
Đo đạc thực tế thông qua `Rfc2898DeriveBytes.Pbkdf2` (SHA-256, 32-byte key) trong `VaultExportTests` trên .NET 10:
- **210.000 vòng (Chuẩn V1/V2 cũ)**: `~25 ms` / lần dẫn xuất
- **300.000 vòng (`VaultExportService` V1)**: `~36 ms` / lần dẫn xuất (`72 ms` cho chu trình round-trip Export + Import)
- **600.000 vòng (Chuẩn OWASP 2023+ / Vault V3 mới)**: `~72 ms` / lần dẫn xuất

> **Đánh giá**: Mức tăng thêm `~47 ms` khi mở/lưu Vault ở mức 600.000 vòng là hoàn toàn không thể nhận biết đối với người dùng trên giao diện (dưới ngưỡng 100 ms phản hồi UI), trong khi tăng gấp ~2.85 lần chi phí tấn công brute-force ngoại tuyến và đạt chuẩn khuyến nghị của OWASP cho PBKDF2-HMAC-SHA256.

## 3. Hiện trạng các điểm nghẽn / rủi ro đã xác định trong mã nguồn
1. **KDF & Định dạng Vault (`Classes/Cryptography.cs`, `Services/Storage/VaultExportService.cs`)**:
   - Vault chính đang cố định ở 210.000 vòng (`SHIRO_SECURE_VAULT_V1_...` / `RAM_SECURE_VAULT_V2_...`).
   - `VaultExportService` đang cố định ở 300.000 vòng (`SHIRO_VAULT_V1`).
   - Cần bổ sung Header V3 (600.000 vòng), giữ tương thích ngược đọc V1/V2 và đánh dấu `NeedsMigration` trong `AccountFileDecoder` để tự động nâng cấp lên V3 ở lần lưu kế tiếp.
2. **Xác thực Nexus & WebServer (`Services/Auth/WebAuthPolicy.cs`)**:
   - Đã dùng `CryptographicOperations.FixedTimeEquals` trong `AccountApiDispatcher.FixedTimeEqualsPassword`.
   - Tuy nhiên khi mở kết nối ra ngoài mạng LAN/WAN (`!IsLoopbackRemote`), mật khẩu tối thiểu đang chỉ yêu cầu 6 ký tự (quá ngắn cho kết nối ngoài loopback). Cần nâng yêu cầu mật khẩu tối thiểu khi lắng nghe/truy cập ngoài loopback lên 12 ký tự (trong khi giữ mức 6 ký tự cho loopback nội bộ để không phá vỡ cấu hình local hiện hữu).
3. **Độ trễ cố định (`Task.Delay`) & Giám sát tiến trình**:
   - Nhiều luồng chờ DOM/cookie/CDP đang ngủ cố định (`Task.Delay`), gây chậm trên máy khỏe hoặc hụt thời gian trên máy yếu.
   - `RobloxProcess.cs` đang dùng vòng lặp `Task.Delay(15000)` để kiểm tra tiến trình thoát thay vì chờ sự kiện `WaitForExitAsync`.
   - `MemoryOptimizer.cs` kích hoạt timer mỗi 30 giây ngay cả khi chưa có tiến trình Roblox nào chạy.
