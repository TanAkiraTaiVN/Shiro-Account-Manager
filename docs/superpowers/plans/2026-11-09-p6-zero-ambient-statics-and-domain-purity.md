# Phase 6 — Zero Ambient Statics, Pure Domain, Async-Void Elimination & Test Suite Hygiene Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Giải quyết triệt để 6 khuyết điểm còn lại sau Phase 5 (5 hàm `async void` trong UI, phụ thuộc ngược của `Domain/` vào ViewModels/Win32 và bản sao `Utilities.FastJson`, 539 điểm truy cập biến tĩnh `Program.Logger` / `AppSettings.*` ở 6 thư mục, kích thước partial class của `AccountManager` / `MainViewModel`, và 6 file test vượt ngưỡng 550 dòng), khóa lại bằng các ratchet test ở mức `0` để đưa dự án từ **9,2/10 lên 9,8–10/10**.

**Architecture:** Tiến hành theo 8 task độc lập, mỗi task có chu trình TDD (Red → Green → Commit) và ratchet tự động:
1. Loại bỏ 5 hàm `async void` cuối cùng trong `FastLoginDialog.axaml.cs` → khóa `AsyncVoidHandlers_InViews_ShouldNotExceedBaseline` ở `0`.
2. Làm sạch `src/Shiro/Domain/` (tách extension `AccountViewModel` khỏi `AccountSearchHelper`, xóa `Utilities.FastJson` trùng lặp, chuyển `Utilities.cs` sang `Services/Ui/`) → khóa `DomainFolder_ShouldNotDependOn_ViewModelsOrAvaloniaOrWin32Registry`.
3. Đưa `Program.Logger` và `AppSettings.*` về `(0, 0)` trong `Domain/`, `Services/Ui/`, `AvaloniaUI/Views/`.
4. Đưa `Program.Logger` và `AppSettings.*` về `(0, 0)` trong `Services/Browser/` và `Services/Windows/`.
5. Đưa `Program.Logger` và `AppSettings.*` về `(0, 0)` trong `Services/Launch/` → toàn bộ 10 thư mục trong `AmbientStaticAccess_ShouldNotExceedPerFolderBaseline` đạt `(0, 0)`.
6. Tách `AccountSessionMaintenanceService` khỏi `AccountManager` để đưa tổng dòng `AccountManager` xuống dưới ngưỡng mặc định `1150` dòng (xóa ngoại lệ `["AccountManager"]` khỏi `NoClass_ShouldExceedLineBudget_AcrossPartials`).
7. Tách bộ lọc danh sách tài khoản (`AccountListFilterController`) khỏi `MainViewModel` và siết chặt ngân sách dòng của `MainViewModel`, `Account`, `ServerListViewModel`.
8. Chia nhỏ 6 file test `> 550` dòng trong `tests/Shiro.Tests/` và khóa bằng ratchet `NoTestFiles_ShouldExceed550Lines`.

**Tech Stack:** .NET 10 (`net10.0-windows10.0.19041.0`, win-x64), C# latest, Avalonia 11.3, Microsoft.Extensions.DependencyInjection, xUnit 2.9, NSubstitute, NetArchTest.eNhancedEdition, Avalonia.Headless.XUnit, coverlet.

**Spec:** Phân tích 6 khuyết điểm còn lại sau Phase 5 trên nhánh `main` (`962f882`) ngày 2026-11-09.

## Global Constraints

- Không thêm NuGet package mới; không đổi `Directory.Packages.props`, `global.json`.
- Mọi file `.cs` trong `src/Shiro` phải `<= 485` dòng (`NoSourceCodeFiles_ShouldExceed500Lines`). Mọi file `.cs` trong `tests/Shiro.Tests` sau Task 8 phải `<= 550` dòng (`NoTestFiles_ShouldExceed550Lines`).
- Comment, XML doc, log message và exception message trong C#: **chỉ tiếng Anh** (`CSharpComments_ShouldNotContainVietnameseDiacritics`).
- Khi sửa file có UTF-8 BOM, giữ nguyên 1 BOM duy nhất (không tạo double BOM).
- Coverage gate không đổi: overall line `>= 75%`, branch `>= 60%`, core `>= 92%`, và mọi file trong `tests/coverage-floors.json` giữ nguyên hoặc tăng branch floor.
- Lệnh chạy toàn bộ test: `dotnet test Shiro.sln -c Release --nologo` (`Failed: 0`).
- Commit theo Conventional Commits ở cuối mỗi task.

## Review Focus

1. **Sự kiện WebView2 trong `FastLoginDialog` sau khi bỏ `async void`** — mọi ngoại lệ phát sinh trong `HandleWebViewInitializedAsync`, `HandleSourceChangedAsync`, `HandleNavigationCompletedAsync`, `HandlePollTimerTickAsync`, `HandleRefillClickedAsync` vẫn phải được bắt trong `try/catch` nội bộ để không tạo `UnobservedTaskException`; kiểm chứng ở Task 1.
2. **Tìm kiếm tài khoản (`AccountSearchQuery`) sau khi tách khỏi `AccountViewModel`** — cả `AccountSearchQuery.IsMatch(Account)` lẫn extension `AccountSearchQuery.IsMatch(AccountViewModel)` phải cho kết quả khớp chính xác với dải số (`1-10`), hậu tố (`#7`), UserId (`>= 4` chữ số) và chuỗi con; kiểm chứng ở Task 2.
3. **Đọc/ghi cấu hình qua `AppServices.*Settings` khi `AppSettings.ActiveIniFile` thay đổi trong test** — các wrapper `AppServices.PromptsSettings`, `AppServices.DeveloperSettings`, `AppServices.WebServerSettings`, `AppServices.AccountControlSettings` phải đọc/ghi trực tiếp vào `AppSettings.ActiveIniFile` hiện hành (không cache `IniSection` cũ khi `AppSettings.ActiveIniFile` được gán lại); kiểm chứng ở Task 3 (`SectionSettings_ReflectActiveIniFileChanges`).
4. **Hành vi `ImportBulkUserPassAsync` và `RefreshStaleCookiesAsync` sau khi tách sang `AccountSessionMaintenanceService`** — các test hiện hữu gọi qua `AccountManager` vẫn hoạt động nhờ ủy quyền mỏng 1 dòng và tôn trọng `lifetimeCancellation`; kiểm chứng ở Task 6.
5. **Bảo toàn số lượng test sau khi tách 6 file test lớn** — tổng số bài test (`>= 1911` + các bài test mới của Phase 6) không được giảm bất kỳ `[Fact]` hay `[Theory]` nào khi tách file ở Task 8.

---

## File Structure

| Đường dẫn | Trách nhiệm | Task |
|---|---|---|
| `src/Shiro/AvaloniaUI/Views/FastLoginDialog.axaml.cs` | Thay 5 hàm `async void` bằng event handler đồng bộ gọi `async Task` | 1 |
| `tests/Shiro.Tests/ArchitectureTests.Ratchets.cs` | Hạ `AsyncVoidHandlers_InViews_ShouldNotExceedBaseline` từ `5` → `0` | 1 |
| `src/Shiro/Domain/AccountSearchHelper.cs` | Chỉ phụ thuộc `Account` và primitive; bỏ `using Shiro.ViewModels;` | 2 |
| `src/Shiro/ViewModels/AccountSearchViewModelExtensions.cs` (mới) | Extension `IsMatch(this AccountSearchQuery, AccountViewModel?)` | 2 |
| `src/Shiro/Services/Ui/Utilities.cs` (dời từ `Domain/Utilities.cs`) | Bỏ `Utilities.FastJson` trùng lặp; giữ các helper UI/Win32/Process | 2 |
| `src/Shiro/Services/AppServices.cs` | Bổ sung `PromptsSettings`, `DeveloperSettings`, `WebServerSettings`, `AccountControlSettings` | 3 |
| `src/Shiro/Services/Auth/AccountSessionMaintenanceService.cs` (mới) | Tách `RefreshStaleCookiesAsync`, `ImportBulkUserPassAsync`, `MakeSafeFileName` khỏi `AccountManager` | 6 |
| `src/Shiro/ViewModels/AccountListFilterController.cs` (mới) | Tách thuật toán lọc & sắp xếp danh sách tài khoản khỏi `MainViewModel.Filtering.cs` | 7 |
| `tests/Shiro.Tests/ArchitectureTests.Layering.cs` & `.Budgets.cs` (mới) | Tách `ArchitectureTests.cs` (997 dòng) thành các file `< 485` dòng | 8 |

---

### Task 1: Loại bỏ 5 hàm `async void` cuối cùng trong `FastLoginDialog` và khóa baseline ở `0`

**Files:**
- Modify: `src/Shiro/AvaloniaUI/Views/FastLoginDialog.axaml.cs:120-269`
- Modify: `tests/Shiro.Tests/ArchitectureTests.Ratchets.cs:13-27`
- Test: `tests/Shiro.Tests/ArchitectureTests.Ratchets.cs`

**Interfaces:**
- Không đổi chữ ký public/internal. 5 handler sự kiện trong `FastLoginDialog.axaml.cs` trở thành `private void` ủy quyền sang `private async Task`:
  - `private void OnWebViewInitialized(object sender, WebView2InitializedEventArgs e) => _ = HandleWebViewInitializedAsync(e);`
  - `private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e) => _ = HandleSourceChangedAsync();`
  - `private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e) => _ = HandleNavigationCompletedAsync(e);`
  - `private void OnPollTimerTick(object? sender, EventArgs e) => _ = HandlePollTimerTickAsync();`
  - `private void OnRefillClicked(object sender, RoutedEventArgs e) => _ = HandleRefillClickedAsync();`

- [ ] **Step 1: Đặt `Baseline = 0` trong `AsyncVoidHandlers_InViews_ShouldNotExceedBaseline` (`tests/Shiro.Tests/ArchitectureTests.Ratchets.cs:15`)**

```csharp
[Fact]
public void AsyncVoidHandlers_InViews_ShouldNotExceedBaseline()
{
    const int Baseline = 0;
```

- [ ] **Step 2: Chạy test để xác nhận FAIL (actual = 5 > 0)**

Run: `dotnet test Shiro.sln -c Release --nologo --filter "FullyQualifiedName~AsyncVoidHandlers_InViews_ShouldNotExceedBaseline"`
Expected: FAIL với `"async void count in AvaloniaUI (5) exceeds baseline (0)."`

- [ ] **Step 3: Chuyển 5 hàm `async void` trong `src/Shiro/AvaloniaUI/Views/FastLoginDialog.axaml.cs` sang `private void` + `private async Task`**

Giữ nguyên toàn bộ khối `try/catch/finally` bên trong mỗi hàm `Handle*Async` để mọi ngoại lệ đều được xử lý an toàn.

- [ ] **Step 4: Chạy lại test để xác nhận PASS**

Run: `dotnet test Shiro.sln -c Release --nologo --filter "FullyQualifiedName~AsyncVoidHandlers_InViews_ShouldNotExceedBaseline|FullyQualifiedName~FastLogin"`
Expected: PASS (`Failed: 0`).

- [ ] **Step 5: Commit**

```bash
git add src/Shiro/AvaloniaUI/Views/FastLoginDialog.axaml.cs tests/Shiro.Tests/ArchitectureTests.Ratchets.cs
git commit -m "refactor(ui): replace remaining async void handlers in FastLoginDialog and lock baseline at zero"
```

---

### Task 2: Làm sạch `src/Shiro/Domain/` và xóa bản sao `Utilities.FastJson`

**Files:**
- Modify: `src/Shiro/Domain/Account.cs:1-5` (xóa `using Microsoft.Win32;`)
- Modify: `src/Shiro/Domain/AccountSearchHelper.cs` (xóa `using Shiro.ViewModels;`, đổi `IsMatch` sang nhận `(string? username, string? alias, string? group, long userId)` và overload `Account?`)
- Create: `src/Shiro/ViewModels/AccountSearchViewModelExtensions.cs`
- Move: `src/Shiro/Domain/Utilities.cs` → `src/Shiro/Services/Ui/Utilities.cs` (`git mv`, giữ `namespace Shiro`, xóa lớp lồng `Utilities.FastJson`)
- Modify: Các file gọi `Utilities.FastJson.*` trong `src/` và `tests/` → đổi sang `FastJson.*` (trong `namespace Shiro.Services`)
- Modify: `tests/Shiro.Tests/ArchitectureTests.Hygiene.cs`, `tests/Shiro.Tests/ArchitectureTests.cs` (`Services_ShouldFollowNamingConvention` thêm `"Utilities"`, cập nhật `AmbientStaticAccess_ShouldNotExceedPerFolderBaseline` cho `Domain` và `Services/Ui`)
- Test: `tests/Shiro.Tests/ArchitectureTests.Hygiene.cs`, `tests/Shiro.Tests/AccountSearchQueryTests.cs`

**Interfaces:**
- Produces:
  - `public bool AccountSearchQuery.IsMatch(string? username, string? alias, string? group, long userId)`
  - `public bool AccountSearchQuery.IsMatch(Account? acc)`
  - `public static bool AccountSearchHelper.IsMatch(Account? acc, string? query)`
  - Trong `src/Shiro/ViewModels/AccountSearchViewModelExtensions.cs` (`namespace Shiro.ViewModels`):
    - `public static bool IsMatch(this AccountSearchQuery query, AccountViewModel? acc)`
    - `public static bool IsMatch(AccountViewModel? acc, string? query)`

- [ ] **Step 1: Viết 2 test thất bại trong `tests/Shiro.Tests/ArchitectureTests.Hygiene.cs`**

```csharp
[Fact]
public void DomainFolder_ShouldNotDependOn_ViewModelsOrAvaloniaOrWin32Registry()
{
    var domainDir = Path.Combine(RepoRoot, "src", "Shiro", "Domain");
    var forbidden = new Regex(@"\b(Shiro\.ViewModels|Shiro\.AvaloniaUI|Avalonia|Microsoft\.Win32)\b");
    var offenders = Directory.GetFiles(domainDir, "*.cs", SearchOption.AllDirectories)
        .Where(f => forbidden.IsMatch(SourceScan.StripStringLiterals(File.ReadAllText(f))))
        .Select(Path.GetFileName)
        .ToList();
    Assert.Empty(offenders);
}

[Fact]
public void FastJson_ShouldHaveSingleDefinition()
{
    var types = CoreAssembly.GetTypes().Where(t => t.Name == "FastJson").Select(t => t.FullName).ToList();
    Assert.Single(types);
}
```

- [ ] **Step 2: Chạy test để xác nhận FAIL**

Run: `dotnet test Shiro.sln -c Release --nologo --filter "FullyQualifiedName~DomainFolder_ShouldNotDependOn|FullyQualifiedName~FastJson_ShouldHaveSingleDefinition"`
Expected: FAIL (`Account.cs`, `AccountSearchHelper.cs`, `Utilities.cs` và 2 type `FastJson`).

- [ ] **Step 3: Thực hiện tách `AccountSearchViewModelExtensions`, xóa `using Microsoft.Win32;` trong `Account.cs`, xóa `Utilities.FastJson` và `git mv src/Shiro/Domain/Utilities.cs src/Shiro/Services/Ui/Utilities.cs`**

1. Trong `src/Shiro/Domain/AccountSearchHelper.cs`: thay `IsMatch(AccountViewModel? acc)` bằng `IsMatch(string? username, string? alias, string? group, long userId)` và `IsMatch(Account? acc) => acc != null && IsMatch(acc.Username, acc.Alias, acc.Group, acc.UserID);`.
2. Tạo `src/Shiro/ViewModels/AccountSearchViewModelExtensions.cs` chứa extension `public static bool IsMatch(this AccountSearchQuery query, AccountViewModel? acc) => query != null && acc != null && query.IsMatch(acc.Username, acc.Alias, acc.Group, acc.UserId);`.
3. Đổi mọi `Utilities.FastJson.` trong `src/` và `tests/` thành `FastJson.` (thêm `using Shiro.Services;` nếu file chưa có), xóa lớp lồng `public static class FastJson` khỏi `Utilities.cs`.
4. `git mv src/Shiro/Domain/Utilities.cs src/Shiro/Services/Ui/Utilities.cs` (cập nhật `Services_ShouldFollowNamingConvention` và `AmbientStaticAccess_ShouldNotExceedPerFolderBaseline` trong `ArchitectureTests.cs` cho 2 dòng `Program.Logger` và 4 dòng `AppSettings.` đi theo `Utilities.cs` từ `Domain` sang `Services/Ui`).

- [ ] **Step 4: Chạy toàn bộ test để xác nhận PASS**

Run: `dotnet test Shiro.sln -c Release --nologo`
Expected: PASS (`Failed: 0`).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "refactor(domain): purify Domain folder, extract AccountSearchViewModelExtensions, and remove duplicate Utilities.FastJson"
```

---

### Task 3: Đưa `Program.Logger` và `AppSettings.*` về `(0, 0)` trong `Domain/`, `Services/Ui/`, và `AvaloniaUI/Views/`

**Files:**
- Modify: `src/Shiro/Services/AppServices.cs` (thêm `PromptsSettings`, `DeveloperSettings`, `WebServerSettings`, `AccountControlSettings`, `QueueDeferredSaveSettings`)
- Modify: Các file trong `src/Shiro/Domain/` (`Account.*.cs`, `GameClass.cs`)
- Modify: Các file trong `src/Shiro/Services/Ui/` (`AccentThemeService.cs`, `AvaloniaDialogService.cs`, `FlagIconService.cs`, `LocalizationService.cs`, `ToastService.cs`, `Utilities.cs`)
- Modify: Các file trong `src/Shiro/AvaloniaUI/Views/` (`AccountFieldsDialog.axaml.cs`, `AccountUtilsDialog.axaml.cs`, `BulkImportDialog.axaml.cs`, `CookieImportDialog.axaml.cs`, `DashboardView.axaml.cs`, `FastLoginDialog*.cs`, `MainWindow*.cs`, `ServerListView.axaml.cs`, `SettingsDialog.axaml.cs`, `ThemeEditorDialog.axaml.cs`)
- Modify: `tests/Shiro.Tests/ArchitectureTests.cs` (`AmbientStaticAccess_ShouldNotExceedPerFolderBaseline`)
- Test: `tests/Shiro.Tests/AppSettingsBehaviorTests.cs`, `tests/Shiro.Tests/ArchitectureTests.cs`

**Interfaces:**
- Produces trên `AppServices` (`src/Shiro/Services/AppServices.cs`):
  - `public static ISettingsService PromptsSettings => new DelegatingSectionSettingsService(() => AppSettings.Prompts);`
  - `public static ISettingsService DeveloperSettings => new DelegatingSectionSettingsService(() => AppSettings.Developer);`
  - `public static ISettingsService WebServerSettings => new DelegatingSectionSettingsService(() => AppSettings.WebServer);`
  - `public static ISettingsService AccountControlSettings => new DelegatingSectionSettingsService(() => AppSettings.AccountControl);`
  - `public static void QueueDeferredSaveSettings() => AppSettings.QueueDeferredSave();`

- [ ] **Step 1: Viết test `SectionSettings_ReflectActiveIniFileChanges` trong `tests/Shiro.Tests/AppSettingsBehaviorTests.cs` và đặt `["Domain"] = (0, 0)`, `["Services/Ui"] = (0, 0)`, `["AvaloniaUI/Views"] = (0, 0)` trong `ArchitectureTests.cs`**

```csharp
[Fact]
public void SectionSettings_ReflectActiveIniFileChanges()
{
    var prev = AppSettings.ActiveIniFile;
    try
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"shiro-sec-{Guid.NewGuid():N}.ini");
        AppSettings.ActiveIniFile = new IniFile(tempPath);
        AppServices.PromptsSettings.Set("TestPromptKey", "true");
        Assert.True(AppSettings.Prompts.Get<bool>("TestPromptKey"));
        File.Delete(tempPath);
    }
    finally
    {
        AppSettings.ActiveIniFile = prev;
    }
}
```

- [ ] **Step 2: Chạy test để xác nhận FAIL**

Run: `dotnet test Shiro.sln -c Release --nologo --filter "FullyQualifiedName~AmbientStaticAccess_ShouldNotExceedPerFolderBaseline|FullyQualifiedName~SectionSettings_ReflectActiveIniFileChanges"`
Expected: FAIL.

- [ ] **Step 3: Thêm các accessor trên `AppServices` và thay thế toàn bộ `Program.Logger` → `AppServices.Logger`, `AppSettings.*` → `AppServices.*` trong `Domain/`, `Services/Ui/`, `AvaloniaUI/Views/`**

Ánh xạ chuẩn:
- `Program.Logger` → `AppServices.Logger`
- `AppSettings.General` → `AppServices.GeneralSettings`
- `AppSettings.Prompts` → `AppServices.PromptsSettings`
- `AppSettings.Watcher` → `AppServices.WatcherSettings`
- `AppSettings.Developer` → `AppServices.DeveloperSettings`
- `AppSettings.Save()` → `AppServices.SaveSettings()`
- `AppSettings.QueueDeferredSave()` → `AppServices.QueueDeferredSaveSettings()`
- `AppSettings.LastValidAccount` / `SelectedAccount` / `SelectedAccounts` → `AppServices.SelectionState.*`
- `AppSettings.CurrentPlaceId` / `CachedServers` / `CurrentClientVersion` / `ShuffleJobId` / `LoadClientVersionAsync` → `AppServices.ServerBrowserState.*`
- `AppSettings.SetDashboardStatus(...)` → `AppServices.HostUiBridge.NotifyWindowStatus(...)`
- `AppSettings.RefreshAccountView(...)` → `AppServices.HostUiBridge.RefreshAccountView(...)`
- `AppSettings.UpdateAccountValidityOnUi(...)` → `AppServices.HostUiBridge.UpdateAccountValidityOnUi(...)`
- `AppSettings.SchedulePresenceRefresh(...)` → `AppServices.HostUiBridge.SchedulePresenceRefresh(...)`
- `AppSettings.SignalNextLaunchAccount()` → `AppServices.HostUiBridge.SignalNextLaunchAccount()`
- `AppSettings.CancelLaunching()` → `AppServices.HostUiBridge.CancelLaunching()`
- `AppSettings.LaunchAppHomeFromHostAsync(...)` → `AppServices.HostUiBridge.LaunchAppHomeAsync(...)`
- `AppSettings.JoinServerFromHostAsync(...)` → `AppServices.HostUiBridge.JoinServerAsync(...)`
- `AppSettings.MultiLaunchHomeFromHost()` → `AppServices.HostUiBridge.MultiLaunchHome()`
- `AppSettings.GetCurrentSelectedAccountsFromHost()` → `AppServices.HostUiBridge.GetCurrentSelectedAccounts()`
- `AppSettings.Current.AccentTheme` → `AppServices.Settings.AccentTheme`

- [ ] **Step 4: Chạy toàn bộ test để xác nhận PASS**

Run: `dotnet test Shiro.sln -c Release --nologo`
Expected: PASS (`Failed: 0`).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "refactor(di): eliminate ambient Program.Logger and AppSettings access in Domain, Services/Ui, and Views"
```

---

### Task 4: Đưa `Program.Logger` và `AppSettings.*` về `(0, 0)` trong `Services/Browser/` và `Services/Windows/`

**Files:**
- Modify: Các file trong `src/Shiro/Services/Browser/` (94 `Program.Logger`, 2 `AppSettings.`)
- Modify: Các file trong `src/Shiro/Services/Windows/` (110 `Program.Logger`, 17 `AppSettings.`)
- Modify: `tests/Shiro.Tests/ArchitectureTests.cs` (`AmbientStaticAccess_ShouldNotExceedPerFolderBaseline`)
- Test: `tests/Shiro.Tests/ArchitectureTests.cs`

**Interfaces:** Consumes `AppServices` accessors từ Task 3.

- [ ] **Step 1: Đặt `["Services/Browser"] = (0, 0)` và `["Services/Windows"] = (0, 0)` trong `tests/Shiro.Tests/ArchitectureTests.cs`**

- [ ] **Step 2: Chạy test để xác nhận FAIL**

Run: `dotnet test Shiro.sln -c Release --nologo --filter "FullyQualifiedName~AmbientStaticAccess_ShouldNotExceedPerFolderBaseline"`
Expected: FAIL với `Services/Browser` và `Services/Windows` vượt ngưỡng `0`.

- [ ] **Step 3: Thay thế toàn bộ `Program.Logger` và `AppSettings.*` trong `src/Shiro/Services/Browser/` và `src/Shiro/Services/Windows/` sang `AppServices.*`**

- [ ] **Step 4: Chạy toàn bộ test để xác nhận PASS**

Run: `dotnet test Shiro.sln -c Release --nologo`
Expected: PASS (`Failed: 0`).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "refactor(di): eliminate ambient Program.Logger and AppSettings access in Services/Browser and Services/Windows"
```

---

### Task 5: Đưa `Program.Logger` và `AppSettings.*` về `(0, 0)` trong `Services/Launch/` — Hoàn tất `(0, 0)` trên toàn bộ 10 thư mục

**Files:**
- Modify: Các file trong `src/Shiro/Services/Launch/` (132 `Program.Logger`, 58 `AppSettings.`)
- Modify: `tests/Shiro.Tests/ArchitectureTests.cs` (`AmbientStaticAccess_ShouldNotExceedPerFolderBaseline`)
- Test: `tests/Shiro.Tests/ArchitectureTests.cs`

**Interfaces:** Consumes `AppServices` accessors từ Task 3.

- [ ] **Step 1: Đặt `["Services/Launch"] = (0, 0)` trong `tests/Shiro.Tests/ArchitectureTests.cs` (tất cả 10 thư mục đều là `(0, 0)`)**

- [ ] **Step 2: Chạy test để xác nhận FAIL**

Run: `dotnet test Shiro.sln -c Release --nologo --filter "FullyQualifiedName~AmbientStaticAccess_ShouldNotExceedPerFolderBaseline"`
Expected: FAIL với `Services/Launch: Program.Logger count 132 exceeds baseline 0`.

- [ ] **Step 3: Thay thế toàn bộ `Program.Logger` và `AppSettings.*` trong `src/Shiro/Services/Launch/` sang `AppServices.*`**

Lưu ý với `AppSettings.MinimumAccountJoinDelaySeconds` / `MaximumAccountJoinDelaySeconds`: dùng hằng số trên `LaunchCoordinator` hoặc thêm `AppServices.MinimumAccountJoinDelaySeconds` / `MaximumAccountJoinDelaySeconds` (giá trị `1` và `3600`).

- [ ] **Step 4: Chạy toàn bộ test để xác nhận PASS**

Run: `dotnet test Shiro.sln -c Release --nologo`
Expected: PASS (`Failed: 0`).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "refactor(di): eliminate ambient Program.Logger and AppSettings access in Services/Launch and lock all folders at zero"
```

---

### Task 6: Tách `AccountSessionMaintenanceService` và gom điều phối Presence về `PresenceService` để đưa `AccountManager` xuống dưới ngưỡng `1150` dòng

**Files:**
- Create: `src/Shiro/Services/Auth/AccountSessionMaintenanceService.cs`
- Modify: `src/Shiro/AccountManager.Accounts.cs` (ủy quyền `RefreshStaleCookiesAsync`, `ImportBulkUserPassAsync`, `MakeSafeFileName` sang `AccountSessionMaintenanceService`)
- Modify: `src/Shiro/Services/Network/PresenceService.cs` (chuyển logic batch/queue `SchedulePresenceRefresh`, `FlushLaunchedPresenceBatchAsync`, `GetPresenceRefreshMinutes` từ `AccountManager.Presence.cs` sang `PresenceService`)
- Modify: `src/Shiro/AccountManager.Presence.cs` (ủy quyền sang `presenceService`)
- Modify: `src/Shiro/Services/AppServices.cs`, `tests/Shiro.Tests/ArchitectureTests.StaticState.cs` (thêm `AccountSessionMaintenanceService` vào DI + allow-list nếu có `.Default`)
- Modify: `tests/Shiro.Tests/ArchitectureTests.cs:345-351` (xóa `["AccountManager"] = 1838` khỏi `baselines` để `AccountManager` chịu ngân sách mặc định `DefaultBudget = 1150`)
- Test: `tests/Shiro.Tests/ArchitectureTests.cs`, `tests/Shiro.Tests/AccountManagerLifecycleAndAccountsBehaviorTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class AccountSessionMaintenanceService` (`namespace Shiro.Services`):
    - `public static AccountSessionMaintenanceService Default { get; }`
    - `internal Task RefreshStaleCookiesAsync(SemaphoreSlim cookieRefreshGate, CancellationToken cancellationToken)`
    - `internal Task ImportBulkUserPassAsync(BrowserService browserService, string combos, Action<string>? statusCallback, CancellationToken lifetimeToken, CancellationToken cancellationToken = default)`
    - `internal static string MakeSafeFileName(string value, string fallback)`

- [ ] **Step 1: Xóa `["AccountManager"] = 1838` khỏi `baselines` trong `NoClass_ShouldExceedLineBudget_AcrossPartials` (`tests/Shiro.Tests/ArchitectureTests.cs:349`)**

- [ ] **Step 2: Chạy test để xác nhận FAIL (`AccountManager: 1779 lines (budget: 1150)`)**

Run: `dotnet test Shiro.sln -c Release --nologo --filter "FullyQualifiedName~NoClass_ShouldExceedLineBudget_AcrossPartials"`
Expected: FAIL.

- [ ] **Step 3: Tách `AccountSessionMaintenanceService` và chuyển điều phối batch presence sang `PresenceService` sao cho tổng dòng `AccountManager` across partials `<= 1150`**

- [ ] **Step 4: Chạy toàn bộ test để xác nhận PASS**

Run: `dotnet test Shiro.sln -c Release --nologo`
Expected: PASS (`Failed: 0`).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "refactor(core): extract AccountSessionMaintenanceService and presence batching to bring AccountManager under default line budget"
```

---

### Task 7: Tách `AccountListFilterController` khỏi `MainViewModel.Filtering.cs` và siết chặt ngân sách dòng của các partial class

**Files:**
- Create: `src/Shiro/ViewModels/AccountListFilterController.cs`
- Modify: `src/Shiro/ViewModels/MainViewModel.Filtering.cs`
- Modify: `tests/Shiro.Tests/ArchitectureTests.cs:345-351` (hạ ngân sách dòng trong `NoClass_ShouldExceedLineBudget_AcrossPartials`)
- Test: `tests/Shiro.Tests/ArchitectureTests.cs`, `tests/Shiro.Tests/ViewModelIntegrationTests.cs`

**Interfaces:**
- Produces:
  - `internal static class AccountListFilterController` (`namespace Shiro.ViewModels`): chứa logic lọc/sắp xếp thuần túy (`FilterAndSortAccounts`, `CompareAccounts`, `ComputeGroupCounts`) được tách từ `MainViewModel.Filtering.cs`.

- [ ] **Step 1: Hạ ngân sách `["MainViewModel"]` trong `ArchitectureTests.cs:348` từ `3300` xuống `3050`, `["Account"]` từ `2400` xuống `2300`, `["ServerListViewModel"]` từ `1650` xuống `1640`**

- [ ] **Step 2: Chạy test để xác nhận FAIL (`MainViewModel: 3268 lines (budget: 3050)`)**

Run: `dotnet test Shiro.sln -c Release --nologo --filter "FullyQualifiedName~NoClass_ShouldExceedLineBudget_AcrossPartials"`
Expected: FAIL.

- [ ] **Step 3: Tách `AccountListFilterController.cs` từ `MainViewModel.Filtering.cs` (giảm ít nhất 220 dòng khỏi `MainViewModel`)**

- [ ] **Step 4: Chạy toàn bộ test để xác nhận PASS**

Run: `dotnet test Shiro.sln -c Release --nologo`
Expected: PASS (`Failed: 0`).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "refactor(viewmodels): extract AccountListFilterController from MainViewModel and tighten partial class line budgets"
```

---

### Task 8: Chia nhỏ 6 file test `> 550` dòng trong `tests/Shiro.Tests/` và khóa bằng ratchet `NoTestFiles_ShouldExceed550Lines`

**Files:**
- Modify & Split:
  1. `tests/Shiro.Tests/ArchitectureTests.cs` (997 dòng) → tách thành `ArchitectureTests.cs`, `ArchitectureTests.Layering.cs`, `ArchitectureTests.Budgets.cs`
  2. `tests/Shiro.Tests/Ui/DialogSmokeTests.cs` (851 dòng) → tách thành `DialogSmokeTests.cs` và `DialogSmokeTests.Extended.cs`
  3. `tests/Shiro.Tests/RobloxHbaAndIpPersistenceTests.cs` (742 dòng) → tách thành `RobloxHbaAndIpPersistenceTests.cs` và `RobloxHbaAndIpPersistenceTests.Extended.cs`
  4. `tests/Shiro.Tests/AccountLaunchServiceBranchTests.cs` (617 dòng) → tách thành `AccountLaunchServiceBranchTests.cs` và `AccountLaunchServiceBranchTests.Fallback.cs`
  5. `tests/Shiro.Tests/AccountManagerLifecycleAndAccountsBehaviorTests.cs` (592 dòng) → tách thành `AccountManagerLifecycleAndAccountsBehaviorTests.cs` và `AccountManagerAccountsBehaviorTests.cs`
  6. `tests/Shiro.Tests/WebServerEndpointsAndWorkflowTests.cs` (581 dòng) → tách thành `WebServerEndpointsAndWorkflowTests.cs` và `WebServerWorkflowBehaviorTests.cs`
- Modify: `tests/Shiro.Tests/ArchitectureTests.Hygiene.cs` (thêm `NoTestFiles_ShouldExceed550Lines`)
- Modify: `CHANGELOG.md` (cập nhật mục `Unreleased` với các hạng mục Phase 6)

- [ ] **Step 1: Thêm test `NoTestFiles_ShouldExceed550Lines` vào `tests/Shiro.Tests/ArchitectureTests.Hygiene.cs`**

```csharp
[Fact]
public void NoTestFiles_ShouldExceed550Lines()
{
    const int MaxTestLines = 550;
    var testsDir = Path.Combine(RepoRoot, "tests", "Shiro.Tests");
    var oversized = Directory.GetFiles(testsDir, "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Replace('\\', '/').Contains("/bin/") && !f.Replace('\\', '/').Contains("/obj/") && !f.EndsWith(".g.cs"))
        .Select(f => (File: Path.GetFileName(f), Lines: File.ReadAllLines(f).Length))
        .Where(x => x.Lines > MaxTestLines)
        .Select(x => $"{x.File} ({x.Lines} lines)")
        .ToList();
    Assert.Empty(oversized);
}
```

- [ ] **Step 2: Chạy test để xác nhận FAIL với đúng 6 file test**

Run: `dotnet test Shiro.sln -c Release --nologo --filter "FullyQualifiedName~NoTestFiles_ShouldExceed550Lines"`
Expected: FAIL với 6 file (`ArchitectureTests.cs`, `DialogSmokeTests.cs`, `RobloxHbaAndIpPersistenceTests.cs`, `AccountLaunchServiceBranchTests.cs`, `AccountManagerLifecycleAndAccountsBehaviorTests.cs`, `WebServerEndpointsAndWorkflowTests.cs`).

- [ ] **Step 3: Tách 6 file test sao cho mọi file trong `tests/Shiro.Tests/` đều `<= 550` dòng (và mọi file `ArchitectureTests*.cs` đều `<= 485` dòng) mà không làm mất bất kỳ `[Fact]` / `[Theory]` nào**

- [ ] **Step 4: Chạy toàn bộ test + kiểm tra coverage gate**

Run:
```powershell
dotnet build Shiro.sln -c Release --nologo
dotnet test tests/Shiro.Tests/Shiro.Tests.csproj -c Release --collect:"XPlat Code Coverage" --settings tests/coverage.runsettings --results-directory coverage
./scripts/Check-Coverage.ps1 -CoverageDir coverage -MinOverall 75.0 -MinBranch 60.0 -MinCore 92.0
Remove-Item -Recurse -Force coverage -ErrorAction SilentlyContinue
```
Expected: `0 Error(s)`, `Failed: 0`, tổng số test `>= 1915`, coverage script exit `0`.

- [ ] **Step 5: Cập nhật `CHANGELOG.md` và Commit**

```bash
git add -A
git commit -m "test(hygiene): split oversized test files and enforce 550-line limit across test suite"
```

---

## Bảng ánh xạ Khuyết điểm → Task → Ratchet bảo vệ

| # | Khuyết điểm còn lại sau Phase 5 | Task | Ratchet khóa chặt |
| :--- | :--- | :---: | :--- |
| 1 | 5 hàm `async void` trong `FastLoginDialog.axaml.cs` | Task 1 | `AsyncVoidHandlers_InViews_ShouldNotExceedBaseline` = `0` |
| 2 | `Domain/` phụ thuộc ngược vào `ViewModels`/`Win32` & bản sao `Utilities.FastJson` | Task 2 | `DomainFolder_ShouldNotDependOn_ViewModelsOrAvaloniaOrWin32Registry`, `FastJson_ShouldHaveSingleDefinition` |
| 3 | 539 điểm gọi `Program.Logger` / `AppSettings.*` ở 6 thư mục | Task 3, 4, 5 | `AmbientStaticAccess_ShouldNotExceedPerFolderBaseline` = `(0, 0)` cho cả 10/10 thư mục |
| 4 | `AccountManager` (1.779 dòng) còn nằm trong danh sách ngoại lệ vượt `1150` dòng | Task 6 | Xóa ngoại lệ `["AccountManager"]` trong `NoClass_ShouldExceedLineBudget_AcrossPartials` |
| 5 | `MainViewModel` (3.268 dòng) ôm cả bộ lọc/sắp xếp tài khoản | Task 7 | Tách `AccountListFilterController`, hạ ngân sách `MainViewModel` xuống `3050` |
| 6 | 6 file trong `tests/Shiro.Tests/` vượt quá 550 dòng (`ArchitectureTests.cs` 997 dòng) | Task 8 | `NoTestFiles_ShouldExceed550Lines` (`<= 550` dòng) |
