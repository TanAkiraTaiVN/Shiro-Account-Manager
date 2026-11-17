> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

# P0 UI Dispatcher, Export/Prompt Security, Navigation & ViewModel Concurrency Fixes

**Goal:** Eliminate `AppDispatcher` double-execution on dispatcher exceptions, stop `ExportVaultAsync` from saving after a cancelled Save dialog and mask passphrase prompts, wire `MainWindow` bidirectional page navigation with `MainViewModel.CurrentPage`, and fix ViewModel async cancellation/re-entrancy bugs across `ServerListViewModel.Games`, `MissingAssetsViewModel`, and `ProxyManagerViewModel`.

**Architecture:** Keep dispatcher fallback limited strictly to headless environments without `Avalonia.Application.Current`, add secret prompt support cleanly through `IDialogService` + `InputDialog` without breaking existing fake/mock `IDialogService` implementations, connect `MainViewModel.OnPageNavigated` <-> `MainWindow.Navigation` without feedback loops, and isolate ViewModel cancellation tokens/command busy guards.

**Tech Stack:** .NET 10 (`net10.0-windows10.0.19041.0`), Avalonia UI 11, MVVM (`ObservableObject`, `RelayCommand`, `AsyncRelayCommand`), `xUnit`, `NSubstitute`.

**Spec:** System Audit Report (`C:/Users/lenovo/.gemini/antigravity/brain/d3d9db7e-c20c-4d8a-b056-391721b18014/system-audit-report.md`) - Section 2 (P0.5) & Section 3.6/3.7.

**Global Constraints:**
- `TreatWarningsAsErrors=true`, `<Nullable>enable</Nullable>`, strict ASCII English in ALL C# comments, docstrings, and log messages.
- Source files `<= 485` lines (`MainViewModel.cs` is 444/485); test files `<= 550` lines.
- `AsyncVoidHandlers_InViews_ShouldNotExceedBaseline`: `0` new `async void` methods in `src/Shiro/AvaloniaUI/`.
- No empty or untyped `catch` blocks; never place `{` or `}` inline in `[Fact]`/`[Theory]` string literals.
- Stage only explicit files per commit (never `git add -A`).

**Review Focus:**
- **Task 1:** Verify `AppDispatcher` executes actions/functions once in headless mode (`Application.Current == null`) and NEVER catches an active `Dispatcher.UIThread` exception to re-execute the delegate on the caller thread.
- **Task 2:** Verify `SettingsSideEffectsService.ExportVaultAsync` returns `false` with zero file writes/toasts when `SaveFilePickerAsync` returns `null`, `InputDialog` enables `PasswordChar = '*'` when `isSecret == true` regardless of localized title text, and all `IDialogService` test fakes/mocks compile and pass.
- **Task 3:** Verify setting `MainViewModel.CurrentPage` (via Command Palette, `NavigateTo*Command`, `Open*Command`, or `SelectRecentGameCommand`) switches `MainWindow` views and sidebar active state, `NavigationPage.About` works symmetrically, and `MainWindow.OnClosed` detaches subscriptions cleanly.
- **Task 4:** Verify `ServerListViewModel.IsSearchingGames` resets to `false` on cancellation/disposal, `MissingAssetsViewModel.BuyAsync` does not cancel an active `LoadAsync` token, and `ProxyManagerViewModel` guards `Add`/`Delete`/`RemoveDead`/`Save` while `TestAllAsync` is running (`IsBusy == true`).

---

### Task 1: Fix `AppDispatcher` Exception Fallthrough Double-Execution

**Context:**
- `src/Shiro/Services/Ui/AppDispatcher.cs:36-60,70-89,99-119`: `try` covers checking `Avalonia.Application.Current != null && Dispatcher.UIThread != null` AND invoking `action()` / `await avaloniaDispatcher.InvokeAsync(func)`. If `action()` or `Dispatcher.UIThread` throws while an active `Application.Current` dispatcher exists, control falls into `catch` (logs) and IMMEDIATELY invokes `action()` / `func()` a second time directly on the caller thread!

**Files:**
- Modify: `src/Shiro/Services/Ui/AppDispatcher.cs`
- Modify: `tests/Shiro.Tests/AvaloniaDialogServiceBehaviorTests.cs`
- Create: `tests/Shiro.Tests/P0UiAndViewModelRegressionTests.cs`

**Interfaces:**
- **Modified:** `AppDispatcher.Post`, `AppDispatcher.PostBackground`, `AppDispatcher.InvokeAsync<T>`, `AppDispatcher.InvokeAsync(Action)` plus optional internal helper `ShouldExecuteInlineWithoutDispatcher(...)` / `PostWithPriority`.

**Steps:**
- [ ] **Step 1: Write failing test(s) (TDD Red)**
  - In `tests/Shiro.Tests/P0UiAndViewModelRegressionTests.cs`:
    - Test headless path (`CustomDispatcher == null` and `Application.Current == null`): throwing inside `AppDispatcher.InvokeAsync(() => throw new InvalidOperationException("boom"))` throws ONCE and executes callback `invocationCount == 1` (not 2!). Same check for `AppDispatcher.InvokeAsync<int>` (`invocationCount == 1`) and `AppDispatcher.Post` throwing or via dispatcher guard helper!
    - Also test an internal/testable seam `AppDispatcher.DispatchOrFallback(bool hasActiveDispatcher, Action dispatch, Action fallback)` (or Avalonia dispatcher mock/seam): when `hasActiveDispatcher == true` and `dispatch()` throws `InvalidOperationException`, `fallback` MUST NOT be invoked (`invocationCount == 1`)!
- [ ] **Step 2: Run targeted tests to verify expected Red failure**
  Run:
  ```powershell
  dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~P0UiAndViewModelRegressionTests|FullyQualifiedName~AvaloniaDialogServiceBehaviorTests"
  ```
- [ ] **Step 3: Implement minimal fix**
  - Edit `src/Shiro/Services/Ui/AppDispatcher.cs`:
    - Check `bool hasActiveDispatcher = Avalonia.Application.Current != null && Dispatcher.UIThread != null;` cleanly (catching ONLY dispatcher acquisition errors if headless `Dispatcher.UIThread` itself throws `PlatformNotSupportedException` during startup probe).
    - Once `avaloniaDispatcher` is acquired and `CheckAccess()` / `Post` / `InvokeAsync` is executed, let delegate exceptions propagate out of `InvokeAsync` (and log cleanly in `Post` without re-executing `action()` inline on the caller thread!).
    - Keep the direct fallback ONLY when no active Avalonia application/dispatcher exists (`Avalonia.Application.Current == null || avaloniaDispatcher == null`).
- [ ] **Step 4: Run Dispatcher tests & ArchitectureTests to verify Green pass**
  Run:
  ```powershell
  dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~P0UiAndViewModelRegressionTests|FullyQualifiedName~AvaloniaDialogServiceBehaviorTests|FullyQualifiedName~ArchitectureTests"
  ```
- [ ] **Step 5: Commit**
  ```powershell
  git add src/Shiro/Services/Ui/AppDispatcher.cs tests/Shiro.Tests/P0UiAndViewModelRegressionTests.cs
  git commit -m "fix(ui): prevent AppDispatcher from re-executing callbacks twice on dispatcher failure"
  ```

---

### Task 2: Fix `ExportVaultAsync` Save Cancel Behavior & Mask Vault Passphrase Prompts

**Context:**
- `src/Shiro/Services/Ui/ISettingsSideEffects.cs:148-169`: when user selects `ExportVaultAsync` and cancels the `SaveFilePickerAsync` dialog (`file == null`), `path` falls through to `path ??= Path.Combine(StoragePaths.BackupsDirectory, ...)`, writing an unrequested vault backup file and displaying a success toast!
- `src/Shiro/Services/Ui/ISettingsSideEffects.cs:150,193` & `src/Shiro/AvaloniaUI/Views/InputDialog.axaml.cs:50-53`: vault passphrase prompts call `ShowInputPromptAsync(...)` whose localized title does not match `"password"` heuristic, leaving plaintext passphrases visible while typing!
- Existing tests implement `IDialogService` (`StubDialogService`, `FakeDialogService`, `WorkflowTestDialogService`) and mock `ShowInputPromptAsync(string, string, string)` with `NSubstitute` (`tests/Shiro.Tests/BugHuntRegressionTests.cs:252`).

**Files:**
- Modify: `src/Shiro/Services/Ui/IDialogService.cs`
- Modify: `src/Shiro/Services/Ui/AvaloniaDialogService.cs`
- Modify: `src/Shiro/AvaloniaUI/Views/InputDialog.axaml.cs`
- Modify: `src/Shiro/Services/Ui/ISettingsSideEffects.cs`
- Modify: `tests/Shiro.Tests/BugHuntRegressionTests.cs`
- Modify: `tests/Shiro.Tests/P0UiAndViewModelRegressionTests.cs`

**Interfaces:**
- **Modified:** `IDialogService` adds `Task<string> ShowSecretPromptAsync(string message, string title, string defaultValue = "") => ShowInputPromptAsync(message, title, defaultValue);` (default interface implementation preserves full compile compatibility with all existing fake implementations!). `AvaloniaDialogService.ShowSecretPromptAsync` delegates to `InputDialog.PromptAsync(..., isSecret: true)`.
- **Modified:** `InputDialog` (`CreateDialogInstance` / `PromptAsync` / `Prompt`) gains optional `bool isSecret = false` parameter setting `TxtInput.PasswordChar = '*'` (or `*`) deterministically without relying on localized title string matching.
- **Modified:** `SettingsSideEffectsService` distinguishes `"no storage provider / headless mode"` vs `"user explicitly cancelled picker"` (`ResolveExportDestinationAsync` / test seam or picker delegate) so cancelling the Save dialog returns `false` without creating any file!

**Steps:**
- [ ] **Step 1: Write failing test(s) (TDD Red)**
  - In `tests/Shiro.Tests/P0UiAndViewModelRegressionTests.cs`:
    - Test `SettingsSideEffectsService.ExportVaultAsync` when picker seam/override returns `cancelled: true` (`null` from `SaveFilePickerAsync` in an interactive window): assert `ExportVaultAsync()` returns `false`, creates zero `.shirovault`/backup files, and does NOT call `dialogs.ShowInfo`!
    - Test `SettingsSideEffectsService.ExportVaultAsync` & `ImportVaultFromPathAsync` invoke `dialogs.ShowSecretPromptAsync(...)` instead of unmasked `ShowInputPromptAsync(...)` when requesting vault passphrase.
    - Test `InputDialog` secret configuration helper (e.g., `InputDialog.ShouldMaskInput(string? caption, bool isSecret)`) returns `true` for ANY localized caption (including Vietnamese `"Nhập mật khẩu kho..."`) whenever `isSecret == true`.
  - Update `tests/Shiro.Tests/BugHuntRegressionTests.cs:252`: configure `dialogs.ShowSecretPromptAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult("Passphrase#123"));` alongside `ShowInputPromptAsync` so NSubstitute intercepts `ImportVaultFromPathAsync`.
- [ ] **Step 2: Run targeted tests to verify expected Red failure**
  Run:
  ```powershell
  dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~P0UiAndViewModelRegressionTests|FullyQualifiedName~BugHuntRegressionTests|FullyQualifiedName~P2CleanupAndUiWiringRegressionTests|FullyQualifiedName~SettingsViewModelTests"
  ```
- [ ] **Step 3: Implement minimal fixes**
  - Edit `src/Shiro/AvaloniaUI/Views/InputDialog.axaml.cs`: add optional `bool isSecret = false` to `CreateDialogInstance`, `PromptAsync`, and `Prompt` (keeping old parameter order defaults intact so existing callers compile unchanged), setting `dialog.TxtInput.PasswordChar = '*';` whenever `isSecret` (or existing password heuristic) is true.
  - Edit `src/Shiro/Services/Ui/IDialogService.cs` & `AvaloniaDialogService.cs`: add `ShowSecretPromptAsync(string message, string title, string defaultValue = "")` (default implementation on `IDialogService`, explicit `isSecret: true` override in `AvaloniaDialogService`).
  - Edit `src/Shiro/Services/Ui/ISettingsSideEffects.cs`:
    - Use `ShowSecretPromptAsync` for Export & Import passphrase prompts (`lines 150, 193`).
    - When `ApplicationLifetime` has an interactive desktop window with `StorageProvider.CanSave`, returning `null` from `SaveFilePickerAsync` MUST cancel and return `false` cleanly (no fallback into `BackupsDirectory` and no success toast!). Also provide an internal seam `internal static Func<string, Task<(bool Confirmed, string? Path)>>? TestPickExportPathOverride` so headless tests can deterministically exercise Save vs Cancel without Avalonia window creation.
- [ ] **Step 4: Run UI/Vault/Settings tests & ArchitectureTests to verify Green pass**
  Run:
  ```powershell
  dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~P0UiAndViewModelRegressionTests|FullyQualifiedName~BugHuntRegressionTests|FullyQualifiedName~P2CleanupAndUiWiringRegressionTests|FullyQualifiedName~SettingsViewModelTests|FullyQualifiedName~ArchitectureTests"
  ```
- [ ] **Step 5: Commit**
  ```powershell
  git add src/Shiro/AvaloniaUI/Views/InputDialog.axaml.cs src/Shiro/Services/Ui/IDialogService.cs src/Shiro/Services/Ui/AvaloniaDialogService.cs src/Shiro/Services/Ui/ISettingsSideEffects.cs tests/Shiro.Tests/BugHuntRegressionTests.cs tests/Shiro.Tests/P0UiAndViewModelRegressionTests.cs
  git commit -m "fix(ui): cancel vault export cleanly on aborted file picker and mask passphrase prompts"
  ```

---

### Task 3: Wire `MainWindow` <-> `MainViewModel.CurrentPage` Bidirectional Navigation

**Context:**
- `src/Shiro/ViewModels/MainViewModel.cs:14-66`: defines `NavigationPage` (`Accounts`, `ServerList`, `RecentGames`, `Settings` — missing `About`), `CurrentPage`, `Is*Active`, and `Action<NavigationPage>? OnPageNavigated`.
- `src/Shiro/AvaloniaUI/Views/MainWindow.axaml.cs` and `MainWindow.Navigation.cs:105-156` never subscribe to `ViewModel.OnPageNavigated` (nor sync `ViewModel.CurrentPage` when sidebar buttons `BtnNavDashboard`, `BtnNavServerList`, `BtnNavRecentGames`, `BtnNavSettings`, `BtnNavAbout` are clicked)! Consequently, Command Palette navigation (`CommandPaletteCatalog.cs:113`), `NavigateTo*Command`, `OpenSettingsCommand`, `OpenServerListCommand`, `OpenRecentGamesCommand`, and `SelectRecentGameCommand` change `ViewModel.CurrentPage` in memory with zero visual page transition in `MainWindow`!

**Files:**
- Modify: `src/Shiro/ViewModels/MainViewModel.cs`
- Modify: `src/Shiro/AvaloniaUI/Views/MainWindow.axaml.cs`
- Modify: `src/Shiro/AvaloniaUI/Views/MainWindow.Navigation.cs`
- Modify: `src/Shiro/AvaloniaUI/Views/MainWindow.axaml` (if nav active class/accessibility or `Tag` bindings improve cleanly, preserving all existing `Click` handlers / names!)
- Modify: `tests/Shiro.Tests/MainViewModelStatusAndCountsTests.cs`
- Modify: `tests/Shiro.Tests/P0UiAndViewModelRegressionTests.cs`

**Interfaces:**
- **Modified:** `NavigationPage` enum adds `About`. `MainViewModel` adds `IsAboutActive` (`OnPropertyChanged(nameof(IsAboutActive))` on `CurrentPage` change) and clears `OnPageNavigated = null` inside `Dispose()`.
- **Modified:** `MainWindow` adds internal/private `ApplyNavigationPage(NavigationPage page)` wired to `ViewModel.OnPageNavigated` during initialization and detached in `OnClosed`, plus keeps `ViewModel.CurrentPage` synchronized inside sidebar click handlers (`OnNavDashboardClicked`, `OnNavServerListClicked`, `OnNavRecentGamesClicked`, `OnNavSettingsClicked`, `OnNavAboutClicked`).

**Steps:**
- [ ] **Step 1: Write failing test(s) (TDD Red)**
  - In `tests/Shiro.Tests/MainViewModelStatusAndCountsTests.cs` (note file line budget `<= 550`!) or `tests/Shiro.Tests/P0UiAndViewModelRegressionTests.cs`:
    - Test `MainViewModel.CurrentPage` supports `NavigationPage.About`, raises `IsAboutActive == true` / `PropertyChanged`, and clears `OnPageNavigated` subscribers on `Dispose()`.
    - Test programmatic navigation helper/wiring: given a `MainViewModel`, subscribing an adapter equivalent / `MainWindow.ResolveNavigationTarget` (or `[AvaloniaFact]` on `MainWindow` if headless Avalonia test runner supports `new MainWindow()` — check `DialogSmokeTests.cs` pattern!): setting `vm.CurrentPage = NavigationPage.Settings` updates active view visibility to `ViewSettings` (`IsVisible == true`) and active nav button to `BtnNavSettings`; `NavigationPage.RecentGames` refreshes recent games & switches to `ViewServerList` + `BtnNavRecentGames`; `NavigationPage.About` switches to `ViewAbout` + `BtnNavAbout`; `NavigationPage.Accounts` switches back to `ViewDashboard` + `BtnNavDashboard`.
- [ ] **Step 2: Run targeted tests to verify expected Red failure**
  Run:
  ```powershell
  dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~P0UiAndViewModelRegressionTests|FullyQualifiedName~MainViewModelStatusAndCountsTests|FullyQualifiedName~DialogSmokeTests"
  ```
- [ ] **Step 3: Implement minimal fixes**
  - Edit `src/Shiro/ViewModels/MainViewModel.cs`:
    - Add `About` to `NavigationPage` (`line 14`).
    - Add `public bool IsAboutActive => CurrentPage == NavigationPage.About;` and raise `OnPropertyChanged(nameof(IsAboutActive))` inside `CurrentPage` setter (`lines 44-63`). Keep `MainViewModel.cs <= 485` lines!
    - Clear `OnPageNavigated = null;` in `Dispose()`.
  - Edit `src/Shiro/AvaloniaUI/Views/MainWindow.axaml.cs` & `MainWindow.Navigation.cs`:
    - In `MainWindow` constructor (`after ViewDashboard.ViewModel` setup around `lines 40-50`): subscribe `ViewDashboard.ViewModel.OnPageNavigated = ApplyNavigationPage;` (and call `ApplyNavigationPage(ViewDashboard.ViewModel.CurrentPage)`).
    - Implement `internal void ApplyNavigationPage(NavigationPage page)` in `MainWindow.Navigation.cs` handling `Accounts`, `ServerList` (sets `ViewServerList.DataContext`), `RecentGames` (reloads recent games + sets `ViewServerList.DataContext`), `Settings`, and `About`, invoking `SetActiveView(...)`.
    - Update `OnNavDashboardClicked`, `OnNavServerListClicked`, `OnNavRecentGamesClicked`, `OnNavSettingsClicked`, `OnNavAboutClicked` to update `if (ViewModel != null) ViewModel.CurrentPage = NavigationPage.*; else ApplyNavigationPage(...);` (guarded so `CurrentPage` setter skips re-entrancy because `SetProperty` no-ops on unchanged value, OR call `SetActiveView` cleanly).
    - Detach `if (ViewDashboard?.ViewModel != null) ViewDashboard.ViewModel.OnPageNavigated = null;` in `MainWindow.OnClosed` before `Dispose()`.
- [ ] **Step 4: Run Navigation/ViewModel tests & ArchitectureTests to verify Green pass**
  Run:
  ```powershell
  dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~P0UiAndViewModelRegressionTests|FullyQualifiedName~MainViewModelStatusAndCountsTests|FullyQualifiedName~DialogSmokeTests|FullyQualifiedName~CommandPalette|FullyQualifiedName~ArchitectureTests"
  ```
  Verify `AsyncVoidHandlers_InViews_ShouldNotExceedBaseline` (`0`) and `MainViewModel.cs <= 485` lines.
- [ ] **Step 5: Commit**
  ```powershell
  git add src/Shiro/ViewModels/MainViewModel.cs src/Shiro/AvaloniaUI/Views/MainWindow.axaml.cs src/Shiro/AvaloniaUI/Views/MainWindow.Navigation.cs tests/Shiro.Tests/P0UiAndViewModelRegressionTests.cs
  git commit -m "fix(ui): wire MainWindow views bidirectionally with MainViewModel.CurrentPage"
  ```

---

### Task 4: Fix Async ViewModel Concurrency & Reset Bugs (`ServerListViewModel.Games`, `MissingAssetsViewModel`, `ProxyManagerViewModel`)

**Context:**
- `src/Shiro/ViewModels/ServerListViewModel.Games.cs:144-151`: when an in-flight `SearchGamesAsync` is cancelled/disposed via `_gamesSearchCts` exchange to `null` (or external cancellation without a replacement search), `!ReferenceEquals(Volatile.Read(ref _gamesSearchCts), cts)` returns early and skips `IsSearchingGames = false`, leaving the UI permanently stuck on a loading spinner!
- `src/Shiro/ViewModels/Dialogs/MissingAssetsViewModel.cs:262-265`: both `LoadAsync` and `BuyAsync` call `DialogViewModelBase.BeginOperation()`, sharing a single `_operationCts` so starting a purchase cancels and disposes an active `LoadAsync` token!
- `src/Shiro/ViewModels/Dialogs/ProxyManagerViewModel.cs:31-35`: `AddProxyCommand`, `DeleteProxyCommand`, `RemoveDeadCommand`, and `SaveCommand` are plain untracked `RelayCommand`s that run without checking `!IsBusy`, allowing user mutations to race with `TestAllAsync`.

**Files:**
- Modify: `src/Shiro/ViewModels/ServerListViewModel.Games.cs`
- Modify: `src/Shiro/ViewModels/Dialogs/MissingAssetsViewModel.cs`
- Modify: `src/Shiro/ViewModels/Dialogs/ProxyManagerViewModel.cs`
- Modify: `tests/Shiro.Tests/ServerListViewModelBehaviorTests.cs`
- Modify: `tests/Shiro.Tests/Dialogs/MissingAssetsViewModelTests.cs`
- Modify: `tests/Shiro.Tests/Dialogs/ProxyManagerViewModelTests.cs`
- Modify: `tests/Shiro.Tests/P0UiAndViewModelRegressionTests.cs`

**Interfaces:**
- **Modified (`ServerListViewModel.Games.cs`):** `finally` resets `IsSearchingGames = false` whenever no OTHER active CTS owns `_gamesSearchCts` (`var current = Volatile.Read(ref _gamesSearchCts); if (current == null || ReferenceEquals(current, cts)) IsSearchingGames = false;`).
- **Modified (`MissingAssetsViewModel.cs`):** Separate `_loadCts` (`BeginLoadOperation()`) and `_purchaseCts` (`BeginPurchaseOperation()`), cancelling both cleanly in `override void OnClosing()`.
- **Modified (`ProxyManagerViewModel.cs`):** Register `AddProxyCommand`, `DeleteProxyCommand`, `RemoveDeadCommand`, and `SaveCommand` via `TrackCommand(new RelayCommand(..., _ => !IsBusy))` (and guard inside command handlers + snapshot `Proxies.ToArray()` inside `TestAllAsync`).

**Steps:**
- [ ] **Step 1: Write failing test(s) (TDD Red)**
  - In `tests/Shiro.Tests/P0UiAndViewModelRegressionTests.cs`:
    - `ServerListViewModel`: start `SearchGamesAsync()`, cancel/dispose `ServerListViewModel` (clearing `_gamesSearchCts` while suspended), await task completion, and assert `vm.IsSearchingGames == false`!
    - `MissingAssetsViewModel`: start `LoadAsync()` with an in-flight slow asset fetch while triggering `BuyAsync(...)` (with confirmation mocked `true`), verify `LoadAsync`'s `CancellationToken` was NOT cancelled when `BuyAsync` began (independent `_purchaseCts`), AND verify `OnClosing()` cancels both!
    - `ProxyManagerViewModel`: start `TestAllAsync()` suspended on a slow/mock proxy probe (`IsBusy == true`), assert `AddProxyCommand.CanExecute(null) == false`, `DeleteProxyCommand.CanExecute(...) == false`, `RemoveDeadCommand.CanExecute(null) == false`, `SaveCommand.CanExecute(null) == false`, and directly calling `DeleteProxy`/`RemoveDead`/`AddProxy` is ignored while `IsBusy` until `TestAllAsync` completes!
- [ ] **Step 2: Run targeted tests to verify expected Red failure**
  Run:
  ```powershell
  dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~P0UiAndViewModelRegressionTests|FullyQualifiedName~ServerListViewModelBehaviorTests|FullyQualifiedName~MissingAssetsViewModelTests|FullyQualifiedName~ProxyManagerViewModelTests|FullyQualifiedName~BugHuntRound2Tests"
  ```
- [ ] **Step 3: Implement minimal fixes**
  - Edit `src/Shiro/ViewModels/ServerListViewModel.Games.cs:144-151`: reset `IsSearchingGames = false` when `Volatile.Read(ref _gamesSearchCts) is null || ReferenceEquals(Volatile.Read(ref _gamesSearchCts), cts)`.
  - Edit `src/Shiro/ViewModels/Dialogs/MissingAssetsViewModel.cs`: add private `CancellationTokenSource? _loadCts` and `_purchaseCts` managed via `ReplaceOperationCts(ref CancellationTokenSource? slot)` (`Interlocked.Exchange` + safe cancel/dispose), keep `LoadAsync` on `_loadCts` and `BuyAsync` on `_purchaseCts`, and cancel/dispose both inside `public override void OnClosing() { CancelSlot(ref _loadCts); CancelSlot(ref _purchaseCts); base.OnClosing(); }`.
  - Edit `src/Shiro/ViewModels/Dialogs/ProxyManagerViewModel.cs:31-35`: wrap `AddProxyCommand`, `DeleteProxyCommand`, `RemoveDeadCommand`, and `SaveCommand` with `TrackCommand(...)` + `!IsBusy` predicates, guard methods with `if (IsBusy) return;`, and snapshot `Proxies.ToArray()` inside `TestAllAsync`.
- [ ] **Step 4: Run ViewModel tests & ArchitectureTests to verify Green pass**
  Run:
  ```powershell
  dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~P0UiAndViewModelRegressionTests|FullyQualifiedName~ServerListViewModelBehaviorTests|FullyQualifiedName~MissingAssetsViewModelTests|FullyQualifiedName~ProxyManagerViewModelTests|FullyQualifiedName~BugHuntRound2Tests|FullyQualifiedName~AuditUiAndAuthRegressionTests|FullyQualifiedName~ArchitectureTests"
  ```
- [ ] **Step 5: Commit**
  ```powershell
  git add src/Shiro/ViewModels/ServerListViewModel.Games.cs src/Shiro/ViewModels/Dialogs/MissingAssetsViewModel.cs src/Shiro/ViewModels/Dialogs/ProxyManagerViewModel.cs tests/Shiro.Tests/P0UiAndViewModelRegressionTests.cs
  git commit -m "fix(vm): fix async search spinner reset, separate MissingAssets CTS, and guard ProxyManager busy state"
  ```

---

## Self-Review & Execution Gate
- All 4 plans cover 100% of P0 findings in `system-audit-report.md` Section 2 with exact test commands, explicit staging lists, strict line limits (`<= 485` src / `<= 550` tests), and preserved coverage floors.
