# Phase 5 — Architecture Debt, Silent Catches & Repo Hygiene Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Giải quyết toàn bộ 8 nhược điểm trong bản đánh giá 7.5/10 (static facade của `AccountManager`, god class, vòng phụ thuộc `AccountRepository → AccountManager`, phụ thuộc ngược tầng của `WebAuthPolicy`, 47 khối catch nuốt lỗi, comment trộn tiếng Việt, thư mục `Classes/` legacy, repo phình), mỗi vấn đề được khoá lại bằng một ratchet test để không tái phát.

**Architecture:** Đi từ ngoài vào trong: (1) gom primitive xác thực vào `WebAuthPolicy`/`CryptoHelper`; (2) loại bỏ catch không kiểu/không log; (3) xoá static facade trạng thái trên `AccountManager`, chuyển chủ sở hữu sang `AppSettings`/`AppServices`; (4) tách persistence và registration ra hai service trong `Services/Storage` để cắt vòng `AccountRepository → AccountManager`; (5) các state service chỉ được resolve qua `AppServices` (composition root); (6) di chuyển `Classes/` vào `Domain/` và `Services/*`; (7) dọn repo và tài liệu. Không đổi hành vi người dùng, ngoại trừ body HTTP 500 không còn lộ exception message.

**Tech Stack:** .NET 10 (`net10.0-windows10.0.19041.0`, win-x64), C# latest, Avalonia 11.3, ASP.NET Core Kestrel, Microsoft.Extensions.DependencyInjection, xUnit 2.9, NSubstitute, NetArchTest.eNhancedEdition, Avalonia.Headless.XUnit, coverlet.

**Spec:** Bản đánh giá ưu/nhược điểm trong cuộc hội thoại ngày 2026-11-08 (tổng 7.5/10; Kiến trúc 6.0, Tổ chức 7.0, Tài liệu 7.0). Số liệu audit dùng trong plan này: [audit.txt](file:///C:/Users/lenovo/.gemini/antigravity/brain/eb67fe7d-db01-41ea-bb42-dd7fd38a8fee/scratch/audit.txt).

## Preconditions (trước Task 1)

- Phase 4 (`fix/p3-structural-and-ram-protection`, plan `2026-11-08-p3-structural-headroom-headless-ui-and-ram-token-protection.md`) đã **merge vào `main`**. Phase 4 Task 3/4 sửa `Classes/RobloxProcess*.cs`, `Classes/RobloxWatcher*.cs`, `Classes/Account*.cs` và đặt `maxLines = 485` — Task 3 và Task 8 của plan này xung đột trực tiếp nếu chạy song song.
- Tạo worktree mới `.worktrees/p5-architecture-debt` trên branch `fix/p5-architecture-debt-and-hygiene` từ `main` sau merge.
- Baseline: `dotnet test Shiro.sln -c Release --nologo` → `Failed: 0` (≥ 1869 test). Ghi số test thực tế vào progress ledger.
- Đo lại các baseline có thể đã bị Phase 4 thay đổi (số catch không kiểu, line budget `AccountManager`) bằng lệnh trong từng task; nếu khác số trong plan, dùng **số đo được** và ghi chú vào ledger.

## Global Constraints

- Không thêm NuGet package mới; không đổi `Directory.Packages.props`, `global.json`.
- Mọi file `.cs` mới/sửa phải ≤ ngưỡng `maxLines` hiện hành trong `ArchitectureTests.cs` (485 sau Phase 4).
- Comment/XML doc trong code: **chỉ tiếng Anh**. README và tài liệu người dùng giữ nguyên tiếng Việt.
- Di chuyển file bằng `git mv` (giữ lịch sử).
- Không đổi route, tham số, mã trạng thái của HTTP/WebSocket API; chỉ đổi body của response 500 (Task 1).
- Log exception bằng `AppServices.Logger.Debug(object message, Exception exception)` (hoặc `Warn` khi người dùng cần biết); message dạng `"[<ClassName>] <what failed>"`. Không bao giờ nội suy cookie, mật khẩu, token vào message.
- Coverage gate CI không đổi: overall ≥ 75%, branch ≥ 60%, core ≥ 92%. Per-file floor trong `tests/coverage-floors.json` phải **đi theo code** khi code bị di chuyển (đổi key sang path mới, giữ nguyên giá trị).
- Không đổi namespace của type domain trong `namespace Shiro` (`Account`, `Game`, `ServerData`, `IniFile`, …) — JSON/INI và test phụ thuộc vào chúng.
- Lệnh test toàn bộ: `dotnet test Shiro.sln -c Release --nologo` → `Failed: 0`. Lệnh test một nhóm: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~<Name>"`.
- Commit theo Conventional Commits, một commit cuối mỗi task (có thể thêm commit nhỏ giữa chừng).

## Review Focus

1. **Settings sau khi xoá `AccountManager.General`** — ghi qua `AppSettings.General.Set(...)` phải đi vào đúng `IniSection` của `AppSettings.ActiveIniFile` và được lưu xuống `RAMSettings.ini`; test ở Task 4 (`AppSettingsGeneral_WritesThroughToActiveIniFileSection`).
2. **Nexus WebSocket với external + mật khẩu yếu** — sau khi Nexus đọc cấu hình từ `AppSettings` thay vì `AccountManager`, kết nối ngoài với mật khẩu < 6 ký tự vẫn bị từ chối; test ở Task 1 (`NexusAuth_ExternalWithShortPassword_IsRejected`).
3. **Response 500 không lộ chi tiết nội bộ** — exception message (có thể chứa URL/cookie fragment) không được xuất hiện trong body; test ở Task 1 (`BlockUserEndpoint_WhenAccountThrows_Returns500WithoutExceptionMessage`).
4. **Deferred save khi tắt app** — sau khi persistence chuyển sang `AccountPersistenceService`, tài khoản bẩn vẫn được flush khi shutdown và save gate vẫn chặn ghi rỗng lúc khởi động; test ở Task 5 (`Shutdown_FlushesDirtyAccounts_ThroughPersistenceService`, `SaveGate_BlocksEmptyWriteDuringStartupWindow`).
5. **Test isolation sau khi bỏ `.Default`** — `AppServices.Override<ISelectionStateService>(fake)` giờ phải được quan sát bởi mọi call site cũ; test ở Task 7 (`SelectionStateOverride_IsObservedByAppSettingsFacade`).

---

## File Structure

| Đường dẫn | Trách nhiệm | Task |
|---|---|---|
| `src/Shiro/Services/Storage/CryptoHelper.cs` | + `FixedTimeEqualsSecret` | 1 |
| `src/Shiro/Services/Auth/WebAuthPolicy.cs` | Chủ sở hữu loopback/origin/password extraction | 1 |
| `src/Shiro/AccountManager.WebServer.cs` | Xoá 6 static forwarder | 1 |
| `tests/Shiro.Tests/SourceScan.cs` (mới) | Helper strip string literal cho ratchet test | 2 |
| `tests/Shiro.Tests/ArchitectureTests.Hygiene.cs` (mới) | Ratchet catch, comment tiếng Việt, namespace `Shiro.Classes`, folder `Classes/` | 2, 8, 9 |
| `src/Shiro/Services/Storage/AccountPersistenceService.cs` + `.Recovery.cs` (mới) | Load/Save/Recovery tài khoản | 5 |
| `src/Shiro/Services/Storage/AccountRegistrationService.cs` (mới) | Add/Remove/Lookup tài khoản | 6 |
| `src/Shiro/Domain/` (mới) | Type domain từ `Classes/` | 8 |
| `src/Shiro/Services/Browser/` (mới) | Puppeteer/Chromium automation từ `Classes/` | 8 |
| `docs/architecture.md`, `CONTRIBUTING.md`, `CHANGELOG.md` | Cập nhật kiến trúc, release build, changelog | 9 |

---

### Task 1: Auth primitives thuộc về `WebAuthPolicy` + không lộ exception ở HTTP 500

**Files:**
- Modify: `src/Shiro/Services/Storage/CryptoHelper.cs`
- Modify: `src/Shiro/Services/Auth/WebAuthPolicy.cs` (dòng 132, 141 + thêm method)
- Modify: `src/Shiro/Services/Network/AccountApiDispatcher.cs:104-180` (xoá 6 static helper, gọi sang `WebAuthPolicy`)
- Modify: `src/Shiro/Services/Network/AccountApiDispatcher.Endpoints.cs:73,223,376,386,396`
- Modify: `src/Shiro/AccountManager.WebServer.cs` (xoá 6 static forwarder; giữ `ApiRequestContext`, `SendKestrelResponse`, `ProcessApiRequestAsync`, `ValidateWebSocketAuth`)
- Modify: `src/Shiro/Nexus/NexusWebSocketServer.cs:50-77,118`
- Modify: `tests/Shiro.Tests/ArchitectureTests.cs:413-422` (thêm thư mục `Nexus` vào scan)
- Test: `tests/Shiro.Tests/WebAuthPolicyTests.cs`, `tests/Shiro.Tests/SecurityHardeningTests.cs`, `tests/Shiro.Tests/AccountManagerBehaviorTests.cs`, `tests/Shiro.Tests/WebServerEndpointsAndWorkflowTests.cs`, `tests/Shiro.Tests/ArchitectureTests.SourceInvariants.cs`

**Interfaces:**
- Produces:
  - `public static bool CryptoHelper.FixedTimeEqualsSecret(string provided, string expected)` — thân hàm chuyển nguyên từ `AccountApiDispatcher.FixedTimeEqualsPassword` (SHA-256 cả hai rồi `CryptographicOperations.FixedTimeEquals`).
  - Trên `WebAuthPolicy`: `IsLoopbackHostName(string host)`, `IsTrustedLocalOrigin(string origin)`, `ExtractHeaderPassword(Func<string,string?> getHeader)`, `ExtractQueryPassword(Func<string,string?> getQuery)`, `ExtractRequestPassword(Func<string,string?> getHeader, Func<string,string?> getQuery)` — cùng chữ ký `public static` như bản trong `AccountApiDispatcher` hiện tại.
  - `internal const string AccountApiDispatcher.InternalErrorMessage = "Internal server error";`

- [ ] **Step 1: Viết test thất bại**

```csharp
// ArchitectureTests.SourceInvariants.cs
[Fact]
public void WebAuthPolicy_ShouldNotDependOn_AccountApiDispatcher()
{
    var text = File.ReadAllText(Path.Combine(RepoRoot, "src", "Shiro", "Services", "Auth", "WebAuthPolicy.cs"));
    Assert.DoesNotContain("AccountApiDispatcher", text);
}

[Fact]
public void AccountManager_ShouldNotExpose_WebAuthForwarders()
{
    string[] removed = { "IsLoopbackHostName", "IsTrustedLocalOrigin", "FixedTimeEqualsPassword",
                         "ExtractHeaderPassword", "ExtractQueryPassword", "ExtractRequestPassword" };
    var present = typeof(AccountManager).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        .Select(m => m.Name).Intersect(removed).ToList();
    Assert.Empty(present);
}

// SecurityHardeningTests.cs
[Theory]
[InlineData("secret123", "secret123", true)]
[InlineData("secret123", "secret124", false)]
[InlineData("short", "muchlongersecret", false)]
[InlineData("", "secret123", false)]
public void FixedTimeEqualsSecret_ComparesByValue(string provided, string expected, bool match) =>
    Assert.Equal(match, CryptoHelper.FixedTimeEqualsSecret(provided, expected));

// WebServerEndpointsAndWorkflowTests.cs — account substitute ném exception có message nhạy cảm
[Fact]
public async Task BlockUserEndpoint_WhenAccountThrows_Returns500WithoutExceptionMessage()
{
    // Arrange: account mà BlockUserIdAsync ném new InvalidOperationException("leak-https://x/?cookie=ABC")
    // Act: gọi endpoint BlockUser qua ProcessApiRequestAsync
    // Assert:
    Assert.Equal(500, response.StatusCode);
    Assert.DoesNotContain("leak-", response.Body);
    Assert.Contains(AccountApiDispatcher.InternalErrorMessage, response.Body);
}

// WebAuthPolicyTests.cs (Review Focus #2)
[Fact]
public void NexusAuth_ExternalWithShortPassword_IsRejected()
{
    var settings = new AuthSettings(AllowExternal: true, EveryRequestRequiresPassword: false, ConfiguredPassword: "abc");
    var request = new AuthRequest(AuthScope.NexusControl, IsLoopbackRemote: false, "192.168.1.5:5242", null, "abc", null);
    Assert.NotEqual(AuthDecision.Allow, WebAuthPolicy.Evaluate(request, settings));
}
```

- [ ] **Step 2: Chạy test, xác nhận FAIL**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~WebAuthPolicy|FullyQualifiedName~FixedTimeEqualsSecret|FullyQualifiedName~BlockUserEndpoint_WhenAccountThrows"`
Expected: FAIL (compile error `FixedTimeEqualsSecret` chưa tồn tại; source invariant fail).

- [ ] **Step 3: Thêm `CryptoHelper.FixedTimeEqualsSecret`, chuyển 5 helper sang `WebAuthPolicy`**, xoá bản trong `AccountApiDispatcher` và 6 forwarder trong `AccountManager.WebServer.cs`. `WebAuthPolicy.Evaluate` gọi `CryptoHelper.FixedTimeEqualsSecret`. Dịch comment tiếng Việt `WebAuthPolicy.cs` (5 dòng quanh dòng 132) sang tiếng Anh, giữ nguyên ý.

- [ ] **Step 4: Thay 3 khối `catch (Exception x) { return reply(x.Message, false, 500, null); }`** ở `Endpoints.cs:376,386,396` bằng: log `AppServices.Logger.Warn("[AccountApiDispatcher] <endpoint> failed", x)` rồi `return reply(InternalErrorMessage, false, 500, null);`.

- [ ] **Step 5: Nexus đọc cấu hình từ `AppSettings`**: `AccountManager.WSPassword` → `AppSettings.WebSocketPassword`; `AccountManager.AccountControl` → `AppSettings.AccountControl`; `AccountManager.WebServer` → `AppSettings.WebServer`; `AccountManager.Extract*Password` → `WebAuthPolicy.Extract*Password`; `AccountManager.GetAccountsSnapshot()` → `AppServices.AccountRepository.GetAll()`.

- [ ] **Step 6: Mở rộng `Services_ShouldUse_AppSettings_And_AccountRepository_InsteadOf_AccountManagerStatic`** để scan thêm `src/Shiro/Nexus`.

- [ ] **Step 7: Cập nhật test cũ** — 39 tham chiếu trong `AccountManagerBehaviorTests.cs` (12), `SecurityHardeningTests.cs` (10), `WebAuthPolicyTests.cs` (17): `AccountManager.X`/`AccountApiDispatcher.X` → `WebAuthPolicy.X`; `FixedTimeEqualsPassword` → `CryptoHelper.FixedTimeEqualsSecret`.

- [ ] **Step 8: Chạy toàn bộ test**

Run: `dotnet test Shiro.sln -c Release --nologo`
Expected: `Failed: 0`.

- [ ] **Step 9: Commit**

```bash
git add -A src/Shiro tests/Shiro.Tests
git commit -m "refactor(auth): move auth primitives into WebAuthPolicy/CryptoHelper and stop leaking exception text in HTTP 500"
```

---

### Task 2: Ratchet catch không kiểu + xử lý nhóm Services (Auth/Launch/Network/Storage)

**Files:**
- Create: `tests/Shiro.Tests/SourceScan.cs`
- Create: `tests/Shiro.Tests/ArchitectureTests.Hygiene.cs`
- Modify (22 vị trí): `Services/Auth/FastLoginCoordinator.Classification.cs` (1), `Services/Auth/RobloxHbaService.Binding.cs` (2), `Services/Auth/RobloxHbaService.cs` (1), `Services/Launch/AutoReconnectService.cs` (1), `Services/Launch/PrerequisiteService.cs` (1), `Services/Launch/RobloxDirectLauncher.DeElevation.cs` (1), `Services/Launch/RobloxPlaceHelper.cs` (1), `Services/Network/AvatarService.cs` (1), `Services/Network/DiscordRpcService.cs` (1), `Services/Network/GameThumbnailService.cs` (2), `Services/Network/ProxyManagerService.cs` (1), `Services/Network/RobloxServerQueryService.cs` (1), `Services/Storage/AccountStore.cs` (3), `Services/Storage/JsonExtensions.cs` (2), `Services/Storage/StoragePaths.cs` (1), `Services/Storage/VaultKeyRecoveryService.AccountMerge.cs` (1), `Services/Storage/VaultKeyRecoveryService.cs` (1)

**Interfaces:**
- Produces:
  - `internal static class SourceScan` với `public static string StripStringLiterals(string source)` (xoá raw `"""…"""`, verbatim `@"…"`, và chuỗi thường `"…"` — để bỏ qua JavaScript nhúng như `catch (_) { }` trong `RobloxHbaService.BrowserHooks.cs`) và `public static IEnumerable<string> AppSourceFiles(string repoRoot)` (mọi `*.cs` dưới `src/Shiro`, trừ `/bin/`, `/obj/`, `*.g.cs`).
  - Hằng `UntypedCatchAllowList = { "AppLogger.cs" }` (logger không thể tự log chính nó).

**Quy tắc sửa (áp dụng cho Task 2 và 3):** mỗi catch phải **hoặc** bắt kiểu cụ thể (`JsonException`, `FormatException`, `IOException`, `UnauthorizedAccessException`, `Win32Exception`, `InvalidOperationException`, `HttpRequestException`, `CryptographicException`, …) **hoặc** là `catch (Exception ex)` có log bằng `AppServices.Logger.Debug("[<Class>] …", ex)`. Không chắc kiểu exception nào → dùng `catch (Exception ex)` + log (tránh crash do thu hẹp sai). Đường nóng chạy theo chu kỳ (polling, timer < 5s) → ưu tiên thu hẹp kiểu, không log. Giá trị trả về khi lỗi giữ nguyên.

- [ ] **Step 1: Viết test ratchet**

```csharp
[Fact]
public void UntypedCatchBlocks_ShouldNotExceedBaseline()
{
    const int Baseline = 22; // 44 lúc bắt đầu Task 2; Task 3 hạ về 0
    var offenders = SourceScan.AppSourceFiles(RepoRoot)
        .Where(f => !UntypedCatchAllowList.Contains(Path.GetFileName(f)))
        .SelectMany(f => Regex.Matches(SourceScan.StripStringLiterals(File.ReadAllText(f)),
                 @"\bcatch\s*\{|\bcatch\s*\(\s*(System\.)?Exception\s*\)")
            .Select(_ => Path.GetFileName(f)))
        .ToList();
    Assert.True(offenders.Count <= Baseline,
        $"Untyped catch count {offenders.Count} > {Baseline}: {string.Join(", ", offenders.GroupBy(x => x).Select(g => $"{g.Key}×{g.Count()}"))}");
}

[Fact]
public void SourceScan_StripsEmbeddedJavaScript()
{
    const string code = "var js = \"\"\"\n try { x(); } catch (_) { }\n\"\"\"; try { } catch { }";
    Assert.Single(Regex.Matches(SourceScan.StripStringLiterals(code), @"\bcatch\s*\{"));
}
```

- [ ] **Step 2: Chạy, xác nhận FAIL**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~UntypedCatchBlocks|FullyQualifiedName~SourceScan_"`
Expected: FAIL `Untyped catch count 44 > 22` (nếu Phase 4 làm số này khác 44, ghi số đo được và đặt `Baseline = <đo được> - 22`).

- [ ] **Step 3: Sửa 22 vị trí trong danh sách Files theo Quy tắc sửa.**

- [ ] **Step 4: Chạy lại test ratchet → PASS, rồi toàn bộ test → `Failed: 0`.**

- [ ] **Step 5: Commit**

```bash
git commit -am "fix(errors): type or log silent catches in Auth/Launch/Network/Storage services and ratchet untyped catches"
```

---

### Task 3: Xử lý catch không kiểu còn lại (Windows/Ui/ViewModels/Views/Classes/AccountManager)

**Files (22 vị trí):** `AvaloniaUI/Views/MessageBoxDialog.axaml.cs` (1), `Classes/Account.SessionState.cs` (1), `Classes/IniFile.cs` (1), `Classes/Presence.cs` (1), `Classes/RobloxBrowserSignup.FormAutomation.cs` (1), `Classes/RobloxProcess.LogPatterns.cs` (1), `Classes/Utilities.cs` (1), `Services/Ui/AccentThemeService.cs` (1), `Services/Ui/FlagIconService.cs` (2), `Services/Ui/LocalizationService.cs` (1), `Services/Ui/ToastService.cs` (1), `Services/Windows/MemoryOptimizer.cs` (2), `Services/Windows/ProcessExtensions.cs` (1), `Services/Windows/RobloxProcessService.cs` (1 — `catch (Exception)` dòng 82), `Services/Windows/RobloxWindowArrangerService.cs` (1), `ViewModels/CommandPaletteItemViewModel.cs` (1), `AccountManager.cs` (1), `AccountManager.Persistence.cs` (2), `AccountManager.Presence.cs` (1)
- Test: `tests/Shiro.Tests/ArchitectureTests.Hygiene.cs`

**Interfaces:** Consumes `SourceScan`, `UntypedCatchAllowList` từ Task 2.

- [ ] **Step 1: Đổi `Baseline` của `UntypedCatchBlocks_ShouldNotExceedBaseline` thành `0`**, chạy test → FAIL với 22 offender.
- [ ] **Step 2: Sửa 22 vị trí theo Quy tắc sửa ở Task 2.** Riêng `MemoryOptimizer.cs:432,450` và `ProcessExtensions.cs:59` là đường nóng → thu hẹp về `Win32Exception`/`InvalidOperationException`/`ArgumentException`, không log. `IniFile.cs:198` → `FormatException`/`InvalidCastException`/`OverflowException`.
- [ ] **Step 3: Chạy `--filter "FullyQualifiedName~UntypedCatchBlocks"` → PASS; toàn bộ test → `Failed: 0`.**
- [ ] **Step 4: Commit** — `fix(errors): eliminate remaining untyped catches and lock baseline at zero`

---

### Task 4: Xoá static facade trạng thái trên `AccountManager`

**Files:**
- Modify: `src/Shiro/AccountManager.cs:28-127` (xoá property/field static liệt kê dưới đây)
- Modify: các partial `src/Shiro/AccountManager.*.cs` dùng các member đó
- Modify: `src/Shiro/Services/Storage/AppSettings.cs` (xoá XML doc comment bị lặp phía trên `General`)
- Test: 25 file test đang tham chiếu `AccountManager.` (dùng `git grep -l "AccountManager\." tests`), `tests/Shiro.Tests/ArchitectureTests.StaticState.cs`

**Interfaces:**
- Produces (bảng thay thế — dùng cho cả src và tests):

| Xoá khỏi `AccountManager` | Thay bằng |
|---|---|
| `SelectedAccounts`, `SelectedAccount`, `LastValidAccount` | `AppServices.SelectionState.<cùng tên>` |
| `RecentGames`, `CachedServers`, `CurrentPlaceId`, `CurrentJobId` | `AppServices.ServerBrowserState.<cùng tên>` |
| `ShuffleJobID` | `AppServices.ServerBrowserState.ShuffleJobId` |
| `CurrentVersion` | `AppServices.ServerBrowserState.CurrentClientVersion` |
| `WSPassword` | `AppSettings.WebSocketPassword` |
| `IniSettings` | `AppSettings.ActiveIniFile` |
| `General` | `AppSettings.General` (kiểu `ISettingsService`; đủ `Exists`/`Get`/`Set` đang dùng) |
| `Developer`, `WebServer`, `AccountControl`, `Watcher`, `Prompts` | `AppSettings.<cùng tên>` (nullable — thêm `?.`/`!` theo ngữ cảnh) |
| `Accounts`, `AccountCount`, `GetAccountsSnapshot()` | `AppServices.AccountRepository.GetAll()` / `.Count` |
| `AddAccountThreadSafe(a)` | `AppServices.AccountRepository.AddThreadSafe(a)` |

  - `public static AccountManager? Instance;` → `public static AccountManager? Instance { get; internal set; }` (test gán 5 chỗ, có `InternalsVisibleTo`).
  - Khởi tạo trong `AccountManager`: chỉ gán `AppSettings.ActiveIniFile = <IniFile>`; **không** gán `AppSettings.General` (để `_generalOverride` null, `General` resolve `ActiveIniFile.Section("General")` — `IniFile.Section` đã cache theo tên).

- [ ] **Step 1: Viết test thất bại**

```csharp
// ArchitectureTests.StaticState.cs
[Fact]
public void AccountManager_ShouldNotExpose_StaticStateFacade()
{
    string[] forbidden = { "SelectedAccounts","RecentGames","SelectedAccount","LastValidAccount","CurrentPlaceId",
        "CurrentJobId","CachedServers","ShuffleJobID","CurrentVersion","WSPassword","IniSettings","General",
        "Developer","WebServer","AccountControl","Watcher","Prompts","Accounts","AccountCount","AddAccountThreadSafe" };
    const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    var present = typeof(AccountManager).GetMembers(flags).Select(m => m.Name).Intersect(forbidden).ToList();
    Assert.Empty(present);
}

[Fact]
public void AccountManager_Instance_IsPropertyWithNonPublicSetter()
{
    var p = typeof(AccountManager).GetProperty("Instance", BindingFlags.Static | BindingFlags.Public);
    Assert.NotNull(p);
    Assert.False(p!.SetMethod!.IsPublic);
}

// Review Focus #1 — SettingsServiceTests.cs (hoặc file test AppSettings hiện có)
[Fact]
public void AppSettingsGeneral_WritesThroughToActiveIniFileSection()
{
    var ini = new IniFile(/* temp path */);
    AppSettings.ActiveIniFile = ini;
    AppSettings.General = null!; // xoá override
    AppSettings.General.Set("SaveThrottleMs", "1234");
    Assert.Equal("1234", ini.Section("General").Get("SaveThrottleMs"));
}
```

- [ ] **Step 2: Chạy → FAIL** (`--filter "FullyQualifiedName~StaticStateFacade|FullyQualifiedName~Instance_IsProperty|FullyQualifiedName~WritesThroughToActiveIniFileSection"`).
- [ ] **Step 3: Xoá các member, áp dụng bảng thay thế trong `src/Shiro/AccountManager*.cs`.** Nếu setter `AppSettings.General` không chấp nhận `null` để xoá override, dùng API reset đã có (`AppSettings.ResetOverrides()`) trong test thay vì gán `null`.
- [ ] **Step 4: Áp dụng bảng thay thế trong tests** (khoảng 300 tham chiếu: `General` 72, `WebServer` 62, `WSPassword` 35, `ReplaceAccountsList`/`ResetAccountsForTests` giữ nguyên vì là internal test seam).
- [ ] **Step 5: Hạ budget `["AccountManager"]` trong `NoClass_ShouldExceedLineBudget_AcrossPartials`** xuống `<số dòng đo được> + 50`.
- [ ] **Step 6: Toàn bộ test → `Failed: 0`.**
- [ ] **Step 7: Commit** — `refactor(state): remove AccountManager static state facade in favor of AppSettings/AppServices owners`

---

### Task 5: Tách `AccountPersistenceService` khỏi `AccountManager`

**Files:**
- Create: `src/Shiro/Services/Storage/AccountPersistenceService.cs` (load/save/save gate/dirty tracking)
- Create: `src/Shiro/Services/Storage/AccountPersistenceService.Recovery.cs` (từ `AccountManager.BackupRecovery.cs` + `PromoteBackupToPrimary`)
- Delete: `src/Shiro/AccountManager.Persistence.cs`, `src/Shiro/AccountManager.BackupRecovery.cs`
- Modify: `src/Shiro/Services/Storage/AccountRepository.cs:280-310`, `src/Shiro/Services/AppServices.cs` (đăng ký), các partial `AccountManager.*.cs` đang gọi `SaveAccounts` (14 chỗ)
- Modify: `tests/coverage-floors.json` (`"AccountManager.Persistence.cs"` → `"Services/Storage/AccountPersistenceService.cs"`, giữ `branch: 67`), `tests/Shiro.Tests/ArchitectureTests.Ratchets.cs` (`requiredHotspots`)
- Test: các test đang dùng `IsDatabasePasswordEncrypted` (9), `LoadAccountsFromStore` (3), `accountStore` (3), `SaveAccounts*`, `TestPasswordPromptAsync`; thêm `tests/Shiro.Tests/AccountPersistenceServiceTests.cs`

**Interfaces:**
- Produces: `public sealed partial class AccountPersistenceService` (namespace `Shiro.Services`) với:
  - `public static AccountPersistenceService Default` (Lazy), ctor `internal AccountPersistenceService(AccountRepository? repository = null)`.
  - Instance members chuyển nguyên thân hàm, bỏ `static`: `bool IsVaultLockedPendingMasterPassword { get; }`, `bool IsDatabasePasswordEncrypted()`, `bool LoadAccountsFromStore(byte[]? hash = null)`, `Task SaveAccountsAsync(bool bypassRateLimit = false, bool bypassCountCheck = false)`, `void SaveAccounts(bool bypassRateLimit = false, bool bypassCountCheck = false)`, `void QueueDeferredSave()`, `bool TryRecoverOrRestoreAccounts(out string statusMessage)`, `internal string PromoteBackupToPrimary(string saveFilePath, DateTime now)`, `internal Func<string,string,Task<string?>>? TestPasswordPromptAsync`, `internal AccountStore Store`.
  - Giữ `public static`: `SaveGate EvaluateSaveGate(...)`, `List<Account> TakeDirtyAccounts(IReadOnlyList<Account>)`, `void RestoreDirty(IEnumerable<Account>)` (hàm thuần, chữ ký giữ nguyên).
  - Trạng thái chuyển theo: `accountStore`, `startTime` (gán lúc service được tạo), `lastSaveTime`, timer deferred save, `accountDataNeedsMigration` nếu chỉ persistence dùng.
- Đăng ký: `services.AddSingleton(AccountPersistenceService.Default);` trong `AppServices.EnsureInitialized`.
- `AccountManager` giữ `private AccountPersistenceService Persistence => AppServices.GetRequiredService<AccountPersistenceService>();` cho các lời gọi nội bộ.

- [ ] **Step 1: Viết test thất bại** trong `AccountPersistenceServiceTests.cs`:
  - `SaveGate_BlocksEmptyWriteDuringStartupWindow` — `EvaluateSaveGate(now: t0+2s, startTime: t0, lastSaveTime: default, …accountCount: 0…)` trả về giá trị chặn (dùng đúng enum `SaveGate` hiện có).
  - `Shutdown_FlushesDirtyAccounts_ThroughPersistenceService` — đánh dấu 1 account dirty, gọi đường shutdown của `AccountManager` (method lifecycle hiện có), assert file vault được ghi và `TakeDirtyAccounts` trả rỗng sau đó.
  - `AccountManager_ShouldNotContain_PersistenceMembers` — reflection: `typeof(AccountManager)` không còn `SaveAccounts`, `LoadAccountsFromStore`, `IsDatabasePasswordEncrypted`, `TryRecoverOrRestoreAccounts`, `QueueDeferredSave`.
- [ ] **Step 2: Chạy → FAIL.**
- [ ] **Step 3: `git mv src/Shiro/AccountManager.Persistence.cs src/Shiro/Services/Storage/AccountPersistenceService.cs`** (giữ lịch sử), đổi class/namespace, bỏ `static` theo Interfaces; gộp `BackupRecovery` vào `.Recovery.cs`. Thay `AccountRepository.Default.X` bằng field `repository` từ ctor.
- [ ] **Step 4: `AccountRepository.Save/SaveAsync/QueueDeferredSave/Load/LoadFromStore/TryRecoverOrRestore/IsPasswordEncrypted`** gọi `AccountPersistenceService` thay vì `AccountManager`.
- [ ] **Step 5: Cập nhật coverage floor + `requiredHotspots`, cập nhật test cũ.**
- [ ] **Step 6: Toàn bộ test → `Failed: 0`; chạy coverage gate:**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj -c Release --collect:"XPlat Code Coverage" --settings tests/coverage.runsettings --results-directory coverage; ./scripts/Check-Coverage.ps1 -CoverageDir coverage -MinOverall 75.0 -MinBranch 60.0 -MinCore 92.0`
Expected: script exit 0.

- [ ] **Step 7: Commit** — `refactor(storage): extract AccountPersistenceService from AccountManager`

---

### Task 6: Tách `AccountRegistrationService` và cắt vòng `AccountRepository → AccountManager`

**Files:**
- Create: `src/Shiro/Services/Storage/AccountRegistrationService.cs`
- Modify: `src/Shiro/AccountManager.Accounts.cs` (chuyển các member dưới đây ra; nếu file còn < 40 dòng thì xoá và chuyển phần còn lại vào `AccountManager.cs`)
- Modify: `src/Shiro/Services/Storage/AccountRepository.cs:172-187,269-275,315-320`, `src/Shiro/Services/AppServices.cs`
- Modify: `tests/Shiro.Tests/ArchitectureTests.cs:417-418` (bỏ ngoại lệ `AccountRepository.cs`), `tests/Shiro.Tests/ArchitectureTests.StaticState.cs`
- Modify: `tests/coverage-floors.json` (`"AccountManager.Accounts.cs"` → `"Services/Storage/AccountRegistrationService.cs"`, giữ `branch: 75`), `ArchitectureTests.Ratchets.cs` `requiredHotspots`
- Test: các test dùng `GetUserIDAsync` (14), `NotifyAccountValidity` (7), `RemoveAccountsFromDatabase` (6), `RemoveAccount` (4)

**Interfaces:**
- Produces: `public sealed class AccountRegistrationService` (namespace `Shiro.Services`), `Default` Lazy, ctor `internal AccountRegistrationService(AccountRepository? repository = null, AccountPersistenceService? persistence = null)`, instance members chuyển từ static (giữ tham số, đổi tên tham số sang camelCase):
  `Task<(bool Success, long UserId, RestResponse? Response)> GetUserIdAsync(string username, CancellationToken ct = default)`,
  `Account? AddAccount(string securityToken, string password = "", string? accountJson = null, bool saveImmediate = true)`,
  `Task<Account?> AddAccountAsync(...)` (cả hai overload hiện có, cùng tham số),
  `internal Account? RegisterValidatedAccount(Account account, string securityToken, string password = "", bool saveImmediate = true)`,
  `bool RemoveAccount(Account account)`, `int RemoveAccountsFromDatabase(IEnumerable<Account> accounts)`,
  `void NotifyAccountValidity(Account account, bool isValid)`, `internal void ClearResolvedUserIdsCacheForTesting()`.
- `AccountManager.MakeSafeFileName` → `Utilities.MakeSafeFileName(string value, string fallback)` (internal static) nếu ngoài `AccountManager` còn dùng; nếu không thì giữ nguyên.
- `AccountRepository.EnsureBackendInitialized()` → `AppServices.HostUiBridge.EnsureBackendInitialized()`.

- [ ] **Step 1: Viết test thất bại**

```csharp
[Fact]
public void ServicesAndNexus_ShouldNotDependOn_AccountManager()
{
    var result = Types.InAssembly(CoreAssembly)
        .That().ResideInNamespace("Shiro.Services").Or().ResideInNamespace("Shiro.Nexus")
        .ShouldNot().HaveDependencyOnAny("Shiro.AccountManager")
        .GetResult();
    Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>()));
}
```
  và bỏ dòng `.Where(f => !f.EndsWith("AccountRepository.cs", …))` trong test decoupling hiện có.
- [ ] **Step 2: Chạy → FAIL** (offender: `AccountRepository`).
- [ ] **Step 3: Tạo `AccountRegistrationService`** (git mv phần lớn nội dung `AccountManager.Accounts.cs` nếu phù hợp), đăng ký `services.AddSingleton(AccountRegistrationService.Default);`.
- [ ] **Step 4: `AccountRepository` chuyển lời gọi** sang `AccountRegistrationService`/`AccountPersistenceService`/`HostUiBridge`. Lời gọi nội bộ trong `AccountManager` đi qua `AppServices.GetRequiredService<AccountRegistrationService>()`.
- [ ] **Step 5: Cập nhật test cũ + coverage floor; hạ budget `["AccountManager"]` xuống `<đo được> + 50`.**
- [ ] **Step 6: Toàn bộ test + coverage gate (lệnh ở Task 5 Step 6) → PASS.**
- [ ] **Step 7: Commit** — `refactor(storage): extract AccountRegistrationService and break AccountRepository→AccountManager cycle`

---

### Task 7: State service chỉ được resolve qua `AppServices` (composition root)

**Files:**
- Modify: mọi file dùng `SelectionStateService.Default` (7), `ServerBrowserStateService.Default` (18), `AccountWorkflowService.Default` (18), `HostUiBridgeService.Default` (15), `AccountRepository.Default` (9) — lấy danh sách bằng `git grep -n -E "\b(SelectionStateService|ServerBrowserStateService|AccountWorkflowService|HostUiBridgeService|AccountRepository)\.Default\b" src`
- Modify: `src/Shiro/Services/Storage/AppSettings.cs` (các property forward state)
- Test: `tests/Shiro.Tests/ArchitectureTests.StaticState.cs`, `tests/Shiro.Tests/ViewModelIntegrationTests.cs`

**Interfaces:**
- Consumes: accessor có sẵn `AppServices.SelectionState`, `.ServerBrowserState`, `.AccountWorkflowService`, `.HostUiBridge`, `.AccountRepository`.
- Allow-list được phép tham chiếu `.Default`: `AppServices.cs`, file khai báo chính type đó, `AccountPersistenceService*.cs`, `AccountRegistrationService.cs` (cần member `internal` của `AccountRepository` như `SyncRoot`, `ReplaceAll`, `FindFirst`, `RemoveMatching` — không đưa lên `IAccountRepository`).

- [ ] **Step 1: Viết test thất bại**

```csharp
[Fact]
public void StateServiceDefaults_ShouldOnlyBeReferenced_ByCompositionRoot()
{
    var allow = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "AppServices.cs", "SelectionStateService.cs", "ServerBrowserStateService.cs", "AccountWorkflowService.cs",
        "HostUiBridgeService.cs", "AccountRepository.cs", "AccountPersistenceService.cs",
        "AccountPersistenceService.Recovery.cs", "AccountRegistrationService.cs" };
    var offenders = SourceScan.AppSourceFiles(RepoRoot)
        .Where(f => !allow.Contains(Path.GetFileName(f)))
        .Where(f => Regex.IsMatch(SourceScan.StripStringLiterals(File.ReadAllText(f)),
            @"\b(SelectionStateService|ServerBrowserStateService|AccountWorkflowService|HostUiBridgeService|AccountRepository)\.Default\b"))
        .Select(Path.GetFileName).ToList();
    Assert.Empty(offenders);
}

// Review Focus #5
[Fact]
public void SelectionStateOverride_IsObservedByAppSettingsFacade()
{
    var fake = Substitute.For<ISelectionStateService>();
    var acc = new Account(/* test ctor dùng trong các test hiện có */);
    fake.SelectedAccount.Returns(acc);
    using (AppServices.Override<ISelectionStateService>(fake))
        Assert.Same(acc, AppSettings.SelectedAccount);
}
```
- [ ] **Step 2: Chạy → FAIL** với danh sách offender.
- [ ] **Step 3: Thay mọi `X.Default` ngoài allow-list bằng accessor `AppServices.*`.** Nếu một call site dùng member không có trên interface (ví dụ `AccountRepository.Default.FindFirst`) và file đó không thuộc allow-list, chuyển logic đó vào `AccountRegistrationService`/`AccountPersistenceService` thay vì mở rộng interface.
- [ ] **Step 4: Toàn bộ test → `Failed: 0`.** Nếu test cũ hỏng do giờ thấy `Override` đang hoạt động, sửa test để dispose scope đúng thứ tự LIFO, không sửa `AppServices`.
- [ ] **Step 5: Commit** — `refactor(di): resolve state services only through AppServices composition root`

---

### Task 8: Dời thư mục `Classes/` vào `Domain/` và `Services/*`

**Files (đều dùng `git mv`):**

| Từ `src/Shiro/Classes/` | Đến | Namespace sau khi dời |
|---|---|---|
| `Account.cs`, `Account.*.cs`, `AccountJson.cs`, `Avatar.cs`, `GameClass.cs`, `Server.cs`, `Utilities.cs` | `src/Shiro/Domain/` | `Shiro` (giữ nguyên) |
| `AccountSearchHelper.cs`, `NaturalStringComparer.cs` | `src/Shiro/Domain/` | `Shiro.Classes` → `Shiro` |
| `AccountBrowser*.cs`, `ChromiumHomeBrowser*.cs`, `RobloxBrowserLogin*.cs`, `RobloxBrowserSignup*.cs` | `src/Shiro/Services/Browser/` | `Shiro.Classes` → `Shiro.Services` |
| `RobloxProcess*.cs`, `RobloxWatcher*.cs` | `src/Shiro/Services/Windows/` | `Shiro.Classes` → `Shiro.Services` |
| `Batch.cs`, `ClientSettingsPatcher.cs` | `src/Shiro/Services/Launch/` | `Shiro.Classes` → `Shiro.Services` |
| `Presence.cs` | `src/Shiro/Services/Network/` | `Shiro.Classes` → `Shiro.Services` |
| `Cryptography.cs` | `src/Shiro/Services/Storage/` | `Shiro.Classes` → `Shiro.Services` |
| `IniFile.cs` | `src/Shiro/Services/Storage/` | `Shiro` (giữ nguyên) |
| `Program.cs` | `src/Shiro/Program.cs` | `Shiro` (giữ nguyên) |
| File do Phase 4 thêm (ví dụ `InMemorySecretProtector.cs`) | `src/Shiro/Services/Storage/` | → `Shiro.Services` |

- Modify: mọi `using Shiro.Classes;` trong `src` và `tests` → xoá, thêm `using Shiro.Services;` nếu file chưa có.
- Modify: `tests/Shiro.Tests/ArchitectureTests.cs:415,420` (`classesDir` → scan `src/Shiro/Domain`; `Directory.GetFiles` sẽ ném nếu thư mục không tồn tại), các ratchet line-budget/path do Phase 4 thêm có chứa `Classes/`.
- Modify: `docs/architecture.md` (cây thư mục).
- Test: `tests/Shiro.Tests/ArchitectureTests.Hygiene.cs`

**Interfaces:** Không đổi tên type hay chữ ký nào. Chỉ đổi đường dẫn và namespace `Shiro.Classes`.

- [ ] **Step 1: Viết test thất bại**

```csharp
[Fact]
public void LegacyClassesFolder_ShouldNotExist() =>
    Assert.False(Directory.Exists(Path.Combine(RepoRoot, "src", "Shiro", "Classes")));

[Fact]
public void NoTypes_ShouldLiveIn_ShiroClassesNamespace() =>
    Assert.Empty(CoreAssembly.GetTypes().Where(t => t.Namespace == "Shiro.Classes").Select(t => t.FullName));
```
- [ ] **Step 2: Chạy → FAIL.**
- [ ] **Step 3: Kiểm tra trước khi đổi namespace:** `git grep -n "\"Shiro.Classes" src tests` phải không có tên type nào bị lưu dạng chuỗi (reflection/serialization). Nếu có, giữ type đó ở `Shiro.Classes` và ghi lý do vào ledger.
- [ ] **Step 4: `git mv` theo bảng, sửa namespace và `using`.** Build: `dotnet build Shiro.sln -c Release --nologo` → `0 Error(s)`. Lỗi mơ hồ tên `Cryptography` với `System.Security.Cryptography` → dùng tên đầy đủ tại call site, không đổi tên class.
- [ ] **Step 5: Toàn bộ test → `Failed: 0`.**
- [ ] **Step 6: Commit** — `refactor(structure): relocate legacy Classes folder into Domain and Services and retire Shiro.Classes namespace`

---

### Task 9: Dọn repo, comment tiếng Anh, tài liệu và chấm lại điểm

**Files:**
- Modify: `src/Shiro/ViewModels/MainViewModel.Filtering.cs` (dịch 5 dòng comment tiếng Việt)
- Modify: `tests/Shiro.Tests/ArchitectureTests.Hygiene.cs`
- Untrack: `.superpowers/sdd/2026-11-01-native-aot-and-source-gen/progress.md` (`.superpowers/` đã có trong `.gitignore` dòng 336)
- Move: `docs/superpowers/plans/2026-11-0[1-7]-*.md` → `docs/superpowers/plans/archive/`; `docs/history/UPGRADE_NOTES.md` → `docs/archive/UPGRADE_NOTES.md`
- Modify: `CONTRIBUTING.md` (thêm mục "Release build" chứa lệnh publish đang được tham chiếu trong comment `Shiro.csproj`), `src/Shiro/Shiro.csproj` (comment `OutputPath`: `UPGRADE_NOTES` → `CONTRIBUTING.md`)
- Modify: `docs/architecture.md` (service mới, cây thư mục mới, mục "Platform scope & accepted risks")
- Modify: `CHANGELOG.md` (mục `Unreleased`)

**Interfaces:** Consumes `SourceScan` từ Task 2.

- [ ] **Step 1: Viết test thất bại**

```csharp
[Fact]
public void CSharpComments_ShouldNotContainVietnameseDiacritics()
{
    var vn = new Regex(@"[\u00C0-\u00FF\u0102\u0103\u0110\u0111\u0128\u0129\u0168\u0169\u01A0\u01A1\u01AF\u01B0\u1EA0-\u1EF9]");
    var comment = new Regex(@"//[^\r\n]*|/\*[\s\S]*?\*/");
    var offenders = SourceScan.AppSourceFiles(RepoRoot)
        .Where(f => comment.Matches(SourceScan.StripStringLiterals(File.ReadAllText(f))).Any(m => vn.IsMatch(m.Value)))
        .Select(Path.GetFileName).ToList();
    Assert.Empty(offenders);
}
```
- [ ] **Step 2: Chạy → FAIL** với `MainViewModel.Filtering.cs` (`WebAuthPolicy.cs` đã sửa ở Task 1).
- [ ] **Step 3: Dịch comment trong `MainViewModel.Filtering.cs`; chạy lại → PASS.**
- [ ] **Step 4: Dọn repo**

```bash
git rm --cached .superpowers/sdd/2026-11-01-native-aot-and-source-gen/progress.md
mkdir docs/superpowers/plans/archive docs/archive
git mv docs/superpowers/plans/2026-11-0[1-7]-*.md docs/superpowers/plans/archive/
git mv docs/history/UPGRADE_NOTES.md docs/archive/UPGRADE_NOTES.md
```
  Verify: `git ls-files .superpowers` → rỗng; `git grep -n "UPGRADE_NOTES" -- ':!docs/archive'` → chỉ còn link trỏ tới `docs/archive/UPGRADE_NOTES.md` (sửa mọi link khác).
- [ ] **Step 5: Cập nhật `CONTRIBUTING.md`, `Shiro.csproj` comment, `docs/architecture.md`.** Mục "Platform scope & accepted risks" ghi rõ: chỉ hỗ trợ Windows x64 do phụ thuộc Win32 (DPAPI, WebView2, job object, WASAPI, tray) được cô lập trong `Services/Windows`; rủi ro ToS của Roblox (multi-client, cookie, anti-AFK) là **rủi ro sản phẩm được chấp nhận**, đã cảnh báo trong README — không có task code.
- [ ] **Step 6: `CHANGELOG.md` → `Unreleased`**: liệt kê Task 1–8 (một dòng mỗi task), ghi rõ thay đổi hành vi duy nhất: body HTTP 500 giờ là `"Internal server error"`.
- [ ] **Step 7: Xác minh cuối**

Run:
```powershell
dotnet build Shiro.sln -c Release --nologo
dotnet test tests/Shiro.Tests/Shiro.Tests.csproj -c Release --collect:"XPlat Code Coverage" --settings tests/coverage.runsettings --results-directory coverage
./scripts/Check-Coverage.ps1 -CoverageDir coverage -MinOverall 75.0 -MinBranch 60.0 -MinCore 92.0
dotnet publish src/Shiro/Shiro.csproj -c Release -r win-x64 --self-contained true -o build/Release /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:EnableCompressionInSingleFile=true
```
Expected: `0 Error(s)`; `Failed: 0`; coverage script exit 0; tồn tại `build/Release/Shiro.exe`. Khởi chạy `Shiro.exe` một lần: mở được vault, danh sách tài khoản hiển thị, đổi một setting rồi khởi động lại thấy setting được giữ.

- [ ] **Step 8: Chấm lại điểm** — chạy lại các lệnh đo của audit ([audit.ps1](file:///C:/Users/lenovo/.gemini/antigravity/brain/eb67fe7d-db01-41ea-bb42-dd7fd38a8fee/scratch/audit.ps1)) và ghi bảng trước/sau vào ledger: static member public/internal của `AccountManager`, số `.Default` ngoài allow-list, untyped catch, comment tiếng Việt, số file trong `Classes/`, file `.superpowers` được track.

- [ ] **Step 9: Commit** — `chore(repo): archive historical plans, untrack agent artifacts, enforce English comments and document platform scope`

---

## Bảng ánh xạ vấn đề → task

| # | Nhược điểm trong đánh giá | Task | Ratchet chống tái phát |
|---|---|---|---|
| 1 | Static facade / service locator `.Default` | 4, 7 | `AccountManager_ShouldNotExpose_StaticStateFacade`, `StateServiceDefaults_ShouldOnlyBeReferenced_ByCompositionRoot` |
| 2 | `AccountManager` god class | 5, 6 | Line budget `["AccountManager"]` hạ 2 lần; `AccountManager_ShouldNotContain_PersistenceMembers` |
| 3 | `WebAuthPolicy` → `AccountApiDispatcher` (ngược tầng) | 1 | `WebAuthPolicy_ShouldNotDependOn_AccountApiDispatcher` |
| 4 | 44 catch nuốt lỗi (+3 `catch (Exception)` không biến) | 2, 3 | `UntypedCatchBlocks_ShouldNotExceedBaseline` = 0 |
| 5 | Comment trộn tiếng Việt | 1, 9 | `CSharpComments_ShouldNotContainVietnameseDiacritics` |
| 6 | Repo phình (`docs/history`, `.superpowers`) | 9 | Bước verify `git ls-files .superpowers` |
| 7 | Thư mục `Classes/` legacy | 8 | `LegacyClassesFolder_ShouldNotExist`, `NoTypes_ShouldLiveIn_ShiroClassesNamespace` |
| 8 | Chỉ chạy Windows / rủi ro ToS | 9 (tài liệu) | — (rủi ro được chấp nhận) |
| + | Vòng `AccountRepository → AccountManager`, Nexus đọc static | 1, 6 | `ServicesAndNexus_ShouldNotDependOn_AccountManager` |
| + | HTTP 500 lộ exception message | 1 | `BlockUserEndpoint_WhenAccountThrows_Returns500WithoutExceptionMessage` |
