# P2 Architecture, State Ownership & v5.0.0 Documentation Sync Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminate the remaining architectural layer violations and `NetArchTest` namespace blind spots around `AccountManager`/`Account`, fix static-event memory leaks on `MainViewModel` and `ServerListViewModel` via `IDisposable`, transfer in-memory account collection ownership from static `AccountManager` into `AccountRepository` while removing synchronous HTTP I/O from the `Account` constructor, and synchronize all `v5.0.0` version metadata and technical documentation under automated test enforcement.

**Architecture:** We complete the Strangler Fig decoupling in four self-contained TDD tasks. First, we route all remaining `MainWindow.Instance` and `InputDialog` calls in `AccountManager*.cs` through `HostUiBridgeService` and `IDialogService`, then tighten `ArchitectureTests` to enforce zero `Shiro.AvaloniaUI.Views` dependencies across every non-UI namespace (including root `namespace Shiro;`). Second, we implement `IDisposable` on `MainViewModel` and `ServerListViewModel` (invoked on `MainWindow.OnClosed`) to unsubscribe all static events (`LocalizationService`, `RobloxAudioService`, `AccentThemeService`, `LaunchCoordinator`, `StreamerModeService`) and cancel active `CancellationTokenSource`s. Third, we move the in-memory account collection, lock, and snapshot cache into `AccountRepository`, remove blocking `httpClient.Send(req)` from `new Account(cookie, accountJson)`, and scrub plaintext backup buffers in `AccountManager.BackupRecovery.cs`. Fourth, we align `Shiro.csproj`, `AboutView.axaml`, `en.json`/`vi.json`, `ApiDocumentationService`, `KestrelWebServer`, and markdown docs on `v5.0.0` with a permanent `ArchitectureTests` invariant.

**Tech Stack:** .NET 10 (`net10.0-windows10.0.19041.0`, `win-x64`), C# 13, Avalonia UI 11, xUnit, NSubstitute, NetArchTest.eNhancedEdition.

**Spec:** `C:\Users\lenovo\.gemini\antigravity\brain\693b9b61-05d2-4a86-8da9-d28370a36cec\project_audit_report.md` (§4.1, §4.2, §4.3, §5 Phase 3)

## Global Constraints

- Target framework: `net10.0-windows10.0.19041.0` (`win-x64`) with `<Nullable>enable</Nullable>` and `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` (`0` warnings allowed).
- File line budgets (`ArchitectureTests.NoSourceCodeFiles_ShouldExceed500Lines` & `NoAxamlFiles_ShouldExceed400Lines`): Every `.cs` file in `src/Shiro` must be `<= 500` lines; every `.axaml` file in `src/Shiro` must be `<= 400` lines with max line length `<= 200` chars.
- Partial class line budgets (`ArchitectureTests.NoClass_ShouldExceedLineBudget_AcrossPartials`): `Account <= 2900`, `MainViewModel <= 3500` (currently `3390`), `AccountManager <= 3400`, `ServerListViewModel <= 1800` (currently `1780`), all other classes `<= 1200`.
- Per-file branch coverage floors (`tests/coverage-floors.json` & `scripts/Check-Coverage.ps1`): Overall Line `>= 75.0%`, Overall Branch `>= 60.0%`, Core `>= 92.0%`, plus all 19 per-file branch floors (`AccountManager.cs >= 85%`, `AccountManager.ModernActions.cs >= 63%`, `AccountManager.Persistence.cs >= 67%`, `AccountManager.Accounts.cs >= 75%`, `AccountManager.Lifecycle.cs >= 70%`).
- Test assertion analyzer (`ArchitectureTests.TestMethods_ShouldAssertOnValues_NotOnlyNullness`): Every `[Fact]`/`[Theory]` must contain strong value assertions (`Assert.Equal`, `Assert.Same`, `Assert.False`, `Assert.Contains`, etc.). Never put inline regex literals containing `{` or `}` inside test method bodies; hoist them to `private static readonly Regex` fields.
- Zero reflection JSON (`ArchitectureTests.ProductionCode_ShouldNotUseReflectionBasedJsonApis`): Every `JsonSerializer` call in `src/Shiro` must use `ShiroAppJson` or `ShiroIndentedJsonContext`.

## Review Focus

1. **Disposed `MainViewModel` or `ServerListViewModel` receiving static events (`LanguageChanged`, `StatusChanged`, `ThemeChanged`, `LaunchProgressChanged`, `StreamerModeService.StateChanged`):** After `Dispose()` is called on `MainViewModel`, firing any of those 5 static events must not invoke `PropertyChanged` or mutate `MainViewModel` / `ServerListViewModel` state (pinned by `MainViewModel_Dispose_UnsubscribesAllStaticEvents_AndIsIdempotent` in Task 2).
2. **`Account(string Cookie, string? AccountJSON = null)` called with raw cookie and `AccountJSON == null` when offline or without network mock:** Must complete instantaneously (`< 50ms`) with `Valid == false` and `SecurityToken == Account.SanitizeCookie(Cookie)` without performing any network request or throwing (pinned by `Account_SyncConstructor_DoesNotPerformNetworkIo_WhenAccountJsonMissingOrInvalid` in Task 3).
3. **`AccountRepository` instance isolation vs `AccountRepository.Default` singleton state:** Creating a standalone `new AccountRepository()` in tests must manage its own isolated in-memory account list and `SnapshotIndex` without mutating `AccountRepository.Default` or `AccountManager.Accounts`, while `AccountManager` static helpers (`GetAccountsSnapshot`, `AccountCount`, `AddAccountThreadSafe`, `ReplaceAccountsList`) stay 100% synchronized with `AccountRepository.Default` (pinned by `AccountRepository_OwnsInMemoryCollection_IsolatesInstances_AndSyncsWithAccountManager` in Task 3).
4. **`AccountManager.ModernJoinServerDirect` and `ModernCloseRoblox` when `HostUiBridgeService` UI callbacks are registered vs null:** When `HostUiBridgeService` UI callbacks (`GetDashboardPlaceIdHandler`, `GetDashboardJobIdHandler`, `NotifyRobloxProcessesClosedOnUiHandler`) are registered, `AccountManager.ModernActions` must consume them cleanly; when null (headless mode), it must fall back to `CurrentPlaceId`, `CurrentJobId`, and `SelectedAccounts` without throwing (pinned by `AccountManager_ModernActions_UsesHostUiBridgeCallbacks_WithoutReferencingViews` in Task 1).
5. **Plaintext backup buffer hygiene in `AccountManager.TryRestoreReadableBackup()`:** When scanning backup candidates in `AccountManager.BackupRecovery.cs`, any decrypted/unprotected `byte[] decoded` buffer must be zeroed via `CryptographicOperations.ZeroMemory(decoded)` in a `finally` block even when deserialization fails or a later candidate wins (pinned by `BackupRecovery_SourceCode_ZeroesDecodedPlaintextBuffersInFinally` in Task 3).

---

### Task 1: Decouple `AccountManager` from `Shiro.AvaloniaUI.Views` and Close the `namespace Shiro;` Blind Spot in `ArchitectureTests`

**Files:**
- Modify: `src/Shiro/Services/Ui/IDialogService.cs:14-16`
- Modify: `src/Shiro/Services/Ui/AvaloniaDialogService.cs:101-106`
- Modify: `src/Shiro/Services/Ui/IHostUiBridgeService.cs:10-30`
- Modify: `src/Shiro/Services/Ui/HostUiBridgeService.cs:12-40`
- Modify: `src/Shiro/AvaloniaUI/Views/MainWindow.axaml.cs:20-38,205-245`
- Modify: `src/Shiro/AccountManager.cs:20-23,260-270,437-449`
- Modify: `src/Shiro/AccountManager.Accounts.cs:16-23,111-113`
- Modify: `src/Shiro/AccountManager.ModernActions.cs:10-30,77-105,136-162`
- Modify: `src/Shiro/AccountManager.Persistence.cs:70-80`
- Modify: `src/Shiro/AccountManager.Presence.cs:35-44`
- Modify: `tests/Shiro.Tests/ArchitectureTests.cs:32-50`
- Test: `tests/Shiro.Tests/AccountManagerBehaviorTests.cs`

**Interfaces:**
- Produces:
  - `IDialogService.ShowInputPromptAsync(string message, string title, string defaultValue = "") -> Task<string>` (with default interface implementation `=> Task.FromResult(ShowInputPrompt(message, title, defaultValue))` so existing test doubles implementing `IDialogService` remain source-compatible)
  - `HostUiBridgeService` UI notification & query hooks:
    - `Action<IReadOnlyList<Account>>? RefreshAccountsOnUiHandler { get; set; }`
    - `Action<Account, bool>? NotifyAccountValidityOnUiHandler { get; set; }`
    - `Action? RefreshLiveProcessStatusOnUiHandler { get; set; }`
    - `Action<string>? UpdateWindowStatusOnUiHandler { get; set; }`
    - `Action<int, bool>? NotifyRobloxProcessesClosedOnUiHandler { get; set; }`
    - `Func<List<Account>?>? GetDashboardSelectedAccountsHandler { get; set; }`
    - `Func<string?>? GetDashboardPlaceIdHandler { get; set; }`
    - `Func<string?>? GetDashboardJobIdHandler { get; set; }`
    - `void NotifyAccountsReplaced(IReadOnlyList<Account> accounts)`
    - `void NotifyAccountValidityChanged(Account account, bool isValid)`
    - `void NotifyLiveProcessStatusChanged()`
    - `void NotifyWindowStatus(string message)`
    - `void NotifyRobloxProcessesClosed(int totalKilled, bool notify)`
    - `List<Account>? TryGetDashboardSelectedAccounts()`
    - `string? TryGetDashboardPlaceId()`
    - `string? TryGetDashboardJobId()`

- [ ] **Step 1: Update `ArchitectureTests.Services_And_Classes_ShouldNotDependOn_Views` and add `AccountManager_ModernActions_UsesHostUiBridgeCallbacks_WithoutReferencingViews`**

In `tests/Shiro.Tests/ArchitectureTests.cs`, update `Services_And_Classes_ShouldNotDependOn_Views` (lines 33-50) to inspect all types in `CoreAssembly` that `DoNotResideInNamespace("Shiro.AvaloniaUI")` (which includes root `namespace Shiro;`, `Shiro.Classes`, `Shiro.Services`, `Shiro.ViewModels`, `Shiro.Nexus`, `Shiro.Http`):

```csharp
        [Fact]
        public void Services_And_Classes_ShouldNotDependOn_Views()
        {
            var coveredTypes = Types.InAssembly(CoreAssembly)
                .That()
                .DoNotResideInNamespace("Shiro.AvaloniaUI")
                .And()
                .DoNotHaveName("AvaloniaDialogService")
                .GetTypes()
                .ToList();

            Assert.Contains(typeof(AccountManager), coveredTypes);
            Assert.Contains(typeof(Account), coveredTypes);

            var result = Types.InAssembly(CoreAssembly)
                .That()
                .DoNotResideInNamespace("Shiro.AvaloniaUI")
                .And()
                .DoNotHaveName("AvaloniaDialogService")
                .ShouldNot()
                .HaveDependencyOnAny("Shiro.AvaloniaUI.Views")
                .GetResult();

            var failingNames = (result.FailingTypes?.Select(t => t.FullName ?? t.Name) ?? Enumerable.Empty<string>())
                .Where(name => !name.StartsWith("Shiro.Services.AvaloniaDialogService", StringComparison.Ordinal))
                .ToList();
            Assert.True(failingNames.Count == 0, $"Non-UI types (except AvaloniaDialogService) should not depend on Views. Violations: {string.Join(", ", failingNames)}");
        }
```

In `tests/Shiro.Tests/AccountManagerBehaviorTests.cs`, add test `AccountManager_ModernActions_UsesHostUiBridgeCallbacks_WithoutReferencingViews`:

```csharp
        [Fact]
        public async Task AccountManager_ModernActions_UsesHostUiBridgeCallbacks_WithoutReferencingViews()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            var oldCoordinator = mgr.launchCoordinator;
            var fakeCoord = new FakeLaunchCoordinator();
            mgr.launchCoordinator = fakeCoord;

            var bridge = HostUiBridgeService.Default;
            var prevPlaceHandler = bridge.GetDashboardPlaceIdHandler;
            var prevJobHandler = bridge.GetDashboardJobIdHandler;
            var prevSelHandler = bridge.GetDashboardSelectedAccountsHandler;
            var prevStatusHandler = bridge.UpdateWindowStatusOnUiHandler;
            var prevClosedHandler = bridge.NotifyRobloxProcessesClosedOnUiHandler;

            var acc = new Account { Username = "BridgeUser", UserID = 7788, Valid = true, SecurityToken = "tok" };
            string? lastStatus = null;
            (int Killed, bool Notify)? closedNotification = null;

            try
            {
                AccountManager.ReplaceAccountsList(new List<Account> { acc });
                bridge.GetDashboardSelectedAccountsHandler = () => new List<Account> { acc };
                bridge.GetDashboardPlaceIdHandler = () => "1818";
                bridge.GetDashboardJobIdHandler = () => "bridge-job-42";
                bridge.UpdateWindowStatusOnUiHandler = msg => lastStatus = msg;
                bridge.NotifyRobloxProcessesClosedOnUiHandler = (killed, notify) => closedNotification = (killed, notify);

                await mgr.ModernJoinServerDirect(new List<Account>(), 0, "");
                Assert.Single(fakeCoord.EnqueuedBatches);
                Assert.Equal(1818L, fakeCoord.EnqueuedBatches[0][0].PlaceId);
                Assert.Equal("bridge-job-42", fakeCoord.EnqueuedBatches[0][0].JobId);
                Assert.False(string.IsNullOrWhiteSpace(lastStatus));

                mgr.ModernCloseRoblox(notify: true);
                Assert.NotNull(closedNotification);
                Assert.True(closedNotification!.Value.Notify);
            }
            finally
            {
                bridge.GetDashboardPlaceIdHandler = prevPlaceHandler;
                bridge.GetDashboardJobIdHandler = prevJobHandler;
                bridge.GetDashboardSelectedAccountsHandler = prevSelHandler;
                bridge.UpdateWindowStatusOnUiHandler = prevStatusHandler;
                bridge.NotifyRobloxProcessesClosedOnUiHandler = prevClosedHandler;
                mgr.launchCoordinator = oldCoordinator;
                AccountManager.ReplaceAccountsList(new List<Account>());
            }
        }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" -c Release --filter "FullyQualifiedName~Services_And_Classes_ShouldNotDependOn_Views|FullyQualifiedName~AccountManager_ModernActions_UsesHostUiBridgeCallbacks_WithoutReferencingViews" --nologo`
Expected: FAIL (`AccountManager` depends on `Shiro.AvaloniaUI.Views` and `HostUiBridgeService` does not yet define the UI sink properties).

- [ ] **Step 3: Implement `IDialogService.ShowInputPromptAsync`, `HostUiBridgeService` UI sink delegates, and decouple `AccountManager*.cs` from `Shiro.AvaloniaUI.Views`**

1. In `src/Shiro/Services/Ui/IDialogService.cs`, add:
   ```csharp
   System.Threading.Tasks.Task<string> ShowInputPromptAsync(string message, string title, string defaultValue = "")
       => System.Threading.Tasks.Task.FromResult(ShowInputPrompt(message, title, defaultValue));
   ```
   And in `src/Shiro/Services/Ui/AvaloniaDialogService.cs`, implement:
   ```csharp
   public Task<string> ShowInputPromptAsync(string message, string title, string defaultValue = "")
   {
       var owner = GetActiveOwner();
       return InputDialog.PromptAsync(message, title, defaultValue, false, owner);
   }
   ```
2. In `src/Shiro/Services/Ui/IHostUiBridgeService.cs` (with default no-op implementations) and `src/Shiro/Services/Ui/HostUiBridgeService.cs`, add the UI sink delegates (`RefreshAccountsOnUiHandler`, `NotifyAccountValidityOnUiHandler`, `RefreshLiveProcessStatusOnUiHandler`, `UpdateWindowStatusOnUiHandler`, `NotifyRobloxProcessesClosedOnUiHandler`, `GetDashboardSelectedAccountsHandler`, `GetDashboardPlaceIdHandler`, `GetDashboardJobIdHandler`) and corresponding methods (`NotifyAccountsReplaced`, `NotifyAccountValidityChanged`, `NotifyLiveProcessStatusChanged`, `NotifyWindowStatus`, `NotifyRobloxProcessesClosed`, `TryGetDashboardSelectedAccounts`, `TryGetDashboardPlaceId`, `TryGetDashboardJobId`).
3. In `src/Shiro/AvaloniaUI/Views/MainWindow.axaml.cs`, wire `HostUiBridgeService.Default`'s UI sink handlers in `MainWindow()` to `RefreshAccounts`, `UpdateAccountValidity`, `RefreshLiveProcessStatus`, `SetStatus`, `ViewDashboard?.ViewModel?.GetSelectedAccounts()`, `ViewDashboard?.ViewModel?.PlaceId`, `ViewDashboard?.ViewModel?.JobId`, and a helper `OnRobloxProcessesClosed(int totalKilled, bool notify)` that updates `ViewDashboard?.ViewModel`.
4. Replace all `MainWindow.Instance` and `InputDialog` references in:
   - `src/Shiro/AccountManager.cs`: remove `using Shiro.AvaloniaUI.Views;`; replace `MainWindow.Instance?.RefreshAccounts(snapshot)` with `HostUiBridgeService.Default.NotifyAccountsReplaced(snapshot)`; replace `InputDialog.Prompt(...)` in `ShowDialog` with `AppServices.Dialogs.ShowInputPrompt(text, caption, defaultText)`.
   - `src/Shiro/AccountManager.Accounts.cs`: replace `MainWindow.Instance?.UpdateAccountValidity(account, isValid)` with `HostUiBridgeService.Default.NotifyAccountValidityChanged(account, isValid)` and `MainWindow.Instance?.RefreshLiveProcessStatus()` with `HostUiBridgeService.Default.NotifyLiveProcessStatusChanged()`.
   - `src/Shiro/AccountManager.ModernActions.cs`: replace `MainWindow.Instance?.SetStatus(message)` with `HostUiBridgeService.Default.NotifyWindowStatus(message)`; replace `MainWindow.Instance?.DashboardView?.ViewModel?.GetSelectedAccounts()` with `HostUiBridgeService.Default.TryGetDashboardSelectedAccounts()`; replace `MainWindow.Instance?.DashboardView?.ViewModel?.PlaceId` with `HostUiBridgeService.Default.TryGetDashboardPlaceId()`; replace `MainWindow.Instance?.DashboardView?.ViewModel?.JobId` with `HostUiBridgeService.Default.TryGetDashboardJobId()`; replace the `MainWindow.Instance?.DashboardView?.ViewModel` block in `ModernCloseRoblox` with `HostUiBridgeService.Default.NotifyRobloxProcessesClosed(totalKilled, notify)`.
   - `src/Shiro/AccountManager.Persistence.cs`: replace `await Shiro.AvaloniaUI.Views.InputDialog.PromptAsync(...)` with `await AppServices.Dialogs.ShowInputPromptAsync(...)`.
   - `src/Shiro/AccountManager.Presence.cs`: replace `AvaloniaUI.Views.MainWindow.Instance?.RefreshLiveProcessStatus()` with `HostUiBridgeService.Default.NotifyLiveProcessStatusChanged()`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" -c Release --filter "FullyQualifiedName~ArchitectureTests|FullyQualifiedName~AccountManagerBehaviorTests|FullyQualifiedName~AccountManagerLifecycle" --nologo`
Expected: PASS (`0` errors, `0` warnings).

- [ ] **Step 5: Commit Task 1**

```bash
git add src/Shiro/Services/Ui/IDialogService.cs src/Shiro/Services/Ui/AvaloniaDialogService.cs src/Shiro/Services/Ui/IHostUiBridgeService.cs src/Shiro/Services/Ui/HostUiBridgeService.cs src/Shiro/AvaloniaUI/Views/MainWindow.axaml.cs src/Shiro/AccountManager.cs src/Shiro/AccountManager.Accounts.cs src/Shiro/AccountManager.ModernActions.cs src/Shiro/AccountManager.Persistence.cs src/Shiro/AccountManager.Presence.cs tests/Shiro.Tests/ArchitectureTests.cs tests/Shiro.Tests/AccountManagerBehaviorTests.cs
git commit -m "refactor(arch): decouple AccountManager from AvaloniaUI.Views and enforce NetArchTest across all non-UI namespaces"
```

---

### Task 2: Implement `IDisposable` on `MainViewModel` and `ServerListViewModel` to Unsubscribe Static Events

**Files:**
- Modify: `src/Shiro/ViewModels/MainViewModel.cs:23-182`
- Modify: `src/Shiro/ViewModels/MainViewModel.Commands.cs:140-150`
- Modify: `src/Shiro/ViewModels/ServerListViewModel.cs:15-40,174-207`
- Modify: `src/Shiro/AvaloniaUI/Views/MainWindow.axaml.cs:200-220`
- Test: `tests/Shiro.Tests/MvvmFoundationTests.cs`

**Interfaces:**
- Consumes: `LocalizationService.LanguageChanged`, `RobloxAudioService.StatusChanged`, `AccentThemeService.ThemeChanged`, `LaunchCoordinator.LaunchProgressChanged`, `StreamerModeService.StateChanged`
- Produces:
  - `MainViewModel : ObservableObject, IDisposable` with `public void Dispose()`
  - `ServerListViewModel : ObservableObject, IDisposable` with `public void Dispose()`

- [ ] **Step 1: Write the failing test in `tests/Shiro.Tests/MvvmFoundationTests.cs`**

Add `MainViewModel_Dispose_UnsubscribesAllStaticEvents_AndIsIdempotent` to `tests/Shiro.Tests/MvvmFoundationTests.cs`:

```csharp
        [Fact]
        public void MainViewModel_Dispose_UnsubscribesAllStaticEvents_AndIsIdempotent()
        {
            var prevLang = LocalizationService.CurrentLanguage;
            var prevTheme = AccentThemeService.CurrentTheme;
            try
            {
                var vm = new MainViewModel();
                var serverVm = vm.ServerListViewModel;
                Assert.IsAssignableFrom<IDisposable>(vm);
                Assert.IsAssignableFrom<IDisposable>(serverVm);

                vm.Dispose();
                vm.Dispose(); // idempotent second call

                int mainPropChanges = 0;
                int serverPropChanges = 0;
                vm.PropertyChanged += (_, _) => mainPropChanges++;
                serverVm.PropertyChanged += (_, _) => serverPropChanges++;

                LocalizationService.SetLanguage(prevLang == AppLanguage.Vietnamese ? AppLanguage.English : AppLanguage.Vietnamese);
                AccentThemeService.ApplyTheme(prevTheme == AccentTheme.Blue ? AccentTheme.Emerald : AccentTheme.Blue);
                StreamerModeService.SetEnabled(!StreamerModeService.IsEnabled);
                StreamerModeService.SetEnabled(!StreamerModeService.IsEnabled);

                Assert.Equal(0, mainPropChanges);
                Assert.Equal(0, serverPropChanges);
            }
            finally
            {
                LocalizationService.SetLanguage(prevLang);
                AccentThemeService.ApplyTheme(prevTheme);
            }
        }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" -c Release --filter "FullyQualifiedName~MainViewModel_Dispose_UnsubscribesAllStaticEvents_AndIsIdempotent" --nologo`
Expected: FAIL (compile error or assertion failure because `MainViewModel` and `ServerListViewModel` do not implement `IDisposable` and leak anonymous lambda handlers on `LocalizationService.LanguageChanged` and `StreamerModeService.StateChanged`).

- [ ] **Step 3: Implement `IDisposable` on `MainViewModel` and `ServerListViewModel` and wire `MainWindow.OnClosed`**

1. In `src/Shiro/ViewModels/MainViewModel.cs`:
   - Declare `public partial class MainViewModel : ObservableObject, IDisposable` and `private int _disposed;`.
   - Replace the anonymous `LocalizationService.LanguageChanged += lang => ...` lambda (lines 97-111) with `LocalizationService.LanguageChanged += OnLanguageChanged;` and a named method `private void OnLanguageChanged(AppLanguage lang)` that dispatches via `AppDispatcher.Post(...)`.
   - Wrap `OnAccentThemeChanged(AccentTheme theme)` body in `AppDispatcher.Post(...)`.
   - Implement `public void Dispose()`:
     ```csharp
     public void Dispose()
     {
         if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0)
             return;

         LocalizationService.LanguageChanged -= OnLanguageChanged;
         RobloxAudioService.StatusChanged -= OnAudioStatusChanged;
         AccentThemeService.ThemeChanged -= OnAccentThemeChanged;
         LaunchCoordinator.LaunchProgressChanged -= OnLaunchProgressChanged;
         StreamerModeService.StateChanged -= OnStreamerModeStateChanged;

         var searchCts = System.Threading.Interlocked.Exchange(ref _searchCts, null);
         SafeExecuteHelper.Try(() => searchCts?.Cancel(), "MainViewModel.Dispose.SearchCtsCancel");
         SafeExecuteHelper.Try(() => searchCts?.Dispose(), "MainViewModel.Dispose.SearchCtsDispose");

         var gameCts = System.Threading.Interlocked.Exchange(ref _gameLookupCts, null);
         SafeExecuteHelper.Try(() => gameCts?.Cancel(), "MainViewModel.Dispose.GameCtsCancel");
         SafeExecuteHelper.Try(() => gameCts?.Dispose(), "MainViewModel.Dispose.GameCtsDispose");

         ServerListViewModel?.Dispose();
         GC.SuppressFinalize(this);
     }
     ```
2. In `src/Shiro/ViewModels/MainViewModel.Commands.cs` (lines 142-149):
   - Replace `StreamerModeService.StateChanged += active => ...` with `StreamerModeService.StateChanged += OnStreamerModeStateChanged;` and define `private void OnStreamerModeStateChanged(bool active)`.
3. In `src/Shiro/ViewModels/ServerListViewModel.cs`:
   - Declare `public partial class ServerListViewModel : ObservableObject, IDisposable` and `private int _disposed;`.
   - Replace `LocalizationService.LanguageChanged += lang => ...` (lines 174-184) with `LocalizationService.LanguageChanged += OnLanguageChanged;` and `private void OnLanguageChanged(AppLanguage lang)` wrapped in `AppDispatcher.Post(...)`.
   - Implement `public void Dispose()` (unsubscribing `LocalizationService.LanguageChanged -= OnLanguageChanged;` and calling `ResetOperationCts(createNew: false)`). Keep total line count across `ServerListViewModel*.cs` `<= 1800` lines.
4. In `src/Shiro/AvaloniaUI/Views/MainWindow.axaml.cs`:
   - Override `protected override void OnClosed(EventArgs e)` to call `ViewDashboard?.ViewModel?.Dispose()`, clear `HostUiBridgeService.Default` UI sink handlers, clear `Instance = null` if `ReferenceEquals(Instance, this)`, and call `base.OnClosed(e)`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" -c Release --filter "FullyQualifiedName~MvvmFoundationTests|FullyQualifiedName~ArchitectureTests" --nologo`
Expected: PASS (`0` failures, `NoClass_ShouldExceedLineBudget_AcrossPartials` passes).

- [ ] **Step 5: Commit Task 2**

```bash
git add src/Shiro/ViewModels/MainViewModel.cs src/Shiro/ViewModels/MainViewModel.Commands.cs src/Shiro/ViewModels/ServerListViewModel.cs src/Shiro/AvaloniaUI/Views/MainWindow.axaml.cs tests/Shiro.Tests/MvvmFoundationTests.cs
git commit -m "fix(mvvm): implement IDisposable on MainViewModel and ServerListViewModel to unsubscribe static events"
```

---

### Task 3: Transfer Account Collection State Ownership to `AccountRepository`, Remove Sync HTTP in `Account` Constructor & Zero Backup Buffers

**Files:**
- Modify: `src/Shiro/Services/Storage/AccountRepository.cs:11-147`
- Modify: `src/Shiro/AccountManager.cs:30-31,132,165-273`
- Modify: `src/Shiro/AccountManager.Accounts.cs:57-70,114-213`
- Modify: `src/Shiro/AccountManager.BackupRecovery.cs:70-100`
- Modify: `src/Shiro/Classes/Account.cs:379-433`
- Modify: `tests/Shiro.Tests/ArchitectureTests.cs:553-586`
- Test: `tests/Shiro.Tests/AccountDecouplingTests.cs`

**Interfaces:**
- Consumes: `Account`, `IAccountRepository`, `HostUiBridgeService`
- Produces:
  - `AccountRepository` instance-owned collection methods:
    - `internal System.Threading.Lock SyncRoot { get; }`
    - `public int Count { get; }`
    - `public IReadOnlyList<Account> GetAll()`
    - `public void AddThreadSafe(Account account)`
    - `internal IReadOnlyList<Account> ReplaceAll(IEnumerable<Account>? accounts)`
    - `internal Account? FindFirst(Func<Account, bool> predicate)`
    - `internal (int RemovedCount, bool IsNowEmpty) RemoveMatching(ISet<Account> toRemove)`
  - Non-blocking `public Account(string Cookie, string? AccountJSON = null)` constructor (parses `AccountJSON` if valid; otherwise sets `Valid = false` without calling `httpClient.Send`)

- [ ] **Step 1: Write the failing tests in `tests/Shiro.Tests/AccountDecouplingTests.cs` and `tests/Shiro.Tests/ArchitectureTests.cs`**

In `tests/Shiro.Tests/AccountDecouplingTests.cs`, add:

```csharp
        [Fact]
        public void AccountRepository_OwnsInMemoryCollection_IsolatesInstances_AndSyncsWithAccountManager()
        {
            var previous = AccountRepository.Default.GetAll().ToList();
            try
            {
                var isolatedRepo = new Services.AccountRepository();
                var isolatedAcc = new Account { UserID = 5001, Username = "IsolatedUser", Valid = true, SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--" + new string('A', 900) };
                isolatedRepo.AddThreadSafe(isolatedAcc);

                Assert.Equal(1, isolatedRepo.Count);
                Assert.Same(isolatedAcc, isolatedRepo.GetById(5001));
                Assert.Null(AccountRepository.Default.GetById(5001));

                var sharedAcc = new Account { UserID = 6001, Username = "SharedUser", Valid = true, SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--" + new string('B', 900) };
                AccountManager.ReplaceAccountsList(new List<Account> { sharedAcc });

                Assert.Equal(1, AccountRepository.Default.Count);
                Assert.Equal(1, AccountManager.AccountCount);
                Assert.Same(sharedAcc, AccountRepository.Default.GetByUsername("shareduser"));
                Assert.Equal(1, isolatedRepo.Count);
            }
            finally
            {
                AccountManager.ReplaceAccountsList(previous);
            }
        }

        [Fact]
        public void Account_SyncConstructor_DoesNotPerformNetworkIo_WhenAccountJsonMissingOrInvalid()
        {
            var accWithoutJson = new Account("_|WARNING:-DO-NOT-SHARE-THIS.--test-cookie");
            Assert.False(accWithoutJson.Valid);
            Assert.Equal("_|WARNING:-DO-NOT-SHARE-THIS.--test-cookie", accWithoutJson.SecurityToken);

            var accWithValidJson = new Account(
                "_|WARNING:-DO-NOT-SHARE-THIS.--valid-cookie",
                "{\"UserId\":123456,\"Name\":\"JsonConstructedUser\",\"DisplayName\":\"JsonUser\"}");
            Assert.True(accWithValidJson.Valid);
            Assert.Equal(123456L, accWithValidJson.UserID);
            Assert.Equal("JsonConstructedUser", accWithValidJson.Username);
        }

        [Fact]
        public void BackupRecovery_SourceCode_ZeroesDecodedPlaintextBuffersInFinally()
        {
            string path = System.IO.Path.Combine(ArchitectureTests.RepoRoot, "src", "Shiro", "AccountManager.BackupRecovery.cs");
            string content = System.IO.File.ReadAllText(path);
            Assert.Contains("CryptographicOperations.ZeroMemory(decoded)", content, StringComparison.Ordinal);
        }
```

In `tests/Shiro.Tests/ArchitectureTests.cs`, update `SourceFiles_ShouldNotUse_SyncOverAsyncBlocking` (lines 574-582) to also forbid synchronous `httpClient.Send(`:

```csharp
                if (content.Contains(".GetAwaiter().GetResult()", StringComparison.Ordinal) ||
                    content.Contains("httpClient.Send(", StringComparison.Ordinal))
                {
                    violations.Add($"{Path.GetFileName(file)} uses synchronous blocking (.GetAwaiter().GetResult() or httpClient.Send)");
                }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" -c Release --filter "FullyQualifiedName~AccountDecouplingTests|FullyQualifiedName~SourceFiles_ShouldNotUse_SyncOverAsyncBlocking" --nologo`
Expected: FAIL (`AccountRepository` delegates to static `AccountManager`, `Account.cs` contains `httpClient.Send(req)`, and `AccountManager.BackupRecovery.cs` does not call `ZeroMemory(decoded)`).

- [ ] **Step 3: Move collection state into `AccountRepository`, remove `httpClient.Send` from `Account.cs`, and zero `decoded` in `AccountManager.BackupRecovery.cs`**

1. In `src/Shiro/Services/Storage/AccountRepository.cs`:
   - Give each `AccountRepository` instance its own `private readonly System.Threading.Lock _syncRoot = new();`, `private List<Account> _accounts = new();`, `private Account[] _cachedSnapshot = Array.Empty<Account>();`, and `private volatile SnapshotIndex? _cachedIndex;`.
   - Expose `internal System.Threading.Lock SyncRoot => _syncRoot;`.
   - Implement `Count`, `GetAll()`, `AddThreadSafe(Account)`, `ReplaceAll(IEnumerable<Account>?)`, `FindFirst(Func<Account, bool>)`, and `RemoveMatching(ISet<Account>)` directly on `AccountRepository`.
2. In `src/Shiro/AccountManager.cs` and `src/Shiro/AccountManager.Accounts.cs`:
   - Remove `private static List<Account> _accounts` and `private static Account[] _cachedAccountsSnapshot` from `AccountManager.cs`.
   - Route `AccountListLock => AccountRepository.Default.SyncRoot`, `GetAccountsSnapshot() => AccountRepository.Default.GetAll()`, `AccountCount => AccountRepository.Default.Count`, `AddAccountThreadSafe(Account) => AccountRepository.Default.AddThreadSafe(account)`, and `ReplaceAccountsList(List<Account>)` through `AccountRepository.Default.ReplaceAll(accounts)` + `HostUiBridgeService.Default.NotifyAccountsReplaced(snapshot)`.
   - In `AccountManager.Accounts.cs`, replace direct `_accounts` lock blocks in `GetUserIDAsync`, `RegisterValidatedAccount`, and `RemoveAccountsFromDatabase` with `AccountRepository.Default.FindFirst(...)` and `AccountRepository.Default.RemoveMatching(...)`.
3. In `src/Shiro/Classes/Account.cs` (lines 382-433):
   - Keep `AccountJSON` parsing in `public Account(string Cookie, string? AccountJSON = null)`, and remove the synchronous `httpClient.Send(req)` network block so missing/invalid `AccountJSON` sets `Valid = false` without blocking threads.
4. In `src/Shiro/AccountManager.BackupRecovery.cs` (lines 75-96):
   - Add `using System.Security.Cryptography;` and wrap the usage of `byte[]? decoded` inside `try { ... } finally { if (decoded != null) CryptographicOperations.ZeroMemory(decoded); }`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" -c Release --filter "FullyQualifiedName~AccountDecouplingTests|FullyQualifiedName~StreamerModeAndStorageOptimizationTests|FullyQualifiedName~AccountManager|FullyQualifiedName~ArchitectureTests" --nologo`
Expected: PASS (`0` failures).

- [ ] **Step 5: Commit Task 3**

```bash
git add src/Shiro/Services/Storage/AccountRepository.cs src/Shiro/AccountManager.cs src/Shiro/AccountManager.Accounts.cs src/Shiro/AccountManager.BackupRecovery.cs src/Shiro/Classes/Account.cs tests/Shiro.Tests/AccountDecouplingTests.cs tests/Shiro.Tests/ArchitectureTests.cs
git commit -m "refactor(state): move account collection ownership to AccountRepository and remove sync HTTP in Account ctor"
```

---

### Task 4: Synchronize `v5.0.0` Version Metadata & Documentation and Enforce Version Parity in `ArchitectureTests`

**Files:**
- Modify: `src/Shiro/Shiro.csproj:17-19`
- Modify: `src/Shiro/AvaloniaUI/Views/AboutView.axaml:22`
- Modify: `src/Shiro/Resources/Strings/en.json:16,24`
- Modify: `src/Shiro/Resources/Strings/vi.json:16,24`
- Modify: `src/Shiro/Services/Network/ApiDocumentationService.cs:26,208`
- Modify: `src/Shiro/Services/Network/KestrelWebServer.cs:173`
- Modify: `SECURITY.md`
- Modify: `CONTRIBUTING.md`
- Modify: `NOTICE.md`
- Modify: `README.md`
- Modify: `CHANGELOG.md`
- Modify: `docs/threat-model.md`
- Modify: `docs/perf-baseline.md`
- Modify: `tests/Shiro.Tests/RobloxSingletonServiceBehaviorTests.cs:25-38`
- Modify: `tests/Shiro.Tests/MemoryOptimizerBehaviorTests.cs:135-146`
- Modify: `tests/Shiro.Tests/ArchitectureTests.cs`

**Interfaces:**
- Consumes: `ApiDocumentationService`, `LocalizationService`, `Shiro.csproj`
- Produces: Synchronized `5.0.0` version across assembly, UI, OpenAPI/docs, localization strings, and markdown docs, guarded by `ArchitectureTests.VersionMetadata_ShouldBeSynchronizedAcrossProjectAndDocs`

- [ ] **Step 1: Write the failing test `VersionMetadata_ShouldBeSynchronizedAcrossProjectAndDocs` in `tests/Shiro.Tests/ArchitectureTests.cs`**

Add `VersionMetadata_ShouldBeSynchronizedAcrossProjectAndDocs` to `tests/Shiro.Tests/ArchitectureTests.cs`:

```csharp
        [Fact]
        public void VersionMetadata_ShouldBeSynchronizedAcrossProjectAndDocs()
        {
            const string ExpectedVersion = "5.0.0";

            string csproj = File.ReadAllText(Path.Combine(RepoRoot, "src", "Shiro", "Shiro.csproj"));
            Assert.Contains($"<Version>{ExpectedVersion}</Version>", csproj, StringComparison.Ordinal);
            Assert.Contains($"<AssemblyVersion>{ExpectedVersion}.0</AssemblyVersion>", csproj, StringComparison.Ordinal);
            Assert.Contains($"<FileVersion>{ExpectedVersion}.0</FileVersion>", csproj, StringComparison.Ordinal);

            Version? asmVersion = CoreAssembly.GetName().Version;
            Assert.Equal(new Version(5, 0, 0, 0), asmVersion);

            string aboutView = File.ReadAllText(Path.Combine(RepoRoot, "src", "Shiro", "AvaloniaUI", "Views", "AboutView.axaml"));
            Assert.Contains($"v{ExpectedVersion}", aboutView, StringComparison.Ordinal);

            string openApiJson = Shiro.Services.ApiDocumentationService.GetOpenApiJson();
            Assert.Contains($"\"version\":\"{ExpectedVersion}\"", openApiJson.Replace(" ", ""), StringComparison.Ordinal);

            string docsHtml = Shiro.Services.ApiDocumentationService.GetDocsHtml();
            Assert.Contains($"Gateway v{ExpectedVersion}", docsHtml, StringComparison.Ordinal);

            string enJson = File.ReadAllText(Path.Combine(RepoRoot, "src", "Shiro", "Resources", "Strings", "en.json"));
            string viJson = File.ReadAllText(Path.Combine(RepoRoot, "src", "Shiro", "Resources", "Strings", "vi.json"));
            Assert.Contains($"Version {ExpectedVersion}", enJson, StringComparison.Ordinal);
            Assert.Contains($"Phiên bản {ExpectedVersion}", viJson, StringComparison.Ordinal);

            string securityMd = File.ReadAllText(Path.Combine(RepoRoot, "SECURITY.md"));
            Assert.Contains("5.0.x", securityMd, StringComparison.Ordinal);

            string noticeMd = File.ReadAllText(Path.Combine(RepoRoot, "NOTICE.md"));
            Assert.Contains($"v{ExpectedVersion}", noticeMd, StringComparison.Ordinal);

            string contributingMd = File.ReadAllText(Path.Combine(RepoRoot, "CONTRIBUTING.md"));
            Assert.Contains("-MinOverall 75.0", contributingMd, StringComparison.Ordinal);
            Assert.Contains("-MinCore 92.0", contributingMd, StringComparison.Ordinal);

            string threatModelMd = File.ReadAllText(Path.Combine(RepoRoot, "docs", "threat-model.md"));
            Assert.DoesNotContain("AccountData.dat", threatModelMd, StringComparison.Ordinal);
            Assert.Contains("AccountData.json", threatModelMd, StringComparison.Ordinal);
        }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" -c Release --filter "FullyQualifiedName~VersionMetadata_ShouldBeSynchronizedAcrossProjectAndDocs" --nologo`
Expected: FAIL (`Shiro.csproj` is still `4.2.0`, `en.json`/`vi.json` and `SECURITY.md` are `4.1.0`, `CONTRIBUTING.md` still has `-MinOverall 45.0`, `docs/threat-model.md` still has `AccountData.dat`).

- [ ] **Step 3: Synchronize `5.0.0` version metadata, update documentation, and tighten `RobloxSingletonServiceBehaviorTests` / `MemoryOptimizerBehaviorTests` assertions**

1. Update `src/Shiro/Shiro.csproj` lines 17-19 to `5.0.0` / `5.0.0.0` / `5.0.0.0`.
2. Update `src/Shiro/AvaloniaUI/Views/AboutView.axaml:22` to `v5.0.0 • Avalonia UI 11.2 • .NET 10 (win-x64)`.
3. Update `"About_Project"` and `"About_VersionInfo"` in `src/Shiro/Resources/Strings/en.json` and `src/Shiro/Resources/Strings/vi.json` from `4.1.0` to `5.0.0`.
4. Update `src/Shiro/Services/Network/ApiDocumentationService.cs` (`"4.2.0"` -> `"5.0.0"`, `Gateway v4.2.0` -> `Gateway v5.0.0`) and `src/Shiro/Services/Network/KestrelWebServer.cs:173` (`"4.2.0"` -> `"5.0.0"`).
5. Update `SECURITY.md` (`5.0.x` supported, dual-KDF V3 PBKDF2 600k + V4 HKDF, WebView2 `https://*.roblox.com` origin lock, Auto-Updater fail-closed SHA-256 & VCRedist Authenticode verification), `CONTRIBUTING.md` (`-MinOverall 75.0 -MinBranch 60.0 -MinCore 92.0`), `NOTICE.md` (`v5.0.0`, 600,000 iterations V3 + HKDF-SHA256 V4), `README.md` (`1,865+` unit tests, current coverage gates), `CHANGELOG.md` (add `[5.0.0]` P0/P1/P2 hardening summary), `docs/threat-model.md` (`AccountData.dat` -> `AccountData.json`, HBA origin lock, Kestrel `Hba*` block, Nexus `UserID` verification, webhook DPAPI encryption), and `docs/perf-baseline.md` (mark §3 items as remediated in `v5.0.0`).
6. In `tests/Shiro.Tests/RobloxSingletonServiceBehaviorTests.cs:25-38`, replace tautological `Assert.InRange(elevated ? 1 : 0, 0, 1)` with concrete state assertions (`Assert.Equal(WindowsIdentity.GetCurrent().Owner?.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ?? false, RobloxSingletonService.IsElevated)` or `Assert.True(RobloxSingletonService.IsEnabled)`), and in `tests/Shiro.Tests/MemoryOptimizerBehaviorTests.cs:135-146`, assert `Assert.Equal(1, count1)` and `Assert.Equal(1, count2)` when optimizing `[Environment.ProcessId]`.

- [ ] **Step 4: Run full test suite and coverage check to verify everything passes**

Run:
```powershell
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" -c Release --collect:"XPlat Code Coverage" --settings "tests/coverage.runsettings" --results-directory "coverage" --nologo
powershell -NoProfile -ExecutionPolicy Bypass -File "scripts/Check-Coverage.ps1" -CoverageDir "coverage" -MinOverall 75.0 -MinBranch 60.0 -MinCore 92.0
```
Expected: All `1868+` tests PASS (`0` warnings, `0` errors), and `SUCCESS: Coverage gates passed.`

- [ ] **Step 5: Commit Task 4**

```bash
git add src/Shiro/Shiro.csproj src/Shiro/AvaloniaUI/Views/AboutView.axaml src/Shiro/Resources/Strings/en.json src/Shiro/Resources/Strings/vi.json src/Shiro/Services/Network/ApiDocumentationService.cs src/Shiro/Services/Network/KestrelWebServer.cs SECURITY.md CONTRIBUTING.md NOTICE.md README.md CHANGELOG.md docs/threat-model.md docs/perf-baseline.md tests/Shiro.Tests/RobloxSingletonServiceBehaviorTests.cs tests/Shiro.Tests/MemoryOptimizerBehaviorTests.cs tests/Shiro.Tests/ArchitectureTests.cs
git commit -m "docs(release): synchronize v5.0.0 metadata and docs and enforce version parity in ArchitectureTests"
```
