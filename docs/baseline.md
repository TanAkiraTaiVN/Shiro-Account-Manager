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

# Milestone 10/10 Baseline (2026-11-04)

| Chỉ số | Giá trị | Trạng thái |
|---|---|---|
| Build Release | 0 Warnings / 0 Errors | Hoàn hảo |
| Unit Tests | 1,555 / 1,555 Pass (0 Failed, 0 Skipped) | 100% Xanh |
| Overall Line Coverage | 75.47% (Gate >= 75.0%) | Vượt ngưỡng |
| Overall Branch Coverage | 63.93% (Gate >= 60.0%) | Vượt ngưỡng |
| Core Security / Persistence | 92.83% (Gate >= 92.0%) | Vượt ngưỡng |
| File .cs > 500 dòng | 0 | Tuân thủ 100% |
| `catch {` trống | 0 | Đã bọc logging/handled |
| Unhandled `async void` | 0 (Chỉ giữ 5 WebView2 UI events) | Đã cô lập |
| `.Result` / `.Wait()` blocking | 0 | Không còn sync-over-async |
| Dialog ViewModels | 100% Decoupled (DialogViewModelBase + IUserPrompt) | Kiểm thử headless độc lập |
| Single-File Publish | `build/Release/Shiro.exe` (~123 MB) | Sẵn sàng triển khai |

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
- [ ] **4. Nexus Game Telemetry & Controller**:
  - Khởi động Kestrel WebSocket server trong tab Nexus.
  - Inject `src/Shiro/Nexus/RAMAccount.lua` vào client Roblox in-game.
  - Kỳ vọng: Tài khoản hiển thị trạng thái "Online" trên dashboard Nexus; tính năng "Execute All" gửi script và in ra console của client; Anti-AFK kích hoạt thành công.
- [ ] **5. Settings & Localization**:
  - Thay đổi ngôn ngữ hiển thị (Tiếng Việt <-> English) trong Settings.
  - Kỳ vọng: Toàn bộ giao diện chính và các hộp thoại mở cập nhật tức thì (FrozenDictionary lookups); nút Save hiển thị phản hồi và tự ẩn sau 3 giây.
- [ ] **6. Updater Resiliency**:
  - Tắt tạm thời kết nối mạng và bấm "Check for Updates".
  - Kỳ vọng: Ứng dụng hiển thị thông báo lỗi thân thiện kèm nút Retry (không bị treo hay tắt đột ngột).
