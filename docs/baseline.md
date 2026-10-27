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
