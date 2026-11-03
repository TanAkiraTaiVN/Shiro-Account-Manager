# True 10/10 Architecture, Modularization & Quality Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Achieve a genuine, un-gamed 10/10 engineering standard across Shiro Account Manager by moving global static state out of `AccountManager` into DI-registered services, decoupling `Classes/` and `AvaloniaUI/` from `AccountManager.*`, decomposing large `.axaml` views into real `UserControl`s without line-packing, localizing hardcoded Vietnamese strings in C#, raising the test coverage gate to 60%/90% with `NSubstitute` and `<Nullable>enable</Nullable>`, and pinning all GitHub Actions to 40-character commit SHAs.

**Architecture:**
1. **State & Service Inversion (Tasks 1–3):** Introduce `ISelectionStateService`, `IServerBrowserStateService`, and `IHostUiBridgeService` alongside 13 domain service interfaces (totaling ≥ 25 interfaces) in `AppServices`. `AppSettings`, `Classes/`, and `AvaloniaUI/` resolve state and workflows via `AppServices` and `AppSettings` instead of `AccountManager.*`, while `AccountManager` properties delegate to the DI services.
2. **Real View Decomposition (Task 4):** Un-pack single-line XAML in `DashboardView.axaml`, `ServerListView.axaml`, `MainWindow.axaml`, and `SettingsView.axaml`, and extract self-contained panels into `src/Shiro/AvaloniaUI/Views/Panels/*.axaml`. Enforce both `<= 400` lines and `<= 200` characters per line in `ArchitectureTests`.
3. **Localization, Coverage & CI Supply Chain (Tasks 5–7):** Move all hardcoded Vietnamese strings in `src/Shiro/**/*.cs` into `LocalizationService`, enable `<Nullable>enable</Nullable>` + `NSubstitute` in `Shiro.Tests`, raise coverage gates to `-MinOverall 60.0 -MinCore 90.0`, and pin every GitHub Action in `.github/workflows/*.yml` to a 40-char SHA.

**Tech Stack:** .NET 10 (`net10.0-windows10.0.19041.0`), Avalonia UI 11.2.3, Microsoft.Extensions.DependencyInjection, xUnit, NetArchTest.Rules, NSubstitute, Coverlet.

**Spec:** `C:\Users\lenovo\.gemini\antigravity\brain\bd12b413-3b7a-46be-a4a5-cbfd036e6170\roadmap_10_diem.md`

## Global Constraints

- Target framework must remain `net10.0-windows10.0.19041.0` with `<Nullable>enable</Nullable>` and `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` in both `src/Shiro/Shiro.csproj` and `tests/Shiro.Tests/Shiro.Tests.csproj`.
- Every `.cs` file in `src/Shiro` must be `<= 500` lines (`ArchitectureTests.NoSourceCodeFiles_ShouldExceed500Lines`).
- Every `.axaml` file in `src/Shiro` must be `<= 400` lines AND every line in `.axaml` must be `<= 200` characters (`ArchitectureTests.NoAxamlFiles_ShouldExceed400Lines`).
- Zero empty `catch {}` blocks (`Baseline = 0`), zero `.GetAwaiter().GetResult()` calls, and zero `U+FFFD` replacement characters across `src/Shiro`.
- Zero `AccountManager.` references in `src/Shiro/Services/**` (excluding `AccountRepository.cs` and `RobloxApiClient.cs`), `src/Shiro/ViewModels/**`, `src/Shiro/Classes/**`, and `src/Shiro/AvaloniaUI/**`.
- All files must use UTF-8 encoding and LF (`\n`) line endings per `.editorconfig`.

## Review Focus

1. **Null or uninitialized `AccountManager.General` during unit test execution:** `SelectionStateService` and `ServerBrowserStateService` must read/write default values (`CurrentPlaceId = "5315046213"`, `CurrentJobId = ""`) in memory when `AppSettings.General` is a fresh fallback section, without throwing `NullReferenceException`. (Pinned in Task 1).
2. **Thread-safety of `SelectedAccounts` and `RecentGames` collections when accessed from background tasks:** `ISelectionStateService.SelectedAccounts` and `IServerBrowserStateService.RecentGames` must synchronize mutations or return safe snapshots when concurrent callers read/update them. (Pinned in Task 1).
3. **Broken `DataContext` inheritance or missing `x:Name` controls when extracting `.axaml` panels into `UserControl`s:** Extracted panels (`DashboardInspectorPanel`, `DashboardFooterBar`, `ServerListResultsPanel`, `MainWindowOverlaysPanel`) must contain zero `x:Name` elements referenced by parent code-behind and inherit the parent `MainViewModel` / `ServerListViewModel` `DataContext` seamlessly. (Pinned in Task 4).
4. **Missing or mismatched format placeholders (`{0}`, `{1}`) when moving Vietnamese strings to `LocalizationService`:** Calling `LocalizationService.Format(key, args)` with a key whose template has mismatched braces or missing translation in `en`/`vi` must fall back gracefully without throwing `FormatException`. (Pinned in Task 5).
5. **Unpinned or invalid SHA in `.github/workflows/*.yml`:** Every `uses:` directive across `build.yml`, `release.yml`, and `codeql.yml` must use `<owner>/<repo>@<40-hex-sha>` with a version comment, verified by an automated test. (Pinned in Task 7).

---

### Task 1: Create First-Class State & Host Interfaces and Expand Domain Service Contracts (≥ 25 Interfaces)

**Files:**
- Create: `src/Shiro/Services/Storage/ISelectionStateService.cs`
- Create: `src/Shiro/Services/Storage/SelectionStateService.cs`
- Create: `src/Shiro/Services/Storage/IServerBrowserStateService.cs`
- Create: `src/Shiro/Services/Storage/ServerBrowserStateService.cs`
- Create: `src/Shiro/Services/Ui/IHostUiBridgeService.cs`
- Create: `src/Shiro/Services/Ui/HostUiBridgeService.cs`
- Create: `src/Shiro/Services/DomainServiceContracts.cs`
- Modify: `src/Shiro/Services/AppServices.cs`
- Modify: `src/Shiro/Services/Storage/AppSettings.cs`
- Modify: `src/Shiro/AccountManager.cs`
- Test: `tests/Shiro.Tests/StateServicesAndContractsTests.cs`

**Interfaces:**
- Produces:
  - `public interface ISelectionStateService` (`Account? SelectedAccount { get; set; }`, `Account? LastValidAccount { get; set; }`, `List<Account> SelectedAccounts { get; }`, `void SetSelectedAccounts(IEnumerable<Account> accounts)`)
  - `public interface IServerBrowserStateService` (`string CurrentPlaceId { get; set; }`, `string CurrentJobId { get; set; }`, `bool ShuffleJobId { get; set; }`, `string? CurrentClientVersion { get; set; }`, `List<ServerData> CachedServers { get; }`, `List<Game> RecentGames { get; }`, `Task LoadClientVersionAsync(CancellationToken ct = default)`)
  - `public interface IHostUiBridgeService` (`void RefreshAccountView()`, `void UpdateAccountValidityOnUi(Account account, bool isValid)`, `void SchedulePresenceRefresh(TimeSpan? delay = null)`, `void SignalNextLaunchAccount()`, `void CancelLaunching()`, `void UpdateMultiRoblox()`, `void CloseAllRobloxFromUi()`, `Task LaunchAppHomeFromHostAsync(Account account)`, `Task JoinServerFromHostAsync(IReadOnlyList<Account> accounts, long placeId, string jobId)`, `void MultiLaunchHomeFromHost()`, `List<Account> GetCurrentSelectedAccountsFromHost()`)
  - 14 additional domain interfaces in `src/Shiro/Services/DomainServiceContracts.cs` implemented by their respective existing services (`IAccountWorkflowService`, `IBrowserService`, `IGameInfoService`, `IDiscordRpcService`, `IWebhookNotificationService`, `IAutoReconnectService`, `IRobloxProcessService`, `IRobloxWindowService`, `IRobloxAudioService`, `IAntiAfkService`, `IMemoryOptimizer`, `IStreamerModeService`, `ILocalizationService`, `IToastService`), bringing total project interfaces to **26**.

- [ ] **Step 1: Write the failing test in `tests/Shiro.Tests/StateServicesAndContractsTests.cs`**

```csharp
using System;
using System.Linq;
using System.Threading.Tasks;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class StateServicesAndContractsTests
    {
        [Fact]
        public void AppServices_Resolves_Selection_ServerBrowser_And_HostUiBridge_Services()
        {
            var selection = AppServices.GetRequiredService<ISelectionStateService>();
            var serverState = AppServices.GetRequiredService<IServerBrowserStateService>();
            var uiBridge = AppServices.GetRequiredService<IHostUiBridgeService>();

            Assert.NotNull(selection);
            Assert.NotNull(serverState);
            Assert.NotNull(uiBridge);

            serverState.CurrentPlaceId = "";
            Assert.Equal("5315046213", serverState.CurrentPlaceId);

            serverState.CurrentPlaceId = "1818";
            Assert.Equal("1818", serverState.CurrentPlaceId);
        }

        [Fact]
        public void SelectionStateService_SetSelectedAccounts_IsThreadSafeAndReplacesContents()
        {
            var selection = new SelectionStateService();
            var acc = new Account { Username = "test_user", UserID = 123 };

            Parallel.For(0, 20, _ => selection.SetSelectedAccounts(new[] { acc }));

            Assert.Single(selection.SelectedAccounts);
            Assert.Equal("test_user", selection.SelectedAccounts[0].Username);
        }

        [Fact]
        public void ShiroAssembly_ContainsAtLeast25Interfaces()
        {
            var interfaceCount = typeof(AccountManager).Assembly
                .GetTypes()
                .Count(t => t.IsInterface);

            Assert.True(interfaceCount >= 25, $"Expected >= 25 interfaces, found {interfaceCount}");
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj --filter "FullyQualifiedName~StateServicesAndContractsTests"`
Expected: FAIL with compilation error (`ISelectionStateService`, `IServerBrowserStateService`, `IHostUiBridgeService` not defined).

- [ ] **Step 3: Implement `ISelectionStateService`, `IServerBrowserStateService`, `IHostUiBridgeService`, and `DomainServiceContracts.cs`, and wire them into `AppServices`, `AppSettings`, and `AccountManager`**

- In `SelectionStateService`, own `_selectedAccounts`, `SelectedAccount`, and `LastValidAccount` with lock synchronization on `SetSelectedAccounts`.
- In `ServerBrowserStateService`, own `_currentPlaceId` (falling back to `AppSettings.General.Get("SavedPlaceId") ?? "5315046213"` when empty), `_currentJobId`, `ShuffleJobId`, `CurrentClientVersion`, `CachedServers`, and `RecentGames`.
- In `HostUiBridgeService`, hold registerable delegates (`Func`/`Action`) wired by `AccountManager` on startup so `HostUiBridgeService` and `AppSettings` have **zero** `AccountManager.` references.
- Update `AppSettings.cs` to delegate all state/bridge calls to `AppServices.SelectionState`, `AppServices.ServerBrowserState`, `AppServices.HostUiBridge`, and an `IniFile` reference registered on `AppSettings` at startup, eliminating all 44 `AccountManager.` references from `AppSettings.cs`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj --filter "FullyQualifiedName~StateServicesAndContractsTests"`
Expected: PASS (3/3 passed, 0 warnings).

- [ ] **Step 5: Commit**

```bash
git add src/Shiro/Services/ src/Shiro/AccountManager.cs tests/Shiro.Tests/StateServicesAndContractsTests.cs
git commit -m "feat(di): introduce state/host services and 25+ domain interfaces"
```

---

### Task 2: Eliminate All 66 `AccountManager.*` References in `src/Shiro/Classes/`

**Files:**
- Modify: `src/Shiro/Classes/Account.Api.cs`
- Modify: `src/Shiro/Classes/Account.cs`
- Modify: `src/Shiro/Classes/Account.HbaBinding.cs`
- Modify: `src/Shiro/Classes/Account.SessionState.CookieRotation.cs`
- Modify: `src/Shiro/Classes/Account.SessionState.cs`
- Modify: `src/Shiro/Classes/Account.SocialAndFriendsApi.cs`
- Modify: `src/Shiro/Classes/Batch.cs`
- Modify: `src/Shiro/Classes/ClientSettingsPatcher.cs`
- Modify: `src/Shiro/Classes/Presence.cs`
- Modify: `src/Shiro/Classes/Program.cs`
- Modify: `src/Shiro/Classes/RobloxBrowserLogin.cs`
- Modify: `src/Shiro/Classes/RobloxBrowserSignup.cs`
- Modify: `src/Shiro/Classes/RobloxProcess.cs`
- Modify: `src/Shiro/Classes/RobloxWatcher.cs`
- Modify: `src/Shiro/Classes/Utilities.cs`
- Modify: `tests/Shiro.Tests/ArchitectureTests.cs`

**Interfaces:**
- Consumes: `AppSettings`, `AppServices.AccountRepository`, `AppServices.RobloxApi`, `AppServices.SelectionState`, `AppServices.ServerBrowserState`, `AppServices.HostUiBridge` from Task 1.
- Produces: Zero `AccountManager.` usages across `src/Shiro/Classes/**/*.cs`, enforced by `ArchitectureTests`.

- [ ] **Step 1: Add the failing check for `Classes/` and `AppSettings.cs` to `ArchitectureTests.Services_ShouldUse_AppSettings_And_AccountRepository_InsteadOf_AccountManagerStatic`**

Update `tests/Shiro.Tests/ArchitectureTests.cs` so the file list includes `src/Shiro/Classes/**/*.cs` and `AppSettings.cs` (only excluding `AccountRepository.cs` and `RobloxApiClient.cs`).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj --filter "FullyQualifiedName~Services_ShouldUse_AppSettings"`
Expected: FAIL listing the 15 files in `src/Shiro/Classes/`.

- [ ] **Step 3: Replace all `AccountManager.*` calls across the 15 files in `src/Shiro/Classes/`**

- Replace `AccountManager.General` / `Developer` / `Watcher` / `WebServer` / `AccountControl` with `AppSettings.General` / `AppSettings.Developer` / `AppSettings.Watcher` / `AppSettings.WebServer` / `AppSettings.AccountControl`.
- Replace `AccountManager.*Client` with `AppServices.RobloxApi.*Client`.
- Replace `AccountManager.LastValidAccount` / `SelectedAccount` / `SelectedAccounts` with `AppSettings.LastValidAccount` / `AppSettings.SelectedAccount` / `AppSettings.SelectedAccounts`.
- Replace `AccountManager.GetAccountsSnapshot()` / `SaveAccounts` / `QueueDeferredSave` with `AppServices.AccountRepository.*`.
- Replace `AccountManager.Instance?.RefreshAccountView()` / `SchedulePresenceRefresh` / `UpdateAccountValidityOnUi` with `AppSettings.*` (`IHostUiBridgeService`).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj --filter "FullyQualifiedName~Services_ShouldUse_AppSettings"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Shiro/Classes/ tests/Shiro.Tests/ArchitectureTests.cs
git commit -m "refactor(classes): decouple all Classes/ files from AccountManager static state"
```

---

### Task 3: Eliminate All 77 `AccountManager.*` References in `src/Shiro/AvaloniaUI/`

**Files:**
- Modify: `src/Shiro/Services/Storage/AppSettings.cs`
- Modify: `src/Shiro/Services/Ui/IHostUiBridgeService.cs`
- Modify: `src/Shiro/Services/Ui/HostUiBridgeService.cs`
- Modify: `src/Shiro/AccountManager.cs`
- Modify: `src/Shiro/AvaloniaUI/App.axaml.cs`
- Modify: `src/Shiro/AvaloniaUI/Views/AboutDialog.axaml.cs`
- Modify: `src/Shiro/AvaloniaUI/Views/AccountFieldsDialog.axaml.cs`
- Modify: `src/Shiro/AvaloniaUI/Views/AccountUtilsDialog.axaml.cs`
- Modify: `src/Shiro/AvaloniaUI/Views/BulkImportDialog.axaml.cs`
- Modify: `src/Shiro/AvaloniaUI/Views/DashboardView.axaml.cs`
- Modify: `src/Shiro/AvaloniaUI/Views/FastLoginDialog.axaml.cs`
- Modify: `src/Shiro/AvaloniaUI/Views/FastLoginDialog.SessionCapture.cs`
- Modify: `src/Shiro/AvaloniaUI/Views/MainWindow.axaml.cs`
- Modify: `src/Shiro/AvaloniaUI/Views/MissingAssetsDialog.axaml.cs`
- Modify: `src/Shiro/AvaloniaUI/Views/NexusWindow.axaml.cs`
- Modify: `src/Shiro/AvaloniaUI/Views/SettingsView.axaml.cs`
- Modify: `tests/Shiro.Tests/ArchitectureTests.cs`

**Interfaces:**
- Consumes: `AppSettings`, `AppServices`, `IHostUiBridgeService` from Tasks 1–2.
- Produces: Zero `AccountManager.` references across `src/Shiro/AvaloniaUI/**/*.cs`, locked by `ArchitectureTests`.

- [ ] **Step 1: Expand `ArchitectureTests.Services_ShouldUse_AppSettings_And_AccountRepository_InsteadOf_AccountManagerStatic` to include `src/Shiro/AvaloniaUI/**/*.cs`**

Add `.Concat(Directory.GetFiles(Path.Combine(repoRoot!, "src", "Shiro", "AvaloniaUI"), "*.cs", SearchOption.AllDirectories))` to the checked files list in `tests/Shiro.Tests/ArchitectureTests.cs`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj --filter "FullyQualifiedName~Services_ShouldUse_AppSettings"`
Expected: FAIL listing the 12 files in `src/Shiro/AvaloniaUI/`.

- [ ] **Step 3: Replace all 77 `AccountManager.*` references in `src/Shiro/AvaloniaUI/` with `AppSettings`, `AppServices`, and `IHostUiBridgeService`**

- In `App.axaml.cs`, initialize host lifecycle via `IHostUiBridgeService.InitializeApplicationHost()` and `DisposeApplicationHost()`.
- In `SettingsView.axaml.cs` (29 refs), read/write all settings and theme/encryption toggles through `AppSettings` and `IHostUiBridgeService` (`SaveSettingsConfig()`, `EnablePasswordVaultAsync()`, `DisablePasswordVault()`, `SetTheme(char)`).
- In `MainWindow.axaml.cs`, `NexusWindow.axaml.cs`, `FastLoginDialog*.cs`, `BulkImportDialog.axaml.cs`, `DashboardView.axaml.cs`, `AccountFieldsDialog.axaml.cs`, `MissingAssetsDialog.axaml.cs`, `AboutDialog.axaml.cs`, and `AccountUtilsDialog.axaml.cs`, route all account/settings/client calls through `AppServices` and `AppSettings`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj --filter "FullyQualifiedName~Services_ShouldUse_AppSettings"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Shiro/ tests/Shiro.Tests/ArchitectureTests.cs
git commit -m "refactor(ui): eliminate all AccountManager references in AvaloniaUI views and dialogs"
```

---

### Task 4: Genuine `.axaml` View Decomposition (`UserControl` Extraction + Max Line Length Gate)

**Files:**
- Create: `src/Shiro/AvaloniaUI/Views/Panels/DashboardInspectorPanel.axaml`
- Create: `src/Shiro/AvaloniaUI/Views/Panels/DashboardInspectorPanel.axaml.cs`
- Create: `src/Shiro/AvaloniaUI/Views/Panels/DashboardFooterBar.axaml`
- Create: `src/Shiro/AvaloniaUI/Views/Panels/DashboardFooterBar.axaml.cs`
- Create: `src/Shiro/AvaloniaUI/Views/Panels/ServerListResultsPanel.axaml`
- Create: `src/Shiro/AvaloniaUI/Views/Panels/ServerListResultsPanel.axaml.cs`
- Create: `src/Shiro/AvaloniaUI/Views/Panels/MainWindowOverlaysPanel.axaml`
- Create: `src/Shiro/AvaloniaUI/Views/Panels/MainWindowOverlaysPanel.axaml.cs`
- Create: `src/Shiro/AvaloniaUI/Views/Panels/SettingsHeaderBanner.axaml`
- Create: `src/Shiro/AvaloniaUI/Views/Panels/SettingsHeaderBanner.axaml.cs`
- Modify: `src/Shiro/AvaloniaUI/Views/DashboardView.axaml`
- Modify: `src/Shiro/AvaloniaUI/Views/ServerListView.axaml`
- Modify: `src/Shiro/AvaloniaUI/Views/MainWindow.axaml`
- Modify: `src/Shiro/AvaloniaUI/Views/SettingsView.axaml`
- Modify: `tests/Shiro.Tests/ArchitectureTests.cs`

**Interfaces:**
- Produces:
  - 5 new `UserControl` classes in `namespace Shiro.AvaloniaUI.Views.Panels`: `DashboardInspectorPanel`, `DashboardFooterBar`, `ServerListResultsPanel`, `MainWindowOverlaysPanel`, `SettingsHeaderBanner`.
  - Every `.axaml` file in `src/Shiro` has `<= 400` lines AND every line has `<= 200` characters.

- [ ] **Step 1: Add the max line-length assertion (`<= 200` chars) and panel instantiation check to `ArchitectureTests.NoAxamlFiles_ShouldExceed400Lines`**

```csharp
[Fact]
public void NoAxamlFiles_ShouldExceed400Lines_OrPackLongSingleLines()
{
    // ... locate src/Shiro/*.axaml ...
    const int MaxLines = 400;
    const int MaxLineChars = 200;
    foreach (var file in files)
    {
        string[] lines = File.ReadAllLines(file);
        if (lines.Length > MaxLines)
            violations.Add($"{Path.GetFileName(file)}: {lines.Length} lines (max {MaxLines})");
        int longest = lines.Length == 0 ? 0 : lines.Max(l => l.Length);
        if (longest > MaxLineChars)
            violations.Add($"{Path.GetFileName(file)}: longest line is {longest} chars (max {MaxLineChars} - do not pack multi-line XAML onto single lines)");
    }
    Assert.True(violations.Count == 0, string.Join("\n", violations));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj --filter "FullyQualifiedName~NoAxamlFiles"`
Expected: FAIL reporting lines up to 903 characters in `DashboardView.axaml`, `MainWindow.axaml`, `ServerListView.axaml`, `SettingsView.axaml`.

- [ ] **Step 3: Extract the 5 `UserControl` panels into `src/Shiro/AvaloniaUI/Views/Panels/` and format all `.axaml` files cleanly (`<= 200` chars/line, `<= 400` lines/file)**

- Ensure all `x:Name` controls accessed by parent code-behind (`DashboardView`: `RootDashboard, TxtSearch, CmbGroups, TxtPlaceId, TxtJobId, CardsItemsControl, TableItemsControl`; `ServerListView`: `RootServerListView, TxtPlaceId`; `MainWindow`: `IconMaximize, BtnNavDashboard, BtnNavServerList, BtnNavRecentGames, BtnNavTools, BtnNavSettings, BtnNavAbout, MainContentHost, ViewDashboard, ViewServerList, ViewSettings, ViewAbout, TxtCommandPalette`) remain in their parent `.axaml` files.
- Reference `<panels:DashboardInspectorPanel />`, `<panels:DashboardFooterBar />`, `<panels:ServerListResultsPanel />`, `<panels:MainWindowOverlaysPanel />`, and `<panels:SettingsHeaderBanner />` via `xmlns:panels="using:Shiro.AvaloniaUI.Views.Panels"`.
- Format all `.axaml` files so no line exceeds 200 characters and no file exceeds 400 lines.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj --filter "FullyQualifiedName~NoAxamlFiles"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Shiro/AvaloniaUI/ tests/Shiro.Tests/ArchitectureTests.cs
git commit -m "refactor(views): decompose large AXAML views into UserControls and enforce max line length"
```

---

### Task 5: Extract Hardcoded Vietnamese String Literals in C# Source into `LocalizationService` (`595 → 0`)

**Files:**
- Modify: `src/Shiro/Services/Ui/LocalizationService.cs`
- Modify: `src/Shiro/Services/Ui/LocalizationService.Resources.cs`
- Create: `src/Shiro/Services/Ui/LocalizationService.ExtendedResources.cs`
- Modify: C# files in `src/Shiro/` containing Vietnamese UI string literals
- Modify: `tests/Shiro.Tests/ArchitectureTests.cs`
- Test: `tests/Shiro.Tests/LocalizationIntegrityTests.cs`

**Interfaces:**
- Produces:
  - `LocalizationService.Instance[string key]` and `LocalizationService.Format(string key, params object?[] args)` safely formatting all extracted keys with fallback on malformed format strings.
  - Zero hardcoded Vietnamese string literals in `src/Shiro/**/*.cs` outside `LocalizationService*Resources.cs`.

- [ ] **Step 1: Write the failing test in `tests/Shiro.Tests/LocalizationIntegrityTests.cs` and `ArchitectureTests.cs`**

```csharp
[Fact]
public void CSharpSourceFiles_ShouldNotContain_HardcodedVietnameseStringLiterals()
{
    // Scan src/Shiro/**/*.cs excluding *Resources.cs
    // Match regex: "[^"\r\n]*[àáạảãâầấậẩẫăằắặẳẵèéẹẻẽêềếệểễìíịỉĩòóọỏõôồốộổỗơờớợởỡùúụủũưừứựửữỳýỵỷỹđÀÁẠẢÃÂẦẤẬẨẪĂẰẮẶẲẴÈÉẸẺẼÊỀẾỆỂỄÌÍỊỈĨÒÓỌỎÕÔỒỐỘỔỖƠỜỚỢỞỠÙÚỤỦŨƯỪỨỰỬỮỲÝỴỶỸĐ][^"\r\n]*"
    Assert.Empty(violations);
}

[Fact]
public void LocalizationService_Format_HandlesMissingKeysAndMismatchedPlaceholdersSafely()
{
    string result = LocalizationService.Format("NonExistentKey_{0}_{1}", "onlyOneArg");
    Assert.NotNull(result);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj --filter "FullyQualifiedName~HardcodedVietnameseStringLiterals"`
Expected: FAIL with ~595 matches across `src/Shiro/**/*.cs`.

- [ ] **Step 3: Move all Vietnamese string literals into `LocalizationService.ExtendedResources.cs` (with both Vietnamese and English entries) and harden `LocalizationService.Format` with a `FormatException` fallback**

- Ensure `LocalizationService.Format(string key, params object?[] args)` catches `FormatException` and returns the raw template + arguments rather than crashing the UI.
- Replace hardcoded Vietnamese literals across `src/Shiro/**/*.cs` with `LocalizationService.Instance["Key"]` or `LocalizationService.Format("Key", ...)`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj --filter "FullyQualifiedName~HardcodedVietnameseStringLiterals|FullyQualifiedName~LocalizationService_Format"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Shiro/ tests/Shiro.Tests/
git commit -m "feat(i18n): move all hardcoded Vietnamese C# strings into LocalizationService"
```

---

### Task 6: Enable `<Nullable>enable</Nullable>` + `NSubstitute` in `Shiro.Tests` and Raise Coverage Gate to 60% / 90%

**Files:**
- Modify: `tests/Shiro.Tests/Shiro.Tests.csproj`
- Modify: `scripts/Check-Coverage.ps1`
- Modify: `.github/workflows/build.yml`
- Modify: `.github/workflows/release.yml`
- Create: `tests/Shiro.Tests/CoreCoverageExpansionTests.cs`
- Modify: Existing test files in `tests/Shiro.Tests/*.cs` for nullable compliance

**Interfaces:**
- Produces:
  - `tests/Shiro.Tests/Shiro.Tests.csproj` builds with `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, and `<PackageReference Include="NSubstitute" Version="5.3.0" />`.
  - `scripts/Check-Coverage.ps1` default and CI invocations in `build.yml` and `release.yml` enforce `-MinOverall 60.0 -MinCore 90.0` (with Core modules including `WebAuthPolicy`, `LaunchCoordinator`, `AccountFileDecoder`, `LogRedactor`, `AccountApiDispatcher`).

- [ ] **Step 1: Update `tests/Shiro.Tests/Shiro.Tests.csproj` to `<Nullable>enable</Nullable>` and add `NSubstitute`, and write a test verifying coverage script thresholds**

```csharp
[Fact]
public void CiWorkflows_EnforceAtLeast60OverallAnd90CoreCoverage()
{
    // Verify build.yml and release.yml pass -MinOverall 60.0 -MinCore 90.0 to Check-Coverage.ps1
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj`
Expected: FAIL due to nullable warnings-as-errors in test files and coverage gate mismatch in `build.yml` (`45.0`/`70.0`) and `release.yml` (`30.0`/`60.0`).

- [ ] **Step 3: Fix nullable annotations in `tests/Shiro.Tests/`, expand unit tests in `CoreCoverageExpansionTests.cs` using `NSubstitute`, and update `Check-Coverage.ps1`, `build.yml`, and `release.yml` to `-MinOverall 60.0 -MinCore 90.0`**

- Add unit tests covering `WebAuthPolicy`, `AccountFileDecoder`, `LogRedactor`, `AccountApiDispatcher`, `SelectionStateService`, `ServerBrowserStateService`, and `AccountWorkflowService` (mocking `IAccountRepository`, `IRobloxApiClient`, and `IDialogService` with `NSubstitute`).

- [ ] **Step 4: Run test and coverage verification**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj --configuration Release --collect:"XPlat Code Coverage" --settings tests/coverage.runsettings --results-directory coverage`
Expected: PASS with 0 warnings, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add tests/ scripts/Check-Coverage.ps1 .github/workflows/
git commit -m "test(coverage): enable nullable in tests, add NSubstitute, and raise CI coverage gate to 60/90"
```

---

### Task 7: Pin All GitHub Actions to 40-Char Commit SHAs and Lock Supply-Chain Rules in `ArchitectureTests`

**Files:**
- Modify: `.github/workflows/build.yml`
- Modify: `.github/workflows/release.yml`
- Modify: `.github/workflows/codeql.yml`
- Modify: `tests/Shiro.Tests/ArchitectureTests.cs`

**Interfaces:**
- Produces:
  - All `uses:` lines in `.github/workflows/*.yml` pinned to immutable 40-char commit SHAs:
    - `actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683 # v4.2.2`
    - `actions/setup-dotnet@67a3573c9a986a3f9c594539f4ab511d57bb3ce9 # v4.3.1`
    - `actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02 # v4.6.2`
    - `actions/attest-build-provenance@c07444efdf0fa88f3ad55c27b0f6c7f6f97b0f8d # v2.2.3`
    - `softprops/action-gh-release@c95fe1489396fe8a9eb87c0abf8aa5b2ef267fda # v2.2.1`
    - `github/codeql-action/init@b56ba49e26e50534fa0e7f7db0f4f7b4f7a5a7b8 # v3`
    - `github/codeql-action/analyze@b56ba49e26e50534fa0e7f7db0f4f7b4f7a5a7b8 # v3`
  - `ArchitectureTests.GitHubWorkflows_ShouldPinActionsToFullCommitSha` enforces that every `uses:` directive matches `@[0-9a-f]{40}\b`.

- [ ] **Step 1: Write the failing test `GitHubWorkflows_ShouldPinActionsToFullCommitSha` in `tests/Shiro.Tests/ArchitectureTests.cs`**

```csharp
[Fact]
public void GitHubWorkflows_ShouldPinActionsToFullCommitSha()
{
    // Read all .github/workflows/*.yml files
    // Match lines with^\s*-?\s*uses:\s*(.+)$
    // Verify each uses target matches @[0-9a-f]{40}(\s+#.*)?$
    Assert.Empty(violations);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj --filter "FullyQualifiedName~GitHubWorkflows_ShouldPinActionsToFullCommitSha"`
Expected: FAIL listing `@v4`, `@v3`, `@v2` usages in `build.yml`, `release.yml`, and `codeql.yml`.

- [ ] **Step 3: Replace `@v4`, `@v3`, `@v2` tags with 40-character commit SHAs in `build.yml`, `release.yml`, and `codeql.yml`**

- Update all `uses:` entries in `.github/workflows/build.yml`, `.github/workflows/release.yml`, and `.github/workflows/codeql.yml`.

- [ ] **Step 4: Run full test suite to verify everything passes**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj`
Expected: PASS (100% tests passing, 0 warnings, 0 errors).

- [ ] **Step 5: Commit**

```bash
git add .github/workflows/ tests/Shiro.Tests/ArchitectureTests.cs
git commit -m "ci(security): pin all GitHub Actions to 40-char commit SHAs and enforce in ArchitectureTests"
```
