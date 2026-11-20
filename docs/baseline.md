# Baseline (2026-10-27)

| Chỉ số | Giá trị |
|---|---|
| Build Release | Xanh |
| Test | 361/361 pass |
| Line coverage | 25.6% |
| Branch coverage | 14.9% |
| File .cs > 500 dòng | 25 |
| `catch {` trống | 324 |
| `async void` | 20 |
| `.Result` / `.Wait()` | 12 / 1 |
| `Task.Run` | 40 |
| Singleton `static ... Instance` | 33 |

## Coverage vùng rủi ro (dòng được phủ / tổng)
- VaultKeyRecoveryService: 0/479 (0%)
- AccountManager.Launch: 0/95; Account.Launcher: 0/317
- AccountManager.Persistence: 49/299 (16%)
- LaunchCoordinator: 38/324 (12%)
- CryptoHelper: 9/19; Cryptography: 58/60; AccountDataSerializer: 44/47; AccountStore: 62/88

---

# Current Verified Baseline (2026-11-20 — Plans 1–4)

| Chỉ số | Giá trị | Trạng thái |
|---|---|---|
| Build Release (`-warnaserror`) | 0 Warnings / 0 Errors | Hoàn hảo |
| Unit Tests (`Shiro.Tests`) | 2,416 / 2,416 test cases Pass (1,819 methods, 0 Failed, 0 Skipped) | 100% Xanh |
| Overall Line Coverage | ~83.7% (Gate >= 75.0%) | Vượt ngưỡng |
| Overall Branch Coverage | ~73.7% (Gate >= 60.0%) | Vượt ngưỡng |
| Core Security / Persistence | 94.09% (955/1015 lines, Gate >= 92.0%) | Vượt ngưỡng |
| File .cs > 485 dòng | 0 | Tuân thủ 100% |
| `catch {` trống / untyped | 0 (chỉ giữ allowlist `AppLogger.cs`) | Đã bọc logging/handled |
| `async void` trong `AvaloniaUI/` | 0 (chỉ giữ 2 `ICommand.Execute` ở `RelayCommand.cs`) | Đã cô lập |
| Sync-over-async blocking | 0 `.GetAwaiter().GetResult()` / `httpClient.Send` | Không còn blocking mới |
| Dialog ViewModels | 100% Decoupled (DialogViewModelBase + IUserPrompt) | Kiểm thử headless độc lập |
| Single-File Publish | `build/Release/Shiro.exe` (~66.7 MiB / 69,929,550 bytes) | Sẵn sàng triển khai |

---

## Task 20: 2026-11 Manual Verification Checklist (Human Runtime Test)

Checklist kiểm thử thủ công runtime trên máy Windows thực tế:

- [ ] **1. Multi-Roblox Client Launch**:
  - Khởi chạy đồng thời 2 tài khoản Roblox từ danh sách.
  - Kỳ vọng: Cả 2 client Roblox đều mở thành công, duy trì kết nối ổn định đồng thời (RobloxSingletonService mutex giải phóng đúng thời điểm, không bị kick do mutex xung đột).
- [ ] **2. In-App Fast Login**:
  - Nhập thông tin đăng nhập trong hộp thoại Fast Login nhúng (WebView2).
  - Kỳ vọng: Tự động bắt cookie `.ROBLOSECURITY`, tài khoản được mã hóa lưu an toàn vào Vault, hộp thoại đóng mượt mà.
- [ ] **3. Bulk Import Format**:
  - Dán danh sách 3 dòng định dạng `username:password` vào Cookie / Batch Import.
  - Kỳ vọng: Hàng đợi Fast Login mở với 3 tài khoản, tuần tự đăng nhập từng tài khoản.
- [ ] **4. Local HTTP & WebSocket Gateway (Kestrel API)**:
  - Bật Local Web Server trong Settings và truy cập `/docs` hoặc `/swagger.json` (alias `/openapi.json`) trên `localhost`.
  - Kỳ vọng: Tài liệu OpenAPI 3.0 hiển thị đầy đủ; các endpoint `/v2/*` (ví dụ `/v2/GetAccountsJson`, `/v2/GetAccounts`) và WebSocket `/ws`, `/events`, `/v2/events` (`/ws?token=` hoặc header) thực thi đúng chính sách xác thực mật khẩu (`>= 12` ký tự khi mở external) / `Origin` và chặn đọc/ghi các field `Hba*`.
- [ ] **5. Settings & Localization**:
  - Thay đổi ngôn ngữ hiển thị (Tiếng Việt <-> English) trong Settings.
  - Kỳ vọng: Toàn bộ giao diện chính và các hộp thoại mở cập nhật tức thì (FrozenDictionary lookups); nút Save hiển thị phản hồi và tự ẩn sau 3 giây.
- [ ] **6. Startup Update Detector Resiliency**:
  - Tắt tạm thời kết nối mạng khi khởi động ứng dụng với tùy chọn `CheckForUpdates` đang bật.
  - Kỳ vọng: `UpdateCheckService` timeout an toàn sau 5 giây ở chế độ nền, ghi log debug gọn và không làm treo hay sập giao diện khởi động.
