# Reliability & Auth Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Đóng lỗ hổng xác thực Nexus/WebServer khi mở LAN, loại bỏ nguy cơ mất dữ liệu khi lưu tài khoản, thay việc phân loại kết quả launch bằng dò chuỗi bằng kết quả có kiểu, và làm `LoadAccounts` kiểm thử được.

**Architecture:** Gom luật xác thực về một hàm thuần `WebAuthPolicy.Evaluate` được cả HTTP API và Nexus WebSocket gọi. Tách quyết định "có lưu không" của `SaveAccounts` thành hàm thuần và chuyển lần lưu bị chặn sang hàng đợi debounce sẵn có. Producer launch trả `LaunchResult` (outcome đặt tại nguồn), API chuỗi cũ thành wrapper. Phần giải mã file tài khoản được tách khỏi UI thành `AccountFileDecoder`.

**Tech Stack:** .NET 10 (`net10.0-windows10.0.19041.0`), ASP.NET Core Kestrel, Avalonia, xUnit.

**Spec:** Đánh giá trong hội thoại ngày 2026-11-01 (mục 🔴 1–2 lần review đầu, mục 1–4 + "nhỏ hơn" lần review thứ hai). Không có file spec riêng.

## Global Constraints

- Không đổi chữ ký public đang có: `Account.JoinServer`, `Account.LaunchAppHome`, `AccountManager.SaveAccounts(bool, bool)`, `AccountManager.LoadAccounts(byte[]?, out bool)`, `IRobloxLauncherService`.
- Chuỗi trả về của các API chuỗi cũ phải **giữ nguyên từng ký tự** (`"Success"`, `Account.BrowserHomeResult`, các thông báo `"ERROR: ..."`).
- Nexus.lua chạy trong Roblox không gửi được header → Nexus được phép nhận mật khẩu qua query `password`. HTTP API chỉ nhận mật khẩu qua query khi request đến từ loopback.
- Độ dài mật khẩu tối thiểu giữ nguyên: 6 ký tự.
- Nexus local (loopback) khi chưa cấu hình mật khẩu vẫn **được phép** (tương thích Nexus.lua hiện có).
- Mỗi task một commit; toàn bộ `dotnet test Shiro.sln` phải xanh sau mỗi task (baseline: 577 passed).
- Test dùng storage phải nằm trong `[Collection("StoragePathsCollection")]` và `StoragePaths.OverrideDataDirectoryForTesting`.

## Review Focus

- Bật `AllowExternalConnections` nhưng chưa đặt mật khẩu → server phải tự lùi về chỉ localhost, không được lắng nghe LAN (test ở Task 1, Step 6).
- Request LAN gửi mật khẩu đúng nhưng qua query string tới HTTP API → phải bị từ chối 401 (test ở Task 1, `Api_External_QueryPasswordRejected`).
- Hai lệnh lưu cách nhau < 1,5 s → lần thứ hai phải được hẹn lưu lại, không bị bỏ (test ở Task 2, `Gate_RateLimitedButDirty_Defers`).
- Thông báo lỗi có chữ "Unsuccessful" hoặc username chứa "429" → không được phân loại thành Success/RateLimited (test ở Task 3, `Legacy_Unsuccessful_IsFailed`).
- Backup bị khoá bằng master password được "promote" → file chính hiện tại và `.old` cũ không được bị xoá (test ở Task 4, `PromoteBackup_PreservesExistingOld`).

---

### Task 1: `WebAuthPolicy` — một luật xác thực dùng chung

**Files:**
- Create: `src/Shiro/Services/WebAuthPolicy.cs`
- Modify: `src/Shiro/AccountManager.WebServer.cs:144-265` (`ExtractRequestPassword`, `ValidateWebSocketAuth`, `ProcessApiRequestAsync`)
- Modify: `src/Shiro/Nexus/NexusWebSocketServer.cs:38-100` (ctor + middleware auth)
- Modify: `src/Shiro/Services/KestrelWebServer.cs:40-62` (ctor/StartAsync, CORS)
- Modify: `src/Shiro/Nexus/Nexus.lua:232-254` (`Nexus:Connect` nhận mật khẩu tuỳ chọn)
- Test: `tests/Shiro.Tests/WebAuthPolicyTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  internal enum AuthScope { PublicTelemetry, Api, NexusControl }
  internal enum AuthDecision { Allow, ForbiddenExternal, ForbiddenHost, ForbiddenOrigin, PasswordNotConfigured, InvalidPassword }
  internal readonly record struct AuthSettings(bool AllowExternal, bool EveryRequestRequiresPassword, string? ConfiguredPassword);
  internal readonly record struct AuthRequest(AuthScope Scope, bool IsLoopbackRemote, string? Host, string? Origin,
                                              string? HeaderPassword, string? QueryPassword);
  internal static class WebAuthPolicy {
      public const int MinPasswordLength = 6;
      public static AuthDecision Evaluate(in AuthRequest request, in AuthSettings settings);
      public static bool IsPasswordConfigured(string? configured);   // != null && Length >= 6
      public static bool CanListenExternally(in AuthSettings settings); // AllowExternal && IsPasswordConfigured
      public static int ToStatusCode(AuthDecision decision);          // Allow 200, Forbidden* 403, còn lại 401
  }
  ```

Luật của `Evaluate` (theo thứ tự, dừng ở luật đầu tiên khớp):
1. `!IsLoopbackRemote && !AllowExternal` → `ForbiddenExternal`.
2. `!AllowExternal`: `Host` không rỗng và không phải loopback (`AccountManager.IsLoopbackHostName`) → `ForbiddenHost`; `Origin` không tin cậy (`AccountManager.IsTrustedLocalOrigin`) → `ForbiddenOrigin`.
3. Mật khẩu hiệu lực = `HeaderPassword`; nếu rỗng thì dùng `QueryPassword` **chỉ khi** `Scope == NexusControl || IsLoopbackRemote`.
4. Bắt buộc mật khẩu khi: `Scope == Api` **hoặc** `EveryRequestRequiresPassword` **hoặc** `!IsLoopbackRemote` **hoặc** (`Scope == NexusControl && IsPasswordConfigured`).
5. Nếu bắt buộc: chưa cấu hình → `PasswordNotConfigured`; mật khẩu hiệu lực rỗng hoặc sai (`AccountManager.FixedTimeEqualsPassword`) → `InvalidPassword`.
6. `Allow`.

- [x] **Step 1: Viết test thất bại** — `WebAuthPolicyTests` dùng `[Theory]`:

```csharp
// (scope, loopback, allowExt, everyReq, configured, header, query, expected)
[InlineData(AuthScope.NexusControl, true,  false, false, null,      null,     null,     AuthDecision.Allow)]               // Nexus local cũ vẫn chạy
[InlineData(AuthScope.NexusControl, false, true,  false, null,      null,     null,     AuthDecision.PasswordNotConfigured)] // lỗ hổng chính
[InlineData(AuthScope.NexusControl, false, true,  false, "secret1", null,     "secret1",AuthDecision.Allow)]               // Lua dùng query
[InlineData(AuthScope.NexusControl, false, true,  false, "secret1", null,     "wrong!", AuthDecision.InvalidPassword)]
[InlineData(AuthScope.NexusControl, true,  false, false, "secret1", null,     null,     AuthDecision.InvalidPassword)]
[InlineData(AuthScope.Api,          true,  false, false, null,      null,     null,     AuthDecision.PasswordNotConfigured)]
[InlineData(AuthScope.Api,          true,  false, false, "secret1", null,     "secret1",AuthDecision.Allow)]               // local được dùng query
[InlineData(AuthScope.Api,          false, true,  false, "secret1", null,     "secret1",AuthDecision.InvalidPassword)]     // Api_External_QueryPasswordRejected
[InlineData(AuthScope.Api,          false, true,  false, "secret1", "secret1",null,     AuthDecision.Allow)]
[InlineData(AuthScope.Api,          false, false, false, "secret1", "secret1",null,     AuthDecision.ForbiddenExternal)]
[InlineData(AuthScope.PublicTelemetry, true, false, false, null,    null,     null,     AuthDecision.Allow)]
[InlineData(AuthScope.PublicTelemetry, true, false, true,  "secret1", null,   null,     AuthDecision.InvalidPassword)]
[InlineData(AuthScope.PublicTelemetry, false, true, false, "secret1", null,   null,     AuthDecision.InvalidPassword)]
```
Thêm `[Fact] Evaluate_LocalOnly_RejectsForeignOrigin` (Origin `https://evil.example` → `ForbiddenOrigin`), `[Fact] Evaluate_LocalOnly_RejectsForeignHost` (Host `192.168.1.5` → `ForbiddenHost`), và `[Fact] CanListenExternally_RequiresConfiguredPassword` (`(true,false,null)` → false, `(true,false,"12345")` → false, `(true,false,"123456")` → true).

- [x] **Step 2: Chạy để thấy fail**
Run: `dotnet test Shiro.sln --filter FullyQualifiedName~WebAuthPolicyTests`
Expected: FAIL biên dịch — `WebAuthPolicy` chưa tồn tại.

- [x] **Step 3: Implement `WebAuthPolicy` trong `src/Shiro/Services/WebAuthPolicy.cs`** theo luật ở trên.

- [x] **Step 4: Chạy test** — Expected: PASS.

- [x] **Step 5: Nối dây ba điểm gọi**
  - `ValidateWebSocketAuth` và `ProcessApiRequestAsync` dựng `AuthSettings` từ `WebServer.Get<bool>("AllowExternalConnections")`, `"EveryRequestRequiresPassword"`, `WSPassword`; scope `PublicTelemetry` cho `GetWebSocketStats`/`WebSocketStats`, `Api` cho phần còn lại. Giữ nguyên các thông điệp `Reply(...)` hiện có, ánh xạ theo `AuthDecision` và mã từ `ToStatusCode`.
  - `NexusWebSocketServer` middleware thay khối kiểm tra dòng 60-100 bằng `WebAuthPolicy.Evaluate` với `AuthScope.NexusControl`, `AllowExternal` lấy từ `AccountManager.AccountControl.Get<bool>("AllowExternalConnections")`.
  - `ExtractRequestPassword` tách thành `ExtractHeaderPassword(getHeader)` (X-Shiro-Auth → X-RAM-Auth → Bearer) và `ExtractQueryPassword(getQuery)` (`Password` → `password` → `token`); giữ `ExtractRequestPassword` cũ làm wrapper để test `SecurityHardeningTests.ExtractRequestPassword_*` vẫn xanh.

- [x] **Step 6: Không lắng nghe LAN khi chưa có mật khẩu** — trong `NexusWebSocketServer.StartAsync` và `KestrelWebServer.StartAsync`, nếu `_allowExternal && !WebAuthPolicy.IsPasswordConfigured(AccountManager.WSPassword)` thì đặt `_allowExternal = false` (bỏ `readonly`) và `Program.Logger?.Warn("[...] External connections require a WebServer password (>= 6 chars); listening on localhost only.")`. Test: `[Fact] KestrelWebServer_ExternalWithoutPassword_FallsBackToLocalhost` — dựng server port ngẫu nhiên với `allowExternal: true`, `WSPassword = ""`, sau `StartAsync` assert property mới `internal bool IsListeningExternally => _allowExternal` là `false`. Khi `_allowExternal` true, đổi `Access-Control-Allow-Origin: *` thành không gửi header CORS (client LAN không phải trình duyệt).

- [x] **Step 7: Nexus.lua** — `function Nexus:Connect(Host, Bypass, Password)`; nếu `Password` khác nil và không rỗng thì nối `&password=` + `HttpService:UrlEncode(Password)` vào URL ở dòng 254. Cập nhật `src/Shiro/Nexus/NexusDocs.md` phần Connect.

- [x] **Step 8: Chạy toàn bộ test**
Run: `dotnet test Shiro.sln` — Expected: tất cả PASS (≥ 577 + test mới).

- [x] **Step 9: Commit**
```bash
git add src/Shiro/Services/WebAuthPolicy.cs src/Shiro/AccountManager.WebServer.cs src/Shiro/Nexus src/Shiro/Services/KestrelWebServer.cs tests/Shiro.Tests/WebAuthPolicyTests.cs
git commit -m "fix(security): unify web/Nexus auth in WebAuthPolicy and refuse LAN without password"
```

---

### Task 2: `SaveAccounts` — không bỏ lần lưu, không ghi đè bản mới

**Files:**
- Modify: `src/Shiro/AccountManager.Persistence.cs:337-452`
- Test: `tests/Shiro.Tests/AccountSaveGateTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  internal enum SaveGate { Proceed, Defer, Skip }
  internal static SaveGate EvaluateSaveGate(DateTime now, DateTime startTime, DateTime lastSaveTime,
      bool bypassRateLimit, bool bypassCountCheck, int accountCount, bool anyDirty);   // trong AccountManager
  internal static List<Account> TakeDirtyAccounts(IReadOnlyList<Account> accounts);  // xoá IsDirty, trả danh sách vừa xoá
  internal static void RestoreDirty(IEnumerable<Account> accounts);                  // đặt lại IsDirty = true
  ```

Luật `EvaluateSaveGate` (theo thứ tự):
1. `!bypassCountCheck && accountCount == 0` → `Skip`.
2. `!bypassRateLimit && !bypassCountCheck && !anyDirty` → `Skip`.
3. `!bypassRateLimit && ((now - startTime) < 2s || (now - lastSaveTime) < 1.5s)` → `Defer`.
4. `Proceed`.

- [x] **Step 1: Viết test thất bại** trong `AccountSaveGateTests`:
  - `Gate_RateLimitedButDirty_Defers`: `now = last + 1s`, `start = now - 10s`, dirty, count 3 → `Defer`.
  - `Gate_StartupWindow_Defers`: `now = start + 1s` → `Defer`.
  - `Gate_NothingDirty_Skips`: không dirty, không bypass → `Skip`.
  - `Gate_EmptyList_Skips`: count 0, không bypassCount → `Skip`.
  - `Gate_Bypass_Proceeds`: rate-limited nhưng `bypassRateLimit: true` → `Proceed`.
  - `TakeDirty_ClearsOnlyDirtyAndReturnsThem`: 3 account, 2 dirty → trả 2, cả 3 `IsDirty == false`.
  - `RestoreDirty_SetsFlagBack`: sau `RestoreDirty(taken)` → 2 account đó `IsDirty == true`.

- [x] **Step 2: Chạy để thấy fail** — `dotnet test Shiro.sln --filter FullyQualifiedName~AccountSaveGateTests` → FAIL biên dịch.

- [x] **Step 3: Implement ba helper**, rồi viết lại phần đầu `SaveAccounts`:
  - Toàn bộ thân (kể cả `GetAccountsSnapshot()` và kiểm tra gate) nằm **trong** `lock (saveLock)`.
  - `Defer` → `QueueDeferredSave(); return;`. `Skip` → `return;`.
  - Ngay sau khi qua gate: `var taken = TakeDirtyAccounts(snapshot);` trước khi serialize. Bỏ hai vòng `acc.IsDirty = false` ở dòng ~377 và ~425 (nhánh hash trùng vẫn `return` bình thường vì đã xoá cờ).
  - Bọc phần serialize + ghi bằng `try/catch (Exception) { RestoreDirty(taken); throw; }`; nhánh `SaveData` rỗng cũng `RestoreDirty(taken)` trước `return`.

- [x] **Step 4: Chạy test** — Expected: PASS; `dotnet test Shiro.sln` toàn bộ PASS.

- [x] **Step 5: Commit**
```bash
git add src/Shiro/AccountManager.Persistence.cs tests/Shiro.Tests/AccountSaveGateTests.cs
git commit -m "fix(persistence): defer rate-limited saves and snapshot accounts inside save lock"
```

---

### Task 3: `LaunchResult` — outcome đặt tại nguồn, không dò chuỗi

**Files:**
- Modify: `src/Shiro/Services/LaunchCoordinator.Models.cs` (thêm `LaunchResult`)
- Modify: `src/Shiro/Services/AccountLaunchService.cs:184-345`, `src/Shiro/Services/AccountLaunchService.Home.cs:120-252`
- Modify: `src/Shiro/Services/BrowserService.cs:~170` (`OpenHomeResultAsync`)
- Modify: `src/Shiro/Classes/Account.Launcher.cs`, `src/Shiro/Classes/Account.Launcher.Home.cs`
- Modify: `src/Shiro/AccountManager.Launch.cs:213-228`, `src/Shiro/Services/LaunchCoordinator.cs:19-40,120-170`, `src/Shiro/Services/LaunchCoordinator.Helpers.cs:69-99,365-395`
- Test: `tests/Shiro.Tests/LaunchResultTests.cs`, cập nhật `tests/Shiro.Tests/LaunchCoordinatorTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public readonly record struct LaunchResult(LaunchOutcome Outcome, string Message) {
      public static LaunchResult Success();                       // (Success, "Success")
      public static LaunchResult BrowserHome();                   // (Success, Account.BrowserHomeResult)
      public static LaunchResult Fail(LaunchOutcome outcome, string message); // message giữ nguyên chuỗi "ERROR: ..." cũ
      public string ToLegacyString() => Message;
      public static LaunchResult FromLegacy(string? legacy);      // chỉ dùng ở biên cho producer chưa chuyển
  }
  // AccountLaunchService
  Task<LaunchResult> JoinServerResultAsync(/* cùng tham số JoinServerAsync */);
  Task<LaunchResult> LaunchAppHomeResultAsync(/* cùng tham số LaunchAppHomeAsync */);
  Task<LaunchResult> LaunchHomeInChromeResultAsync(Account account, CancellationToken ct = default);
  // Account
  Task<LaunchResult> JoinServerResult(/* cùng tham số JoinServer */);
  Task<LaunchResult> LaunchAppHomeResult(bool preferChrome, CancellationToken ct = default);
  // BrowserService
  Task<LaunchResult> OpenHomeResultAsync(Account account, bool preferChrome, CancellationToken ct = default);
  // LaunchCoordinator
  public LaunchCoordinator(Func<LaunchRequest, CancellationToken, Task<LaunchResult>> executeLaunch);
  ```

`FromLegacy` luật nghiêm: `null`/trắng → `Failed`; đúng `"Success"` (Ordinal) hoặc đúng `Account.BrowserHomeResult` → `Success`; còn lại → `Failed`. Không dò "429"/"timeout".

- [x] **Step 1: Viết test thất bại** trong `LaunchResultTests`:
  - `Legacy_ExactSuccess_IsSuccess`, `Legacy_BrowserHome_IsSuccess`.
  - `Legacy_Unsuccessful_IsFailed`: `FromLegacy("ERROR: Unsuccessful join").Outcome == Failed`.
  - `Legacy_UsernameWith429_IsFailed`: `FromLegacy("ERROR: Failed to launch Roblox for user429").Outcome == Failed`.
  - `Fail_PreservesMessage`: `Fail(RateLimited, "ERROR: x").ToLegacyString() == "ERROR: x"`.
  - Trong `LaunchCoordinatorTests`: `Coordinator_UsesOutcomeFromResult` — executor trả `LaunchResult.Fail(LaunchOutcome.RateLimited, "ERROR: anything")` → `LaunchProgress.Status == RateLimited`, `Result == "ERROR: anything"`.

- [x] **Step 2: Chạy để thấy fail** — `dotnet test Shiro.sln --filter "FullyQualifiedName~LaunchResultTests|FullyQualifiedName~LaunchCoordinatorTests"` → FAIL biên dịch.

- [x] **Step 3: Implement**
  - Trong `AccountLaunchService*`: đổi thân các method hiện có thành `*ResultAsync`, mỗi `return` cũ thành `LaunchResult`: nhánh HTTP 429 → `RateLimited`; nhánh không lấy được Authentication Ticket → `TicketError`; `"Success"` → `Success()`; còn lại → `Failed`. Method chuỗi cũ thành `(await XResultAsync(...).ConfigureAwait(false)).ToLegacyString()`.
  - `Account`/`BrowserService`: thêm các method `*Result` gọi tương ứng.
  - `ExecuteLaunchRequestAsync` trả `Task<LaunchResult>` và gọi `OpenHomeResultAsync`/`JoinServerResult`.
  - `LaunchCoordinator`: `state.Result = r.Message; state.Status = r.Outcome;`. Xoá `ClassifyResult`; `IsSuccessfulResult(state)` thành `state.Status == LaunchOutcome.Success`. Kiểm tra "signed you out" ở dòng ~190 đổi thành `state.Status == LaunchOutcome.TicketError`. Hai chỗ đặt `state.Result` captcha (Helpers ~369, ~392) đặt thêm `state.Status = LaunchOutcome.Failed`.
  - Sửa các test hiện có tạo `new LaunchCoordinator(...)` để executor trả `LaunchResult`.

- [x] **Step 4: Chạy test** — Expected: PASS; `dotnet test Shiro.sln` toàn bộ PASS.

- [x] **Step 5: Commit**
```bash
git add src/Shiro tests/Shiro.Tests/LaunchResultTests.cs tests/Shiro.Tests/LaunchCoordinatorTests.cs
git commit -m "refactor(launch): typed LaunchResult replaces substring result classification"
```

---

### Task 4: `AccountFileDecoder` + `LoadAccounts` không còn `goto`, không mất `.old`

> Ghi chú hiệu chỉnh: lời gọi đệ quy `LoadAccounts()` hiện tại **có** dừng (lần hai rơi vào nhánh vault-key thất bại và `return`), nên vấn đề thực là: khó test, `goto`, và xoá `.old` trước khi biết backup mở được.

**Files:**
- Create: `src/Shiro/Services/AccountFileDecoder.cs`
- Modify: `src/Shiro/AccountManager.Persistence.cs:109-296`
- Test: `tests/Shiro.Tests/AccountFileDecoderTests.cs`, thêm vào `tests/Shiro.Tests/AccountPersistenceTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  internal enum AccountDecodeStatus { Empty, Loaded, NeedsMasterPassword, Corrupt }
  internal sealed record AccountDecodeResult(AccountDecodeStatus Status, List<Account> Accounts,
      bool NeedsMigration, bool LegacyPlaintext, Exception? Error);
  internal sealed record AccountDecodeKeys(
      Func<byte[]> GetVaultKeyCopy,                          // StoragePaths.GetOrCreateVaultKeyCopy
      Func<byte[], byte[]?> TryRecoverVaultKey,              // VaultKeyRecoveryService.TryRecoverInProcess; null = không recover
      Func<byte[], (byte[] Plain, bool LegacyScope)> UnprotectDpapi,
      bool PlaintextOptIn);
  internal static class AccountFileDecoder {
      public static AccountDecodeResult Decode(byte[] data, byte[]? masterPassword, AccountDecodeKeys keys);
  }
  // AccountManager
  private AccountDecodeKeys CreateDecodeKeys(bool allowRecovery);
  private bool TryLoadFromBackup(byte[] backupData);       // thay khối 215-294, trả true nếu đã nạp
  internal static string PromoteBackupToPrimary(string saveFilePath, DateTime now); // trả đường dẫn file chính cũ đã giữ lại
  ```

`Decode` tái hiện đúng các nhánh dòng 119-209: dữ liệu rỗng → `Empty`; có header mã hoá + `masterPassword != null` → giải bằng master; có header + null → vault key, thất bại thì `TryRecoverVaultKey`, vẫn thất bại → `NeedsMasterPassword`; không header → DPAPI (`LegacyScope` ⇒ `NeedsMigration`), thất bại thì plaintext nếu `PlaintextOptIn` (⇒ `LegacyPlaintext`, `NeedsMigration`), không thì `Corrupt` kèm `Error`. Ném ngoại lệ là không được phép: mọi lỗi thành `Status`.

`PromoteBackupToPrimary`: nếu file chính tồn tại thì đổi tên thành `{saveFilePath}.old.{now:yyyyMMddHHmmss}` (không xoá `.old` hiện có), rồi `File.Move(backup, saveFilePath)`.

- [x] **Step 1: Viết test thất bại**
  - `AccountFileDecoderTests` (không cần storage, dùng key giả 32 byte):
    - `Decode_Empty_ReturnsEmpty`.
    - `Decode_VaultEncrypted_Loads` — `Cryptography.Encrypt(Serialize([acc]), key)` → `Loaded`, 1 account.
    - `Decode_WrongVaultNoRecovery_NeedsMasterPassword`.
    - `Decode_RecoveryKeyUsedWhenVaultFails` — `TryRecoverVaultKey` trả đúng key → `Loaded`.
    - `Decode_MasterPassword_Loads`.
    - `Decode_GarbageWithoutOptIn_IsCorruptWithError` — `UnprotectDpapi` ném → `Corrupt`, `Error != null`.
    - `Decode_PlaintextWithOptIn_LegacyPlaintext` — `LegacyPlaintext && NeedsMigration`.
  - `AccountPersistenceTests.PromoteBackup_PreservesExistingOld`: tạo file chính "A", `.old` "OLD", `.backup` "B"; gọi `PromoteBackupToPrimary(path, new DateTime(2026,11,1,22,0,0))` → file chính = "B", `.old` vẫn = "OLD", `path + ".old.20261101220000"` = "A".

- [x] **Step 2: Chạy để thấy fail** — `dotnet test Shiro.sln --filter "FullyQualifiedName~AccountFileDecoderTests|FullyQualifiedName~PromoteBackup"` → FAIL biên dịch.

- [x] **Step 3: Implement `AccountFileDecoder` và `PromoteBackupToPrimary`.**

- [x] **Step 4: Viết lại `LoadAccounts`** dùng `Decode` cho file chính; `Corrupt` giữ hành vi cũ (`AtomicWriteBytes(SaveFilePath + ".bak", ...)` + `ShowError`); `NeedsMasterPassword` giữ hành vi cũ (`EnableModernDashboard(); return;`). Khối backup chuyển vào `TryLoadFromBackup`: decode backup với vault key (không recovery) → `Loaded` thì nạp + `SaveAccounts(true)`; `NeedsMasterPassword` + người dùng đồng ý → `PromoteBackupToPrimary(...)`, `EnableModernDashboard()`, `return` từ `LoadAccounts` (không đệ quy); còn lại + người dùng đồng ý → nạp nếu `Loaded`, không thì log + `ShowError("Failed to load backup file!")`. Xoá nhãn `BackupHandled` và `goto`.

- [x] **Step 5: Chạy test** — `dotnet test Shiro.sln` toàn bộ PASS (đặc biệt `TryRestoreReadableBackup_*`, `LoadAccountsFromStore_*`).

- [x] **Step 6: Commit**
```bash
git add src/Shiro/Services/AccountFileDecoder.cs src/Shiro/AccountManager.Persistence.cs tests/Shiro.Tests/AccountFileDecoderTests.cs tests/Shiro.Tests/AccountPersistenceTests.cs
git commit -m "refactor(persistence): extract AccountFileDecoder, remove goto and preserve .old on backup promotion"
```

---

### Task 5: Dọn nhỏ — `AsyncRelayCommand` an toàn luồng, log thay vì nuốt lỗi

**Files:**
- Modify: `src/Shiro/ViewModels/RelayCommand.cs:42-190` (`AsyncRelayCommand`, `AsyncRelayCommand<T>`)
- Modify: `src/Shiro/Services/LaunchCoordinator.Helpers.cs:~50` (`GetSmartLaunchMinDelayMs`), `src/Shiro/Services/DiscordRpcService.cs:177,259,293`, `src/Shiro/Services/BloxGenService.cs:289`, `src/Shiro/Services/LaunchCoordinator.Helpers.cs:180,238`
- Test: thêm vào `tests/Shiro.Tests/MvvmFoundationTests.cs`

**Interfaces:** không thay đổi public.

- [x] **Step 1: Viết test thất bại** — `AsyncRelayCommand_ConcurrentExecute_RunsOnce`: executor `await gate.Task` và tăng `Interlocked.Increment(ref count)`; gọi `Execute(null)` từ 8 luồng qua `Parallel.For`, rồi `gate.SetResult()` và chờ 200 ms → `count == 1`.

- [x] **Step 2: Chạy để thấy fail** — `dotnet test Shiro.sln --filter FullyQualifiedName~AsyncRelayCommand_ConcurrentExecute` → FAIL (count > 1) hoặc flaky-pass; nếu pass ngẫu nhiên, chạy 5 lần.

- [x] **Step 3: Implement** — `_isExecuting` thành `int`; `Execute` dùng `if (Interlocked.CompareExchange(ref _isExecuting, 1, 0) != 0) return;` (sau khi kiểm `_canExecute`), `finally` dùng `Volatile.Write(ref _isExecuting, 0)`; `CanExecute` đọc `Volatile.Read(ref _isExecuting) == 0`. Áp dụng cho cả hai lớp async.

- [x] **Step 4: Thay các `catch { }` liệt kê ở Files** bằng `catch (Exception ex) { Program.Logger?.Debug($"[<Lớp>.<Method>] {ex.Message}"); }`. Không đổi luồng điều khiển.

- [x] **Step 5: Chạy toàn bộ test** — `dotnet test Shiro.sln` → PASS.

- [x] **Step 6: Commit**
```bash
git add src/Shiro/ViewModels/RelayCommand.cs src/Shiro/Services tests/Shiro.Tests/MvvmFoundationTests.cs
git commit -m "fix(mvvm): atomic AsyncRelayCommand guard; log previously swallowed exceptions"
```
